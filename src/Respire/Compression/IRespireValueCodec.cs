using System.Buffers;

namespace Respire.Compression;

/// <summary>Encodes and decodes value payloads independently of their serializer and Redis command.</summary>
/// <remarks>Implementations must be thread-safe and must not retain input spans. Array-returning members
/// return caller-owned storage; destination overloads append to the supplied writer. Raw commands do not apply a codec automatically.</remarks>
public interface IRespireValueCodec
{
    /// <summary>Returns an owned encoded value, including any framing required by this codec.</summary>
    byte[] Encode(ReadOnlySpan<byte> payload);

    /// <summary>Appends the encoded value to a destination. The default implementation copies the owned Encode result.</summary>
    void Encode(ReadOnlySpan<byte> payload, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Write(Encode(payload));
    }

    /// <summary>Returns an owned decoded value. Implementations must bound decompressed output.</summary>
    byte[] Decode(ReadOnlySpan<byte> payload);

    /// <summary>Appends the decoded value to a destination. The default implementation copies the owned Decode result.</summary>
    void Decode(ReadOnlySpan<byte> payload, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Write(Decode(payload));
    }
}

/// <summary>Immutable settings captured by a framed value codec when it is constructed.</summary>
public sealed record RespireValueCodecOptions
{
    /// <summary>Minimum input bytes before trying compression. Smaller values still receive a frame. Default: 1 KiB.</summary>
    public int MinimumLength { get; init; } = 1024;

    /// <summary>Maximum original or decoded payload bytes. Checked before output allocation. Default: 64 MiB.</summary>
    /// <remarks>A checksum-valid frame can allocate up to this limit before decompression rejects it.
    /// Choose a lower limit for smaller application values. This does not cap total working memory or object size.</remarks>
    public int MaximumDecodedLength { get; init; } = 64 * 1024 * 1024;
}
