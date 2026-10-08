using Microsoft.Extensions.Logging;
using Respire.Infrastructure;
using Respire.Networking;

namespace Respire.Internal;

/// <summary>
/// Read routing for <see cref="RespireReadFrom"/> policies: replica selection, the preferred-role
/// fallbacks, and replica route refresh after redirects and on revalidation.
/// </summary>
internal sealed partial class ClusterRouter
{
    private long _replicaRefreshWarningNotBefore;
    // Coverage marker only. Unknown-slot refresh state belongs to the coordinator below.
    private readonly ClusterReplicaSet _unknownReplicaRoutes = new([], TimeSpan.Zero);
    private readonly ClusterReplicaDiscovery _unknownReplicaDiscovery;

    private bool HasReplicaCoverage(int slot)
        => GetKnownReplicas(slot) is { } routes && !ReferenceEquals(routes, _unknownReplicaRoutes);

    internal ValueTask<RespireConnection> GetReadConnectionAsync(
        int? slot, RespireReadFrom readFrom, CancellationToken cancellationToken, DiscoveryRound? discovery = null,
        string? preferredZone = null, HashSet<RespireConnectionMultiplexer>? excluded = null,
        RespireTelemetry.ErrorObservation observation = default)
    {
        if (readFrom == RespireReadFrom.Primary || slot is null)
        {
            return GetConnectionAsync(slot, cancellationToken, discovery);
        }

        // Role fallback narrows eligibility to replicas, but redirects and pool replacement
        // must still rank those replicas by the original client's configured zone.
        if (readFrom == RespireReadFrom.Replica && preferredZone is not null)
            return GetReplicaConnectionAsync(slot.Value, cancellationToken, lastError: null, discovery, RespireReadFrom.AzAffinity, excluded, observation);
        return GetReadConnectionWithPolicyAsync(slot.Value, readFrom, cancellationToken, discovery, excluded, observation);
    }

    /// <summary>Selects a replacement after retirement, keeping the primary path's retry loop.</summary>
    internal ValueTask<RespireConnection> GetReadReplacementConnectionAsync(
        int? slot, RespireReadFrom readFrom, CancellationToken cancellationToken, DiscoveryRound? discovery,
        string? preferredZone = null, RespireTelemetry.ErrorObservation observation = default)
    {
        if (readFrom == RespireReadFrom.Primary || slot is null)
            return GetReplacementConnectionAsync(null, slot, null, cancellationToken, discovery, preferredZone);
        return GetReadConnectionAsync(slot.Value, readFrom, cancellationToken, discovery, preferredZone, observation: observation);
    }

    private async ValueTask<RespireConnection> GetReadConnectionWithPolicyAsync(
        int slot, RespireReadFrom readFrom, CancellationToken cancellationToken, DiscoveryRound? discovery,
        HashSet<RespireConnectionMultiplexer>? excluded, RespireTelemetry.ErrorObservation observation)
    {
        if (readFrom == RespireReadFrom.Nearest)
            return await GetNearestReadConnectionAsync(slot, cancellationToken, discovery, excluded: excluded, observation: observation).ConfigureAwait(false);
        if (readFrom is RespireReadFrom.PrimaryPreferred)
        {
            try
            {
                return await GetPrimaryReadConnectionAsync(slot, readFrom, cancellationToken, discovery, excluded).ConfigureAwait(false);
            }
            catch (Exception error) when (IsReadCandidateFailure(error, cancellationToken))
            {
                observation.Handled(error);
                return await GetReplicaConnectionAsync(slot, cancellationToken, error, discovery, excluded: excluded, observation: observation).ConfigureAwait(false);
            }
        }

        try
        {
            return await GetReplicaConnectionAsync(slot, cancellationToken, lastError: null, discovery: discovery, readFrom, excluded, observation).ConfigureAwait(false);
        }
        catch (Exception error) when (ReadFallbackPolicy.AllowsPrimaryFallback(readFrom)
            && IsReadCandidateFailure(error, cancellationToken))
        {
            return await GetPrimaryReadConnectionAsync(slot, readFrom, cancellationToken, discovery, excluded).ConfigureAwait(false);
        }
    }

