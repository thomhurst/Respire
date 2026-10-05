using Respire.Commands;
using Respire.Internal;

namespace Respire;

/// <summary>Server flush commands queued on a batch or transaction. Requires RespireOptions.AllowAdmin.</summary>
/// <remarks>
/// Each command affects only its execution node. Cluster batches route these keyless commands to
/// one node; transactions use their selected node. Neither form fans out or makes a Cluster-wide
/// flush atomic. Use IServerCommands for immediate fan-out to all discovered primaries.
/// </remarks>
public interface IBatchServerCommands
{
    /// <summary>Queues FLUSHDB on the execution node. Returns true for OK.</summary>
    RespirePending<bool> FlushDatabase(ServerFlushMode mode = ServerFlushMode.Default);

    /// <summary>Queues FLUSHALL on the execution node. Returns true for OK.</summary>
    RespirePending<bool> FlushAll(ServerFlushMode mode = ServerFlushMode.Default);
}

internal sealed class BatchServerCommands(IPendingSink sink) : IBatchServerCommands
{
    public RespirePending<bool> FlushDatabase(ServerFlushMode mode = ServerFlushMode.Default)
        => Flush("FLUSHDB", false, mode);

    public RespirePending<bool> FlushAll(ServerFlushMode mode = ServerFlushMode.Default)
        => Flush("FLUSHALL", true, mode);

    private RespirePending<bool> Flush(string operation, bool allDatabases, ServerFlushMode mode)
    {
        var verb = ServerCommands.FlushVerb(allDatabases, mode);
        ServerCommands.EnsureAdminAllowed(sink.Client, operation);
        return sink.Add<Cmd, bool>(operation, new Cmd(verb),
            static (_, value) => ResponseReader.Ok(in value));
    }
}
