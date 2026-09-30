using Respire.Protocol;

namespace Respire.Internal;

internal static class FunctionResponseReader
{
    internal static RespireFunctionLibraryInfo[] Libraries(in RespValue value)
    {
        var values = Array(in value);
        var result = new RespireFunctionLibraryInfo[values.Length];
        for (var i = 0; i < result.Length; i++)
        {
            var library = values[i];
            var functions = Field(in library, "functions");
            var entries = Array(in functions);
            var parsed = new RespireFunctionInfo[entries.Length];
            for (var j = 0; j < parsed.Length; j++)
            {
                var function = entries[j];
                var flags = Field(in function, "flags");
                var flagValues = Array(in flags, allowSet: true);
                var names = new string[flagValues.Length];
                for (var k = 0; k < names.Length; k++) names[k] = Text(in flagValues[k]);
                parsed[j] = new(Text(Field(in function, "name")), NullableText(Field(in function, "description")), names);
            }
            result[i] = new(Text(Field(in library, "library_name")), Text(Field(in library, "engine")),
                parsed, NullableText(Field(in library, "library_code", optional: true)));
        }
        return result;
    }

    internal static RespireFunctionStats Stats(in RespValue value)
    {
        RespireRunningFunction? running = null;
        var script = Field(in value, "running_script");
        if (!script.IsNull)
        {
            var command = Field(in script, "command");
            var arguments = Array(in command);
            var owned = new byte[arguments.Length][];
            for (var i = 0; i < owned.Length; i++) owned[i] = arguments[i].AsSpan().ToArray();
            running = new(Text(Field(in script, "name")), owned, Integer(Field(in script, "duration_ms")));
        }
        var engines = Field(in value, "engines");
        var pairs = Map(in engines);
        var result = new Dictionary<string, RespireFunctionEngineStats>(StringComparer.Ordinal);
        for (var i = 0; i < pairs.Length; i += 2)
        {
            var stats = pairs[i + 1];
            result.Add(Text(in pairs[i]), new(Integer(Field(in stats, "libraries_count")), Integer(Field(in stats, "functions_count"))));
        }
        return new(running, result);
    }

    private static RespValue Field(in RespValue value, string name, bool optional = false)
    {
        var pairs = Map(in value);
        for (var i = 0; i < pairs.Length; i += 2)
            if (Text(in pairs[i]) == name) return pairs[i + 1];
        if (optional) return RespValue.Null;
        throw new RespireProtocolException($"FUNCTION response lacks '{name}'.");
    }
    private static ReadOnlySpan<RespValue> Map(in RespValue value)
    {
        if (value.Type is not (RespDataType.Map or RespDataType.Array) || value.AsArray().Length % 2 != 0)
            throw new RespireProtocolException("FUNCTION response requires key/value pairs.");
        return value.AsArray();
    }
    private static ReadOnlySpan<RespValue> Array(in RespValue value, bool allowSet = false)
    {
        if (value.Type != RespDataType.Array && !(allowSet && value.Type == RespDataType.Set))
            throw new RespireProtocolException("FUNCTION response requires an array.");
        return value.AsArray();
    }
    private static string? NullableText(in RespValue value) => value.IsNull ? null : Text(in value);
    private static string Text(in RespValue value)
    {
        if (value.Type is not (RespDataType.BulkString or RespDataType.SimpleString or RespDataType.VerbatimString))
            throw new RespireProtocolException("FUNCTION response requires a string.");
        return value.AsString();
    }
    private static long Integer(in RespValue value)
    {
        if (value.Type != RespDataType.Integer) throw new RespireProtocolException("FUNCTION response requires an integer.");
        return value.AsInteger();
    }
}
