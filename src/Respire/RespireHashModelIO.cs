using System.ComponentModel;
using Respire.Protocol;

namespace Respire;

/// <summary>Wire operations used by generated hash mappers.</summary>
/// <remarks>Use the generated mapper to validate and encode model properties.</remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class RespireHashModelIO
{
    /// <summary>Writes present fields, then removes absent mapped fields. The two commands are not atomic.</summary>
    public static async ValueTask WriteAsync(IRespireClient client, RespireKey key,
        IReadOnlyDictionary<string, string> fields, string[] mappedFields, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(mappedFields);
        cancellationToken.ThrowIfCancellationRequested();
        // Own the key across both commands, including caller-owned binary memory.
        var keyValue = key.Snapshot().AsValue();
        var writes = new RespireValue[1 + fields.Count * 2];
        writes[0] = keyValue;
        var index = 1;
        foreach (var field in fields)
        {
            ArgumentNullException.ThrowIfNull(field.Value);
            writes[index++] = field.Key;
            writes[index++] = field.Value;
        }
        var removals = new List<RespireValue> { keyValue };
        foreach (var field in mappedFields)
            if (!fields.ContainsKey(field)) removals.Add(field);

        if (writes.Length > 1)
        {
            using var reply = await client.ExecuteAsync(RespireCommands.Hash.HSET, writes, cancellationToken: cancellationToken).ConfigureAwait(false);
            ValidateWriteReply(reply, fields.Count);
        }
        if (removals.Count > 1)
        {
            using var reply = await client.ExecuteAsync(RespireCommands.Hash.HDEL, removals.ToArray(), cancellationToken: cancellationToken).ConfigureAwait(false);
            ValidateWriteReply(reply, removals.Count - 1);
        }
    }

    /// <summary>Reads one full hash, validating pairs and preserving case-sensitive field names.</summary>
    public static async ValueTask<Dictionary<string, string>> ReadAsync(IRespireClient client, RespireKey key,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        cancellationToken.ThrowIfCancellationRequested();
        using var reply = await client.ExecuteAsync(RespireCommands.Hash.HGETALL, [key.Snapshot().AsValue()], cancellationToken: cancellationToken).ConfigureAwait(false);
        if (reply.Type is not (RespDataType.Array or RespDataType.Map) || reply.Count % 2 != 0)
            throw new RespireProtocolException("HGETALL must return complete field/value pairs.");
        var fields = new Dictionary<string, string>(reply.Count / 2, StringComparer.Ordinal);
        for (var index = 0; index < reply.Count; index += 2)
        {
            var name = ReadString(reply[index]);
            var value = ReadString(reply[index + 1]);
            if (!fields.TryAdd(name, value))
                throw new RespireProtocolException("HGETALL returned a duplicate field.");
        }
        return fields;
    }

    /// <summary>Reads explicit mapped fields with one HMGET, preserving missing values.</summary>
    public static async ValueTask<Dictionary<string, string?>> ReadPartialAsync(IRespireClient client, RespireKey key,
        string[] fields, string[] mappedFields, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(mappedFields);
        cancellationToken.ThrowIfCancellationRequested();
        if (fields.Length == 0) throw new ArgumentException("Select at least one mapped hash field.", nameof(fields));
        var names = (string[])fields.Clone();
        var selected = new Dictionary<string, string?>(names.Length, StringComparer.Ordinal);
        var arguments = new RespireValue[names.Length + 1];
        arguments[0] = key.Snapshot().AsValue();
        for (var index = 0; index < names.Length; index++)
        {
            var name = names[index];
            if (name is null || Array.IndexOf(mappedFields, name) < 0 || !selected.TryAdd(name, null))
                throw new ArgumentException("Select distinct mapped hash field names, using exact casing.", nameof(fields));
            arguments[index + 1] = name;
        }
        using var reply = await client.ExecuteAsync(RespireCommands.Hash.HMGET, arguments, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (reply.Type != RespDataType.Array || reply.Count != names.Length)
            throw new RespireProtocolException("HMGET must return one value per requested field.");
        for (var index = 0; index < names.Length; index++)
            selected[names[index]] = reply[index].IsNull ? null : ReadString(reply[index]);
        return selected;
    }

    private static string ReadString(RespireResult reply)
        => reply.Type == RespDataType.BulkString ? reply.AsString()
            : throw new RespireProtocolException("Hash fields and values must be bulk strings.");

    private static void ValidateWriteReply(RespireResult reply, int count)
    {
        if (reply.Type != RespDataType.Integer || reply.AsInteger() < 0 || reply.AsInteger() > count)
            throw new RespireProtocolException("Hash writes must return a valid field count.");
    }
}
