using System.Globalization;

namespace Respire.Internal;

/// <summary>Conservative parsing of Valkey 9.1's optional scripting engine inventory.</summary>
internal static class ScriptingEngineInfo
{
    internal static bool IsScriptingCommand(string? command) => command is
        "EVAL" or "EVAL_RO" or "EVALSHA" or "EVALSHA_RO" or
        "SCRIPT LOAD" or "FUNCTION LOAD" or "FUNCTION RESTORE" or "FCALL" or "FCALL_RO";

    internal static string? MissingEngine(RespireServerException error)
    {
        if (!IsScriptingCommand(error.CommandName)) return null;
        const string evalPrefix = "ERR Could not find scripting engine '";
        const string functionPrefix = "ERR Engine '";
        var message = error.Message;
        var prefix = message.StartsWith(evalPrefix, StringComparison.Ordinal) ? evalPrefix : functionPrefix;
        var suffix = prefix == evalPrefix ? "'" : "' not found";
        if (!message.StartsWith(prefix, StringComparison.Ordinal) || !message.EndsWith(suffix, StringComparison.Ordinal))
            return null;
        var length = message.Length - prefix.Length - suffix.Length;
        if (length <= 0) return null;
        var engine = message.Substring(prefix.Length, length);
        return engine.IndexOfAny(['\'', '\r', '\n']) < 0 ? engine : null;
    }

    // Unknown sections, malformed inventories, and partial replies never prove absence.
    internal static bool ConfirmsAbsence(string info, string engine)
    {
        int? count = null;
        var section = false;
        var entries = new Dictionary<int, string>();
        foreach (var raw in info.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line == "# Scripting Engines")
            {
                if (section) return false;
                section = true;
                continue;
            }
            if (!section || line.StartsWith('#')) return false;
            if (line.StartsWith("engines_count:", StringComparison.Ordinal))
            {
                if (count is not null || !int.TryParse(line.AsSpan(14), NumberStyles.None,
                        CultureInfo.InvariantCulture, out var parsed)) return false;
                count = parsed;
            }
            else if (line.StartsWith("engine_", StringComparison.Ordinal))
            {
                var colon = line.IndexOf(':');
                if (colon < 7 || !int.TryParse(line.AsSpan(7, colon - 7), NumberStyles.None,
                        CultureInfo.InvariantCulture, out var index)) return false;
                string? name = null;
                foreach (var field in line[(colon + 1)..].Split(','))
                {
                    if (!field.StartsWith("name=", StringComparison.Ordinal)) continue;
                    if (name is not null || field.Length == 5) return false;
                    name = field[5..];
                }
                if (name is null || !entries.TryAdd(index, name)) return false;
            }
        }
        if (!section || count is null || entries.Count != count) return false;
        for (var i = 0; i < count; i++)
            if (!entries.TryGetValue(i, out var name) || string.Equals(name, engine, StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }
}
