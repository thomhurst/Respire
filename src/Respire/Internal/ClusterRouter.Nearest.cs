using Respire.Infrastructure;
using Respire.Networking;

namespace Respire.Internal;

internal sealed partial class ClusterRouter
{
    internal ReadLatencySampler<RespireConnection>? NearestLatency;
    internal RespireReadRoutingSnapshot? LastReadSelection;
    // LazyInitializer's ref-object overload uses Monitor internally; do not replace with Lock.
    private object? _nearestGate;
    private int _nearestCursor;
    private HashSet<int>? _nearestReplicaDiscoveries;

    private async ValueTask<RespireConnection> GetNearestReadConnectionAsync(
        int slot, CancellationToken cancellationToken, DiscoveryRound? discovery, RespireTelemetry.ErrorObservation observation, bool retry = true,
        long? samplingDeadline = null, Exception? previousFailure = null,
        HashSet<RespireConnectionMultiplexer>? excluded = null, RespireReadFrom readFrom = RespireReadFrom.Nearest,
        RespireConnection? excludedPeer = null, RespireReadFrom? snapshotReadFrom = null)
    {
        var preferredZone = ReadFallbackPolicy.UsesAvailabilityZone(readFrom) ? _options.ClientAvailabilityZone : null;
        var includePrimary = readFrom is RespireReadFrom.Nearest or RespireReadFrom.AzAffinityReplicasAndPrimary;
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
        if (readFrom == RespireReadFrom.Nearest)
        {
            var candidate = await GetNearestPrimaryAsync(slot, sampler, cancellationToken, discovery, observation,
                preferredZone, excluded, excludedPeer).ConfigureAwait(false);
            primary = candidate.Primary;
            lastError = ConnectionFailures.Combine(lastError, candidate.Failure);
        }
        var route = RoutingSnapshot[slot];
        owner = route.Primary;
        if (primary is not null && !ReferenceEquals(primary.Multiplexer, owner))
        {
            // Retry a replacement owner before unknown replica coverage can block this read.
            if (retry)
                return await GetNearestReadConnectionAsync(slot, cancellationToken, discovery, observation, retry: false,
                    samplingDeadline: deadline, previousFailure: lastError, excluded: excluded, readFrom: readFrom,
                    excludedPeer: excludedPeer, snapshotReadFrom: snapshotReadFrom).ConfigureAwait(false);
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
                catch (Exception error) when (IsReadCandidateFailure(error, cancellationToken))
                {
                    ReadEndpointRouter.RecordCandidateFailure(observation, error);
                    lastError = ConnectionFailures.Combine(lastError, error);
                }
            }
        }

