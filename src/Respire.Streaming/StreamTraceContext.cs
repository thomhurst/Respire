using System.Diagnostics;
using System.Text;

namespace Respire.Streaming;

internal static class StreamTraceContext
{
    internal static ActivityContext Extract(RespireStreamEntry entry, RespireStreamWorkerOptions options)
    {
        var parent = ReadField(entry, options.TraceParentField, 55);
        if (parent is not { Length: 55 } || !parent.StartsWith("00-", StringComparison.Ordinal)
            || !ActivityContext.TryParse(parent, null, isRemote: true, out var context)) return default;
        // TryParse accepts upper-case hex on some runtimes. W3C fields require lower-case hex.
        for (var i = 3; i < parent.Length; i++)
            if (i is 35 or 52 ? parent[i] != '-' : !IsHex(parent[i])) return default;
        var state = ReadField(entry, options.TraceStateField, 512);
        return new(context.TraceId, context.SpanId, context.TraceFlags,
            IsValidState(state) ? state : null, isRemote: true);
    }

    private static string? ReadField(RespireStreamEntry entry, string? name, int maximum)
    {
        if (name is null) return null;
        byte[]? found = null;
        foreach (var field in entry.Fields)
        {
            if (field.Key != name) continue;
            if (found is not null || field.Value.Length > maximum) return null;
            found = field.Value;
        }
        if (found is null) return null;
        foreach (var value in found)
            if (value is < 32 or > 126) return null;
        return Encoding.ASCII.GetString(found);
    }

    private static bool IsHex(char value) => value is >= '0' and <= '9' or >= 'a' and <= 'f';
    private static bool IsKeyCharacter(char value)
        => value is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' or '*' or '/';

    private static bool IsValidState(string? state)
    {
        if (string.IsNullOrEmpty(state)) return false;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in state.Split(','))
        {
            var member = raw.Trim(' ');
            var equals = member.IndexOf('=');
            if (equals < 1 || equals > 256 || equals == member.Length - 1) return false;
            var key = member[..equals];
            if (!keys.Add(key) || keys.Count > 32) return false;
            var at = key.IndexOf('@');
            if (at < 0)
            {
                if (key[0] is < 'a' or > 'z') return false;
            }
            else if (at is < 1 or > 241 || key.Length - at - 1 is < 1 or > 14
                || key[0] is not (>= 'a' and <= 'z' or >= '0' and <= '9')
                || key[at + 1] is < 'a' or > 'z') return false;
            for (var i = 0; i < key.Length; i++)
                if (i != at && !IsKeyCharacter(key[i])) return false;
            var value = member.AsSpan(equals + 1);
            if (value.Length > 256 || value[^1] == ' ') return false;
            foreach (var character in value)
                if (character is < ' ' or > '~' or '=' or ',') return false;
        }
        return true;
    }
}
