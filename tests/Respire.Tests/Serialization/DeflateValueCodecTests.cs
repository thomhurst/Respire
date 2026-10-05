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
