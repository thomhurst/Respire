using Respire.Protocol;

namespace Respire;

internal static class CommandMetadataParser
{
    internal static string Text(in RespValue value)
    {
        Require(value.Type is RespDataType.SimpleString or RespDataType.BulkString or RespDataType.VerbatimString);
        return value.AsString();
    }

    private static byte[] Bytes(in RespValue value)
    {
        Require(value.Type is RespDataType.SimpleString or RespDataType.BulkString);
        return value.AsSpan().ToArray();
    }

    private static long Integer(in RespValue value)
    {
        Require(value.Type == RespDataType.Integer);
        return value.AsInteger();
    }

    private static ReadOnlySpan<RespValue> Items(in RespValue value, bool allowSet = false)
    {
        Require(value.Type == RespDataType.Array || (allowSet && value.Type == RespDataType.Set));
        return value.AsArray();
    }

    private static string[] Strings(in RespValue value)
    {
        var items = Items(in value, allowSet: true);
        var result = new string[items.Length];
        for (var index = 0; index < items.Length; index++) result[index] = Text(in items[index]);
        return result;
    }

    internal static byte[][] Keys(in RespValue value)
    {
        var items = Items(in value);
        var result = new byte[items.Length][];
        for (var index = 0; index < items.Length; index++) result[index] = Bytes(in items[index]);
        return result;
    }

    internal static bool Ok(in RespValue value)
    {
        Require(value.Type == RespDataType.SimpleString && value.AsSpan().SequenceEqual("OK"u8));
        return true;
    }

    internal static RespireBackgroundPersistenceResult Background(in RespValue value)
    {
        var message = Text(in value);
        var state = message switch
        {
            "Background saving started" or "Background append only file rewriting started" => RespireBackgroundPersistenceState.Started,
            "Background saving scheduled" or "Background append only file rewriting scheduled" => RespireBackgroundPersistenceState.Scheduled,
            _ => RespireBackgroundPersistenceState.Unknown,
        };
        return new(state, message);
    }

    internal static RespireCommandInfo?[] Info(in RespValue value)
    {
        var items = Items(in value);
        var result = new RespireCommandInfo?[items.Length];
        for (var index = 0; index < items.Length; index++)
            result[index] = items[index].IsNull ? null : InfoEntry(in items[index]);
        return result;
    }

    private static RespireCommandInfo InfoEntry(in RespValue value)
    {
        var row = Items(in value);
        Require(row.Length >= 6);
        var name = Text(in row[0]);
        var arity = Integer(in row[1]);
        var flags = Strings(in row[2]);
        var first = Integer(in row[3]);
        var last = Integer(in row[4]);
        var step = Integer(in row[5]);
        var categories = row.Length > 6 ? Strings(in row[6]) : [];
        var tips = row.Length > 7 ? Strings(in row[7]) : [];
        var specifications = row.Length > 8 ? OwnItems(Items(in row[8], allowSet: true)) : [];
        RespireCommandInfo[] subcommands = [];
        if (row.Length > 9)
        {
            // Redis uses an empty RESP3 set when this command has no subcommands.
            var nested = Items(in row[9], allowSet: true);
            subcommands = new RespireCommandInfo[nested.Length];
            for (var index = 0; index < nested.Length; index++) subcommands[index] = InfoEntry(in nested[index]);
        }
        return new(name, arity, flags, first, last, step, categories, tips, specifications,
            subcommands, row.Length > 10 ? OwnItems(row[10..]) : []);
    }

    internal static RespireCommandDocumentation[] Docs(in RespValue value)
    {
        Require(value.Type is RespDataType.Array or RespDataType.Map);
        var commands = value.AsArray();
        Require(commands.Length % 2 == 0);
        var result = new RespireCommandDocumentation[commands.Length / 2];
        // Redis can repeat a name when the request repeats it. Preserve entries in reply order.
        for (var index = 0; index < result.Length; index++)
            result[index] = Documentation(Text(in commands[index * 2]), in commands[index * 2 + 1]);
        return result;
    }

