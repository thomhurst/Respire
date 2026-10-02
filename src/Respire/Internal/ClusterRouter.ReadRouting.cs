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
    private readonly ClusterReplicaSet _unknownReplicaRoutes = new([], TimeSpan.Zero);

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
        if (routes is null)
        {
            var refresh = _unknownReplicaRoutes.JoinOrStartRefresh(() => RefreshReplicaRoutesAsync(slot));
            if (refresh is not null) await refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
            routes = GetKnownReplicas(slot);
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
                var nodes = routes.Nodes;
                var start = routes.NextStart();
                for (var offset = 0; offset < nodes.Length; offset++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var node = nodes[(start + offset) % nodes.Length];
                    if (node.IsRetired) continue;
                    attempted++;
                    try
                    {
                        await EnsureRouteNodeConnectedAsync(node, cancellationToken, discovery).ConfigureAwait(false);
                        if (node.IsRetired) continue;
                        // A healthy read never redirects, so old routes are revalidated in the
                        // background. A failover that promoted this replica then retires it.
                        // RefreshReplicaRoutesAsync catches and logs every refresh failure.
                        if (routes.IsDueForRevalidation)
                        {
                            _ = routes.JoinOrStartRefresh(() => RefreshReplicaRoutesAsync(slot));
                        }
                        return node.GetConnection(slot);
                    }
                    catch (Exception error) when (IsReadCandidateFailure(error, cancellationToken))
                    {
                        lastError = error;
                    }
                }
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

    internal async ValueTask<RespireConnection> GetPinnedReadConnectionAsync(
        int slot, RespireConnectionMultiplexer node, CancellationToken cancellationToken)
    {
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
            timeout.CancelAfter(_options.ConnectTimeout + (_options.CommandTimeout ?? _options.ConnectTimeout));
            // A connected replica may be the only reachable node after its primary fails.
            // Query it first so a failed primary cannot consume the shared refresh deadline.
            foreach (var replica in GetKnownReplicas(slot)?.Nodes ?? [])
            {
                if (replica.IsConnected && !replica.IsRetired
                    && await TryRefreshTopologyAsync(replica, timeout.Token, discovery: null,
                        keepUncoveredOwners: true).ConfigureAwait(false))
                    return;
            }
            if (Volatile.Read(ref _masters).Length == 0)
            {
                await EnsureConnectedAsync(timeout.Token, discovery: null).ConfigureAwait(false);
                if (GetKnownReplicas(slot) is not null) return;
            }
            var owner = await TryRefreshSlotThroughKnownMastersAsync(
                slot, failedOwner: null, cancellationToken: timeout.Token, discovery: null,
                keepUncoveredOwners: true).ConfigureAwait(false);
            if (owner is null) LogReplicaRefreshFailure(slot, error: null);
        }
        catch (Exception error)
        {
            if (!_stopDiscovery.IsCancellationRequested) LogReplicaRefreshFailure(slot, error);
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
    /// True when a preferred policy should retry a read on the other server role after
    /// <paramref name="error"/>. Reads are idempotent, so one retry is safe.
    /// </summary>
    /// <remarks>
    /// Covers server-side unavailability of the chosen role: <c>LOADING</c> while a node loads its
    /// dataset, <c>MASTERDOWN</c> from a replica that lost its primary link, and <c>CLUSTERDOWN</c>
    /// from a node whose view of the cluster is failing. Strict policies never switch roles.
    /// </remarks>
    internal static bool CanFallBackToOtherRole(
        RespireServerException error, RespireReadFrom readFrom, int? slot, bool onReplica)
    {
        if (slot is null || error.Code is not (RespireErrorCodes.Loading or RespireErrorCodes.MasterDown
            or RespireErrorCodes.ClusterDown))
        {
            return false;
        }

        return readFrom switch
        {
            RespireReadFrom.ReplicaPreferred => onReplica,
            RespireReadFrom.PrimaryPreferred => !onReplica,
            _ => false,
        };
    }

    internal static bool IsReplicaConnection(RespireConnection connection)
        => connection.Multiplexer?.Options.ReadOnly == true;

    /// <summary>
    /// Selects the other server role after <see cref="CanFallBackToOtherRole"/> accepted
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

    // ASK sends one command to the importing primary during slot migration. Its replicas do not
    // own the key yet, so a strict Replica read fails instead of silently reading from a primary.
    internal static bool IsStrictReplicaAsk(RespireServerException error, RespireReadFrom readFrom)
        => readFrom == RespireReadFrom.Replica && error.Code == RespireErrorCodes.Ask;

    internal static RespireConnectionException CreateStrictReplicaAskException(RespireServerException error, int? slot)
        => new($"Redis Cluster slot {slot} is migrating and ASK redirects to a primary, so a Replica read cannot follow it.", error);

    internal ValueTask<bool> RefreshTopologyFromAsync(
        RespireConnection connection, CancellationToken cancellationToken, DiscoveryRound? discovery)
        => connection.Multiplexer is { } node
            ? TryRefreshTopologyAsync(node, cancellationToken, discovery)
            : ValueTask.FromResult(false);

    internal async ValueTask<RespireConnection> SelectReadConnectionAfterRedirectAsync(
        RespireConnection redirected, int? slot, RespireReadFrom readFrom,
        CancellationToken cancellationToken, DiscoveryRound? discovery)
    {
        if (readFrom == RespireReadFrom.Primary || slot is not { } value) return redirected;
        if (!await RefreshTopologyFromAsync(redirected, cancellationToken, discovery).ConfigureAwait(false))
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
