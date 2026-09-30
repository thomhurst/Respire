using System.Buffers;
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

    /// <summary>Constructs a custom codec with a stable algorithm ID from 16 through 255 and a snapshot of its settings.</summary>
    protected RespireValueCodec(byte algorithmId, RespireValueCodecOptions? options = null)
        : this(algorithmId, options, allowReservedAlgorithm: false)
    {
    }

    // Reserved IDs are available only to built-in implementations and explicitly trusted
    // optional codec assemblies. External subclasses use the protected custom-ID constructor.
    internal RespireValueCodec(byte algorithmId, RespireValueCodecOptions? options, bool allowReservedAlgorithm)
    {
        if (algorithmId == 0 || (!allowReservedAlgorithm && algorithmId < 16))
            throw new ArgumentOutOfRangeException(nameof(algorithmId), "Custom codec algorithm IDs must be between 16 and 255.");
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
        var originalLength = payload.Length;
        var encoded = CompressIfSmaller(payload, out var algorithm);
        var frame = new byte[HeaderLength + encoded.Length];
        WriteFrame(encoded, originalLength, algorithm, frame);
        return frame;
    }

    /// <summary>Appends a frame directly to the destination without allocating an intermediate frame array.</summary>
    public void Encode(ReadOnlySpan<byte> payload, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var originalLength = payload.Length;
        var encoded = CompressIfSmaller(payload, out var algorithm);
        var length = HeaderLength + encoded.Length;
        WriteFrame(encoded, originalLength, algorithm, destination.GetSpan(length)[..length]);
        destination.Advance(length);
    }

    private ReadOnlySpan<byte> CompressIfSmaller(ReadOnlySpan<byte> payload, out byte algorithm)
    {
        if (payload.Length > MaximumDecodedLength)
            throw new ArgumentOutOfRangeException(nameof(payload), "Value exceeds the codec's maximum decoded length.");
        algorithm = 0;
        if (payload.Length >= MinimumLength && !payload.IsEmpty)
        {
            var compressed = Compress(payload);
            if (compressed.Length < payload.Length)
            {
                payload = compressed;
                algorithm = AlgorithmId;
            }
        }

        return payload;
    }

    private static void WriteFrame(ReadOnlySpan<byte> payload, int originalLength, byte algorithm, Span<byte> frame)
    {
        Magic.CopyTo(frame);
        frame[4] = 1;
        frame[5] = algorithm;
        BinaryPrimitives.WriteInt32LittleEndian(frame[6..], originalLength);
        WriteChecksum(payload, frame.Slice(10, 8));
        payload.CopyTo(frame[HeaderLength..]);
    }

    /// <inheritdoc/>
    public byte[] Decode(ReadOnlySpan<byte> payload)
    {
        var encoded = ValidateFrame(payload, out var algorithm, out var length);
        if (algorithm == 0) return encoded.ToArray();
        var decoded = new byte[length];
        Decompress(encoded, decoded);
        return decoded;
    }

    /// <summary>Validates and appends a decoded value directly to the destination.</summary>
    /// <remarks>Advances only after successful decoding. On failure, uncommitted destination memory may have been written.</remarks>
    public void Decode(ReadOnlySpan<byte> payload, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var encoded = ValidateFrame(payload, out var algorithm, out var length);
        if (length == 0) return;
        var decoded = destination.GetSpan(length)[..length];
        if (algorithm == 0) encoded.CopyTo(decoded);
        else Decompress(encoded, decoded);
        destination.Advance(length);
    }

    private ReadOnlySpan<byte> ValidateFrame(ReadOnlySpan<byte> payload, out byte algorithm, out int length)
    {
        if (payload.Length < HeaderLength || !payload[..4].SequenceEqual(Magic))
            throw new InvalidDataException("Value is not a complete Respire codec frame.");
        if (payload[4] != 1) throw new InvalidDataException("Unsupported Respire codec frame version.");
        algorithm = payload[5];
        if (algorithm != 0 && algorithm != AlgorithmId)
            throw new InvalidDataException($"Codec algorithm {algorithm} is not supported by this decoder.");
        var declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(payload[6..]);
        if (declaredLength > MaximumDecodedLength)
            throw new InvalidDataException("Decoded value exceeds the codec's maximum decoded length.");
        length = (int)declaredLength;
        var encoded = payload[HeaderLength..];
        if (algorithm == 0 ? encoded.Length != length : encoded.Length >= length)
            throw new InvalidDataException("Codec payload length does not match its frame.");
        Span<byte> checksum = stackalloc byte[8];
        WriteChecksum(encoded, checksum);
        if (!checksum.SequenceEqual(payload.Slice(10, 8)))
            throw new InvalidDataException("Codec payload checksum does not match its frame.");
        return encoded;
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
