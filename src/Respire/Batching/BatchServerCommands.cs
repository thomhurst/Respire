using Respire.Commands;
using Respire.Internal;

namespace Respire;

/// <summary>Server commands queued on a batch or transaction. Mutating administration requires RespireOptions.AllowAdmin.</summary>
/// <remarks>
/// Each command affects only its execution node. Cluster batches route keyless flushes to one
/// discovered primary; transactions use their selected node. Neither form fans out or makes a
/// Cluster-wide operation atomic. Keyless batch commands form a separate group, so their order
/// relative to keyed commands is not guaranteed. Use IServerCommands for immediate flush fan-out.
/// </remarks>
public interface IBatchServerCommands
{
    /// <summary>Queues CLIENT LIST on the execution node, with owned typed results.</summary>
    /// <remarks>IDs and filters refer only to that node. Keyless batches do not pin a previously observed endpoint.</remarks>
    RespirePending<RespireServerClientInfo[]> Clients(RespireClientFilterOptions options);

    /// <summary>Queues CLIENT KILL on the execution node and returns the number killed. Requires AllowAdmin.</summary>
    /// <remarks>SkipMe refers to the batch or transaction socket, not every connection owned by this client.</remarks>
    RespirePending<long> KillClients(RespireClientFilterOptions options);

    /// <summary>Queues FLUSHDB on the execution node. Returns true for OK.</summary>
    RespirePending<bool> FlushDatabase(ServerFlushMode mode = ServerFlushMode.Default);

    /// <summary>Queues FLUSHALL on the execution node. Returns true for OK.</summary>
    RespirePending<bool> FlushAll(ServerFlushMode mode = ServerFlushMode.Default);
}

internal sealed class BatchServerCommands(IPendingSink sink) : IBatchServerCommands
{
    public RespirePending<RespireServerClientInfo[]> Clients(RespireClientFilterOptions options)
        => sink.Add<CmdN, RespireServerClientInfo[]>("CLIENT LIST", ClientFilterArguments.Build(options, false),
            static (_, value) => ServerCommands.ParseClientList(in value));

    public RespirePending<long> KillClients(RespireClientFilterOptions options)
    {
        ServerCommands.EnsureAdminAllowed(sink.Client, "CLIENT KILL");
        return sink.Add<CmdN, long>("CLIENT KILL", ClientFilterArguments.Build(options, true),
            static (_, value) => value.AsInteger());
    }

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
