using System.Runtime.CompilerServices;
using Respire.Commands;
using Respire.Protocol;

namespace Respire;

public partial interface IServerCommands
{
    /// <summary>Lists matching clients on one execution node. IDs are local to that node.</summary>
    /// <remarks>The token is required to preserve ClientsAsync(default). Use a connection handle to select a known endpoint.</remarks>
    ValueTask<RespireServerClientInfo[]> ClientsAsync(RespireClientFilterOptions options, CancellationToken cancellationToken);

    /// <summary>Lists matching clients separately on every discovered Cluster member, including replicas.</summary>
    /// <remarks>The token is required to preserve ClientsOnAllNodesAsync(default). IDs are interpreted independently on each node.</remarks>
    ValueTask<RespireServerResult<RespireServerClientInfo[]>[]> ClientsOnAllNodesAsync(
        RespireClientFilterOptions options, CancellationToken cancellationToken);

    /// <summary>Kills matching clients on one execution node and returns the count. Requires AllowAdmin.</summary>
    /// <remarks>Does not fan out. Use a connection handle for a known endpoint. SkipMe concerns only the executing socket.</remarks>
    ValueTask<long> KillClientsAsync(RespireClientFilterOptions options, CancellationToken cancellationToken = default);
}

internal sealed partial class ServerCommands
{
    public ValueTask<RespireServerClientInfo[]> ClientsAsync(RespireClientFilterOptions options, CancellationToken cancellationToken)
        => ConvertAsync("CLIENT LIST", ClientFilterArguments.Build(options, false), cancellationToken,
            static (ServerCommands _, in RespValue value) => ParseClientList(in value));

    public ValueTask<RespireServerResult<RespireServerClientInfo[]>[]> ClientsOnAllNodesAsync(
        RespireClientFilterOptions options, CancellationToken cancellationToken)
        => FanOutAsync("CLIENT LIST", ClientFilterArguments.Build(options, false), cancellationToken,
            static (ServerCommands _, in RespValue value) => ParseClientList(in value));

    public ValueTask<long> KillClientsAsync(RespireClientFilterOptions options, CancellationToken cancellationToken = default)
    {
        EnsureAdminAllowed("CLIENT KILL");
        return client.IntegerAsync("CLIENT KILL", ClientFilterArguments.Build(options, true), cancellationToken);
    }
}

public sealed partial class RespireServerClientConnection
{
    /// <summary>Lists matching clients on this handle's endpoint, using its original socket.</summary>
    /// <remarks>Options are validated synchronously before I/O.</remarks>
    public ValueTask<RespireServerClientInfo[]> ClientsAsync(RespireClientFilterOptions options,
        CancellationToken cancellationToken = default)
        => ReadClientsAsync(ClientFilterArguments.Build(options, false), cancellationToken);

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<RespireServerClientInfo[]> ReadClientsAsync(CmdN command, CancellationToken cancellationToken)
        => await ConvertAsync("CLIENT LIST", command, cancellationToken,
            static (RespireServerClientConnection _, in RespValue reply) => ServerCommands.ParseClientList(in reply)).ConfigureAwait(false);

    /// <summary>Kills matching clients on this handle's endpoint and returns the count. Requires AllowAdmin.</summary>
    /// <remarks>SkipMe refers to this handle's socket only. Killing it invalidates the handle; it never reconnects.</remarks>
    public ValueTask<long> KillClientsAsync(RespireClientFilterOptions options,
        CancellationToken cancellationToken = default)
    {
        EnsureAdmin("CLIENT KILL");
        return KillClientsCoreAsync(ClientFilterArguments.Build(options, true), cancellationToken);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<long> KillClientsCoreAsync(CmdN command, CancellationToken cancellationToken)
        => await ConvertAsync("CLIENT KILL", command, cancellationToken,
            static (RespireServerClientConnection _, in RespValue reply) => reply.AsInteger()).ConfigureAwait(false);
}