    private async ValueTask<ClusterReplicaSet?> GetReplicaRoutesAsync(int slot, CancellationToken cancellationToken)
    {
        while (true)
        {
            var routes = GetKnownReplicas(slot);
            if (routes is not null && !ReferenceEquals(routes, _unknownReplicaRoutes)) return routes;
            // Persistently uncovered slots can require sequential rounds; another slot's partial
            // reply must not consume this slot's independent coverage attempt (#731).
            var attempt = await _unknownReplicaDiscovery.DiscoverAsync(slot, cancellationToken).ConfigureAwait(false);
            routes = GetKnownReplicas(slot);
            if (routes is not null && !ReferenceEquals(routes, _unknownReplicaRoutes)) return routes;
            // An owner change can invalidate discovery after it returns. Only a still-valid
            // uncovered attempt may fail this read; otherwise obtain fresh coverage.
            if (_unknownReplicaDiscovery.IsCurrent(slot, attempt)) return null;
        }
    }

    private async ValueTask<RespireConnection> GetPrimaryReadConnectionAsync(
        int slot, RespireReadFrom readFrom, CancellationToken cancellationToken, DiscoveryRound? discovery,
        HashSet<RespireConnectionMultiplexer>? excluded = null)
    {
        if (excluded is not null && GetKnownSlotOwner(slot) is { } current && excluded.Contains(current))
            throw new RespireConnectionException("The primary dedicated connection candidate already failed.");
        var primary = await GetConnectionAsync(slot, cancellationToken, discovery).ConfigureAwait(false);
        if (primary.Multiplexer is { } selected && excluded?.Contains(selected) == true)
            throw new RespireConnectionException("The primary dedicated connection candidate already failed.");
        return ReadFallbackPolicy.UsesAvailabilityZone(readFrom)
            && primary.Multiplexer is { } owner && _options.ClientAvailabilityZone is { } zone
            ? owner.GetConnectionForZone(zone, slot) : primary;
    }

    private async ValueTask<RespireConnection> GetReplicaConnectionAsync(
        int slot, CancellationToken cancellationToken, Exception? lastError, DiscoveryRound? discovery,
        RespireReadFrom readFrom = RespireReadFrom.Replica, HashSet<RespireConnectionMultiplexer>? excluded = null,
        RespireTelemetry.ErrorObservation observation = default)
    {
        var routes = await GetReplicaRoutesAsync(slot, cancellationToken).ConfigureAwait(false);

        var attempted = 0;
        ClusterReplicaSet? tried = null;
        for (var round = 0; routes is not null; round++)
        {
            // A refresh that reports the same routes keeps the same set. Retrying it would only
            // repeat the failures just observed, so only a changed set gets a second attempt.
            if (!ReferenceEquals(routes, tried) && routes.Nodes.Length > 0)
            {
                tried = routes;
                var selection = await TrySelectReplicaAsync(routes, slot, cancellationToken, discovery, readFrom, excluded, observation: observation).ConfigureAwait(false);
                attempted += selection.Attempted;
                lastError = selection.LastError ?? lastError;
                if (selection.Connection is { } connection) return connection;
            }

            if (round > 0) break;
            // Empty or exhausted routes refresh at most once per interval for this range. Callers
            // that arrive while a refresh runs wait for it instead of failing against stale routes.
            var refresh = JoinReplicaRefresh(routes, slot);
            if (refresh is not null) await refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
            var current = GetKnownReplicas(slot);
            // A fast refresh can finish and retire the selected node before selection fails.
            // Its old range is throttled, but the already-published replacement still gets
            // the same bounded second selection attempt as an in-flight refresh.
            if (refresh is null && ReferenceEquals(current, routes)) break;
            routes = current;
        }

        var detail = attempted == 0 ? "no replica available" : "no healthy replica";
        var message = $"Redis Cluster slot {slot} has {detail} for read routing.";
        throw lastError is null
            ? new RespireConnectionException(message)
            : new RespireConnectionException(message, lastError);
    }

