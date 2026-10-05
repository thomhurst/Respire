using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Reservoir;
using Respire.Compression;
using Respire.Compression.Zstd;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using ZstdSharp;

namespace Respire.Tests.Serialization;

public class ZstdValueCodecTests
{
    [Test]
    public async Task ReusedContextsMatchFreshContextsAcrossLevelsAndFailures()
    {
        var random = new byte[16384];
        new Random(980).NextBytes(random);
        var repeated = new byte[16384];
        for (var index = 0; index < repeated.Length; index++) repeated[index] = (byte)(index % 97);
        var boundary = new byte[65536];
        var large = new byte[65537];
        // Alternate incompressible input (TryWrap returns false), compressed input, and discarded
        // large workspaces. Each resulting frame must match a completely fresh context.
        foreach (var level in new[] { 3, 10, -1, 0, 22, 1, -131072, 3 })
        {
            var codec = new ZstdValueCodec(new() { MinimumLength = 0 }, level);
            foreach (var payload in new[] { random, repeated, boundary, large, repeated })
            {
                using var fresh = new Compressor(level);
                var compressed = fresh.Wrap(payload).ToArray();
                var frame = codec.Encode(payload);
                var expected = compressed.Length < payload.Length ? compressed : payload;
                await Assert.That(frame.AsSpan(RespireValueCodec.HeaderLength).SequenceEqual(expected)).IsTrue();
                await Assert.That(new ZstdValueCodec().Decode(frame).AsSpan().SequenceEqual(payload)).IsTrue();
            }
            byte[] block = [0x28, 0xb5, 0x2f, 0xfd, 0x20, 64, 0x03, 0x02, 0, 0x41];
            foreach (var invalidLength in new[] { 63, 65 })
            {
                await Assert.That(() => codec.Decode(Frame(block, invalidLength))).Throws<InvalidDataException>();
                await Assert.That(codec.Decode(codec.Encode(repeated)).AsSpan().SequenceEqual(repeated)).IsTrue();
            }
        }
    }

    [Test]
    public async Task ConcurrentCodecsKeepLevelsAndPayloadsIndependent()
    {
        await Task.WhenAll(Enumerable.Range(0, 16).Select(worker => Task.Run(async () =>
        {
            var level = (worker % 4) switch { 0 => -1, 1 => 0, 2 => 3, _ => 10 };
            var codec = new ZstdValueCodec(new() { MinimumLength = 0 }, level);
            var payload = Enumerable.Range(0, 16384).Select(index => (byte)((index + worker) % 127)).ToArray();
            using var fresh = new Compressor(level);
            var expected = fresh.Wrap(payload).ToArray();
            for (var iteration = 0; iteration < 32; iteration++)
            {
                var frame = codec.Encode(payload);
                await Assert.That(frame.AsSpan(RespireValueCodec.HeaderLength).SequenceEqual(expected)).IsTrue();
                await Assert.That(codec.Decode(frame).AsSpan().SequenceEqual(payload)).IsTrue();
            }
        })));
    }

    [Test]
    public async Task PoolEvictionAndTrimmingDisposeContextMemory()
    {
        using var compressors = new ObjectPool<Compressor, ZstdValueCodec.CompressorPolicy>(1);
        using var decompressors = new ObjectPool<Decompressor, ZstdValueCodec.DecompressorPolicy>(1);
        var firstCompressor = compressors.Rent();
        var secondCompressor = compressors.Rent();
        var firstDecompressor = decompressors.Rent();
        var secondDecompressor = decompressors.Rent();
        compressors.Return(firstCompressor);
        compressors.Return(secondCompressor);
        decompressors.Return(firstDecompressor);
        decompressors.Return(secondDecompressor);
        // Overflow may discard either object; exactly one remains alive before trimming.
        await Assert.That(IsAlive(firstCompressor) != IsAlive(secondCompressor)).IsTrue();
        await Assert.That(IsAlive(firstDecompressor) != IsAlive(secondDecompressor)).IsTrue();
        compressors.Clear();
        decompressors.Clear();
        await Assert.That(IsAlive(firstCompressor) || IsAlive(secondCompressor)).IsFalse();
        await Assert.That(IsAlive(firstDecompressor) || IsAlive(secondDecompressor)).IsFalse();
        // A context returned after its pool is disposed must also release its unmanaged memory.
        var outstandingCompressor = compressors.Rent();
        var outstandingDecompressor = decompressors.Rent();
        compressors.Dispose();
        decompressors.Dispose();
        compressors.Return(outstandingCompressor);
        decompressors.Return(outstandingDecompressor);
        await Assert.That(IsAlive(outstandingCompressor) || IsAlive(outstandingDecompressor)).IsFalse();
    }

