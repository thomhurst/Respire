using Respire.Protocol;

namespace Respire;

internal static class ValkeySlotMigrationParser
{
    internal static RespireValkeySlotMigration[] Parse(in RespValue value)
    {
        if (value.Type != RespDataType.Array)
            throw new RespireProtocolException("Valkey slot migrations must be an array.");
        var rows = value.AsArray();
        var result = new RespireValkeySlotMigration[rows.Length];
        for (var index = 0; index < rows.Length; index++)
        {
            try { result[index] = Row(in rows[index]); }
            catch (RespireProtocolException error)
            {
                throw new RespireProtocolException($"Invalid Valkey slot migration row {index}.", error);
            }
        }
        return result;
    }

    private static RespireValkeySlotMigration Row(in RespValue value)
    {
        if (value.Type is not (RespDataType.Array or RespDataType.Map))
            throw new RespireProtocolException("Migration fields must be a map or array of pairs.");
        var pairs = value.AsArray();
        if (pairs.Length % 2 != 0) throw new RespireProtocolException("Incomplete migration field/value pair.");
        var fields = new Dictionary<string, RespValue>(StringComparer.Ordinal);
        for (var index = 0; index < pairs.Length; index += 2)
            if (!fields.TryAdd(ServerDiagnosticsParser.Text(in pairs[index]), pairs[index + 1]))
                throw new RespireProtocolException("Duplicate migration field.");
        var name = Text("name");
        var operation = Text("operation");
        var slots = Text("slot_ranges");
        var source = OptionalText("source_node");
        var target = OptionalText("target_node");
        var created = Time("create_time");
        var updated = Time("last_update_time");
        var acknowledged = Time("last_ack_time");
        var state = Text("state");
        var message = Text("message");
        var cow = ServerDiagnosticsParser.NonnegativeInteger(Take("cow_size"));
        long? remaining = fields.Remove("remaining_repl_size", out var size)
            ? ServerDiagnosticsParser.NonnegativeInteger(in size) : null;
        var additional = new Dictionary<string, RespireResult>(StringComparer.Ordinal);
        foreach (var pair in fields) additional.Add(pair.Key, new RespireResult(pair.Value.ToOwned()));
        return new(name, operation, slots, source, target, created, updated, acknowledged,
            state, message, cow, remaining, additional);

        RespValue Take(string key) => fields.Remove(key, out var item) ? item
            : throw new RespireProtocolException($"Missing migration field {key}.");
        string Text(string key) => ServerDiagnosticsParser.Text(Take(key));
        string? OptionalText(string key) => fields.Remove(key, out var item) ? ServerDiagnosticsParser.Text(in item) : null;
        DateTimeOffset Time(string key)
        {
            var seconds = ServerDiagnosticsParser.NonnegativeInteger(Take(key));
            try { return DateTimeOffset.FromUnixTimeSeconds(seconds); }
            catch (ArgumentOutOfRangeException error)
            {
                throw new RespireProtocolException($"Migration timestamp {key} is outside the supported range.", error);
            }
        }
    }
}
