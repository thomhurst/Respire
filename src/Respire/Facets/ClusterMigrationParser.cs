using Respire.Protocol;

namespace Respire;

internal static class ClusterMigrationParser
{
    internal static string TaskId(in RespValue value)
    {
        if (value.Type != RespDataType.BulkString || value.AsSpan().IsEmpty)
            throw new RespireProtocolException("CLUSTER MIGRATION IMPORT must return a nonempty bulk task ID.");
        return value.AsString();
    }

    internal static RespireClusterMigrationTask[] Tasks(in RespValue value)
    {
        if (value.Type != RespDataType.Array)
            throw new RespireProtocolException("CLUSTER MIGRATION STATUS must return an array.");
        var rows = value.AsArray();
        var result = new RespireClusterMigrationTask[rows.Length];
        for (var index = 0; index < rows.Length; index++) result[index] = Task(in rows[index]);
        return result;
    }

    private static RespireClusterMigrationTask Task(in RespValue value)
    {
        if (value.Type is not (RespDataType.Array or RespDataType.Map))
            throw new RespireProtocolException("CLUSTER MIGRATION task must be a map or field/value array.");
        var pairs = value.AsArray();
        if (pairs.Length % 2 != 0)
            throw new RespireProtocolException("CLUSTER MIGRATION task has an incomplete field/value pair.");
        var fields = new Dictionary<string, RespValue>(StringComparer.Ordinal);
        for (var index = 0; index < pairs.Length; index += 2)
            if (!fields.TryAdd(ServerDiagnosticsParser.Text(in pairs[index]), pairs[index + 1]))
                throw new RespireProtocolException("CLUSTER MIGRATION task has a duplicate field.");
        var id = Text(fields, "id");
        if (id.Length == 0) throw new RespireProtocolException("CLUSTER MIGRATION task ID is empty.");
        var slots = Text(fields, "slots");
        var source = Text(fields, "source");
        var destination = Text(fields, "dest");
        var operation = Text(fields, "operation");
        var state = Text(fields, "state");
        var error = Text(fields, "last_error");
        var retries = ServerDiagnosticsParser.NonnegativeInteger(Take(fields, "retries"));
        var created = Timestamp(Take(fields, "create_time"), allowUnset: false)!.Value;
        var started = Timestamp(Take(fields, "start_time"), allowUnset: true);
        var ended = Timestamp(Take(fields, "end_time"), allowUnset: true);
        var pause = ServerDiagnosticsParser.NonnegativeInteger(Take(fields, "write_pause_ms"));
        var additional = new Dictionary<string, RespireResult>(StringComparer.Ordinal);
        foreach (var (name, field) in fields) additional.Add(name, new RespireResult(field.ToOwned()));
        return new(id, slots, source, destination, operation, state, error, retries, created, started, ended, pause, additional);
    }

    private static string Text(Dictionary<string, RespValue> fields, string name)
        => ServerDiagnosticsParser.Text(Take(fields, name));

    private static RespValue Take(Dictionary<string, RespValue> fields, string name)
        => fields.Remove(name, out var value) ? value : throw new RespireProtocolException($"CLUSTER MIGRATION task is missing {name}.");

    private static DateTimeOffset? Timestamp(in RespValue value, bool allowUnset)
    {
        if (value.Type != RespDataType.Integer)
            throw new RespireProtocolException("CLUSTER MIGRATION timestamps must be integer Unix milliseconds.");
        var milliseconds = value.AsInteger();
        if (allowUnset && milliseconds == -1) return null;
        if (milliseconds < 0) throw new RespireProtocolException("CLUSTER MIGRATION timestamp is negative.");
        try { return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds); }
        catch (ArgumentOutOfRangeException error)
        { throw new RespireProtocolException("CLUSTER MIGRATION timestamp is outside the supported range.", error); }
    }
}
