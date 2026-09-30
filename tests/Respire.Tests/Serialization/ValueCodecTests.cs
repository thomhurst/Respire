using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Respire.Compression;
using Respire.Serialization;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Serialization;

public class ValueCodecTests
{
    [Test]
    [Arguments("brotli")]
    [Arguments("deflate")]
    [Arguments("lz4")]
    public async Task FramingKeepsMixedValuesOwnedAndUsesCompressionOnlyWhenSmaller(string algorithm)
    {
        var codec = Create(algorithm);
        var large = Encoding.UTF8.GetBytes(new string('x', 4096));
        byte[] small = [0, 1, 255];
        var random = new byte[4096];
        new Random(521).NextBytes(random);
        foreach (var input in new[] { Array.Empty<byte>(), small, large, random })
        {
            var frame = codec.Encode(input);
            var restored = codec.Decode(frame);
            await Assert.That(restored).IsEquivalentTo(input);
            var encodedDestination = new ArrayBufferWriter<byte>();
            encodedDestination.Write(new byte[] { 42 });
            codec.Encode(input, encodedDestination);
            await Assert.That(encodedDestination.WrittenMemory.ToArray()).IsEquivalentTo(new byte[] { 42 }.Concat(frame));
            var decodedDestination = new ArrayBufferWriter<byte>();
            decodedDestination.Write(new byte[] { 42 });
            codec.Decode(frame, decodedDestination);
            await Assert.That(decodedDestination.WrittenMemory.ToArray()).IsEquivalentTo(new byte[] { 42 }.Concat(input));
            await Assert.That(frame.Length).IsLessThanOrEqualTo(input.Length + RespireValueCodec.HeaderLength);
            await Assert.That(frame[5]).IsEqualTo(ReferenceEquals(input, large) ? codec.AlgorithmId : (byte)0);
            if (input.Length != 0)
            {
                var first = restored[0];
                frame[^1] ^= 1;
                await Assert.That(restored[0]).IsEqualTo(first);
            }
        }
        var owned = codec.Encode(small);
        small[0] = 99;
        await Assert.That(codec.Decode(owned)[0]).IsEqualTo((byte)0);
    }

    [Test]
    public async Task UncompressedVersionOneHasAStableFormatAndWorksAcrossAlgorithms()
    {
        byte[] expected = [0x52, 0x56, 0x43, 0, 1, 0, 3, 0, 0, 0,
            0xba, 0x78, 0x16, 0xbf, 0x8f, 0x01, 0xcf, 0xea, 0x61, 0x62, 0x63];
        var frame = new BrotliValueCodec().Encode("abc"u8);
        await Assert.That(frame).IsEquivalentTo(expected);
        await Assert.That(new DeflateValueCodec().Decode(frame)).IsEquivalentTo("abc"u8.ToArray());
    }

