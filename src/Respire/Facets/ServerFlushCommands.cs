using Respire.Commands;
using Respire.Internal;

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
    private static readonly Verb FlushDbVerb = new(-1, "FLUSHDB");
    private static readonly Verb FlushDbSyncVerb = new(-1, "FLUSHDB", "SYNC");
    private static readonly Verb FlushDbAsyncVerb = new(-1, "FLUSHDB", "ASYNC");
    private static readonly Verb FlushAllVerb = new(-1, "FLUSHALL");
    private static readonly Verb FlushAllSyncVerb = new(-1, "FLUSHALL", "SYNC");
    private static readonly Verb FlushAllAsyncVerb = new(-1, "FLUSHALL", "ASYNC");

    public ValueTask FlushDatabaseAsync(ServerFlushMode mode, CancellationToken cancellationToken)
    {
        var owner = DispatchResponseSource<bool>.Start();
        var observation = owner.Observation;
        try
        {
            return DispatchResponseSource.Complete(owner.Attach(FlushAsync("FLUSHDB", FlushVerb(false, mode), cancellationToken, observation: observation)));
        }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    public ValueTask FlushAllAsync(ServerFlushMode mode, CancellationToken cancellationToken)
    {
        var owner = DispatchResponseSource<bool>.Start();
        var observation = owner.Observation;
        try
        {
            return DispatchResponseSource.Complete(owner.Attach(FlushAsync("FLUSHALL", FlushVerb(true, mode), cancellationToken, observation: observation)));
        }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<bool> FlushAsync(string operation, Verb verb, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation = default)
    {
        EnsureAdminAllowed(operation);
        var command = new Cmd(verb);
        return client.Core.Cluster is null
            ? client.OkResultAsync(operation, command, cancellationToken, observation: observation)
            : DispatchResponseSource.Await(FlushClusterAsync(operation, command, cancellationToken, observation: observation));
    }

    internal static Verb FlushVerb(bool allDatabases, ServerFlushMode mode) => (allDatabases, mode) switch
    {
        (false, ServerFlushMode.Default) => FlushDbVerb,
        (false, ServerFlushMode.Sync) => FlushDbSyncVerb,
        (false, ServerFlushMode.Async) => FlushDbAsyncVerb,
        (true, ServerFlushMode.Default) => FlushAllVerb,
        (true, ServerFlushMode.Sync) => FlushAllSyncVerb,
        (true, ServerFlushMode.Async) => FlushAllAsyncVerb,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown flush mode."),
    };
}
