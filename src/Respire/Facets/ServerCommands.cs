using System.Globalization;
using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;

namespace Respire;

/// <summary>A client row returned by CLIENT LIST.</summary>
public sealed record RespireServerClientInfo(
    long Id,
    string Address,
    string? Name,
    int Database,
    string Flags,
    string Command,
    string? User,
    long AgeSeconds,
    long IdleSeconds,
    IReadOnlyDictionary<string, string> Attributes);

/// <summary>A row returned by SLOWLOG GET.</summary>
public readonly record struct RespireSlowLogEntry(
    long Id,
    DateTimeOffset Timestamp,
    TimeSpan Duration,
    string[] Command,
    string? ClientAddress,
    string? ClientName);

/// <summary>A row returned by LATENCY LATEST.</summary>
public readonly record struct RespireLatencySample(
    string Event,
    DateTimeOffset Latest,
    TimeSpan LatestLatency,
    TimeSpan MaxLatency);

/// <summary>Top-level MEMORY STATS values keyed by Redis' stat names.</summary>
public sealed record RespireMemoryStats(IReadOnlyDictionary<string, RespireMemoryStatValue> Values)
{
    /// <summary>Gets a named stat, or null when absent.</summary>
    public RespireMemoryStatValue? this[string name]
        => Values.TryGetValue(name, out var value) ? value : null;
}

