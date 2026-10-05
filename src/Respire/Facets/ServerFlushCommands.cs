using Respire.Commands;

namespace Respire;

/// <summary>Memory reclamation mode for FLUSHDB and FLUSHALL. All modes remove keys logically before replying.</summary>
public enum ServerFlushMode
{
    /// <summary>Uses the server's configured default.</summary>
    Default,
    /// <summary>Reclaims memory synchronously. Requires Redis 6.2 or later.</summary>
    Sync,
    /// <summary>Reclaims memory asynchronously. Requires Redis 4.0 or later.</summary>
    Async,
}

internal sealed partial class ServerCommands
{
    private static readonly Verb FlushDb = new(-1, "FLUSHDB");
    private static readonly Verb FlushDbSync = new(-1, "FLUSHDB", "SYNC");
    private static readonly Verb FlushDbAsync = new(-1, "FLUSHDB", "ASYNC");
    private static readonly Verb FlushAll = new(-1, "FLUSHALL");
    private static readonly Verb FlushAllSync = new(-1, "FLUSHALL", "SYNC");
    private static readonly Verb FlushAllAsyncVerb = new(-1, "FLUSHALL", "ASYNC");

    public ValueTask FlushDatabaseAsync(ServerFlushMode mode, CancellationToken cancellationToken = default)
        => FlushAsync("FLUSHDB", FlushVerb(false, mode), cancellationToken);

    public ValueTask FlushAllAsync(ServerFlushMode mode, CancellationToken cancellationToken = default)
        => FlushAsync("FLUSHALL", FlushVerb(true, mode), cancellationToken);

    private ValueTask FlushAsync(string operation, Verb verb, CancellationToken cancellationToken)
    {
        EnsureAdminAllowed(operation);
        var command = new Cmd(verb);
        return client.Core.Cluster is null
            ? client.OkAsync(operation, command, cancellationToken)
            : FlushClusterAsync(operation, command, cancellationToken);
    }

    internal static Verb FlushVerb(bool allDatabases, ServerFlushMode mode) => (allDatabases, mode) switch
    {
        (false, ServerFlushMode.Default) => FlushDb,
        (false, ServerFlushMode.Sync) => FlushDbSync,
        (false, ServerFlushMode.Async) => FlushDbAsync,
        (true, ServerFlushMode.Default) => FlushAll,
        (true, ServerFlushMode.Sync) => FlushAllSync,
        (true, ServerFlushMode.Async) => FlushAllAsyncVerb,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown flush mode."),
    };
}
