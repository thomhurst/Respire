using System.Text;

namespace Respire.Internal;

/// <summary>
/// UTF-8 decoding that widens eligible ASCII payloads directly into the final string and preserves
/// <see cref="Encoding.UTF8"/>'s replacement fallback for other payloads.
/// </summary>
internal static class Utf8String
{
    internal static string GetString(ReadOnlyMemory<byte> utf8)
    {
        if (utf8.IsEmpty)
        {
            return string.Empty;
        }

        if (Ascii.IsValid(utf8.Span))
        {
            // Retain the memory owner as state, including custom MemoryManager storage.
            return string.Create(utf8.Length, utf8, static (chars, state) =>
                Ascii.ToUtf16(state.Span, chars, out _));
        }

        return Encoding.UTF8.GetString(utf8.Span);
    }

    internal static string GetString(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty)
        {
            return string.Empty;
        }

        // Validate before allocating: a failed ASCII conversion into a byte-length string
        // would discard that string and allocate a second one for Unicode or invalid UTF-8.
        if (Ascii.IsValid(utf8))
        {
            return string.Create(utf8.Length, utf8, static (chars, state) =>
                Ascii.ToUtf16(state, chars, out _));
        }

        return Encoding.UTF8.GetString(utf8);
    }
}
