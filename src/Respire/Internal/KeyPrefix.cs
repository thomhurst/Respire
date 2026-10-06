using System.Text;

namespace Respire.Internal;

/// <summary>Immutable prefix encoding shared by every key resolved through a client view.</summary>
internal sealed class KeyPrefix(string text)
{
    internal string Text { get; } = text;
    internal byte[] Bytes { get; } = Encoding.UTF8.GetBytes(text);

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
