using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;

namespace Respire;

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

    /// <summary>Configuration values matching a glob pattern. Redis: CONFIG GET.</summary>
    ValueTask<Dictionary<string, string>> ConfigGetAsync(string pattern, CancellationToken cancellationToken = default);

    /// <summary>Sets a configuration value. Requires <see cref="RespireOptions.AllowAdmin"/>. Redis: CONFIG SET.</summary>
    ValueTask ConfigSetAsync(string name, RespireValue value, CancellationToken cancellationToken = default);
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
        var parts = reply.AsArray();
        var seconds = long.Parse(parts[0].AsString());
        var microseconds = long.Parse(parts[1].AsString());
        reply.Dispose();
        return DateTimeOffset.FromUnixTimeSeconds(seconds).AddTicks(microseconds * TimeSpan.TicksPerMicrosecond);
    }

    public ValueTask<Dictionary<string, string>> ConfigGetAsync(string pattern, CancellationToken cancellationToken = default)
        => client.StringMapAsync("CONFIG GET", new Cmd1(Verbs.ConfigGet, pattern), cancellationToken);

    public ValueTask ConfigSetAsync(string name, RespireValue value, CancellationToken cancellationToken = default)
    {
        EnsureAdminAllowed("CONFIG SET");
        return client.OkAsync("CONFIG SET", new Cmd2(Verbs.ConfigSet, name, value), cancellationToken);
    }

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
