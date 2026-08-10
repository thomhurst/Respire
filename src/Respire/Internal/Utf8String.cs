using System.Text;

namespace Respire.Internal;

/// <summary>
/// UTF-8 decoding with an ASCII fast path. RESP payloads (keys, statuses, numbers, most
/// values) are overwhelmingly ASCII, where a vectorized validity scan plus a single widening
/// pass beats <see cref="Encoding.UTF8"/>'s two validating passes by 20–40%.
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

#if NET9_0_OR_GREATER
        if (Ascii.IsValid(utf8))
        {
            return string.Create(utf8.Length, utf8, static (chars, state) =>
                Ascii.ToUtf16(state, chars, out _));
        }
#else
        // string.Create cannot take a span as state before net9.0; widen through the stack
        // for small payloads and let larger ones fall through.
        if (utf8.Length <= 256 && Ascii.IsValid(utf8))
        {
            Span<char> chars = stackalloc char[256];
            Ascii.ToUtf16(utf8, chars, out var written);
            return new string(chars[..written]);
        }
#endif

        return Encoding.UTF8.GetString(utf8);
    }
}
