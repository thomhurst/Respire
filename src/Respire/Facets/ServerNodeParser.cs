using System.Globalization;
using Respire.Protocol;

namespace Respire;

internal static class ServerNodeParser
{
    internal static bool Ok(in RespValue value)
    {
        if (value.Type != RespDataType.SimpleString || !value.AsSpan().SequenceEqual("OK"u8))
            throw new RespireProtocolException("Server mutation must return OK.");
        return true;
    }

    internal static RespireMigrateResult Migration(in RespValue value)
    {
        if (value.Type != RespDataType.SimpleString)
            throw new RespireProtocolException("MIGRATE must return OK or NOKEY.");
        if (value.AsSpan().SequenceEqual("OK"u8)) return RespireMigrateResult.Migrated;
        if (value.AsSpan().SequenceEqual("NOKEY"u8)) return RespireMigrateResult.NoKey;
        throw new RespireProtocolException("MIGRATE must return OK or NOKEY.");
    }

    internal static RespireCommandKeyFlags[] KeysAndFlags(in RespValue value)
    {
        var rows = Array(in value);
        var result = new RespireCommandKeyFlags[rows.Length];
        for (var index = 0; index < rows.Length; index++)
        {
            var row = Array(in rows[index]);
            if (row.Length != 2) throw new RespireProtocolException("COMMAND GETKEYSANDFLAGS requires key/flags pairs.");
            result[index] = new(AclParser.Bytes(in row[0]), AclParser.Strings(in row[1]));
        }
        return result;
    }

    internal static RespireBackupStatus BackupStatus(in RespValue value)
    {
        if (value.Type is not (RespDataType.Array or RespDataType.Map))
            throw new RespireProtocolException("BACKUP STATUS must return a map or array of field/value pairs.");
        var pairs = value.AsArray();
        if (pairs.Length % 2 != 0) throw new RespireProtocolException("BACKUP STATUS has an incomplete field/value pair.");
        var fields = new Dictionary<string, RespValue>(StringComparer.Ordinal);
        for (var index = 0; index < pairs.Length; index += 2)
        {
            if (!fields.TryAdd(ServerDiagnosticsParser.Text(in pairs[index]), pairs[index + 1]))
                throw new RespireProtocolException("BACKUP STATUS has a duplicate field.");
        }
        var state = ServerDiagnosticsParser.Text(Take(fields, "state"));
        var error = ServerDiagnosticsParser.Text(Take(fields, "error"));
        var start = Timestamp(Take(fields, "start_time"));
        var end = Timestamp(Take(fields, "end_time"));
        var additional = new Dictionary<string, RespireResult>(StringComparer.Ordinal);
        foreach (var (name, field) in fields) additional.Add(name, RespireResult.CreateOwned(in field));
        return new(state, error, start, end, additional);
    }

    private static RespValue Take(Dictionary<string, RespValue> fields, string name)
        => fields.Remove(name, out var value) ? value : throw new RespireProtocolException($"BACKUP STATUS is missing {name}.");

    private static DateTimeOffset? Timestamp(in RespValue value)
    {
        long seconds;
        if (value.Type == RespDataType.Integer) seconds = value.AsInteger();
        else if (!long.TryParse(ServerDiagnosticsParser.Text(in value), NumberStyles.None, CultureInfo.InvariantCulture, out seconds))
            throw new RespireProtocolException("BACKUP STATUS timestamps must be nonnegative Unix seconds.");
        if (seconds < 0) throw new RespireProtocolException("BACKUP STATUS timestamps must be nonnegative Unix seconds.");
        try { return seconds == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(seconds); }
        catch (ArgumentOutOfRangeException error) { throw new RespireProtocolException("BACKUP STATUS timestamp is outside the supported range.", error); }
    }

    private static ReadOnlySpan<RespValue> Array(in RespValue value)
        => value.Type == RespDataType.Array ? value.AsArray() : throw new RespireProtocolException("Server rows must be arrays.");
}