    private static RespireCommandDocumentation Documentation(string name, in RespValue value)
    {
        var fields = Fields(in value);
        var summary = OptionalText(fields, "summary");
        var since = OptionalText(fields, "since");
        var group = OptionalText(fields, "group");
        var complexity = OptionalText(fields, "complexity");
        var module = OptionalText(fields, "module");
        var flags = OptionalStrings(fields, "doc_flags");
        var deprecated = OptionalText(fields, "deprecated_since");
        var replaced = OptionalText(fields, "replaced_by");
        RespireCommandHistory[] history = [];
        if (fields.Remove("history", out var historyValue))
        {
            var items = Items(in historyValue, allowSet: true);
            history = new RespireCommandHistory[items.Length];
            for (var index = 0; index < items.Length; index++)
            {
                var row = Items(in items[index]);
                Require(row.Length == 2);
                history[index] = new(Text(in row[0]), Text(in row[1]));
            }
        }
        var arguments = Arguments(fields);
        var subcommands = fields.Remove("subcommands", out var nested) ? Docs(in nested) : [];
        return new(name, summary, since, group, complexity, module, flags, deprecated, replaced,
            history, arguments, subcommands, OwnFields(fields));
    }

    private static RespireCommandArgument[] Arguments(Dictionary<string, RespValue> fields)
    {
        if (!fields.Remove("arguments", out var value)) return [];
        var items = Items(in value);
        var result = new RespireCommandArgument[items.Length];
        for (var index = 0; index < items.Length; index++)
        {
            var argument = Fields(in items[index]);
            var name = Text(Take(argument, "name"));
            var type = Text(Take(argument, "type"));
            var display = OptionalText(argument, "display_text");
            long? specification = argument.Remove("key_spec_index", out var keyIndex) ? Integer(in keyIndex) : null;
            var token = OptionalText(argument, "token");
            var summary = OptionalText(argument, "summary");
            var since = OptionalText(argument, "since");
            var deprecated = OptionalText(argument, "deprecated_since");
            var flags = OptionalStrings(argument, "flags");
            var nested = Arguments(argument);
            result[index] = new(name, type, display, specification, token, summary, since, deprecated,
                flags, nested, OwnFields(argument));
        }
        return result;
    }

    internal static RespireModuleInfo[] Modules(in RespValue value)
    {
        var items = Items(in value);
        var result = new RespireModuleInfo[items.Length];
        for (var index = 0; index < items.Length; index++)
        {
            var fields = Fields(in items[index]);
            var name = Text(Take(fields, "name"));
            var version = Integer(Take(fields, "ver"));
            var path = fields.Remove("path", out var pathValue) ? Bytes(in pathValue) : null;
            var arguments = fields.Remove("args", out var argsValue) ? Keys(in argsValue) : null;
            result[index] = new(name, version, path, arguments, OwnFields(fields));
        }
        return result;
    }

    private static Dictionary<string, RespValue> Fields(in RespValue value)
    {
        Require(value.Type is RespDataType.Array or RespDataType.Map);
        var items = value.AsArray();
        Require(items.Length % 2 == 0);
        var result = new Dictionary<string, RespValue>(items.Length / 2, StringComparer.Ordinal);
        for (var index = 0; index < items.Length; index += 2)
            Require(result.TryAdd(Text(in items[index]), items[index + 1]));
        return result;
    }

    private static RespValue Take(Dictionary<string, RespValue> fields, string name)
        => fields.Remove(name, out var value) ? value : throw new RespireProtocolException($"Command metadata is missing '{name}'.");

    private static string? OptionalText(Dictionary<string, RespValue> fields, string name)
        => fields.Remove(name, out var value) ? Text(in value) : null;

    private static string[] OptionalStrings(Dictionary<string, RespValue> fields, string name)
        => fields.Remove(name, out var value) ? Strings(in value) : [];

    private static Dictionary<string, RespireResult> OwnFields(Dictionary<string, RespValue> fields)
    {
        var result = new Dictionary<string, RespireResult>(fields.Count, StringComparer.Ordinal);
        foreach (var (name, value) in fields) result.Add(name, new RespireResult(value.ToOwned()));
        return result;
    }

    private static RespireResult[] OwnItems(ReadOnlySpan<RespValue> items)
    {
        var result = new RespireResult[items.Length];
        for (var index = 0; index < items.Length; index++) result[index] = new(items[index].ToOwned());
        return result;
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new RespireProtocolException("Unexpected command metadata or persistence reply shape.");
    }
}
