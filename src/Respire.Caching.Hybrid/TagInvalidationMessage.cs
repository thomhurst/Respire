using System.Buffers.Binary;
using System.Text;

namespace Respire.Caching.Hybrid;

// Versioned, bounded UTF-8 frame. No HybridCache payload or private tag-marker format is parsed.
internal sealed class TagInvalidationMessage(string cacheNamespace, int maximumBytes)
{
    private const int HeaderBytes = 32;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly byte[] _namespace = Utf8.GetBytes(cacheNamespace);

    internal byte[] Encode(Guid sender, string tag, DateTimeOffset timestamp)
    {
        var tagBytes = ValidateTag(tag);
        var message = new byte[HeaderBytes + _namespace.Length + tagBytes];
        "RHT1"u8.CopyTo(message);
        sender.TryWriteBytes(message.AsSpan(4, 16));
        BinaryPrimitives.WriteInt64LittleEndian(message.AsSpan(20, 8), timestamp.UtcTicks);
        BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(28, 4), _namespace.Length);
        _namespace.CopyTo(message, HeaderBytes);
        Utf8.GetBytes(tag.AsSpan(), message.AsSpan(HeaderBytes + _namespace.Length));
        return message;
    }

    internal int ValidateTag(string tag)
    {
        var length = Utf8.GetByteCount(tag);
        if (length > maximumBytes - HeaderBytes - _namespace.Length)
            throw new ArgumentException("The tag exceeds MaxTagInvalidationMessageBytes.", nameof(tag));
        return length;
    }

    internal bool TryDecode(ReadOnlySpan<byte> message, out Guid sender, out string tag, out DateTimeOffset timestamp)
    {
        sender = default;
        tag = "";
        timestamp = default;
        if (message.Length <= HeaderBytes + _namespace.Length || message.Length > maximumBytes
            || !message[..4].SequenceEqual("RHT1"u8)
            || BinaryPrimitives.ReadInt32LittleEndian(message.Slice(28, 4)) != _namespace.Length
            || !message.Slice(HeaderBytes, _namespace.Length).SequenceEqual(_namespace)) return false;
        var ticks = BinaryPrimitives.ReadInt64LittleEndian(message.Slice(20, 8));
        if (ticks < 0 || ticks > DateTimeOffset.MaxValue.UtcTicks) return false;
        try { tag = Utf8.GetString(message[(HeaderBytes + _namespace.Length)..]); }
        catch (DecoderFallbackException) { return false; }
        if (string.IsNullOrWhiteSpace(tag)) return false;
        sender = new Guid(message.Slice(4, 16));
        timestamp = new DateTimeOffset(ticks, TimeSpan.Zero);
        return true;
    }

    internal static void ValidateNamespace(string cacheNamespace, int maximumBytes)
    {
        if (Utf8.GetByteCount(cacheNamespace) >= maximumBytes - HeaderBytes)
            throw new ArgumentException("The namespace leaves no room for a tag message.", nameof(cacheNamespace));
    }
}
