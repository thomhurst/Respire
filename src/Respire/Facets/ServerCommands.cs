using Respire.Commands;
using Respire.Protocol;

namespace Respire;

/// <summary>Server administration and introspection commands.</summary>
public interface IServerCommands
{
    /// <summary>The INFO text, optionally one section ("server", "memory", …). Redis: INFO.</summary>
    ValueTask<string> InfoAsync(string? section = null, CancellationToken cancellationToken = default);

    /// <summary>Number of keys in the current database. Redis: DBSIZE.</summary>
    ValueTask<long> DatabaseSizeAsync(CancellationToken cancellationToken = default);

    /// <summary>Deletes every key in the current database. Redis: FLUSHDB.</summary>
    ValueTask FlushDatabaseAsync(CancellationToken cancellationToken = default);

    /// <summary>Deletes every key in every database. Redis: FLUSHALL.</summary>
    ValueTask FlushAllAsync(CancellationToken cancellationToken = default);

    /// <summary>The server's clock. Redis: TIME.</summary>
    ValueTask<DateTimeOffset> TimeAsync(CancellationToken cancellationToken = default);

    /// <summary>Configuration values matching a glob pattern. Redis: CONFIG GET.</summary>
    ValueTask<Dictionary<string, string>> ConfigGetAsync(string pattern, CancellationToken cancellationToken = default);

    /// <summary>Sets a configuration value. Redis: CONFIG SET.</summary>
    ValueTask ConfigSetAsync(string name, RespireValue value, CancellationToken cancellationToken = default);
}

internal sealed class ServerCommands(RespireClient client) : IServerCommands
{
    public ValueTask<string> InfoAsync(string? section = null, CancellationToken cancellationToken = default)
        => section is null
            ? client.StringAsync("INFO", new Cmd(Verbs.Info), cancellationToken)
            : client.StringAsync("INFO", new Cmd1(Verbs.Info, section), cancellationToken);

    public ValueTask<long> DatabaseSizeAsync(CancellationToken cancellationToken = default)
        => client.IntegerAsync("DBSIZE", new RawCommand(RespCommands.DbSize), cancellationToken);

    public ValueTask FlushDatabaseAsync(CancellationToken cancellationToken = default)
        => client.OkAsync("FLUSHDB", new RawCommand(RespCommands.FlushDb), cancellationToken);

    public ValueTask FlushAllAsync(CancellationToken cancellationToken = default)
        => client.OkAsync("FLUSHALL", new RawCommand(RespCommands.FlushAll), cancellationToken);

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
        => client.OkAsync("CONFIG SET", new Cmd2(Verbs.ConfigSet, name, value), cancellationToken);
}
