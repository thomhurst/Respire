using System.Runtime.CompilerServices;
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
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<RespireServerClientInfo[]> ClientsAsync(RespireClientFilterOptions options,
        CancellationToken cancellationToken = default)
    {
        using var reply = await SendAsync("CLIENT LIST", ClientFilterArguments.Build(options, false), cancellationToken).ConfigureAwait(false);
        return ServerCommands.ParseClientList(in reply);
    }

    /// <summary>Kills matching clients on this handle's endpoint and returns the count. Requires AllowAdmin.</summary>
    /// <remarks>SkipMe refers to this handle's socket only. Killing it invalidates the handle; it never reconnects.</remarks>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<long> KillClientsAsync(RespireClientFilterOptions options,
        CancellationToken cancellationToken = default)
    {
        EnsureAdmin("CLIENT KILL");
        using var reply = await SendAsync("CLIENT KILL", ClientFilterArguments.Build(options, true), cancellationToken).ConfigureAwait(false);
        return reply.AsInteger();
    }
}
