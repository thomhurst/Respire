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
    private readonly ClusterReplicaSet _initialReplicaRoutes = new([], TimeSpan.Zero);
    // Unknown slots have no shard identity yet. Share their work only with the same slot;
    // a partial reply for another slot must not consume this slot's discovery interval.
    // The key space is bounded by 16384 slots. Publication removes covered entries even when
    // their replica list is empty; uncovered entries retain their in-flight gate and throttle.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, ClusterReplicaSet> _unknownReplicaRoutes = new();

    internal ValueTask<RespireConnection> GetReadConnectionAsync(
        int? slot, RespireReadFrom readFrom, CancellationToken cancellationToken, DiscoveryRound? discovery = null)
    {
        if (readFrom == RespireReadFrom.Primary || slot is null)
        {
            return GetConnectionAsync(slot, cancellationToken, discovery);
        }

        return GetReadConnectionWithPolicyAsync(slot.Value, readFrom, cancellationToken, discovery);
    }

    /// <summary>Selects a replacement after retirement, keeping the primary path's retry loop.</summary>
    internal ValueTask<RespireConnection> GetReadReplacementConnectionAsync(
        int? slot, RespireReadFrom readFrom, CancellationToken cancellationToken, DiscoveryRound? discovery)
        => readFrom == RespireReadFrom.Primary || slot is null
            ? GetReplacementConnectionAsync(null, slot, null, cancellationToken, discovery)
            : GetReadConnectionWithPolicyAsync(slot.Value, readFrom, cancellationToken, discovery);

    private async ValueTask<RespireConnection> GetReadConnectionWithPolicyAsync(
        int slot, RespireReadFrom readFrom, CancellationToken cancellationToken, DiscoveryRound? discovery)
    {
        if (readFrom is RespireReadFrom.PrimaryPreferred)
        {
            try
            {
                return await GetConnectionAsync(slot, cancellationToken, discovery).ConfigureAwait(false);
            }
            catch (Exception error) when (IsReadCandidateFailure(error, cancellationToken))
            {
                return await GetReplicaConnectionAsync(slot, cancellationToken, error, discovery).ConfigureAwait(false);
            }
        }

        try
        {
            return await GetReplicaConnectionAsync(slot, cancellationToken, lastError: null, discovery: discovery).ConfigureAwait(false);
        }
        catch (Exception error) when (readFrom == RespireReadFrom.ReplicaPreferred
            && IsReadCandidateFailure(error, cancellationToken))
        {
            return await GetConnectionAsync(slot, cancellationToken, discovery).ConfigureAwait(false);
        }
    }

    private async ValueTask<RespireConnection> GetReplicaConnectionAsync(
        int slot, CancellationToken cancellationToken, Exception? lastError, DiscoveryRound? discovery)
    {
        var routes = GetKnownReplicas(slot);
        if (routes is null && Volatile.Read(ref _masters).Length == 0)
        {
            // Seed connection readiness precedes its topology reply. Wait for the shared
            // initial discovery before attempting an uncovered slot independently.
            var initial = _initialReplicaRoutes.JoinOrStartRefresh(() => RefreshReplicaRoutesAsync(slot));
            if (initial is not null) await initial.WaitAsync(cancellationToken).ConfigureAwait(false);
            routes = GetKnownReplicas(slot);
            if (routes is not null) _unknownReplicaRoutes.TryRemove(slot, out _);
        }
        if (routes is null)
        {
            var unknown = _unknownReplicaRoutes.GetOrAdd(slot, static _ => new([], TimeSpan.Zero));
            var refresh = unknown.JoinOrStartRefresh(() => RefreshReplicaRoutesAsync(slot));
            if (refresh is not null) await refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
            routes = GetKnownReplicas(slot);
            if (routes is not null) _unknownReplicaRoutes.TryRemove(slot, out _);
        }

        var attempted = 0;
        ClusterReplicaSet? tried = null;
        for (var round = 0; routes is not null; round++)
        {
            // A refresh that reports the same routes keeps the same set. Retrying it would only
            // repeat the failures just observed, so only a changed set gets a second attempt.
            if (!ReferenceEquals(routes, tried) && routes.Nodes.Length > 0)
            {
                tried = routes;
                var selection = await TrySelectReplicaAsync(routes, slot, cancellationToken, discovery).ConfigureAwait(false);
                attempted += selection.Attempted;
                lastError = selection.LastError ?? lastError;
                if (selection.Connection is { } connection) return connection;
            }

            if (round > 0) break;
            // Empty or exhausted routes refresh at most once per interval for this range. Callers
            // that arrive while a refresh runs wait for it instead of failing against stale routes.
            var refresh = routes.JoinOrStartRefresh(() => RefreshReplicaRoutesAsync(slot));
            if (refresh is null) break;
            await refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
            routes = GetKnownReplicas(slot);
        }

        var detail = attempted == 0 ? "no replica available" : "no healthy replica";
        var message = $"Redis Cluster slot {slot} has {detail} for read routing.";
        throw lastError is null
            ? new RespireConnectionException(message)
            : new RespireConnectionException(message, lastError);
    }

    private async ValueTask<(RespireConnection? Connection, Exception? LastError, int Attempted)> TrySelectReplicaAsync(
        ClusterReplicaSet routes, int slot, CancellationToken cancellationToken, DiscoveryRound? discovery)
    {
        var candidates = new ClusterReplicaSelector(routes);
        var attempted = 0;
        Exception? lastError = null;
        cancellationToken.ThrowIfCancellationRequested();
        while (candidates.TryNext(out var node))
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempted++;
            try
            {
                await EnsureRouteNodeConnectedAsync(node, cancellationToken, discovery).ConfigureAwait(false);
                if (node.IsRetired) continue;
                // Healthy reads do not redirect; background revalidation discovers promotions.
                // RefreshReplicaRoutesAsync catches and logs every refresh failure.
                if (routes.IsDueForRevalidation)
                    _ = routes.JoinOrStartRefresh(() => RefreshReplicaRoutesAsync(slot));
                return (node.GetConnection(slot), lastError, attempted);
            }
            catch (Exception error) when (IsReadCandidateFailure(error, cancellationToken))
            {
                lastError = error;
            }
        }
        return (null, lastError, attempted);
    }

    internal async ValueTask<RespireConnection> GetPinnedReadConnectionAsync(
        int slot, RespireConnectionMultiplexer node, CancellationToken cancellationToken, bool revalidate = false)
    {
        if (revalidate && GetKnownReplicas(slot) is { IsDueForRevalidation: true } previous)
        {
            var refresh = previous.JoinOrStartRefresh(() => RefreshReplicaRoutesAsync(slot));
            if (refresh is not null) await refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        if (ReferenceEquals(GetKnownSlotOwner(slot), node))
        {
            await EnsureRouteNodeConnectedAsync(node, cancellationToken, discovery: null).ConfigureAwait(false);
            return node.GetConnection(slot);
        }
        var routes = GetKnownReplicas(slot);
        if (routes is null || !routes.Nodes.Contains(node))
            throw new RespireConnectionException("The Redis Cluster node that issued this cursor left the slot's read topology.");
        await EnsureRouteNodeConnectedAsync(node, cancellationToken, discovery: null).ConfigureAwait(false);
        if (!ReferenceEquals(GetKnownReplicas(slot), routes) || !routes.Nodes.Contains(node))
            throw new RespireConnectionException("The Redis Cluster node that issued this cursor left the slot's read topology.");
        return node.GetConnection(slot);
    }

    // Shared by every caller that joins the refresh, so it is not bound to any one caller's
    // cancellation. It stops on router disposal or after one connect plus one command timeout.
    private async Task RefreshReplicaRoutesAsync(int slot)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopDiscovery.Token);
            var budget = _options.ConnectTimeout + (_options.CommandTimeout ?? _options.ConnectTimeout);
            timeout.CancelAfter(budget);
            var replicas = GetKnownReplicas(slot)?.Nodes ?? [];
            if (Volatile.Read(ref _masters).Length == 0
                && !replicas.Any(static node => node.IsConnected && !node.IsRetired))
            {
                await EnsureConnectedAsync(timeout.Token, discovery: null).ConfigureAwait(false);
                if (GetKnownReplicas(slot) is not null) return;
            }
            var candidates = replicas.Where(static node => node.IsConnected && !node.IsRetired)
                .Concat(Volatile.Read(ref _masters)).Distinct().ToArray();
            var version = CaptureTopologyVersion();
            var refreshRound = new ReplicaRefreshRound(slot, GetKnownSlotOwner(slot));
            // Each known candidate gets the shared deadline. Parallel probes prevent stalled
            // nodes from consuming healthy nodes' time; one snapshot batch fences late replies.
            var attempts = candidates.Select(node => TryRefreshReplicaCandidateAsync(
                node, slot, timeout.Token, version, refreshRound)).ToArray();
            try
            {
                if (await FirstSuccessfulReplicaProbeAsync(attempts).ConfigureAwait(false)) return;
                if (!refreshRound.PublishEmpty(this)) LogReplicaRefreshFailure(slot, error: null);
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
            return await TryRefreshTopologyAsync(node, cancellationToken, discovery: null,
                expectedTopologyVersion, refreshRound.SnapshotBatch, keepUncoveredOwners: true, requiredSlot: slot,
                replicaRefresh: refreshRound).ConfigureAwait(false);
        }
        catch (Exception error) when (cancellationToken.IsCancellationRequested
            && (error is OperationCanceledException || IsDiscoveryFailure(error)))
        {
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
        _logger?.LogWarning(error, "Replica route refresh for Redis Cluster slot {Slot} failed", slot);
    }

    /// <summary>
    /// Selects the other server role after <see cref="ReadFallbackPolicy.CanFallBackToOtherRole"/> accepted
    /// <paramref name="error"/>. When no candidate of that role is reachable, the server error is
    /// surfaced unchanged.
    /// </summary>
    internal async ValueTask<RespireConnection> GetOtherRoleReadConnectionAsync(
        int slot, RespireReadFrom readFrom, RespireServerException error,
        CancellationToken cancellationToken, DiscoveryRound? discovery)
    {
        _logger?.LogDebug(
            "Redis Cluster slot {Slot} returned {Code} on the preferred role; {ReadFrom} read retries on the other role",
            slot, error.Code, readFrom);
        try
        {
            return readFrom == RespireReadFrom.ReplicaPreferred
                ? await GetConnectionAsync(slot, cancellationToken, discovery).ConfigureAwait(false)
                : await GetReplicaConnectionAsync(slot, cancellationToken, lastError: error, discovery).ConfigureAwait(false);
        }
        catch (Exception candidateError) when (IsReadCandidateFailure(candidateError, cancellationToken))
        {
            // The server rejection explains why the read failed; retain it if fallback is unreachable.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            throw;
        }
    }

    internal async ValueTask<DedicatedConnectionPool> GetOtherRoleDedicatedPoolAsync(
        int slot, RespireReadFrom readFrom, RespireServerException error,
        CancellationToken cancellationToken, DiscoveryRound? discovery)
    {
        var connection = await GetOtherRoleReadConnectionAsync(slot, readFrom, error, cancellationToken, discovery)
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
        CancellationToken cancellationToken, DiscoveryRound? discovery)
    {
        if (readFrom == RespireReadFrom.Primary || slot is not { } value) return redirected;
        if (!await RefreshTopologyFromAsync(redirected, value, cancellationToken, discovery).ConfigureAwait(false))
        {
            _logger?.LogWarning(
                "Unable to refresh replica routes for Redis Cluster slot {Slot} from {Host}:{Port} after a redirect; {ReadFrom} read uses {Fallback}",
                value, redirected.Host, redirected.Port, readFrom,
                readFrom == RespireReadFrom.Replica ? "no route" : "the redirected primary");
            if (readFrom == RespireReadFrom.Replica)
            {
                throw new RespireConnectionException(
                    $"Unable to refresh replica routes for Redis Cluster slot {value} after a redirect.");
            }
            return redirected;
        }

        return await GetReadConnectionAsync(value, readFrom, cancellationToken, discovery).ConfigureAwait(false);
    }
}
