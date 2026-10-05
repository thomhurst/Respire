using System.Buffers;
using System.IO.Compression;

namespace Respire.Compression;

/// <summary>Brotli value compression using versioned Respire frames and algorithm ID 1.</summary>
public sealed class BrotliValueCodec : RespireValueCodec
{
    private readonly int _quality;

    /// <summary>Creates a thread-safe codec. Quality ranges from 0 through 11; the default is 4.</summary>
    public BrotliValueCodec(RespireValueCodecOptions? options = null, int quality = 4) : base(1, options, allowReservedAlgorithm: true)
    {
        if (quality is < 0 or > 11) throw new ArgumentOutOfRangeException(nameof(quality));
        _quality = quality;
    }

    /// <inheritdoc/>
    protected override byte[] Compress(ReadOnlySpan<byte> payload)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BrotliEncoder.GetMaxCompressedLength(payload.Length));
        try
        {
            // The window is encoded in the Brotli stream; decoding does not depend on this encoder setting.
            if (!BrotliEncoder.TryCompress(payload, buffer, out var written, _quality, window: 22))
                throw new InvalidDataException("Brotli could not encode the value within its output bound.");
            return buffer.AsSpan(0, written).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    /// <inheritdoc/>
    protected override bool TryCompress(ReadOnlySpan<byte> payload, Span<byte> destination, out int bytesWritten)
        => BrotliEncoder.TryCompress(payload, destination, out bytesWritten, _quality, window: 22);

    /// <inheritdoc/>
    protected override void Decompress(ReadOnlySpan<byte> payload, Span<byte> destination)
    {
        var decoder = new BrotliDecoder();
        try
        {
            var status = decoder.Decompress(payload, destination, out var consumed, out var written);
            if (status != OperationStatus.Done || consumed != payload.Length || written != destination.Length)
                throw new InvalidDataException("Brotli payload is malformed or has an unexpected decoded length.");
        }
        finally
        {
            decoder.Dispose();
        }
    }
}
