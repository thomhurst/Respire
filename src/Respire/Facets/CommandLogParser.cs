using Respire.Protocol;

namespace Respire;

internal static class CommandLogParser
{
    internal static RespireCommandLogEntry[] Parse(in RespValue value, RespireCommandLogType type)
    {
        var rows = Array(in value);
        var result = new RespireCommandLogEntry[rows.Length];
        for (var index = 0; index < rows.Length; index++)
        {
            try { result[index] = Entry(in rows[index], type); }
            catch (RespireProtocolException error)
            {
                throw new RespireProtocolException($"COMMANDLOG entry at index {index}: {error.Message}", error);
            }
        }
        return result;
    }

    private static RespireCommandLogEntry Entry(in RespValue value, RespireCommandLogType type)
    {
        var fields = Array(in value);
        if (fields.Length < 6) throw new RespireProtocolException("COMMANDLOG entry must contain at least six fields.");
        var id = NonnegativeInteger(in fields[0]);
        var timestamp = NonnegativeInteger(in fields[1]);
        var metric = NonnegativeInteger(in fields[2]);
        var arguments = Array(in fields[3]);
        var ownedArguments = new byte[arguments.Length][];
        for (var argument = 0; argument < arguments.Length; argument++) ownedArguments[argument] = Bytes(in arguments[argument]);
        var address = Bytes(in fields[4]);
        var name = Bytes(in fields[5]);
        var additional = new RespireResult[fields.Length - 6];
        for (var field = 6; field < fields.Length; field++) additional[field - 6] = new(fields[field].ToOwned());
        return new(type, id, timestamp, metric, ownedArguments, address, name, additional);
    }

    internal static long NonnegativeInteger(in RespValue value)
    {
        if (value.Type != RespDataType.Integer || value.AsInteger() < 0)
            throw new RespireProtocolException("COMMANDLOG counts, IDs, timestamps, and metrics must be nonnegative integers.");
        return value.AsInteger();
    }

    internal static bool Ok(in RespValue value)
    {
        if (value.Type != RespDataType.SimpleString || !value.AsSpan().SequenceEqual("OK"u8))
            throw new RespireProtocolException("COMMANDLOG RESET must return OK.");
        return true;
    }

    private static ReadOnlySpan<RespValue> Array(in RespValue value)
    {
        if (value.Type != RespDataType.Array) throw new RespireProtocolException("COMMANDLOG entries and arguments must be arrays.");
        return value.AsArray();
    }

    private static byte[] Bytes(in RespValue value)
    {
        if (value.Type is not (RespDataType.BulkString or RespDataType.SimpleString or RespDataType.VerbatimString))
            throw new RespireProtocolException("COMMANDLOG arguments and client metadata must be strings.");
        return value.AsSpan().ToArray();
    }
}
