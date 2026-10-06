using System.Text;

namespace Respire.Internal;

/// <summary>
/// UTF-8 decoding that widens ASCII directly into the final string and preserves
/// <see cref="Encoding.UTF8"/>'s replacement fallback for other payloads.
/// </summary>
internal static class Utf8String
{
    internal static string GetString(ReadOnlyMemory<byte> utf8)
    {
        if (utf8.IsEmpty) return string.Empty;
        if (!Ascii.IsValid(utf8.Span)) return Encoding.UTF8.GetString(utf8.Span);
        // Retain the memory owner as state, including custom MemoryManager storage.
        return string.Create(utf8.Length, utf8, static (chars, state) =>
            Ascii.ToUtf16(state.Span, chars, out _));
    }

    internal static unsafe string GetString(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty)
        {
            return string.Empty;
        }

#if !NET9_0_OR_GREATER
        // Keep the existing net8 limit: scanning a long ASCII prefix before Unicode
        // fallback adds work that the runtime decoder already performs.
        if (utf8.Length > 256) return Encoding.UTF8.GetString(utf8);
#endif

        // Validate before allocating: a failed ASCII conversion into a byte-length string
        // would discard that string and allocate a second one for Unicode or invalid UTF-8.
        if (!Ascii.IsValid(utf8)) return Encoding.UTF8.GetString(utf8);

#if NET9_0_OR_GREATER
        return string.Create(utf8.Length, utf8, static (chars, state) =>
            Ascii.ToUtf16(state, chars, out _));
#else
        // string.Create invokes its action synchronously. Keep the source pinned until it
        // finishes, using pointer/length state because net8 cannot use a span as that state.
        fixed (byte* source = utf8)
        {
            return string.Create(utf8.Length, (Source: (nint)source, Length: utf8.Length),
                static (chars, state) => Ascii.ToUtf16(
                    new ReadOnlySpan<byte>((byte*)state.Source, state.Length), chars, out _));
        }
#endif
    }
}
