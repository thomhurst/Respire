using Respire.Networking;

namespace Respire.Internal;

internal sealed partial class ClusterRouter
{
    internal ReadLatencySampler<RespireConnection>? NearestLatency;
    private object? _nearestGate;
    private int _nearestCursor;
    private HashSet<int>? _nearestReplicaDiscoveries;

    private async ValueTask<RespireConnection> GetNearestReadConnectionAsync(
        int slot, CancellationToken cancellationToken, DiscoveryRound? discovery, bool retry = true,
        long? samplingDeadline = null, Exception? previousFailure = null)
    {
        var deadline = samplingDeadline ?? NearestReadSelection.CreateDeadline();
        var sampler = LazyInitializer.EnsureInitialized(ref NearestLatency, ref _nearestGate, static () => ReadLatencySampler.Create());
        if (Volatile.Read(ref _disposed) != 0)
        {
            await sampler.DisposeAsync().ConfigureAwait(false);
            throw new ObjectDisposedException(nameof(ClusterRouter));
        }
        RespireConnection? primary = null;
        Exception? lastError = previousFailure;
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
        var route = RoutingSnapshot[slot];
        owner = route.Primary;
        if (primary is not null && !ReferenceEquals(primary.Multiplexer, owner))
        {
            // Retry a replacement owner before unknown replica coverage can block this read.
            if (retry)
                return await GetNearestReadConnectionAsync(slot, cancellationToken, discovery, retry: false,
                    samplingDeadline: deadline, previousFailure: lastError).ConfigureAwait(false);
            primary = null;
        }
        var routes = route.Replicas;
        if (routes is null || ReferenceEquals(routes, _unknownReplicaRoutes))
        {
            routes = null;
            if (primary is not null)
            {
                StartNearestReplicaDiscovery(slot);
            }
            else
            {
                try { routes = await GetReplicaRoutesAsync(slot, cancellationToken).ConfigureAwait(false); }
                catch (Exception error) when (IsReadCandidateFailure(error, cancellationToken)) { lastError = error; }
            }
        }

        var nodes = routes?.Nodes ?? [];
        var start = (uint)Interlocked.Increment(ref _nearestCursor);
        var best = new NearestReadSelection<RespireConnection>(start, nodes.Length + 1);
        while (best.TryNext(out var index))
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            var latency = sampler.GetLatencyAsync(connection, default);
            if (connection.IsAcceptingCommands) best.QueueSample(connection, latency);
        }
        using var samplingWait = best.HasPendingSamples
            ? NearestReadSelection.CreateWaitCancellation(deadline, cancellationToken) : null;
        while (best.TryNextSample(out var pending))
        {
            var latency = await NearestReadSelection.GetLatencyAsync(pending.Latency, samplingWait, cancellationToken).ConfigureAwait(false);
            if (pending.Candidate.IsAcceptingCommands) best.Consider(pending.Candidate, latency, pending.Linked, pending.Order);
        }
        if (best.TryGet(out var selected))
        {
            var node = selected.Multiplexer;
            // Sampling may await: revalidate both roles together against the latest publication.
            var currentRoute = RoutingSnapshot[slot];
            if (selected.IsAcceptingCommands && node is { IsRetired: false }
                && (ReferenceEquals(currentRoute.Primary, node) || currentRoute.Replicas?.Nodes.Contains(node) == true))
            {
                if (routes is { IsDueForRevalidation: true })
                    _ = routes.JoinOrStartRefresh(() => RefreshReplicaRoutesAsync(slot));
                return selected;
            }
        }
        if (retry)
        {
            // A concurrent publication may remove every captured candidate before queueing.
            // Retry that publication directly; otherwise join the range's throttled refresh,
            // which can learn a replacement through another still-healthy master.
            var currentRoute = RoutingSnapshot[slot];
            if (ReferenceEquals(owner, currentRoute.Primary) && ReferenceEquals(routes, currentRoute.Replicas)
                && routes?.JoinOrStartRefresh(() => RefreshReplicaRoutesAsync(slot)) is { } refresh)
                await refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await GetNearestReadConnectionAsync(slot, cancellationToken, discovery, retry: false,
                samplingDeadline: deadline, previousFailure: lastError).ConfigureAwait(false);
        }
        throw new RespireConnectionException($"Redis Cluster slot {slot} has no healthy eligible endpoint for Nearest reads.",
            lastError ?? new InvalidOperationException("The read topology changed during selection."));
    }

    private void StartNearestReplicaDiscovery(int slot)
    {
        lock (_nodesGate)
        {
            // One waiter per uncovered slot; the coordinator still shares and serializes probes.
            if (_disposed != 0 || !(_nearestReplicaDiscoveries ??= []).Add(slot)) return;
        }
        _ = DiscoverNearestReplicasAsync(slot);
    }

    private async Task DiscoverNearestReplicasAsync(int slot)
    {
        try { await GetReplicaRoutesAsync(slot, _stopDiscovery.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_stopDiscovery.IsCancellationRequested) { }
        catch (Exception error) { LogReplicaRefreshFailure(slot, error); }
        finally
        {
            lock (_nodesGate) _nearestReplicaDiscoveries!.Remove(slot);
        }
    }
}