        var nodes = routes?.Nodes ?? [];
        var start = _options.ReplicaLatencyTolerance is not null && routes is not null
            ? routes.NextLatencyStart() : (uint)Interlocked.Increment(ref _nearestCursor);
        var best = new NearestReadSelection<RespireConnection>(start, nodes.Length + 1, _options.ReplicaLatencyTolerance);
        while (best.TryNext(out var index))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RespireConnection connection;
            if (index == 0)
            {
                if (primary is null || primary.Multiplexer is { } primaryOwner && excluded?.Contains(primaryOwner) == true) continue;
                // The slot-affinity socket can have a pending probe while a sibling is idle. With no
                // idle sibling, keep that socket so its probe can still answer within the budget.
                connection = primary.Multiplexer is { } multiplexer
                    ? NearestReadSelection.AvoidPendingProbe(multiplexer, primary, sampler, preferredZone) ?? primary : primary;
            }
            else
            {
                var node = nodes[index - 1];
                if (node.IsRetired || excluded?.Contains(node) == true || ReferenceEquals(node, excludedPeer?.Multiplexer)
                    || !sampler.CanConnect(node)) continue;
                try
                {
                    observation.UseEndpoint(node.ActiveConnectionEndpoint, cancellationToken);
                    await EnsureRouteNodeConnectedAsync(node, cancellationToken, discovery).ConfigureAwait(false);
                    if (node.IsRetired) continue;
                    sampler.ConnectionSucceeded(node);
                    var affinity = GetNodeReadConnection(node, slot, readFrom);
                    connection = NearestReadSelection.AvoidPendingProbe(node, affinity, sampler, preferredZone) ?? affinity;
                }
                catch (Exception error) when (IsReadCandidateFailure(error, cancellationToken))
                {
                    ReadEndpointRouter.RecordCandidateFailure(observation, error, observation.Endpoint);
                    lastError = ConnectionFailures.Combine(lastError, ConnectionFailures.AtEndpoint(error, node.ActiveConnectionEndpoint));
                    sampler.ConnectionFailed(node);
                    continue;
                }
            }
            if (!connection.IsAcceptingCommands
                || excludedPeer is not null && !HedgedReadPolicy.IsDifferentPeer(excludedPeer, connection)) continue;
            var latency = sampler.GetLatencyAsync(connection, default, probeDeadline: deadline);
            if (connection.IsAcceptingCommands) best.QueueSample(connection, latency,
                priority: ReadFallbackPolicy.LatencyPriority(readFrom, index == 0,
                    ReadFallbackPolicy.IsSameZone(connection, preferredZone), linked: true));
        }
        using var samplingWait = best.HasPendingSamples
            ? NearestReadSelection.CreateWaitCancellation(deadline, cancellationToken) : null;
        while (best.TryNextSample(out var pending))
        {
            var latency = await NearestReadSelection.GetLatencyAsync(pending.Latency, samplingWait, cancellationToken)
                .ConfigureAwait(false);
            if (pending.Candidate.IsAcceptingCommands)
                best.Consider(pending.Candidate, latency, pending.Linked, pending.Order, pending.Priority);
        }
        if (!best.HasPreferredCandidate
            && ReadFallbackPolicy.ShouldProbeLocalPrimary(readFrom, GetKnownSlotOwner(slot), preferredZone))
        {
            var candidate = await GetNearestPrimaryAsync(slot, sampler, cancellationToken, discovery, observation,
                preferredZone, excluded, excludedPeer).ConfigureAwait(false);
            lastError = ConnectionFailures.Combine(lastError, candidate.Failure);
            if (candidate.Primary is { } fallback)
            {
                var connection = fallback.Multiplexer is { } multiplexer
                    ? NearestReadSelection.AvoidPendingProbe(multiplexer, fallback, sampler, preferredZone) ?? fallback : fallback;
                using var wait = NearestReadSelection.CreateWaitCancellation(deadline, cancellationToken);
                var latency = await NearestReadSelection.GetLatencyAsync(
                    sampler.GetLatencyAsync(connection, default, probeDeadline: deadline), wait, cancellationToken).ConfigureAwait(false);
                if (connection.IsAcceptingCommands)
                    best.Consider(connection, latency, order: 0, priority: ReadFallbackPolicy.LatencyPriority(readFrom, true,
                        ReadFallbackPolicy.IsSameZone(connection, preferredZone), true));
            }
        }
        if (best.TryGet(out var selected))
        {
            // Sampling may await: revalidate both roles together against the latest publication.
            var currentRoute = RoutingSnapshot[slot];
            if (IsCurrentNearestCandidate(selected, currentRoute, includePrimary)
                && (_options.ReplicaLatencyTolerance is null || best.AllBandCandidatesMatch((currentRoute, includePrimary, sampledPrimary: owner),
                    // A role change invalidates the priority assigned before sampling, even if both roles are eligible.
                    static (candidate, state) => IsCurrentNearestCandidate(candidate, state.currentRoute, state.includePrimary)
                        && ReferenceEquals(candidate.Multiplexer, state.sampledPrimary)
                            == ReferenceEquals(candidate.Multiplexer, state.currentRoute.Primary))))
            {
                if (routes is { IsDueForRevalidation: true })
                    _ = JoinReplicaRefresh(routes, slot);
                if (_options.ReplicaLatencyTolerance is not null)
                {
                    var previous = Volatile.Read(ref LastReadSelection);
                    // Role fallback narrows eligibility but diagnostics retain the client's policy.
                    var snapshot = best.CaptureSnapshot(snapshotReadFrom ?? readFrom, slot,
                        static connection => new(connection.Host, connection.Port), previous);
                    if (!ReferenceEquals(previous, snapshot)) Volatile.Write(ref LastReadSelection, snapshot);
                }
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
                && routes is not null && JoinReplicaRefresh(routes, slot) is { } refresh)
                await refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await GetNearestReadConnectionAsync(slot, cancellationToken, discovery, observation, retry: false,
                samplingDeadline: deadline, previousFailure: lastError, excluded: excluded, readFrom: readFrom,
                excludedPeer: excludedPeer, snapshotReadFrom: snapshotReadFrom).ConfigureAwait(false);
        }
        throw new RespireConnectionException($"Redis Cluster slot {slot} has no healthy eligible endpoint for {readFrom} reads.",
            lastError ?? new InvalidOperationException("The read topology changed during selection.")) { HasAmbiguousEndpoint = true };
    }

    private static bool IsCurrentNearestCandidate(RespireConnection connection, ClusterRoutingSnapshot.SlotRoute route, bool includePrimary)
        => connection.IsAcceptingCommands && connection.Multiplexer is { IsRetired: false } node
            && (includePrimary && ReferenceEquals(route.Primary, node) || route.Replicas?.Nodes.Contains(node) == true);

    private async ValueTask<(RespireConnection? Primary, Exception? Failure)> GetNearestPrimaryAsync(
        int slot, ReadLatencySampler<RespireConnection> sampler, CancellationToken cancellationToken,
        DiscoveryRound? discovery, RespireTelemetry.ErrorObservation observation, string? preferredZone,
        HashSet<RespireConnectionMultiplexer>? excluded, RespireConnection? excludedPeer)
    {
        var owner = GetKnownSlotOwner(slot);
        if (owner is not null && (excluded?.Contains(owner) == true || !sampler.CanConnect(owner))) return default;
        try
        {
            var primary = await GetConnectionAsync(slot, cancellationToken, discovery, observation).ConfigureAwait(false);
            if (preferredZone is not null && primary.Multiplexer is { } node)
                primary = node.GetConnectionForZone(preferredZone, slot);
            if (primary.Multiplexer is { } connected) sampler.ConnectionSucceeded(connected);
            return excludedPeer is not null && !HedgedReadPolicy.IsDifferentPeer(excludedPeer, primary)
                ? default : (primary, null);
        }
        catch (Exception error) when (IsReadCandidateFailure(error, cancellationToken))
        {
            ReadEndpointRouter.RecordCandidateFailure(observation, error, observation.Endpoint);
            if (owner is not null) sampler.ConnectionFailed(owner);
            return (null, error);
        }
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
