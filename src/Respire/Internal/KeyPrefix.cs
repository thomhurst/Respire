using System.Text;

namespace Respire.Internal;

/// <summary>Immutable prefix encoding shared by every key resolved through a client view.</summary>
internal sealed class KeyPrefix
{
    private const int HashTagsDisabled = -2;
    internal string Text { get; }
    internal byte[] Bytes { get; }
    private readonly int _tagStart;
    private readonly int _binaryTagStart;
    private readonly int _fixedSlot;

    internal KeyPrefix(string text)
    {
        Text = text;
        Bytes = Encoding.UTF8.GetBytes(text);
        _tagStart = text.IndexOf('{');
        _binaryTagStart = Bytes.AsSpan().IndexOf((byte)'{');
        var close = _tagStart < 0 ? -1 : text.AsSpan(_tagStart + 1).IndexOf('}');
        _fixedSlot = close > 0 ? ClusterHash.GetSlot(text) : -1;
        // An empty first tag disables tag selection, including later tags in the suffix.
        if (close == 0) _tagStart = _binaryTagStart = HashTagsDisabled;
    }

    /// <summary>Materializes the exact wire bytes for an explicitly owned representation.</summary>
    internal byte[] Materialize(string? key, ReadOnlyMemory<byte> bytes)
    {
        var payload = new byte[GetWireLength(key, bytes)];
        WritePayload(key, bytes, payload);
        return payload;
    }

    /// <summary>Preserves text identity and uses the shared decoder for binary keys.</summary>
    internal string GetString(string? key, ReadOnlyMemory<byte> bytes)
        => key is not null ? Text + key : Utf8String.GetString(Materialize(null, bytes).AsMemory());

    /// <summary>Hashes only a nonempty first tag, including a tag split across the prefix boundary.</summary>
    internal bool TryGetTaggedSlot(string? key, ReadOnlyMemory<byte> bytes, out int slot)
    {
        slot = _fixedSlot;
        if (slot >= 0) return true;
        if (_tagStart == HashTagsDisabled) return false;
        if (key is not null)
        {
            if (!TrySelectTag<char>(Text.AsSpan(), key.AsSpan(), _tagStart, '{', '}', out var prefixTag, out var suffixTag))
                return false;
            slot = ClusterHash.GetTagSlot(prefixTag, suffixTag);
        }
        else
        {
            if (!TrySelectTag<byte>(Bytes, bytes.Span, _binaryTagStart, (byte)'{', (byte)'}', out var prefixTag, out var suffixTag))
                return false;
            slot = ClusterHash.GetTagSlot(prefixTag, suffixTag);
        }
        return true;
    }

    private static bool TrySelectTag<T>(ReadOnlySpan<T> prefix, ReadOnlySpan<T> suffix, int tagStart,
        T openBrace, T closeBrace, out ReadOnlySpan<T> prefixTag, out ReadOnlySpan<T> suffixTag)
        where T : IEquatable<T>
    {
        prefixTag = default;
        suffixTag = default;
        if (tagStart >= 0)
        {
            var close = suffix.IndexOf(closeBrace);
            prefixTag = prefix[(tagStart + 1)..];
            if (close < 0 || prefixTag.IsEmpty && close == 0) return false;
            suffixTag = suffix[..close];
            return true;
        }

        var open = suffix.IndexOf(openBrace);
        if (open < 0) return false;
        var tag = suffix[(open + 1)..];
        var end = tag.IndexOf(closeBrace);
        if (end <= 0) return false;
        suffixTag = tag[..end];
        return true;
    }

    private bool JoinsSurrogatePair(string? key)
        => Text.Length != 0 && char.IsHighSurrogate(Text[^1])
            && key is { Length: > 0 } && char.IsLowSurrogate(key[0]);

    internal int GetWireLength(string? key, ReadOnlyMemory<byte> bytes)
    {
        if (key is null) return checked(Bytes.Length + bytes.Length);
        if (JoinsSurrogatePair(key))
            return checked(Bytes.Length + 1 + Encoding.UTF8.GetByteCount(key.AsSpan(1)));
        return checked(Bytes.Length + Encoding.UTF8.GetByteCount(key));
    }

    internal int WritePayload(string? key, ReadOnlyMemory<byte> bytes, Span<byte> destination)
    {
        if (JoinsSurrogatePair(key))
        {
            // String concatenation pairs these UTF-16 code units before UTF-8 encoding.
            // Replace the cached trailing replacement character with the complete pair.
            var prefixLength = Bytes.Length - 3;
            Bytes.AsSpan(0, prefixLength).CopyTo(destination);
            Span<char> pair = stackalloc char[2] { Text[^1], key![0] };
            var written = Encoding.UTF8.GetBytes(pair, destination[prefixLength..]);
            return prefixLength + written + Encoding.UTF8.GetBytes(key.AsSpan(1), destination[(prefixLength + written)..]);
        }

        Bytes.CopyTo(destination);
        if (key is not null)
            return Bytes.Length + Encoding.UTF8.GetBytes(key, destination[Bytes.Length..]);
        bytes.Span.CopyTo(destination[Bytes.Length..]);
        return Bytes.Length + bytes.Length;
    }
}
