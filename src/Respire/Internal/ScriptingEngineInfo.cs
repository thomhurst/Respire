using System.Globalization;
using Respire.Protocol;

namespace Respire.Internal;

/// <summary>Conservative parsing of Valkey 9.1's optional scripting engine inventory.</summary>
internal static class ScriptingEngineInfo
{
    internal static bool IsScriptingCommand(string? command) => command is
        "EVAL" or "EVAL_RO" or "SCRIPT LOAD" or "FUNCTION LOAD" or "FUNCTION RESTORE";

    internal static string? ExpectedEngine<TCommand>(in TCommand command, string operation)
        where TCommand : struct, IRespCommand
    {
        if (operation == "FUNCTION RESTORE") return null;
        if (!command.TryGetArgument(0, out var source)) return null;
        if (operation == "FUNCTION LOAD" && source.EqualsAsciiIgnoreCase("REPLACE")
            && !command.TryGetArgument(1, out source)) return null;
        var text = source.ToString();
        if (!text.StartsWith("#!", StringComparison.Ordinal))
            return operation is "EVAL" or "EVAL_RO" or "SCRIPT LOAD" ? "lua" : null;
        var newline = text.IndexOf('\n');
        if (newline < 0) return null;
        var end = 2;
        while (end < newline && text[end] is not (' ' or '\t' or '\r' or '\v' or '\f'))
        {
            // Redis accepts quoted/escaped headers. Leave those to the server rather than
            // approximate its argument parser and attribute an application error incorrectly.
            if (text[end] is '\'' or '"' or '\\' or '\0') return null;
            end++;
        }
        return end > 2 ? text[2..end] : null;
    }

    internal static string? MissingEngine(RespireServerException error, string? expectedEngine)
    {
        if (!IsScriptingCommand(error.CommandName)) return null;
        const string evalPrefix = "ERR Could not find scripting engine '";
        const string functionPrefix = "ERR Engine '";
        var message = error.Message;
        var prefix = error.CommandName is "FUNCTION LOAD" or "FUNCTION RESTORE" ? functionPrefix : evalPrefix;
        var suffix = prefix == evalPrefix ? "'" : "' not found";
        if (!message.StartsWith(prefix, StringComparison.Ordinal) || !message.EndsWith(suffix, StringComparison.Ordinal))
            return null;
        var length = message.Length - prefix.Length - suffix.Length;
        if (length <= 0) return null;
        var engine = message.Substring(prefix.Length, length);
        if (engine.IndexOfAny(['\'', '\r', '\n']) >= 0) return null;
        // EVALSHA/FCALL cannot identify their source engine and never use this classifier.
        // RESTORE errors originate while loading serialized libraries, not executing a function.
        return error.CommandName == "FUNCTION RESTORE"
            || string.Equals(engine, expectedEngine, StringComparison.OrdinalIgnoreCase) ? engine : null;
    }

    // Unknown sections, malformed inventories, and partial replies never prove absence.
    internal static bool ConfirmsAbsence(string info, string engine)
        => ParseInventory(info) is { } engines && !engines.Contains(engine);

    internal static HashSet<string>? ParseInventory(string info)
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
                if (section) return null;
                section = true;
                continue;
            }
            if (!section || line.StartsWith('#')) return null;
            if (line.StartsWith("engines_count:", StringComparison.Ordinal))
            {
                if (count is not null || !int.TryParse(line.AsSpan(14), NumberStyles.None,
                        CultureInfo.InvariantCulture, out var parsed)) return null;
                count = parsed;
            }
            else if (line.StartsWith("engine_", StringComparison.Ordinal))
            {
                var colon = line.IndexOf(':');
                if (colon < 7 || !int.TryParse(line.AsSpan(7, colon - 7), NumberStyles.None,
                        CultureInfo.InvariantCulture, out var index)) return null;
                string? name = null;
                foreach (var field in line[(colon + 1)..].Split(','))
                {
                    if (!field.StartsWith("name=", StringComparison.Ordinal)) continue;
                    if (name is not null || field.Length == 5) return null;
                    name = field[5..];
                }
                if (name is null || !entries.TryAdd(index, name)) return null;
            }
        }
        if (!section || count is null || entries.Count != count) return null;
        var engines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < count; i++)
            if (!entries.TryGetValue(i, out var name) || !engines.Add(name)) return null;
        return engines;
    }
}
