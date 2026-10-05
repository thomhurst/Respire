using System.Buffers;
using System.IO.Compression;
using Respire.Compression;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Serialization;

public class DeflateValueCodecTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BothWriteOverloadsDiscardOverflowWithoutTouchingAdjacentMemory(bool useSpan)
    {
        byte[] destination = [42, 0, 0, 0, 0, 43];
        byte[] first = [1, 2, 3];
        byte[] tooLarge = [4, 5];
        bool overflowed;
        long written;
        unsafe
        {
            fixed (byte* pointer = destination)
            {
                using var stream = new DeflateValueCodec.BoundedWriteStream(pointer + 1, 4);
                if (useSpan)
                {
                    stream.Write(first.AsSpan());
                    stream.Write(tooLarge.AsSpan());
                    stream.Write(new byte[] { 6 }.AsSpan());
                }
                else
                {
                    stream.Write(first, 0, first.Length);
                    stream.Write(tooLarge, 0, tooLarge.Length);
                    stream.Write(new byte[] { 6 }, 0, 1);
                }
                overflowed = stream.Overflowed;
                written = stream.Length;
            }
        }
        await Assert.That(overflowed).IsTrue();
        await Assert.That(written).IsEqualTo(3);
        await Assert.That(destination.AsSpan().SequenceEqual(new byte[] { 42, 1, 2, 3, 0, 43 })).IsTrue();
    }

    [Test]
    [Arguments(CompressionLevel.Fastest)]
    [Arguments(CompressionLevel.Optimal)]
    [Arguments(CompressionLevel.SmallestSize)]
    [Arguments(CompressionLevel.NoCompression)]
    public async Task BoundedCompressionMatchesIndependentDeflateAndKeepsOwnership(CompressionLevel level)
    {
        var codec = new DeflateValueCodec(new() { MinimumLength = 0 }, level);
        var random = new byte[65536];
        new Random(982).NextBytes(random);
        foreach (var input in new[] { Array.Empty<byte>(), new byte[1], new byte[4096], random })
        {
            using var reference = new MemoryStream();
            using (var encoder = new DeflateStream(reference, level, leaveOpen: true)) encoder.Write(input);
            var compressed = reference.ToArray();
            var expected = compressed.Length < input.Length ? compressed : input;
            var frame = codec.Encode(input);
            await Assert.That(frame.AsSpan(RespireValueCodec.HeaderLength).ToArray()).IsEquivalentTo(expected);
            await Assert.That(frame[5]).IsEqualTo(compressed.Length < input.Length ? (byte)2 : (byte)0);
            var padded = new byte[frame.Length + 11];
            frame.CopyTo(padded, 7);
            var destination = new ArrayBufferWriter<byte>();
            destination.Write(new byte[] { 42 });
            codec.Decode(padded.AsSpan(7, frame.Length), destination);
            await Assert.That(destination.WrittenMemory.ToArray()).IsEquivalentTo(new byte[] { 42 }.Concat(input));
            await Assert.That(padded.AsSpan(7, frame.Length).ToArray()).IsEquivalentTo(frame);
        }
    }
}
