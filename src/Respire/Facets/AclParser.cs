using System.Globalization;
using Respire.Protocol;

namespace Respire;

internal static class AclParser
{
    internal static byte[] Bytes(in RespValue value)
    {
        RequireString(in value);
        return value.AsSpan().ToArray();
    }

    private static string Text(in RespValue value)
    {
        RequireString(in value);
        return value.AsString();
    }

    private static void RequireString(in RespValue value)
    {
        if (value.Type is not (RespDataType.SimpleString or RespDataType.BulkString))
            throw new RespireProtocolException("ACL text fields must be strings.");
    }

    private static ReadOnlySpan<RespValue> Items(in RespValue value, bool allowSet = false)
    {
        if (value.Type != RespDataType.Array && !(allowSet && value.Type == RespDataType.Set))
            throw new RespireProtocolException("ACL lists must be arrays.");
        return value.AsArray();
    }

    internal static byte[][] ByteStrings(in RespValue value)
    {
        var items = Items(in value);
        var result = new byte[items.Length][];
        for (var index = 0; index < items.Length; index++) result[index] = Bytes(in items[index]);
        return result;
    }

    internal static string[] Strings(in RespValue value)
    {
        var items = Items(in value, allowSet: true);
        var result = new string[items.Length];
        for (var index = 0; index < items.Length; index++) result[index] = Text(in items[index]);
        return result;
    }

    internal static bool Ok(in RespValue value)
    {
        if (value.Type != RespDataType.SimpleString || !value.AsSpan().SequenceEqual("OK"u8))
            throw new RespireProtocolException("ACL mutation must return OK.");
        return true;
    }

    internal static long NonnegativeInteger(in RespValue value)
    {
        if (value.Type != RespDataType.Integer || value.AsInteger() < 0)
            throw new RespireProtocolException("ACL counts and identifiers must be nonnegative integers.");
        return value.AsInteger();
    }

    internal static RespireAclDryRunResult DryRun(in RespValue value)
    {
        // Redis uses a bulk string for simulated denial and +OK for success. Actual command
        // errors (including the caller's NOPERM) are thrown by the response pipeline.
        if (value.Type == RespDataType.BulkString) return new(false, value.AsString());
        Ok(in value);
        return new(true, null);
    }

    internal static RespireAclUser? User(in RespValue value)
    {
        if (value.IsNull) return null;
        var fields = Fields(in value);
        var flags = Strings(Take(fields, "flags"));
        var passwords = Strings(Take(fields, "passwords"));
        var commands = Text(Take(fields, "commands"));
        var keys = Patterns(Take(fields, "keys"));
        var channels = fields.Remove("channels", out var channelValue) ? Patterns(in channelValue) : null;
        RespireAclSelector[] selectors = [];
        if (fields.Remove("selectors", out var selectorValue))
        {
            var items = Items(in selectorValue);
            selectors = new RespireAclSelector[items.Length];
            for (var index = 0; index < items.Length; index++) selectors[index] = Selector(in items[index]);
        }
        return new(flags, passwords, commands, keys, channels, selectors, OwnRemaining(fields));
    }

    private static RespireAclSelector Selector(in RespValue value)
    {
        var fields = Fields(in value);
        var commands = Text(Take(fields, "commands"));
        var keys = Patterns(Take(fields, "keys"));
        var channels = fields.Remove("channels", out var channelValue) ? Patterns(in channelValue) : null;
        return new(commands, keys, channels, OwnRemaining(fields));
    }

    private static RespireAclPatterns Patterns(in RespValue value)
        => value.Type == RespDataType.Array ? new(null, ByteStrings(in value)) : new(Bytes(in value), null);

    internal static RespireAclLogEntry[] Log(in RespValue value)
    {
        var items = Items(in value);
        var result = new RespireAclLogEntry[items.Length];
        for (var index = 0; index < items.Length; index++) result[index] = LogEntry(in items[index]);
        return result;
    }

    private static RespireAclLogEntry LogEntry(in RespValue value)
    {
        var fields = Fields(in value);
        var count = NonnegativeInteger(Take(fields, "count"));
        var reason = Text(Take(fields, "reason"));
        var context = Text(Take(fields, "context"));
        var resource = Bytes(Take(fields, "object"));
        var username = Bytes(Take(fields, "username"));
        var age = Age(Take(fields, "age-seconds"));
        var info = Bytes(Take(fields, "client-info"));
        var entryId = OptionalInteger(fields, "entry-id");
        var created = OptionalInteger(fields, "timestamp-created");
        var updated = OptionalInteger(fields, "timestamp-last-updated");
        return new(count, reason, context, resource, username, age, info, entryId, created, updated, OwnRemaining(fields));
    }

    private static double Age(in RespValue value)
    {
        double age;
        if (value.Type == RespDataType.Double) age = value.AsDouble();
        else if (!double.TryParse(Text(in value), NumberStyles.Float, CultureInfo.InvariantCulture, out age))
            throw new RespireProtocolException("ACL log age must be a finite nonnegative number.");
        if (!double.IsFinite(age) || age < 0)
            throw new RespireProtocolException("ACL log age must be a finite nonnegative number.");
        return age;
    }

    private static long? OptionalInteger(Dictionary<string, RespValue> fields, string name)
        => fields.Remove(name, out var value) ? NonnegativeInteger(in value) : null;

    private static Dictionary<string, RespValue> Fields(in RespValue value)
    {
        if (value.Type is not (RespDataType.Array or RespDataType.Map))
            throw new RespireProtocolException("ACL structures must be maps or arrays of field/value pairs.");
        var items = value.AsArray();
        if (items.Length % 2 != 0) throw new RespireProtocolException("ACL structures contain an incomplete field/value pair.");
        var result = new Dictionary<string, RespValue>(items.Length / 2, StringComparer.Ordinal);
        for (var index = 0; index < items.Length; index += 2)
        {
            if (!result.TryAdd(Text(in items[index]), items[index + 1]))
                throw new RespireProtocolException("ACL structures contain duplicate fields.");
        }
        return result;
    }

    private static RespValue Take(Dictionary<string, RespValue> fields, string name)
        => fields.Remove(name, out var value) ? value : throw new RespireProtocolException($"ACL structure is missing field '{name}'.");

    private static Dictionary<string, RespireResult> OwnRemaining(Dictionary<string, RespValue> fields)
    {
        var result = new Dictionary<string, RespireResult>(fields.Count, StringComparer.Ordinal);
        foreach (var (name, value) in fields) result.Add(name, RespireResult.CreateOwned(in value));
        return result;
    }
}
