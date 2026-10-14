using System.Buffers;
using System.Text;
using System.Text.Unicode;

namespace Respire.Internal;

/// <summary>Formats binary text without materializing a joined prefix or a decoded string.</summary>
internal static class ArgumentFormatting
{
    internal static void ValidateFormat(ReadOnlySpan<char> format)
    {
        if (!format.IsEmpty && !format.SequenceEqual("G") && !format.SequenceEqual("g"))
            throw new FormatException("Only the general (G) format is supported.");
    }

    internal static bool TryCopy(ReadOnlySpan<char> text, Span<char> destination, out int written)
    {
        written = 0;
        if (!text.TryCopyTo(destination)) return false;
        written = text.Length;
        return true;
    }

    internal static bool TryEncode(ReadOnlySpan<char> text, Span<byte> destination, out int written)
    {
        if (Utf8.FromUtf16(text, destination, out _, out written) == OperationStatus.Done) return true;
        written = 0;
        return false;
    }

    internal static bool TryEncode(ReadOnlySpan<char> first, ReadOnlySpan<char> second,
        Span<byte> destination, out int written)
    {
        written = 0;
        var joinsPair = !first.IsEmpty && !second.IsEmpty
            && char.IsHighSurrogate(first[^1]) && char.IsLowSurrogate(second[0]);
        if (!TryEncode(joinsPair ? first[..^1] : first, destination, out var prefixWritten)) return false;
        if (joinsPair)
        {
            var rune = new Rune(first[^1], second[0]);
            if (!rune.TryEncodeToUtf8(destination[prefixWritten..], out var pairWritten)) return false;
            prefixWritten += pairWritten;
            second = second[1..];
        }
        if (!TryEncode(second, destination[prefixWritten..], out var suffixWritten)) return false;
        written = prefixWritten + suffixWritten;
        return true;
    }

    internal static bool TryDecode(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second,
        Span<char> destination, out int written)
    {
        written = 0;
        if (second.IsEmpty)
        {
            if (Utf8.ToUtf16(first, destination, out _, out written) == OperationStatus.Done) return true;
            written = 0;
            return false;
        }
        var offset = 0;
        while (!first.IsEmpty || !second.IsEmpty)
        {
            var rune = ReadRune(ref first, ref second);
            if (!rune.TryEncodeToUtf16(destination[offset..], out var count)) return false;
            offset += count;
        }
        written = offset;
        return true;
    }

    internal static bool TryNormalize(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second,
        Span<byte> destination, out int written)
    {
        written = 0;
        if (second.IsEmpty && Utf8.IsValid(first))
        {
            if (!first.TryCopyTo(destination)) return false;
            written = first.Length;
            return true;
        }
        var offset = 0;
        while (!first.IsEmpty || !second.IsEmpty)
        {
            var rune = ReadRune(ref first, ref second);
            if (!rune.TryEncodeToUtf8(destination[offset..], out var count)) return false;
            offset += count;
        }
        written = offset;
        return true;
    }

    internal static bool TryNormalize(ReadOnlySpan<char> text, Span<char> destination, out int written)
    {
        written = 0;
        var offset = 0;
        while (!text.IsEmpty)
        {
            Rune.DecodeFromUtf16(text, out var rune, out var consumed);
            if (!rune.TryEncodeToUtf16(destination[offset..], out var count)) return false;
            text = text[consumed..];
            offset += count;
        }
        written = offset;
        return true;
    }

    private static Rune ReadRune(ref ReadOnlySpan<byte> first, ref ReadOnlySpan<byte> second)
    {
        if (first.IsEmpty)
        {
            first = second;
            second = default;
        }

        var status = Rune.DecodeFromUtf8(first, out var rune, out var consumed);
        if (status == OperationStatus.NeedMoreData && !second.IsEmpty)
        {
            // stackalloc-audit[656d9422aa6a2134]: init: both copies fill boundary[..length];
            // read: DecodeFromUtf8 reads only that slice; failure: a copy failure exits before decoding.
            Span<byte> boundary = stackalloc byte[4];
            first.CopyTo(boundary);
            var length = first.Length + Math.Min(4 - first.Length, second.Length);
            second[..(length - first.Length)].CopyTo(boundary[first.Length..]);
            Rune.DecodeFromUtf8(boundary[..length], out rune, out consumed);
        }

        var fromFirst = Math.Min(consumed, first.Length);
        first = first[fromFirst..];
        second = second[(consumed - fromFirst)..];
        return rune;
    }
}