    [Test]
    [Arguments("brotli")]
    [Arguments("deflate")]
    [Arguments("lz4")]
    public async Task InvalidFramesFailWithoutLegacyFallback(string algorithm)
    {
        var codec = Create(algorithm);
        var original = codec.Encode(Encoding.UTF8.GetBytes(new string('x', 4096)));
        var invalid = new List<byte[]> { "unframed legacy data"u8.ToArray(), original[..17], original[..^1] };
        foreach (var offset in new[] { 0, 4, 5, 10, original.Length - 1 })
        {
            var copy = original.ToArray();
            copy[offset] ^= 0x7f;
            invalid.Add(copy);
        }
        foreach (var length in new uint[] { 4095, 4097, uint.MaxValue })
        {
            var copy = original.ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(6), length);
            invalid.Add(copy);
        }
        var corrupt = original.ToArray();
        corrupt.AsSpan(RespireValueCodec.HeaderLength).Fill(0xff);
        RefreshChecksum(corrupt); // Exercise the decompressor's validation after a structurally valid frame.
        invalid.Add(corrupt);
        foreach (var frame in invalid)
        {
            await Assert.That(() => codec.Decode(frame)).Throws<InvalidDataException>();
            var destination = new ArrayBufferWriter<byte>();
            destination.Write(new byte[] { 42 });
            await Assert.That(() => codec.Decode(frame, destination)).Throws<InvalidDataException>();
            await Assert.That(destination.WrittenCount).IsEqualTo(1);
            await Assert.That(destination.WrittenMemory.ToArray()).IsEquivalentTo(new byte[] { 42 });
        }
        await Assert.That(() => Create(algorithm == "brotli" ? "deflate" : "brotli").Decode(original)).Throws<InvalidDataException>();
    }

    [Test]
    [Arguments("brotli")]
    [Arguments("deflate")]
    [Arguments("lz4")]
    public async Task SizeLimitsAndThresholdAreExplicit(string algorithm)
    {
        var bounded = Create(algorithm, new() { MaximumDecodedLength = 64, MinimumLength = 64 });
        var data = new byte[64];
        var frame = bounded.Encode(data);
        await Assert.That(frame[5]).IsEqualTo(bounded.AlgorithmId);
        await Assert.That(bounded.Decode(frame)).IsEquivalentTo(data);
        await Assert.That(bounded.Encode(data[..63])[5]).IsEqualTo((byte)0);
        await Assert.That(() => bounded.Encode(new byte[65])).Throws<ArgumentOutOfRangeException>();
        var tooLarge = Create(algorithm).Encode(new byte[4096]);
        await Assert.That(() => bounded.Decode(tooLarge)).Throws<InvalidDataException>();
        var plain = bounded.Encode("x"u8);
        await Assert.That(() => bounded.Decode([.. plain, 0])).Throws<InvalidDataException>();
    }

    [Test]
    public async Task InvalidConfigurationIsRejectedAtConstruction()
    {
        await Assert.That(new RespireValueCodecOptions().MaximumDecodedLength).IsEqualTo(8 * 1024 * 1024);
        for (byte algorithm = 0; algorithm < 16; algorithm++)
            await Assert.That(() => new CustomCodec(algorithm)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(new CustomCodec(16).AlgorithmId).IsEqualTo((byte)16);
        await Assert.That(new CustomCodec(255).AlgorithmId).IsEqualTo((byte)255);
        await Assert.That(() => new BrotliValueCodec(quality: -1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new BrotliValueCodec(quality: 12)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new DeflateValueCodec(level: (CompressionLevel)99)).Throws<ArgumentOutOfRangeException>();
        foreach (var options in new RespireValueCodecOptions[]
                 { new() { MinimumLength = -1 }, new() { MaximumDecodedLength = 0 }, new() { MaximumDecodedLength = int.MaxValue } })
            await Assert.That(() => new BrotliValueCodec(options)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new RespireValueCodecSerializer(null!, new BrotliValueCodec())).Throws<ArgumentNullException>();
        await Assert.That(() => new RespireValueCodecSerializer(RespireSerializer.Default, null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task ExistingCustomCodecsKeepDestinationFallbacks()
    {
        IRespireValueCodec codec = new ArrayOnlyCodec();
        var encoded = new ArrayBufferWriter<byte>();
        encoded.Write(new byte[] { 42 });
        codec.Encode("abc"u8, encoded);
        await Assert.That(encoded.WrittenMemory.ToArray()).IsEquivalentTo(new byte[] { 42, 97, 98, 99 });
        var decoded = new ArrayBufferWriter<byte>();
        decoded.Write(new byte[] { 43 });
        codec.Decode("abc"u8, decoded);
        await Assert.That(decoded.WrittenMemory.ToArray()).IsEquivalentTo(new byte[] { 43, 97, 98, 99 });
        await Assert.That(() => codec.Encode(Array.Empty<byte>(), null!)).Throws<ArgumentNullException>();
        await Assert.That(() => codec.Decode(Array.Empty<byte>(), null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task SerializerUsesBothCodecDestinationOverloads()
    {
        var codec = new DestinationCodec();
        var serializer = new RespireValueCodecSerializer(new BinarySerializer(), codec);
        var value = new BinaryValue([255, 0, 128]);
        var generic = new ArrayBufferWriter<byte>();
        serializer.Serialize(generic, value);
        await Assert.That(generic.WrittenMemory.ToArray()).IsEquivalentTo(value.Bytes);
        var runtime = new ArrayBufferWriter<byte>();
        serializer.Serialize(runtime, typeof(BinaryValue), value);
        await Assert.That(runtime.WrittenMemory.ToArray()).IsEquivalentTo(value.Bytes);
        await Assert.That(serializer.Deserialize<BinaryValue>(generic.WrittenSpan)!.Bytes).IsEquivalentTo(value.Bytes);
        await Assert.That(((BinaryValue)serializer.Deserialize(typeof(BinaryValue), runtime.WrittenSpan)!).Bytes).IsEquivalentTo(value.Bytes);
        await Assert.That(codec.Writes).IsEqualTo(2);
        await Assert.That(codec.Reads).IsEqualTo(2);
    }

    [Test]
    public async Task ScratchBuffersGrowPreserveBytesAndClearEveryReturnedRental()
    {
        var pool = new RecordingPool();
        using var buffer = new PooledByteBufferWriter(pool);
        buffer.GetSpan(1)[0] = 42;
        buffer.Advance(1);
        buffer.GetMemory(512).Span.Fill(99); // Include uncommitted bytes in clearing.
        await Assert.That(buffer.WrittenSpan.ToArray()).IsEquivalentTo(new byte[] { 42 });
        await Assert.That(pool.Returns).IsEqualTo(1);
        buffer.Dispose();
        buffer.Dispose();
        await Assert.That(pool.Returns).IsEqualTo(2);
        await Assert.That(pool.ClearRequests.All(clear => clear)).IsTrue();
        await Assert.That(() => buffer.GetMemory(1)).Throws<ObjectDisposedException>();
        await Assert.That(() => buffer.Advance(0)).Throws<ObjectDisposedException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DecodeFailureDisposesUncommittedScratchStorage(bool runtimeType)
    {
        var codec = new FailingDestinationCodec();
        var serializer = new RespireValueCodecSerializer(new BinarySerializer(), codec);
        await Assert.That(() =>
        {
            if (runtimeType) serializer.Deserialize(typeof(BinaryValue), Array.Empty<byte>());
            else serializer.Deserialize<BinaryValue>(Array.Empty<byte>());
        }).Throws<InvalidDataException>();
        await Assert.That(() => codec.Destination!.GetMemory(1)).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task BoundedCompressionBypassesOwnedArrayFallbackAndRejectsInvalidLengths()
    {
        var codec = new SpanCodec();
        var frame = codec.Encode(new byte[64]);
        await Assert.That(frame[5]).IsEqualTo((byte)16);
        await Assert.That(codec.Decode(frame)).IsEquivalentTo(new byte[64]);
        codec.InvalidLength = true;
        await Assert.That(() => codec.Encode(new byte[64])).Throws<InvalidDataException>();
        var destination = new ArrayBufferWriter<byte>();
        await Assert.That(() => codec.Encode(new byte[64], destination)).Throws<InvalidDataException>();
        await Assert.That(destination.WrittenCount).IsEqualTo(0);
    }

    [Test]
    [Arguments("brotli")]
    [Arguments("deflate")]
    [Arguments("lz4")]
    public async Task OneCodecSupportsConcurrentCalls(string algorithm)
    {
        var codec = Create(algorithm);
        var work = Enumerable.Range(0, 16).Select(index => Task.Run(() =>
        {
            var bytes = Encoding.UTF8.GetBytes(new string((char)('a' + index), 4096 + index));
            return codec.Decode(codec.Encode(bytes)).AsSpan().SequenceEqual(bytes);
        }));
        await Assert.That((await Task.WhenAll(work)).All(value => value)).IsTrue();
    }

    [Test]
    [Arguments("brotli")]
    [Arguments("deflate")]
    [Arguments("lz4")]
    public async Task DecoratorPreservesGenericAndRuntimeTypedCustomSerializerCalls(string algorithm)
    {
        var inner = new BinarySerializer();
        var codec = Create(algorithm);
        var serializer = new RespireValueCodecSerializer(inner, codec);
        var value = new BinaryValue([255, 0, 128]);
        var generic = new ArrayBufferWriter<byte>();
        serializer.Serialize(generic, value);
        await Assert.That(serializer.Deserialize<BinaryValue>(generic.WrittenSpan)!.Bytes).IsEquivalentTo(value.Bytes);
        var runtime = new ArrayBufferWriter<byte>();
        serializer.Serialize(runtime, typeof(BinaryValue), value);
        await Assert.That(((BinaryValue)serializer.Deserialize(typeof(BinaryValue), runtime.WrittenSpan)!).Bytes)
            .IsEquivalentTo(value.Bytes);
        await Assert.That(inner.GenericWrites).IsEqualTo(1);
        await Assert.That(inner.GenericReads).IsEqualTo(1);
        await Assert.That(inner.RuntimeWrites).IsEqualTo(1);
        await Assert.That(inner.RuntimeReads).IsEqualTo(1);
        await Assert.That(codec.Decode(generic.WrittenSpan)).IsEquivalentTo(value.Bytes);
    }

    internal static RespireValueCodec Create(string algorithm, RespireValueCodecOptions? options = null)
        => algorithm switch
        {
            "brotli" => new BrotliValueCodec(options),
            "deflate" => new DeflateValueCodec(options),
            "lz4" => new Lz4ValueCodec(options),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
        };

    [Test]
    public async Task SourceGeneratedSerializerStillUsesItsConfiguredTypeMetadata()
    {
        var serializer = new RespireValueCodecSerializer(
            SystemTextJsonSerializer.FromContext(TestJsonContext.Default), new BrotliValueCodec());
        var value = new SystemTextJsonSerializerTests.Payload("name", 42);
        var buffer = new ArrayBufferWriter<byte>();
        serializer.Serialize(buffer, value);
        await Assert.That(serializer.Deserialize<SystemTextJsonSerializerTests.Payload>(buffer.WrittenSpan)).IsEqualTo(value);
    }

    private static void RefreshChecksum(byte[] frame)
        => SHA256.HashData(frame.AsSpan(RespireValueCodec.HeaderLength)).AsSpan(0, 8).CopyTo(frame.AsSpan(10));

    private sealed record BinaryValue(byte[] Bytes);

    private sealed class CustomCodec(byte algorithm) : RespireValueCodec(algorithm)
    {
        protected override byte[] Compress(ReadOnlySpan<byte> payload) => payload.ToArray();
        protected override void Decompress(ReadOnlySpan<byte> payload, Span<byte> destination) => payload.CopyTo(destination);
    }

    private sealed class ArrayOnlyCodec : IRespireValueCodec
    {
        public byte[] Encode(ReadOnlySpan<byte> payload) => payload.ToArray();
        public byte[] Decode(ReadOnlySpan<byte> payload) => payload.ToArray();
    }

    private sealed class DestinationCodec : IRespireValueCodec
    {
        internal int Writes, Reads;
        public byte[] Encode(ReadOnlySpan<byte> payload) => throw new InvalidOperationException("The serializer must use the destination overload.");
        public byte[] Decode(ReadOnlySpan<byte> payload) => throw new InvalidOperationException("The serializer must use the destination overload.");
        public void Encode(ReadOnlySpan<byte> payload, IBufferWriter<byte> destination)
        {
            Writes++;
            destination.Write(payload);
        }
        public void Decode(ReadOnlySpan<byte> payload, IBufferWriter<byte> destination)
        {
            Reads++;
            destination.Write(payload);
        }
    }

    private sealed class FailingDestinationCodec : IRespireValueCodec
    {
        internal IBufferWriter<byte>? Destination;
        public byte[] Encode(ReadOnlySpan<byte> payload) => throw new NotSupportedException();
        public byte[] Decode(ReadOnlySpan<byte> payload) => throw new NotSupportedException();
        public void Decode(ReadOnlySpan<byte> payload, IBufferWriter<byte> destination)
        {
            Destination = destination;
            destination.GetSpan(32).Fill(99);
            throw new InvalidDataException("Deliberate decoder failure after an uncommitted write.");
        }
    }

    private sealed class SpanCodec() : RespireValueCodec(16, new() { MinimumLength = 0 })
    {
        internal bool InvalidLength;
        protected override byte[] Compress(ReadOnlySpan<byte> payload) => throw new InvalidOperationException("Owned compression must be bypassed.");
        protected override bool TryCompress(ReadOnlySpan<byte> payload, Span<byte> destination, out int bytesWritten)
        {
            destination[0] = 0;
            bytesWritten = InvalidLength ? destination.Length + 1 : 1;
            return true;
        }
        protected override void Decompress(ReadOnlySpan<byte> payload, Span<byte> destination) => destination.Clear();
    }

    private sealed class RecordingPool : ArrayPool<byte>
    {
        internal int Returns;
        internal readonly List<bool> ClearRequests = [];
        public override byte[] Rent(int minimumLength) => new byte[minimumLength];
        public override void Return(byte[] array, bool clearArray = false)
        {
            Returns++;
            ClearRequests.Add(clearArray);
            if (clearArray) Array.Clear(array);
        }
    }
    private sealed class BinarySerializer : IRespireSerializer
    {
        internal int GenericWrites, GenericReads, RuntimeWrites, RuntimeReads;
        public void Serialize<T>(IBufferWriter<byte> destination, T value)
        { GenericWrites++; destination.Write(((BinaryValue)(object)value!).Bytes); }
        public T? Deserialize<T>(ReadOnlySpan<byte> payload)
        { GenericReads++; return (T)(object)new BinaryValue(payload.ToArray()); }
        public void Serialize(IBufferWriter<byte> destination, Type type, object? value)
        { RuntimeWrites++; destination.Write(((BinaryValue)value!).Bytes); }
        public object? Deserialize(Type type, ReadOnlySpan<byte> payload)
        { RuntimeReads++; return new BinaryValue(payload.ToArray()); }
    }
}