    private static bool IsAlive(Compressor context)
    {
        try { context.ResetStream(); return true; }
        catch (ObjectDisposedException) { return false; }
    }

    private static bool IsAlive(Decompressor context)
    {
        try { context.ResetStream(); return true; }
        catch (ObjectDisposedException) { return false; }
    }

    [Test]
    public async Task TinyValuesAndCompressionLevelsRoundTrip()
    {
        foreach (var level in new[] { -131072, -1, 0, 1, 3, 10, 22 })
        {
            var codec = new ZstdValueCodec(new() { MinimumLength = 0 }, level);
            foreach (var length in new[] { 0, 1, 2, 16, 64, 4096 })
            {
                var payload = Enumerable.Repeat((byte)'A', length).ToArray();
                var frame = codec.Encode(payload);
                await Assert.That(new ZstdValueCodec().Decode(frame)).IsEquivalentTo(payload);
                await Assert.That(frame.Length).IsLessThanOrEqualTo(length + RespireValueCodec.HeaderLength);
                if (length <= 2) await Assert.That(frame[5]).IsEqualTo((byte)0);
            }
        }
        await Assert.That(new ZstdValueCodec().Encode(new byte[4096])[5]).IsEqualTo((byte)4);
    }

    [Test]
    public async Task InvalidLevelsFailAtConstruction()
    {
        foreach (var level in new[] { int.MinValue, -131073, 23, int.MaxValue })
            await Assert.That(() => new ZstdValueCodec(level: level)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task DecoderAcceptsIndependentRleFrameAndRejectsAdditionalFrames()
    {
        // Standard magic, single-segment descriptor, content size 64, final RLE block of 64 'A's.
        byte[] block = [0x28, 0xb5, 0x2f, 0xfd, 0x20, 64, 0x03, 0x02, 0, 0x41];
        var codec = new ZstdValueCodec();
        await Assert.That(codec.Decode(Frame(block, 64))).IsEquivalentTo(Enumerable.Repeat((byte)'A', 64));
        // An empty ordinary frame and a zero-length skippable frame are valid Zstandard
        // stream suffixes, but our algorithm ID requires exactly one ordinary frame.
        byte[] empty = [0x28, 0xb5, 0x2f, 0xfd, 0x20, 0, 1, 0, 0];
        byte[] skippable = [0x50, 0x2a, 0x4d, 0x18, 0, 0, 0, 0];
        var badBlock = block.ToArray();
        badBlock[6] = 0x07; // Reserved block type.
        foreach (var invalid in new[]
        {
            block[..^1], block.Concat(new byte[] { 0 }).ToArray(), badBlock,
            block.Concat(empty).ToArray(), block.Concat(skippable).ToArray(), skippable,
        })
        {
            var frame = Frame(invalid, 64); // Valid outer checksum exercises decoder validation.
            await Assert.That(() => codec.Decode(frame)).Throws<InvalidDataException>();
            var writer = new ArrayBufferWriter<byte>();
            writer.Write(new byte[] { 42 });
            await Assert.That(() => codec.Decode(frame, writer)).Throws<InvalidDataException>();
            await Assert.That(writer.WrittenCount).IsEqualTo(1);
        }
        foreach (var length in new[] { 63, 65 })
            await Assert.That(() => codec.Decode(Frame(block, length))).Throws<InvalidDataException>();
    }

    [Test]
    public async Task OversizedFrameIsRejectedBeforeDestinationAllocation()
    {
        var codec = new ZstdValueCodec(new() { MaximumDecodedLength = 64 });
        var oversized = new ZstdValueCodec().Encode(new byte[4096]);
        await Assert.That(() => codec.Decode(oversized, new RejectingWriter())).Throws<InvalidDataException>();
    }

    private static byte[] Frame(byte[] payload, int decodedLength)
    {
        var frame = new byte[RespireValueCodec.HeaderLength + payload.Length];
        "RVC\0"u8.CopyTo(frame);
        frame[4] = 1;
        frame[5] = 4;
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(6), decodedLength);
        SHA256.HashData(payload).AsSpan(0, 8).CopyTo(frame.AsSpan(10));
        payload.CopyTo(frame, RespireValueCodec.HeaderLength);
        return frame;
    }

    private sealed class RejectingWriter : IBufferWriter<byte>
    {
        public void Advance(int count) => throw new InvalidOperationException("Invalid input must not advance the writer.");
        public Memory<byte> GetMemory(int sizeHint = 0) => throw new InvalidOperationException("Size validation must precede allocation.");
        public Span<byte> GetSpan(int sizeHint = 0) => throw new InvalidOperationException("Size validation must precede allocation.");
    }
}
