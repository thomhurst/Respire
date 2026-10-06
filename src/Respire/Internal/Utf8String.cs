using System.Text;

namespace Respire.Internal;

/// <summary>
/// UTF-8 decoding that widens eligible ASCII payloads directly into the final string and preserves
/// <see cref="Encoding.UTF8"/>'s replacement fallback for other payloads.
/// </summary>
internal static class Utf8String
{
#if !NET9_0_OR_GREATER
    // Preserve the original net8 cutoff: CI run 37399372813 found a large-Unicode
    // regression without it. See docs/STACKALLOC_AUDIT.md for the measured scope.
    private const int Net8DirectAsciiMaxByteLength = 256;
#endif

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

    internal static unsafe string GetString(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty)
        {
            return string.Empty;
        }

        // Validate before allocating: a failed ASCII conversion into a byte-length string
        // would discard that string and allocate a second one for Unicode or invalid UTF-8.
#if NET9_0_OR_GREATER
        if (Ascii.IsValid(utf8))
        {
            return string.Create(utf8.Length, utf8, static (chars, state) =>
                Ascii.ToUtf16(state, chars, out _));
        }
#else
        // Keep the existing net8 limit: scanning a long ASCII prefix before Unicode
        // fallback adds work that the runtime decoder already performs.
        if (utf8.Length <= Net8DirectAsciiMaxByteLength && Ascii.IsValid(utf8))
        {
            // string.Create invokes its action synchronously. Keep the source pinned until it
            // finishes, using pointer/length state because net8 cannot use a span as that state.
            fixed (byte* source = utf8)
            {
                return string.Create(utf8.Length, (Source: (nint)source, Length: utf8.Length),
                    static (chars, state) => Ascii.ToUtf16(
                        new ReadOnlySpan<byte>((byte*)state.Source, state.Length), chars, out _));
            }
        }
#endif

        return Encoding.UTF8.GetString(utf8);
    }
}