/// <summary>A MEMORY STATS scalar value or nested stat map.</summary>
public readonly record struct RespireMemoryStatValue(
    string? Scalar,
    IReadOnlyDictionary<string, RespireMemoryStatValue>? Children)
{
    /// <summary>Whether this value contains nested stats.</summary>
    public bool IsMap => Children is not null;

    /// <summary>Parses the scalar as an integer, or returns null.</summary>
    public long? AsInt64
        => long.TryParse(Scalar, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    /// <summary>Parses the scalar as a floating-point number, or returns null.</summary>
    public double? AsDouble
        => double.TryParse(Scalar, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    /// <inheritdoc/>
    public override string ToString()
        => Scalar ?? (Children is null ? string.Empty : $"Map({Children.Count})");
}

/// <summary>A server role returned by Redis ROLE.</summary>
public enum RespireServerRoleKind
{
    /// <summary>An unrecognized role.</summary>
    Unknown,
    /// <summary>A writable master.</summary>
    Master,
    /// <summary>A replica of another server.</summary>
    Replica,
    /// <summary>A Sentinel monitor.</summary>
    Sentinel,
}

/// <summary>A replica row inside a master ROLE reply.</summary>
public readonly record struct RespireReplicaInfo(string Host, int Port, long ReplicationOffset);

/// <summary>The parsed ROLE reply for master, replica, and sentinel servers.</summary>
public sealed record RespireServerRole(
    RespireServerRoleKind Kind,
    string RawRole,
    long? ReplicationOffset,
    RespireReplicaInfo[] Replicas,
    string? MasterHost,
    int? MasterPort,
    string? ReplicationState,
    long? DataReceived,
    string[] MonitoredMasters);

/// <summary>Server administration and introspection commands.</summary>
public interface IServerCommands
{
    /// <summary>The INFO text, optionally one section ("server", "memory", …). Redis: INFO.</summary>
    ValueTask<string> InfoAsync(string? section = null, CancellationToken cancellationToken = default);

    /// <summary>Number of keys in the current database. Redis: DBSIZE.</summary>
    ValueTask<long> DatabaseSizeAsync(CancellationToken cancellationToken = default);

    /// <summary>Deletes every key in the current database. Requires <see cref="RespireOptions.AllowAdmin"/>. Redis: FLUSHDB.</summary>
    ValueTask FlushDatabaseAsync(CancellationToken cancellationToken = default);

    /// <summary>Deletes every key in every database. Requires <see cref="RespireOptions.AllowAdmin"/>. Redis: FLUSHALL.</summary>
    ValueTask FlushAllAsync(CancellationToken cancellationToken = default);

    /// <summary>The server's clock. Redis: TIME.</summary>
    ValueTask<DateTimeOffset> TimeAsync(CancellationToken cancellationToken = default);

    /// <summary>Connected clients parsed from CLIENT LIST.</summary>
    ValueTask<RespireServerClientInfo[]> ClientsAsync(CancellationToken cancellationToken = default);

    /// <summary>Closes a client by server client id using CLIENT KILL ID ... SKIPME yes/no.</summary>
    ValueTask<bool> KillClientAsync(
        long clientId,
        bool skipMe = true,
        CancellationToken cancellationToken = default);

    /// <summary>Slow command log entries. Redis: SLOWLOG GET [count].</summary>
    ValueTask<RespireSlowLogEntry[]> SlowLogAsync(
        long? count = null,
        CancellationToken cancellationToken = default);

    /// <summary>Clears the slow command log. Redis: SLOWLOG RESET.</summary>
    ValueTask ResetSlowLogAsync(CancellationToken cancellationToken = default);

    /// <summary>Latest latency samples. Redis: LATENCY LATEST.</summary>
    ValueTask<RespireLatencySample[]> LatestLatencyAsync(CancellationToken cancellationToken = default);

    /// <summary>Resets all latency events. Redis: LATENCY RESET.</summary>
    ValueTask<long> ResetLatencyAsync(CancellationToken cancellationToken = default);

    /// <summary>Resets one latency event. Redis: LATENCY RESET event.</summary>
    ValueTask<long> ResetLatencyAsync(string eventName, CancellationToken cancellationToken = default);

    /// <summary>Approximate memory used by a key, or null when the key is missing. Redis: MEMORY USAGE.</summary>
    ValueTask<long?> MemoryUsageAsync(
        RespireKey key,
        long? samples = null,
        CancellationToken cancellationToken = default);

    /// <summary>Server memory counters. Redis: MEMORY STATS.</summary>
    ValueTask<RespireMemoryStats> MemoryStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>The server replication role. Redis: ROLE.</summary>
    ValueTask<RespireServerRole> RoleAsync(CancellationToken cancellationToken = default);

    /// <summary>The last successful save timestamp. Redis: LASTSAVE.</summary>
    ValueTask<DateTimeOffset> LastSaveAsync(CancellationToken cancellationToken = default);

    /// <summary>Number of commands known by the server. Redis: COMMAND COUNT.</summary>
    ValueTask<long> CommandCountAsync(CancellationToken cancellationToken = default);

    /// <summary>Command names known by the server. Redis: COMMAND LIST.</summary>
    ValueTask<string[]> CommandListAsync(CancellationToken cancellationToken = default);

    /// <summary>Configuration values matching a glob pattern. Redis: CONFIG GET.</summary>
    ValueTask<Dictionary<string, string>> ConfigAsync(string pattern, CancellationToken cancellationToken = default);

    /// <summary>Sets a configuration value. Requires <see cref="RespireOptions.AllowAdmin"/>. Redis: CONFIG SET.</summary>
    ValueTask SetConfigAsync(string name, RespireValue value, CancellationToken cancellationToken = default);
}

internal sealed class ServerCommands(RespireClient client) : IServerCommands
{
    public ValueTask<string> InfoAsync(string? section = null, CancellationToken cancellationToken = default)
        => section is null
            ? client.StringAsync("INFO", new Cmd(Verbs.Info), cancellationToken)
            : client.StringAsync("INFO", new Cmd1(Verbs.Info, section), cancellationToken);

    public ValueTask<long> DatabaseSizeAsync(CancellationToken cancellationToken = default)
        => client.Core.Cluster is null
            ? client.IntegerAsync("DBSIZE", new RawCommand(RespCommands.DbSize), cancellationToken)
            : DatabaseSizeClusterAsync(cancellationToken);

    public ValueTask FlushDatabaseAsync(CancellationToken cancellationToken = default)
    {
        EnsureAdminAllowed("FLUSHDB");
        return client.Core.Cluster is null
            ? client.OkAsync("FLUSHDB", new RawCommand(RespCommands.FlushDb), cancellationToken)
            : FlushClusterAsync("FLUSHDB", RespCommands.FlushDb, cancellationToken);
    }

    public ValueTask FlushAllAsync(CancellationToken cancellationToken = default)
    {
        EnsureAdminAllowed("FLUSHALL");
        return client.Core.Cluster is null
            ? client.OkAsync("FLUSHALL", new RawCommand(RespCommands.FlushAll), cancellationToken)
            : FlushClusterAsync("FLUSHALL", RespCommands.FlushAll, cancellationToken);
    }

    public ValueTask<RespireServerClientInfo[]> ClientsAsync(CancellationToken cancellationToken = default)
        => ConvertAsync(
            "CLIENT LIST", new Cmd(Verbs.ClientList), cancellationToken,
            static (ServerCommands _, in RespValue value) => ParseClientList(in value));

    public ValueTask<bool> KillClientAsync(
        long clientId,
        bool skipMe = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(clientId);
        return client.FlagAsync(
            "CLIENT KILL",
            new Cmd4(Verbs.ClientKill, "ID", clientId, "SKIPME", skipMe ? "yes" : "no"),
            cancellationToken);
    }

    public ValueTask<RespireSlowLogEntry[]> SlowLogAsync(
        long? count = null,
        CancellationToken cancellationToken = default)
    {
        if (count is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        return count is { } limit
            ? ConvertAsync(
                "SLOWLOG GET", new Cmd1(Verbs.SlowLogGet, limit), cancellationToken,
                static (ServerCommands _, in RespValue value) => ParseSlowLog(in value))
            : ConvertAsync(
                "SLOWLOG GET", new Cmd(Verbs.SlowLogGet), cancellationToken,
                static (ServerCommands _, in RespValue value) => ParseSlowLog(in value));
    }

    public ValueTask ResetSlowLogAsync(CancellationToken cancellationToken = default)
        => client.OkAsync("SLOWLOG RESET", new Cmd(Verbs.SlowLogReset), cancellationToken);

    public ValueTask<RespireLatencySample[]> LatestLatencyAsync(CancellationToken cancellationToken = default)
        => ConvertAsync(
            "LATENCY LATEST", new Cmd(Verbs.LatencyLatest), cancellationToken,
            static (ServerCommands _, in RespValue value) => ParseLatencyLatest(in value));

    public ValueTask<long> ResetLatencyAsync(CancellationToken cancellationToken = default)
        => client.IntegerAsync("LATENCY RESET", new Cmd(Verbs.LatencyReset), cancellationToken);

    public ValueTask<long> ResetLatencyAsync(string eventName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        return client.IntegerAsync("LATENCY RESET", new Cmd1(Verbs.LatencyReset, eventName), cancellationToken);
    }

    public ValueTask<long?> MemoryUsageAsync(
        RespireKey key,
        long? samples = null,
        CancellationToken cancellationToken = default)
    {
        if (samples is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(samples));
        }

        return samples is { } sampleCount
            ? client.IntegerOrNullAsync(
                "MEMORY USAGE",
                new Cmd3(Verbs.MemoryUsage, client.Key(in key), "SAMPLES", sampleCount),
                cancellationToken)
            : client.IntegerOrNullAsync(
                "MEMORY USAGE",
                new Cmd1(Verbs.MemoryUsage, client.Key(in key)),
                cancellationToken);
    }

    public ValueTask<RespireMemoryStats> MemoryStatsAsync(CancellationToken cancellationToken = default)
        => ConvertAsync(
            "MEMORY STATS", new Cmd(Verbs.MemoryStats), cancellationToken,
            static (ServerCommands _, in RespValue value) => new RespireMemoryStats(ParseMemoryMap(value.AsArray())));

    public ValueTask<RespireServerRole> RoleAsync(CancellationToken cancellationToken = default)
        => ConvertAsync(
            "ROLE", new Cmd(Verbs.Role), cancellationToken,
            static (ServerCommands _, in RespValue value) => ParseRole(in value));

    public ValueTask<DateTimeOffset> LastSaveAsync(CancellationToken cancellationToken = default)
        => ConvertAsync(
            "LASTSAVE", new Cmd(Verbs.LastSave), cancellationToken,
            static (ServerCommands _, in RespValue value) => FromUnixTimeSeconds(ReadInt64(in value)));

    public ValueTask<long> CommandCountAsync(CancellationToken cancellationToken = default)
        => client.IntegerAsync("COMMAND COUNT", new Cmd(Verbs.CommandCount), cancellationToken);

    public ValueTask<string[]> CommandListAsync(CancellationToken cancellationToken = default)
        => client.StringArrayAsync("COMMAND LIST", new Cmd(Verbs.CommandList), cancellationToken);

    private async ValueTask<long> DatabaseSizeClusterAsync(CancellationToken cancellationToken)
    {
        var connections = await client.Core.Cluster!.GetMasterConnectionsAsync(cancellationToken).ConfigureAwait(false);
        long total = 0;
        foreach (var connection in connections)
        {
            var reply = await client.SendOnConnectionAsync(
                    "DBSIZE", connection, new RawCommand(RespCommands.DbSize), cancellationToken)
                .ConfigureAwait(false);
            try
            {
                total = checked(total + ResponseReader.Integer(in reply));
            }
            finally
            {
                reply.Dispose();
            }
        }

        return total;
    }

    private async ValueTask FlushClusterAsync(
        string operation,
        byte[] command,
        CancellationToken cancellationToken)
    {
        var connections = await client.Core.Cluster!.GetMasterConnectionsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var connection in connections)
        {
            var reply = await client.SendOnConnectionAsync(
                    operation, connection, new RawCommand(command), cancellationToken)
                .ConfigureAwait(false);
            try
            {
                ResponseReader.ExpectOk(in reply);
            }
            finally
            {
                reply.Dispose();
            }
        }
    }

    public async ValueTask<DateTimeOffset> TimeAsync(CancellationToken cancellationToken = default)
    {
        var reply = await client.SendAsync("TIME", new RawCommand(RespCommands.Time), cancellationToken).ConfigureAwait(false);
        try
        {
            var parts = reply.AsArray();
            var seconds = parts.Length > 0 ? ReadInt64(in parts[0]) : 0;
            var microseconds = parts.Length > 1 ? ReadInt64(in parts[1]) : 0;
            return FromUnixTimeSeconds(seconds).Add(FromMicroseconds(microseconds));
        }
        finally
        {
            reply.Dispose();
        }
    }

    public ValueTask<Dictionary<string, string>> ConfigAsync(string pattern, CancellationToken cancellationToken = default)
        => client.StringMapAsync("CONFIG GET", new Cmd1(Verbs.ConfigGet, pattern), cancellationToken);

    public ValueTask SetConfigAsync(string name, RespireValue value, CancellationToken cancellationToken = default)
    {
        EnsureAdminAllowed("CONFIG SET");
        return client.OkAsync("CONFIG SET", new Cmd2(Verbs.ConfigSet, name, value), cancellationToken);
    }

    private async ValueTask<TResult> ConvertAsync<TCommand, TResult>(
        string operation,
        TCommand command,
        CancellationToken cancellationToken,
        ResponseConverter<ServerCommands, TResult> converter)
        where TCommand : struct, IRespCommand
    {
        var reply = await client.SendAsync(operation, command, cancellationToken).ConfigureAwait(false);
        try
        {
            return converter(this, in reply);
        }
        finally
        {
            reply.Dispose();
        }
    }

    private static RespireServerClientInfo[] ParseClientList(in RespValue value)
    {
        var text = value.AsString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var clients = new RespireServerClientInfo[lines.Length];
        for (var i = 0; i < lines.Length; i++)
        {
            var attributes = ParseClientAttributes(lines[i]);
            clients[i] = new RespireServerClientInfo(
                ReadAttributeInt64(attributes, "id"),
                ReadAttributeString(attributes, "addr") ?? string.Empty,
                ReadAttributeString(attributes, "name"),
                ToInt32(ReadAttributeInt64(attributes, "db")),
                ReadAttributeString(attributes, "flags") ?? string.Empty,
                ReadAttributeString(attributes, "cmd") ?? string.Empty,
                ReadAttributeString(attributes, "user"),
                ReadAttributeInt64(attributes, "age"),
                ReadAttributeInt64(attributes, "idle"),
                attributes);
        }

        return clients;
    }

    private static Dictionary<string, string> ParseClientAttributes(string line)
    {
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var attributes = new Dictionary<string, string>(fields.Length, StringComparer.Ordinal);
        foreach (var field in fields)
        {
            var separator = field.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            attributes[field[..separator]] = field[(separator + 1)..];
        }

        return attributes;
    }

    private static RespireSlowLogEntry[] ParseSlowLog(in RespValue value)
    {
        var entries = value.AsArray();
        if (entries.Length == 0)
        {
            return [];
        }

        var result = new RespireSlowLogEntry[entries.Length];
        for (var i = 0; i < entries.Length; i++)
        {
            var parts = entries[i].AsArray();
            result[i] = new RespireSlowLogEntry(
                parts.Length > 0 ? ReadInt64(in parts[0]) : 0,
                parts.Length > 1 ? FromUnixTimeSeconds(ReadInt64(in parts[1])) : DateTimeOffset.UnixEpoch,
                parts.Length > 2 ? FromMicroseconds(ReadInt64(in parts[2])) : TimeSpan.Zero,
                parts.Length > 3 ? ParseStringArray(in parts[3]) : [],
                parts.Length > 4 ? ReadOptionalString(in parts[4]) : null,
                parts.Length > 5 ? ReadOptionalString(in parts[5]) : null);
        }

        return result;
    }

    private static RespireLatencySample[] ParseLatencyLatest(in RespValue value)
    {
        var entries = value.AsArray();
        if (entries.Length == 0)
        {
            return [];
        }

        var result = new RespireLatencySample[entries.Length];
        for (var i = 0; i < entries.Length; i++)
        {
            var parts = entries[i].AsArray();
            result[i] = new RespireLatencySample(
                parts.Length > 0 ? parts[0].AsString() : string.Empty,
                parts.Length > 1 ? FromUnixTimeSeconds(ReadInt64(in parts[1])) : DateTimeOffset.UnixEpoch,
                parts.Length > 2 ? FromMilliseconds(ReadInt64(in parts[2])) : TimeSpan.Zero,
                parts.Length > 3 ? FromMilliseconds(ReadInt64(in parts[3])) : TimeSpan.Zero);
        }

        return result;
    }

    private static Dictionary<string, RespireMemoryStatValue> ParseMemoryMap(ReadOnlySpan<RespValue> elements)
    {
        var result = new Dictionary<string, RespireMemoryStatValue>(elements.Length / 2, StringComparer.Ordinal);
        for (var i = 0; i + 1 < elements.Length; i += 2)
        {
            result[elements[i].AsString()] = ParseMemoryValue(in elements[i + 1]);
        }

        return result;
    }

    private static RespireMemoryStatValue ParseMemoryValue(in RespValue value)
        => value.Type is RespDataType.Array or RespDataType.Map
            ? new RespireMemoryStatValue(null, ParseMemoryMap(value.AsArray()))
            : new RespireMemoryStatValue(ReadScalarString(in value), null);

    private static RespireServerRole ParseRole(in RespValue value)
    {
        var parts = value.AsArray();
        if (parts.Length == 0)
        {
            return new RespireServerRole(
                RespireServerRoleKind.Unknown, string.Empty, null, [], null, null, null, null, []);
        }

        var rawRole = parts[0].AsString();
        if (rawRole.Equals("master", StringComparison.OrdinalIgnoreCase))
        {
            return new RespireServerRole(
                RespireServerRoleKind.Master,
                rawRole,
                parts.Length > 1 ? ReadInt64(in parts[1]) : null,
                parts.Length > 2 ? ParseReplicas(in parts[2]) : [],
                null,
                null,
                null,
                null,
                []);
        }

        if (rawRole.Equals("slave", StringComparison.OrdinalIgnoreCase)
            || rawRole.Equals("replica", StringComparison.OrdinalIgnoreCase))
        {
            return new RespireServerRole(
                RespireServerRoleKind.Replica,
                rawRole,
                null,
                [],
                parts.Length > 1 ? ReadOptionalString(in parts[1]) : null,
                parts.Length > 2 ? ToInt32(ReadInt64(in parts[2])) : null,
                parts.Length > 3 ? ReadOptionalString(in parts[3]) : null,
                parts.Length > 4 ? ReadInt64(in parts[4]) : null,
                []);
        }

        if (rawRole.Equals("sentinel", StringComparison.OrdinalIgnoreCase))
        {
            return new RespireServerRole(
                RespireServerRoleKind.Sentinel,
                rawRole,
                null,
                [],
                null,
                null,
                null,
                null,
                parts.Length > 1 ? ParseStringArray(in parts[1]) : []);
        }

        return new RespireServerRole(
            RespireServerRoleKind.Unknown, rawRole, null, [], null, null, null, null, []);
    }

    private static RespireReplicaInfo[] ParseReplicas(in RespValue value)
    {
        var entries = value.AsArray();
        if (entries.Length == 0)
        {
            return [];
        }

        var replicas = new List<RespireReplicaInfo>(entries.Length);
        foreach (var entry in entries)
        {
            var parts = entry.AsArray();
            if (parts.Length < 3)
            {
                continue;
            }

            replicas.Add(new RespireReplicaInfo(
                parts[0].AsString(),
                ToInt32(ReadInt64(in parts[1])),
                ReadInt64(in parts[2])));
        }

        return replicas.ToArray();
    }

    private static string[] ParseStringArray(in RespValue value)
    {
        var elements = value.AsArray();
        if (elements.Length == 0)
        {
            return value.Type is RespDataType.Array or RespDataType.Set ? [] : [value.AsString()];
        }

        var result = new string[elements.Length];
        for (var i = 0; i < elements.Length; i++)
        {
            result[i] = elements[i].AsString();
        }

        return result;
    }

    private static string? ReadAttributeString(IReadOnlyDictionary<string, string> attributes, string name)
        => attributes.TryGetValue(name, out var value) && value.Length > 0 ? value : null;

    private static long ReadAttributeInt64(IReadOnlyDictionary<string, string> attributes, string name)
        => attributes.TryGetValue(name, out var value)
           && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;

    private static long ReadInt64(in RespValue value)
    {
        if (value.Type == RespDataType.Integer)
        {
            return value.AsInteger();
        }

        if (long.TryParse(value.AsString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
        {
            return integer;
        }

        return double.TryParse(value.AsString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? (long)number
            : 0;
    }

    private static string? ReadOptionalString(in RespValue value)
    {
        if (value.IsNull)
        {
            return null;
        }

        var text = value.AsString();
        return text.Length == 0 ? null : text;
    }

    private static string? ReadScalarString(in RespValue value)
        => value.Type switch
        {
            RespDataType.Null => null,
            RespDataType.Integer => value.AsInteger().ToString(CultureInfo.InvariantCulture),
            RespDataType.Double => value.AsDouble().ToString(CultureInfo.InvariantCulture),
            RespDataType.Boolean => value.AsBoolean() ? "true" : "false",
            _ => value.AsString(),
        };

    private static DateTimeOffset FromUnixTimeSeconds(long seconds)
    {
        if (seconds <= DateTimeOffset.MinValue.ToUnixTimeSeconds())
        {
            return DateTimeOffset.MinValue;
        }

        if (seconds >= DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            return DateTimeOffset.MaxValue;
        }

        return DateTimeOffset.FromUnixTimeSeconds(seconds);
    }

    private static TimeSpan FromMicroseconds(long microseconds)
        => FromTicksSaturating(microseconds, TimeSpan.TicksPerMicrosecond);

    private static TimeSpan FromMilliseconds(long milliseconds)
        => FromTicksSaturating(milliseconds, TimeSpan.TicksPerMillisecond);

    private static TimeSpan FromTicksSaturating(long value, long ticksPerUnit)
    {
        if (value > long.MaxValue / ticksPerUnit)
        {
            return TimeSpan.MaxValue;
        }

        if (value < long.MinValue / ticksPerUnit)
        {
            return TimeSpan.MinValue;
        }

        return TimeSpan.FromTicks(value * ticksPerUnit);
    }

    private static int ToInt32(long value)
        => value > int.MaxValue ? int.MaxValue : value < int.MinValue ? int.MinValue : (int)value;

    private void EnsureAdminAllowed(string operation)
    {
        if (!client.Core.Options.AllowAdmin)
        {
            throw new NotSupportedException(
                $"{operation} is disabled by default because it is an administrative command. " +
                $"Set {nameof(RespireOptions)}.{nameof(RespireOptions.AllowAdmin)} to true to enable it.");
        }
    }
}
