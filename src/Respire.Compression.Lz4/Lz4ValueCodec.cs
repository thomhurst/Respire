using System.Buffers;
using K4os.Compression.LZ4;

namespace Respire.Compression;

/// <summary>LZ4 block compression using version 1 Respire frames and reserved algorithm ID 3.</summary>
/// <remarks>Requires the optional Respire.Compression.Lz4 package. Payloads use raw LZ4 blocks,
/// not LZ4 frame streams or K4os pickles. Decoding never requires the encoder's compression level.</remarks>
public sealed class Lz4ValueCodec : RespireValueCodec
{
    private readonly LZ4Level _level;

    /// <summary>Creates a thread-safe codec. Level 0 is fast (the default); levels 3 through 12 use high compression.</summary>
    public Lz4ValueCodec(RespireValueCodecOptions? options = null, int level = 0)
        : base(3, options, allowReservedAlgorithm: true)
    {
        if (level != 0 && level is not (>= 3 and <= 12))
            throw new ArgumentOutOfRangeException(nameof(level), "LZ4 level must be 0 or between 3 and 12.");
        _level = (LZ4Level)level;
    }

    /// <inheritdoc/>
    protected override byte[] Compress(ReadOnlySpan<byte> payload)
    {
        var capacity = LZ4Codec.MaximumOutputSize(payload.Length);
        if (capacity <= 0) throw new InvalidDataException("LZ4 input exceeds its supported block size.");
        var buffer = ArrayPool<byte>.Shared.Rent(capacity);
        try
        {
            if (!TryCompress(payload, buffer.AsSpan(0, capacity), out var written))
                throw new InvalidDataException("LZ4 could not encode the value within its output bound.");
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
        var written = LZ4Codec.Encode(payload, destination, _level);
        bytesWritten = Math.Max(0, written);
        return written > 0;
    }

    /// <inheritdoc/>
    protected override void Decompress(ReadOnlySpan<byte> payload, Span<byte> destination)
    {
        if (LZ4Codec.Decode(payload, destination) != destination.Length)
            throw new InvalidDataException("LZ4 payload is malformed or has an unexpected decoded length.");
    }
}
