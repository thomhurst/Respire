using System.IO.Compression;

namespace Respire.Compression;

/// <summary>Raw DEFLATE value compression using versioned Respire frames and algorithm ID 2.</summary>
public sealed class DeflateValueCodec : RespireValueCodec
{
    private readonly CompressionLevel _level;

    /// <summary>Creates a thread-safe codec using the specified compression level. The default is Fastest.</summary>
    public DeflateValueCodec(RespireValueCodecOptions? options = null, CompressionLevel level = CompressionLevel.Fastest)
        : base(2, options, allowReservedAlgorithm: true)
    {
        if (!Enum.IsDefined(level)) throw new ArgumentOutOfRangeException(nameof(level));
        _level = level;
    }

    /// <inheritdoc/>
    protected override byte[] Compress(ReadOnlySpan<byte> payload)
    {
        using var output = new MemoryStream();
        using (var compressor = new DeflateStream(output, _level, leaveOpen: true)) compressor.Write(payload);
        return output.ToArray();
    }

    /// <inheritdoc/>
    protected override void Decompress(ReadOnlySpan<byte> payload, Span<byte> destination)
    {
        using var input = new MemoryStream(payload.ToArray(), writable: false);
        using var decoder = new DeflateStream(input, CompressionMode.Decompress);
        try
        {
            decoder.ReadExactly(destination);
            if (decoder.ReadByte() != -1)
                throw new InvalidDataException("DEFLATE output exceeds its declared decoded length.");
        }
        catch (EndOfStreamException error)
        {
            throw new InvalidDataException("DEFLATE output is shorter than its declared decoded length.", error);
        }
    }
}
