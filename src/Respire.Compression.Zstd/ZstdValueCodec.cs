using System.Buffers;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace Respire.Compression;

/// <summary>Zstandard compression using version 1 Respire frames and reserved algorithm ID 4.</summary>
/// <remarks>Requires the optional Respire.Compression.Zstd package. Each compressed payload is one
/// ordinary Zstandard frame without an external dictionary. Per-call contexts are disposed before return,
/// so the codec supports concurrent calls and does not require disposal.</remarks>
public sealed class ZstdValueCodec : RespireValueCodec
{
    private readonly int _level;
    private static ReadOnlySpan<byte> FrameMagic => [0x28, 0xb5, 0x2f, 0xfd];

    /// <summary>Creates a codec with compression level 3 by default. Supported levels are -131072 through 22;
    /// zero selects the dependency's default level of 3. Negative levels favor speed over compression ratio.</summary>
    public ZstdValueCodec(RespireValueCodecOptions? options = null, int level = 3)
        : base(4, options, allowReservedAlgorithm: true)
    {
        if (level < Compressor.MinCompressionLevel || level > Compressor.MaxCompressionLevel)
            throw new ArgumentOutOfRangeException(nameof(level), "Zstandard compression level is outside the supported range.");
        _level = level;
    }

    /// <inheritdoc/>
    protected override byte[] Compress(ReadOnlySpan<byte> payload)
    {
        var capacity = Compressor.GetCompressBound(payload.Length);
        if (capacity <= 0) throw new InvalidDataException("Zstandard input exceeds its supported frame size.");
        var buffer = ArrayPool<byte>.Shared.Rent(capacity);
        try
        {
            if (!TryCompress(payload, buffer.AsSpan(0, capacity), out var written))
                throw new InvalidDataException("Zstandard could not encode the value within its output bound.");
            return buffer.AsSpan(0, written).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    /// <inheritdoc/>
    protected override bool TryCompress(ReadOnlySpan<byte> payload, Span<byte> destination, out int bytesWritten)
    {
        using var compressor = new Compressor(_level);
        return compressor.TryWrap(payload, destination, out bytesWritten);
    }

    /// <inheritdoc/>
    protected override void Decompress(ReadOnlySpan<byte> payload, Span<byte> destination)
    {
        // The base validates the outer decoded length against MaximumDecodedLength before
        // allocating this exact-sized destination. Unwrap remains bounded by that span.
        if (!IsSingleFrame(payload))
            throw new InvalidDataException("Zstandard payload must contain exactly one ordinary frame.");
        try
        {
            using var decompressor = new Decompressor();
            if (decompressor.Unwrap(payload, destination) != destination.Length)
                throw new InvalidDataException("Zstandard payload has an unexpected decoded length.");
        }
        catch (ZstdException error)
        {
            throw new InvalidDataException("Zstandard payload is malformed or exceeds its declared length.", error);
        }
    }

    private static unsafe bool IsSingleFrame(ReadOnlySpan<byte> payload)
    {
        // The dependency's decoder also accepts concatenated and skippable frames. Our wire ID
        // deliberately denotes one ordinary frame, so reject those before bounded decoding.
        if (!payload.StartsWith(FrameMagic)) return false;
        fixed (byte* source = payload)
            return Methods.ZSTD_findFrameCompressedSize(source, (nuint)payload.Length) == (nuint)payload.Length;
    }
}
