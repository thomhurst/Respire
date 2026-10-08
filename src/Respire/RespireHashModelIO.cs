using System.ComponentModel;
using Respire.Protocol;

namespace Respire;

/// <summary>Wire operations used by generated hash mappers.</summary>
/// <remarks>Use the generated mapper to validate and encode model properties.</remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class RespireHashModelIO
{
    private static readonly IReadOnlyDictionary<string, long> EmptyFieldTtls = new Dictionary<string, long>();

    /// <summary>Writes present fields, then removes absent mapped fields. The two commands are not atomic.</summary>
    public static ValueTask WriteAsync(IRespireClient client, RespireKey key,
        IReadOnlyDictionary<string, string> fields, string[] mappedFields, CancellationToken cancellationToken = default)
        => WriteAsync(client, key, fields, mappedFields, EmptyFieldTtls,
            RespireHashExpiryMode.HSetEx, cancellationToken);

    /// <summary>Writes mapped fields with explicit field expiries, then removes absent mapped fields.</summary>
    public static ValueTask WriteAsync(IRespireClient client, RespireKey key,
        IReadOnlyDictionary<string, string> fields, string[] mappedFields,
        IReadOnlyDictionary<string, long> fieldTtls, RespireHashExpiryMode expiryMode,
        CancellationToken cancellationToken = default)
        => WriteAsync(client, key, fields, mappedFields, fieldTtls, expiryMode, cancellationToken, null);

    internal static async ValueTask WriteAsync(IRespireClient client, RespireKey key,
        IReadOnlyDictionary<string, string> fields, string[] mappedFields,
        IReadOnlyDictionary<string, long> fieldTtls, RespireHashExpiryMode expiryMode,
        CancellationToken cancellationToken, Action? onWriteStarting)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(mappedFields);
        ArgumentNullException.ThrowIfNull(fieldTtls);
        if (!Enum.IsDefined(expiryMode)) throw new ArgumentOutOfRangeException(nameof(expiryMode));
        cancellationToken.ThrowIfCancellationRequested();
        // Own all inputs across commands, including caller-owned binary key memory.
        var keyValue = key.Snapshot().AsValue();
        var snapshot = new Dictionary<string, string>(fields, StringComparer.Ordinal);
        var names = (string[])mappedFields.Clone();
        var expiries = new Dictionary<string, long>(fieldTtls, StringComparer.Ordinal);
        if (expiries.Values.Any(ttl => ttl <= 0)) throw new ArgumentOutOfRangeException(nameof(fieldTtls));
        var expiring = snapshot.Where(field => expiries.ContainsKey(field.Key))
            .GroupBy(field => expiries[field.Key]).Select(group => (Ttl: group.Key, Fields: group.ToArray())).ToArray();
        var ordinary = snapshot.Where(field => !expiries.ContainsKey(field.Key)).ToArray();
        var writes = new RespireValue[1 + ordinary.Length * 2];
        writes[0] = keyValue;
        var index = 1;
        foreach (var field in snapshot) ArgumentNullException.ThrowIfNull(field.Value);
        foreach (var field in ordinary)
        {
            writes[index++] = field.Key;
            writes[index++] = field.Value;
        }
        var removals = new List<RespireValue> { keyValue };
        foreach (var field in names)
            if (!snapshot.ContainsKey(field)) removals.Add(field);

        // Check every discovered node, without caching capabilities across failover or topology changes.
        if (expiring.Length != 0)
        {
            var command = expiryMode == RespireHashExpiryMode.HSetEx ? RespireCommands.Hash.HSETEX : RespireCommands.Hash.HPEXPIRE;
            var unsupportedMessage = expiryMode == RespireHashExpiryMode.HSetEx
                ? "Generated field expiry requires HSETEX (Redis 8.0+). Opt in to HSetThenExpire for Redis 7.4+. No fields were written."
                : "Generated field expiry requires HPEXPIRE (Redis 7.4+). No fields were written.";
            var nodes = await client.Server.CommandInfoOnAllNodesAsync([command], cancellationToken).ConfigureAwait(false);
            if (nodes.Length == 0) throw new NotSupportedException(unsupportedMessage);
            foreach (var node in nodes)
            {
                if (!node.IsSuccess) throw node.Error!;
                if (node.Value.Length != 1 || node.Value[0] is null)
                    throw new NotSupportedException(unsupportedMessage);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        // Preflight is read-only. Only mutating commands can leave uncertain writes behind.
        if (onWriteStarting is not null && (expiring.Length != 0 || writes.Length > 1 || removals.Count > 1))
        {
            onWriteStarting();
            // Tracked writes must settle before the overlap guard can be released.
            cancellationToken = CancellationToken.None;
        }
        foreach (var group in expiring)
        {
            var pairs = new RespireValue[group.Fields.Length * 2];
            for (var field = 0; field < group.Fields.Length; field++)
            {
                pairs[field * 2] = group.Fields[field].Key;
                pairs[field * 2 + 1] = group.Fields[field].Value;
            }
            if (expiryMode == RespireHashExpiryMode.HSetEx)
            {
                try
                {
                    using var reply = await client.ExecuteAsync(RespireCommands.Hash.HSETEX,
                        [keyValue, "PX", group.Ttl, "FIELDS", group.Fields.Length, .. pairs], cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (reply.Type != RespDataType.Integer || reply.AsInteger() != 1)
                        throw new RespireProtocolException("Unconditional HSETEX must return 1.");
                }
                catch (RespireServerException error) when (error.Message.StartsWith("ERR unknown command 'HSETEX'", StringComparison.OrdinalIgnoreCase))
                {
                    throw new NotSupportedException("Generated field expiry requires HSETEX (Redis 8.0+). Opt in to HSetThenExpire for Redis 7.4+.", error);
                }
            }
            else
            {
                using (var reply = await client.ExecuteAsync(RespireCommands.Hash.HSET,
                    [keyValue, .. pairs], cancellationToken: cancellationToken).ConfigureAwait(false))
                    ValidateWriteReply(reply, group.Fields.Length);
                using var expiry = await client.ExecuteAsync(RespireCommands.Hash.HPEXPIRE,
                    [keyValue, group.Ttl, "FIELDS", group.Fields.Length, .. group.Fields.Select(field => (RespireValue)field.Key)],
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (expiry.Type != RespDataType.Array || expiry.Count != group.Fields.Length)
                    throw new RespireProtocolException("HPEXPIRE must return one status per field.");
                for (var field = 0; field < expiry.Count; field++)
                {
                    if (expiry[field].Type != RespDataType.Integer)
                        throw new RespireProtocolException("HPEXPIRE must return integer field statuses.");
                    var status = expiry[field].AsInteger();
                    if (status is not (-2 or 0 or 1 or 2))
                        throw new RespireProtocolException($"HPEXPIRE returned unknown field status {status}.");
                    if (status != 1)
                        throw new InvalidOperationException($"HPEXPIRE did not apply expiry to field '{group.Fields[field].Key}' (status {status}). The field may have been removed concurrently.");
                }
            }
        }

        if (writes.Length > 1)
        {
            using var reply = await client.ExecuteAsync(RespireCommands.Hash.HSET, writes, cancellationToken: cancellationToken).ConfigureAwait(false);
            ValidateWriteReply(reply, ordinary.Length);
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
