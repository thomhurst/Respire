using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Respire.Compression;

/// <summary>Shared versioned framing, size bounds, and corruption checks for value compression codecs.</summary>
/// <remarks>Frames use RVC-NUL, version 1, an algorithm byte, a little-endian original length,
/// and the first eight SHA-256 bytes of the encoded payload. Algorithm 0 is uncompressed;
/// 1/2 are Brotli/Deflate, 3/4 are reserved for LZ4/Zstandard, and 16-255 are available to custom codecs.
/// SHA-256 provides a platform implementation without adding a hashing dependency; truncating it bounds
/// frame overhead. The checksum detects accidental corruption, not authentication. Unframed input is rejected.</remarks>
public abstract class RespireValueCodec : IRespireValueCodec
{
    /// <summary>Number of bytes preceding the encoded payload in a version 1 frame.</summary>
    public const int HeaderLength = 18;
    private static ReadOnlySpan<byte> Magic => "RVC\0"u8;

    /// <summary>Constructs a codec with a nonzero stable algorithm ID and a snapshot of its settings.</summary>
    protected RespireValueCodec(byte algorithmId, RespireValueCodecOptions? options = null)
    {
        if (algorithmId == 0) throw new ArgumentOutOfRangeException(nameof(algorithmId));
        options ??= new();
        ArgumentOutOfRangeException.ThrowIfNegative(options.MinimumLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumDecodedLength);
        if (options.MaximumDecodedLength > Array.MaxLength - HeaderLength)
            throw new ArgumentOutOfRangeException(nameof(options), "Maximum decoded length leaves no room for framing.");
        AlgorithmId = algorithmId;
        MinimumLength = options.MinimumLength;
        MaximumDecodedLength = options.MaximumDecodedLength;
    }

    /// <summary>The stable identifier of this codec's compressed payload format.</summary>
    public byte AlgorithmId { get; }
    /// <summary>The minimum original length before attempting compression.</summary>
    public int MinimumLength { get; }
    /// <summary>The maximum allowed original or decoded length.</summary>
    public int MaximumDecodedLength { get; }

    /// <inheritdoc/>
    public byte[] Encode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaximumDecodedLength)
            throw new ArgumentOutOfRangeException(nameof(payload), "Value exceeds the codec's maximum decoded length.");
        var originalLength = payload.Length;
        byte algorithm = 0;
        if (payload.Length >= MinimumLength && !payload.IsEmpty)
        {
            var compressed = Compress(payload);
            if (compressed.Length < payload.Length)
            {
                payload = compressed;
                algorithm = AlgorithmId;
            }
        }

        var frame = new byte[HeaderLength + payload.Length];
        Magic.CopyTo(frame);
        frame[4] = 1;
        frame[5] = algorithm;
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(6), originalLength);
        WriteChecksum(payload, frame.AsSpan(10, 8));
        payload.CopyTo(frame.AsSpan(HeaderLength));
        return frame;
    }

    /// <inheritdoc/>
    public byte[] Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < HeaderLength || !payload[..4].SequenceEqual(Magic))
            throw new InvalidDataException("Value is not a complete Respire codec frame.");
        if (payload[4] != 1) throw new InvalidDataException("Unsupported Respire codec frame version.");
        var algorithm = payload[5];
        if (algorithm != 0 && algorithm != AlgorithmId)
            throw new InvalidDataException($"Codec algorithm {algorithm} is not supported by this decoder.");
        var length = BinaryPrimitives.ReadUInt32LittleEndian(payload[6..]);
        if (length > MaximumDecodedLength)
            throw new InvalidDataException("Decoded value exceeds the codec's maximum decoded length.");
        var encoded = payload[HeaderLength..];
        if (algorithm == 0 ? encoded.Length != length : encoded.Length >= length)
            throw new InvalidDataException("Codec payload length does not match its frame.");
        Span<byte> checksum = stackalloc byte[8];
        WriteChecksum(encoded, checksum);
        if (!checksum.SequenceEqual(payload.Slice(10, 8)))
            throw new InvalidDataException("Codec payload checksum does not match its frame.");
        if (algorithm == 0) return encoded.ToArray();

        var decoded = new byte[(int)length];
        Decompress(encoded, decoded);
        return decoded;
    }

    /// <summary>Returns the owned compressed payload, without a frame. Called only above the configured threshold.</summary>
    protected abstract byte[] Compress(ReadOnlySpan<byte> payload);

    /// <summary>Fills the entire bounded destination or throws InvalidDataException for malformed data or a length mismatch.</summary>
    protected abstract void Decompress(ReadOnlySpan<byte> payload, Span<byte> destination);

    private static void WriteChecksum(ReadOnlySpan<byte> payload, Span<byte> destination)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(payload, hash);
        hash[..8].CopyTo(destination);
    }
}
