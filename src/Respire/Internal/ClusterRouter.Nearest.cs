using Respire.Networking;

namespace Respire.Internal;

internal sealed partial class ClusterRouter
{
    internal ReadLatencySampler<RespireConnection>? NearestLatency;
    private object? _nearestGate;
    private int _nearestCursor;

    private async ValueTask<RespireConnection> GetNearestReadConnectionAsync(
        int slot, CancellationToken cancellationToken, DiscoveryRound? discovery, bool retry = true)
    {
        var sampler = LazyInitializer.EnsureInitialized(ref NearestLatency, ref _nearestGate, static () => ReadLatencySampler.Create());
        if (Volatile.Read(ref _disposed) != 0)
        {
            await sampler.DisposeAsync().ConfigureAwait(false);
            throw new ObjectDisposedException(nameof(ClusterRouter));
        }
        RespireConnection? primary = null;
        Exception? lastError = null;
        var owner = GetKnownSlotOwner(slot);
        if (owner is null || sampler.CanConnect(owner))
        {
            try
            {
                primary = await GetConnectionAsync(slot, cancellationToken, discovery).ConfigureAwait(false);
                if (primary.Multiplexer is { } connected) sampler.ConnectionSucceeded(connected);
            }
            catch (Exception error) when (IsReadCandidateFailure(error, cancellationToken))
            {
                lastError = error;
                if (owner is not null) sampler.ConnectionFailed(owner);
            }
        }
        var routes = GetKnownReplicas(slot);
        if (routes is null || ReferenceEquals(routes, _unknownReplicaRoutes))
        {
            routes = null;
            try { routes = await GetReplicaRoutesAsync(slot, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (IsReadCandidateFailure(error, cancellationToken)) { lastError = error; }
        }

        var nodes = routes?.Nodes ?? [];
        var best = new NearestReadSelection<RespireConnection>();
        var start = (uint)Interlocked.Increment(ref _nearestCursor);
        for (var offset = 0; offset <= nodes.Length; offset++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = (int)((start + (uint)offset) % (uint)(nodes.Length + 1));
            RespireConnection connection;
            if (index == 0)
            {
                if (primary is null) continue;
                connection = primary;
            }
            else
            {
                var node = nodes[index - 1];
                if (node.IsRetired || !sampler.CanConnect(node)) continue;
                try
                {
                    await EnsureRouteNodeConnectedAsync(node, cancellationToken, discovery).ConfigureAwait(false);
                    if (node.IsRetired) continue;
                    connection = node.GetConnection(slot);
                    sampler.ConnectionSucceeded(node);
                }
                catch (Exception error) when (IsReadCandidateFailure(error, cancellationToken))
                {
                    lastError = error;
                    sampler.ConnectionFailed(node);
                    continue;
                }
            }
            if (!connection.IsAcceptingCommands) continue;
            var latency = await sampler.GetLatencyAsync(connection, cancellationToken).ConfigureAwait(false);
            if (connection.IsAcceptingCommands) best.Consider(connection, latency);
        }
        if (best.TryGet(out var selected))
        {
            var node = selected.Multiplexer;
            if (selected.IsAcceptingCommands && node is { IsRetired: false }
                && (ReferenceEquals(GetKnownSlotOwner(slot), node) || GetKnownReplicas(slot)?.Nodes.Contains(node) == true))
            {
                if (routes is { IsDueForRevalidation: true })
                    _ = routes.JoinOrStartRefresh(() => RefreshReplicaRoutesAsync(slot));
                return selected;
            }
            if (retry) return await GetNearestReadConnectionAsync(slot, cancellationToken, discovery, retry: false).ConfigureAwait(false);
        }
        throw new RespireConnectionException($"Redis Cluster slot {slot} has no healthy eligible endpoint for Nearest reads.",
            lastError ?? new InvalidOperationException("The read topology changed during selection."));
    }
}