    private async ValueTask<(RespireConnection? Connection, Exception? LastError, int Attempted)> TrySelectReplicaAsync(
        ClusterReplicaSet routes, int slot, CancellationToken cancellationToken, DiscoveryRound? discovery,
        RespireReadFrom readFrom, HashSet<RespireConnectionMultiplexer>? excluded,
        RespireConnection? excludedPeer = null, RespireTelemetry.ErrorObservation observation = default)
    {
        var candidates = new ClusterReplicaSelector(routes);
        var attempted = 0;
        Exception? lastError = null;
        var fallbacks = new ReadFallbackPolicy.ReplicaCandidates<RespireConnection>();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (candidates.TryNext(out var node))
            {
                if (excluded?.Contains(node) == true || ReferenceEquals(node, excludedPeer?.Multiplexer)) continue;
                cancellationToken.ThrowIfCancellationRequested();
                attempted++;
                try
                {
                    await EnsureRouteNodeConnectedAsync(node, cancellationToken, discovery).ConfigureAwait(false);
                    if (node.IsRetired) continue;
                    // Healthy reads do not redirect; background revalidation discovers promotions.
                    // RefreshReplicaRoutesAsync catches and logs every refresh failure.
                    if (routes.IsDueForRevalidation)
                        _ = JoinReplicaRefresh(routes, slot);
                    var connection = GetNodeReadConnection(node, slot, readFrom);
                    if (excludedPeer is not null && !HedgedReadPolicy.IsDifferentPeer(excludedPeer, connection)) continue;
                    if (fallbacks.Offer(connection, ReadFallbackPolicy.IsSameZone(connection, _options.ClientAvailabilityZone),
                        linked: true, readFrom))
                        return (connection, lastError, attempted);
                }
                catch (Exception error) when (IsReadCandidateFailure(error, cancellationToken))
                {
                    // The logical read borrows this owner through selection and eventual conversion.
                    // Report the rejected endpoint, not the aggregate no-healthy-replica wrapper.
                    observation.Handled(error);
                    lastError = error;
                }
            }
            if (ReadFallbackPolicy.ShouldProbeLocalPrimary(readFrom, GetKnownSlotOwner(slot), _options.ClientAvailabilityZone))
            {
                try
                {
                    var primary = await GetPrimaryReadConnectionAsync(slot, readFrom, cancellationToken, discovery, excluded).ConfigureAwait(false);
                    if (ReadFallbackPolicy.IsSameZone(primary, _options.ClientAvailabilityZone)
                        && (excludedPeer is null || HedgedReadPolicy.IsDifferentPeer(excludedPeer, primary)))
                        return (primary, lastError, attempted);
                }
                catch (Exception error) when (IsReadCandidateFailure(error, cancellationToken))
                {
                    observation.Handled(error);
                    lastError = error;
                }
            }
            while (fallbacks.TryTake(out var fallback))
                if (fallback.IsAcceptingCommands && fallback.Multiplexer is { IsRetired: false } owner
                    && GetKnownReplicas(slot) is { } current && current.Nodes.Contains(owner))
                    return (fallback, lastError, attempted);
            return (null, lastError, attempted);
        }
        finally { fallbacks.Dispose(); }
    }

    internal async ValueTask<RespireConnection> GetPinnedReadConnectionAsync(
        int slot, RespireConnectionMultiplexer node, CancellationToken cancellationToken, bool revalidate = false,
        RespireReadFrom readFrom = RespireReadFrom.Primary)
    {
        var needsReplicaRevalidation = revalidate && !ReferenceEquals(GetKnownSlotOwner(slot), node);
        if (needsReplicaRevalidation && !HasReplicaCoverage(slot))
        {
            await GetReplicaRoutesAsync(slot, cancellationToken).ConfigureAwait(false);
        }
        else if (needsReplicaRevalidation && GetKnownReplicas(slot) is { IsDueForRevalidation: true } previous)
        {
            var refresh = JoinReplicaRefresh(previous, slot);
            if (refresh is not null) await refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        var route = RoutingSnapshot[slot];
        if (ReferenceEquals(route.Primary, node))
        {
            await EnsureRouteNodeConnectedAsync(node, cancellationToken, discovery: null).ConfigureAwait(false);
            return GetNodeReadConnection(node, slot, readFrom);
        }
        var routes = route.Replicas;
        if (routes is null || !routes.Nodes.Contains(node))
            throw CursorReadTopologyChanged();
        await EnsureRouteNodeConnectedAsync(node, cancellationToken, discovery: null).ConfigureAwait(false);
        if (!ReferenceEquals(GetKnownReplicas(slot), routes) || !routes.Nodes.Contains(node))
            throw CursorReadTopologyChanged();
        return GetNodeReadConnection(node, slot, readFrom);
    }

    private RespireConnection GetNodeReadConnection(RespireConnectionMultiplexer node, int slot, RespireReadFrom readFrom)
        => ReadFallbackPolicy.UsesAvailabilityZone(readFrom) && _options.ClientAvailabilityZone is { } zone
            ? node.GetConnectionForZone(zone, slot) : node.GetConnection(slot);

    private static RespireConnectionException CursorReadTopologyChanged()
        => new("The Redis Cluster node that issued this cursor left the slot's read topology.");

    // Keep the slot-capturing callback in a cold method. Declaring it in an async selector
    // allocates its closure before even a prepared, healthy route can return.
    private Task? JoinReplicaRefresh(ClusterReplicaSet routes, int slot)
        => routes.JoinOrStartRefresh(() => RefreshReplicaRoutesAsync(slot));

    // Shared by every caller that joins the refresh, so it is not bound to any one caller's
    // cancellation. It stops on router disposal or after one connect plus one command timeout.
    private async Task RefreshReplicaRoutesAsync(int slot)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopDiscovery.Token);
            var budget = _options.ConnectTimeout + (_options.CommandTimeout ?? _options.ConnectTimeout);
            timeout.CancelAfter(budget);
            var (snapshot, version) = CaptureReplicaRefreshTopology();
            var replicas = snapshot[slot].Replicas?.Nodes ?? [];
            if (snapshot.Masters.Length == 0
                && !replicas.Any(static node => node.IsConnected && !node.IsRetired))
            {
                await EnsureConnectedAsync(timeout.Token, discovery: null).ConfigureAwait(false);
                if (HasReplicaCoverage(slot)) return;
                (snapshot, version) = CaptureReplicaRefreshTopology();
                replicas = snapshot[slot].Replicas?.Nodes ?? [];
            }
            var masters = snapshot.Masters;
            // A connected seed can survive a failed initial CLUSTER SLOTS query without any
            // known masters. EnsureConnectedAsync deliberately does not query it again.
            if (masters.Length == 0 && Volatile.Read(ref _seed) is { IsConnected: true, IsRetired: false } seed)
                masters = [seed];
            var candidates = replicas.Where(static node => node.IsConnected && !node.IsRetired)
                .Concat(masters).Distinct().ToArray();
            var refreshRound = new ReplicaRefreshRound(slot, snapshot[slot].Primary);
            // Each known candidate gets the shared deadline. Parallel probes prevent stalled
            // nodes from consuming healthy nodes' time; one snapshot batch fences late replies.
            var attempts = candidates.Select(node => TryRefreshReplicaCandidateAsync(
                node, slot, timeout.Token, version, refreshRound)).ToArray();
            try
            {
                if (await FirstSuccessfulReplicaProbeAsync(attempts).ConfigureAwait(false)) return;
                if (!refreshRound.PublishEmpty(this)) LogReplicaRefreshFailure(slot, refreshRound.Failure);
            }
            finally
            {
                // Stop losing probes and observe all work before disposing its shared token.
                await timeout.CancelAsync().ConfigureAwait(false);
                await Task.WhenAll(attempts).ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            if (!_stopDiscovery.IsCancellationRequested) LogReplicaRefreshFailure(slot, error);
        }
    }

    private (ClusterRoutingSnapshot Snapshot, long Version) CaptureReplicaRefreshTopology()
    {
        // The refresh's original owner and redirect fence must describe the same state.
        // A newer fence paired with an older owner could authorize a stale empty reply.
        lock (_nodesGate) return (_topology, _topologyVersion);
    }

    // Register each probe once instead of rebuilding a WhenAny list after every completion.
    // The owner cancels and drains all probes after the first success or error.
    internal static Task<bool> FirstSuccessfulReplicaProbeAsync(Task<bool>[] attempts)
    {
        if (attempts.Length == 0) return Task.FromResult(false);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var remaining = attempts.Length;
        foreach (var attempt in attempts) _ = ObserveAsync(attempt);
        return completion.Task;

        async Task ObserveAsync(Task<bool> attempt)
        {
            try
            {
                if (await attempt.ConfigureAwait(false)) completion.TrySetResult(true);
            }
            catch (Exception error)
            {
                completion.TrySetException(error);
            }
            finally
            {
                if (Interlocked.Decrement(ref remaining) == 0) completion.TrySetResult(false);
            }
        }
    }

    private async Task<bool> TryRefreshReplicaCandidateAsync(
        RespireConnectionMultiplexer node, int slot, CancellationToken cancellationToken,
        long expectedTopologyVersion, ReplicaRefreshRound refreshRound)
    {
        try
        {
            if (Volatile.Read(ref NearestLatency) is not null)
            {
                // Nearest can serve a healthy primary while this advisory discovery is pending.
                // Do not put CLUSTER SLOTS ahead of its reads in the data connection's FIFO.
                var endpoint = node.ActiveConnectionEndpoint;
                await using var connection = await RespireConnection.ConnectAsync(endpoint.Host, endpoint.Port,
                    CreateConnectionOptions(), _logger, cancellationToken).ConfigureAwait(false);
                var load = await TryLoadSlotsAsync(node, cancellationToken,
                    expectedTopologyVersion: expectedTopologyVersion, snapshotBatch: refreshRound.SnapshotBatch,
                    keepUncoveredOwners: true, requiredSlot: slot, replicaRefresh: refreshRound,
                    queryConnection: connection).ConfigureAwait(false);
                return load.Loaded && load.CoversRequiredSlot;
            }
            return await TryRefreshTopologyAsync(node, cancellationToken, discovery: null,
                expectedTopologyVersion, refreshRound.SnapshotBatch, keepUncoveredOwners: true, requiredSlot: slot,
                replicaRefresh: refreshRound).ConfigureAwait(false);
        }
        catch (Exception error) when (cancellationToken.IsCancellationRequested
            && (error is OperationCanceledException || IsDiscoveryFailure(error)))
        {
            return false;
        }
        catch (Exception error) when (CanRetryDiscoveryFailure(error, cancellationToken, discovery: null))
        {
            refreshRound.RecordFailure(error);
            return false;
        }
    }

    private void LogReplicaRefreshFailure(int slot, Exception? error)
    {
        // One warning per router per interval, including concurrent first-use refreshes.
        var now = Environment.TickCount64;
        var next = Volatile.Read(ref _replicaRefreshWarningNotBefore);
        if (now < next || Interlocked.CompareExchange(ref _replicaRefreshWarningNotBefore,
                now + ClusterReplicaSet.RefreshIntervalMilliseconds, next) != next) return;
        _logger?.ClusterReplicaRouteRefreshFailed(slot, error);
    }

    /// <summary>
    /// Selects the role chosen by a successful <see cref="ReadFallbackPolicy.RoleFallback"/>
    /// transition. When no candidate of that role is reachable, its original server rejection
    /// is surfaced unchanged.
    /// </summary>
    internal async ValueTask<RespireConnection> GetOtherRoleReadConnectionAsync(
        int slot, ReadFallbackPolicy.RoleFallback fallback,
        CancellationToken cancellationToken, DiscoveryRound? discovery,
        RespireTelemetry.ErrorObservation observation = default)
    {
        var readFrom = fallback.Policy;
        var error = fallback.OriginalFailure!;
        _logger?.ClusterReadRoleRetry(slot, error.Code, readFrom);
        try
        {
            if (fallback.ReplicaOnly == false)
                return await GetPrimaryReadConnectionAsync(slot, readFrom, cancellationToken, discovery).ConfigureAwait(false);

            // Preserve replica zone ranking without probing the primary that just rejected the read.
            var replicaPolicy = ReadFallbackPolicy.UsesAvailabilityZone(readFrom)
                ? RespireReadFrom.AzAffinity : RespireReadFrom.Replica;
            return await GetReplicaConnectionAsync(slot, cancellationToken, lastError: error, discovery, replicaPolicy, observation: observation).ConfigureAwait(false);
        }
        catch (Exception candidateError) when (IsReadCandidateFailure(candidateError, cancellationToken))
        {
            if (fallback.ReplicaOnly == false) observation.Handled(candidateError);
            // The server rejection explains why the read failed; retain it if fallback is unreachable.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            throw;
        }
    }

    internal async ValueTask<DedicatedConnectionPool> GetOtherRoleDedicatedPoolAsync(
        int slot, ReadFallbackPolicy.RoleFallback fallback,
        CancellationToken cancellationToken, DiscoveryRound? discovery,
        RespireTelemetry.ErrorObservation observation = default)
    {
        var connection = await GetOtherRoleReadConnectionAsync(slot, fallback, cancellationToken, discovery, observation)
            .ConfigureAwait(false);
        return connection.Multiplexer is { } node
            ? GetOrCreateDedicatedPool(node)
            : GetOrCreateDedicatedPool(new RespireEndpoint(connection.Host, connection.Port));
    }

    // Connect and handshake timeouts surface as OperationCanceledException on a token the caller
    // does not own. Only the caller's own cancellation must stop fallback to another candidate.
    private static bool IsReadCandidateFailure(Exception error, CancellationToken cancellationToken)
        => !cancellationToken.IsCancellationRequested
            && (error is OperationCanceledException || IsDiscoveryFailure(error));

    private ValueTask<bool> RefreshTopologyFromAsync(
        RespireConnection connection, int slot, CancellationToken cancellationToken, DiscoveryRound? discovery)
        => connection.Multiplexer is { } node
            ? TryRefreshTopologyAsync(node, cancellationToken, discovery, keepUncoveredOwners: true, requiredSlot: slot)
            : ValueTask.FromResult(false);

    internal async ValueTask<RespireConnection> SelectReadConnectionAfterRedirectAsync(
        RespireConnection redirected, int? slot, RespireReadFrom readFrom,
        CancellationToken cancellationToken, DiscoveryRound? discovery, string? preferredZone = null,
        RespireTelemetry.ErrorObservation observation = default)
    {
        if (readFrom == RespireReadFrom.Primary || slot is not { } value) return redirected;
        if (!await RefreshTopologyFromAsync(redirected, value, cancellationToken, discovery).ConfigureAwait(false))
        {
            _logger?.ClusterReplicaRedirectRefreshFailed(value, redirected.Host, redirected.Port, readFrom, readFrom == RespireReadFrom.Replica ? "no route" : "the redirected primary");
            if (readFrom == RespireReadFrom.Replica)
            {
                throw new RespireConnectionException(
                    $"Unable to refresh replica routes for Redis Cluster slot {value} after a redirect.");
            }
            return redirected;
        }

        return await GetReadConnectionAsync(value, readFrom, cancellationToken, discovery, preferredZone, observation: observation).ConfigureAwait(false);
    }
}
