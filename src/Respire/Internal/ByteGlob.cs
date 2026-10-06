namespace Respire.Internal;

/// <summary>Case-sensitive, bytewise Redis/Valkey glob matching for locally filtered scan suffixes.</summary>
internal static class ByteGlob
{
    internal static bool IsMatch(ReadOnlySpan<byte> value, ReadOnlySpan<byte> pattern)
    {
        var position = 0;
        var offset = 0;
        var star = -1;
        var retry = 0;
        while (offset < value.Length)
        {
            if (position < pattern.Length && pattern[position] == (byte)'*')
            {
                star = ++position;
                retry = offset;
                continue;
            }
            var next = position;
            if (next < pattern.Length && MatchesToken(pattern, ref next, value[offset]))
            {
                position = next;
                offset++;
                continue;
            }
            if (star < 0 || retry == value.Length) return false;
            position = star;
            offset = ++retry;
        }
        while (position < pattern.Length && pattern[position] == (byte)'*') position++;
        return position == pattern.Length;
    }

    private static bool MatchesToken(ReadOnlySpan<byte> pattern, ref int position, byte value)
    {
        var token = pattern[position++];
        if (token == (byte)'?') return true;
        if (token == (byte)'\\' && position < pattern.Length)
            return pattern[position++] == value;
        if (token != (byte)'[') return token == value;

        var negate = position < pattern.Length && pattern[position] == (byte)'^';
        if (negate) position++;
        var matches = false;
        while (position < pattern.Length && pattern[position] != (byte)']')
        {
            var start = pattern[position++];
            if (start == (byte)'\\' && position < pattern.Length)
            {
                matches |= pattern[position++] == value;
            }
            else if (position + 1 < pattern.Length && pattern[position] == (byte)'-')
            {
                position++;
                var end = pattern[position++];
                matches |= value >= Math.Min(start, end) && value <= Math.Max(start, end);
            }
            else
            {
                matches |= start == value;
            }
        }
        if (position < pattern.Length) position++;
        return matches != negate;
    }
}
