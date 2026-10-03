using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Respire.Compression;
using Respire.Compression.Lz4;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Serialization;

public class Lz4ValueCodecTests
{
    [Test]
    public async Task ZeroThresholdHandlesEmptyAndTinyCompressionDestinations()
    {
        var codec = new Lz4ValueCodec(new() { MinimumLength = 0 });
        for (var length = 0; length <= 32; length++)
        {
            var payload = Enumerable.Range(0, length).Select(value => (byte)value).ToArray();
            var frame = codec.Encode(payload);
            await Assert.That(frame[5]).IsEqualTo((byte)0);
            await Assert.That(codec.Decode(frame)).IsEquivalentTo(payload);
        }
    }

    [Test]
    [Arguments(0)]
    [Arguments(3)]
    [Arguments(4)]
    [Arguments(5)]
    [Arguments(6)]
    [Arguments(7)]
    [Arguments(8)]
    [Arguments(9)]
    [Arguments(10)]
    [Arguments(11)]
    [Arguments(12)]
    public async Task CompressionLevelsShareTheReservedBlockFormat(int level)
    {
        var payload = new byte[8192];
        new Random(525).NextBytes(payload.AsSpan(0, 64));
        for (var offset = 64; offset < payload.Length; offset += 64)
            payload.AsSpan(0, 64).CopyTo(payload.AsSpan(offset));
        var frame = new Lz4ValueCodec(level: level).Encode(payload);
        await Assert.That(frame[4]).IsEqualTo((byte)1);
        await Assert.That(frame[5]).IsEqualTo((byte)3);
        await Assert.That(frame.Length).IsLessThan(payload.Length);
        // The decoder uses no encoder-level state.
        await Assert.That(new Lz4ValueCodec().Decode(frame)).IsEquivalentTo(payload);
    }

    [Test]
    public async Task InvalidLevelsAndOptionsFailAtConstruction()
    {
        foreach (var level in new[] { int.MinValue, -1, 1, 2, 13, int.MaxValue })
            await Assert.That(() => new Lz4ValueCodec(level: level)).Throws<ArgumentOutOfRangeException>();
        foreach (var options in new RespireValueCodecOptions[]
                 { new() { MinimumLength = -1 }, new() { MaximumDecodedLength = 0 }, new() { MaximumDecodedLength = int.MaxValue } })
            await Assert.That(() => new Lz4ValueCodec(options)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task DecoderAcceptsIndependentBlockAndRejectsTrailingBytes()
    {
        // Raw LZ4: one 'A', a 26-byte match at offset 1, then five terminal literals.
        byte[] block = [0x1f, 0x41, 1, 0, 7, 0x50, 0x41, 0x41, 0x41, 0x41, 0x41];
        var codec = new Lz4ValueCodec();
        await Assert.That(codec.Decode(Frame(block, 32))).IsEquivalentTo(Enumerable.Repeat((byte)'A', 32));
        foreach (var invalid in new[] { block[..^1], new byte[] { 0xff }, block.Concat(new byte[] { 0 }).ToArray() })
        {
            var frame = Frame(invalid, 32); // Correct checksum forces block-level validation.
            await Assert.That(() => codec.Decode(frame)).Throws<InvalidDataException>();
        }
    }

    [Test]
    public async Task DecodedSizeIsCheckedBeforeRequestingDestinationMemory()
    {
        var codec = new Lz4ValueCodec(new() { MaximumDecodedLength = 64 });
        var oversized = new Lz4ValueCodec().Encode(new byte[4096]);
        await Assert.That(() => codec.Decode(oversized, new RejectingWriter())).Throws<InvalidDataException>();
        var uncompressed = new BrotliValueCodec().Encode("abc"u8);
        await Assert.That(codec.Decode(uncompressed)).IsEquivalentTo("abc"u8.ToArray());
    }

    private static byte[] Frame(byte[] payload, int decodedLength)
    {
        var frame = new byte[RespireValueCodec.HeaderLength + payload.Length];
        "RVC\0"u8.CopyTo(frame);
        frame[4] = 1;
        frame[5] = 3;
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
