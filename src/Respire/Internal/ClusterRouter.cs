using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Respire.Commands;
using Respire.Infrastructure;
using Respire.Networking;

namespace Respire.Internal;

internal sealed partial class ClusterRouter : IAsyncDisposable
{
    private const int MaxRedirects = 5;
    private static readonly RawCommand Asking = new("*1\r\n$6\r\nASKING\r\n"u8.ToArray());
    private readonly RespireOptions _options;
    private readonly ILogger? _logger;
    private readonly RespireConnectionOptions _commandConnectionOptions;
    private readonly RespireEndpoint[] _seeds;
    private readonly RespireConnectionMultiplexer _primary;
    private readonly ClusterNodeIdentityIndex _identities;
    private readonly Dictionary<RespireConnectionMultiplexer, Action<int, RespireConnectionStateChange>> _nodeStateHandlers = [];
    private readonly Dictionary<RespireConnectionMultiplexer, DedicatedConnectionPool> _dedicatedPools = [];
    private readonly Dictionary<RespireConnectionMultiplexer, Action> _dedicatedMovingHandlers = [];
    private readonly Dictionary<CorrectionPoolIdentity, CorrectionPoolEntry> _correctionPools = [];
    private readonly object _nodesGate = new();
    private readonly RespireConnectionMultiplexer?[] _slots = new RespireConnectionMultiplexer?[ClusterHash.SlotCount];
    // Every slot in a range shares one replica set, including its cursor and refresh throttle.
    private readonly ClusterReplicaSet?[] _replicasBySlot = new ClusterReplicaSet?[ClusterHash.SlotCount];
    // MOVED clears a slot's replicas to this shared set, so a burst of reads across the moved
    // slots coalesces into one topology refresh. Replaced once a refresh has started through it.
    private RespireConnectionMultiplexer[] _replicaNodes = [];
    private RespireConnectionMultiplexer[] _masters = [];
    // Published with the slot map under _nodesGate but read without it. Replicas only serve as
    // topology-refresh fallbacks, so a reader that pairs a new slot map with the previous replica
    // list is harmless. Replica read routing would need one snapshot holding both.
    private ClusterTopologyReplica[] _replicas = [];
    private int[] _masterSlotCounts = [];
    private readonly SemaphoreSlim _seedGate = new(1, 1);
    private RespireConnectionMultiplexer? _seed;
    private int _hasCompleteTopology;
    private long _topologyVersion;
    private readonly Dictionary<RespireConnectionMultiplexer, long> _redirectVersions = [];
    private long _nextDiscoveryGeneration;
    private long _publishedDiscoveryGeneration;
    // Only direct route mutations advance per-slot versions. These reject stale discovery
    // even when a route changes away and back
    // to the same transport (an owner-reference comparison cannot detect that ABA case).
    private readonly long[] _slotVersions = new long[ClusterHash.SlotCount];
    private readonly object?[] _slotSnapshotBatches = new object?[ClusterHash.SlotCount];
    // Includes discovery publications, so queued SMIGRATED work can detect every newer
    // route mutation without treating completed discovery as a direct-route fence.
    // Owner-change fence for queued SMIGRATED work, separate from _slotVersions: it covers
    // discovery publications (which keep their slot version) but not same-owner redirects.
    // Owner-change fence for queued SMIGRATED work, separate from _slotVersions. Values come
    // from ClusterSlotMutationClock. Which paths write which fence:
    // - MOVED with a new owner: _slotVersions (++_topologyVersion) and a fresh mutation token.
    // - MOVED to the current owner: _slotVersions only. It is not an owner change, so queued
    //   SMIGRATED work for that slot must still apply.
    // - Slot clear: _slotVersions and a fresh mutation token.
    // - Discovery owner change: a fresh mutation token; the slot version is kept.
    // - SMIGRATED move: _slotVersions (one ++_topologyVersion per migration) and the
    //   notification's own receive-time token, so FIFO chains (A->B then B->C) both apply while
    //   a callback overtaken by a later owner change is rejected.
    // ClusterSlotFences documents when a dependent migration may cross its fence.
    private readonly long[] _slotMutationVersions = new long[ClusterHash.SlotCount];
    private readonly ClusterSlotFences _slotFences = new();    private int _disposed;
    private readonly TimeProvider _topologyRefreshClock;
    private readonly ClusterTopologyRefreshScheduler _topologyRefresh;

    internal ClusterRouter(RespireOptions options, RespireConnectionMultiplexer primary, Func<long>? migrationClock = null)
        : this(options, primary, options.ToConnectionOptions(enableMaintenanceNotifications: true), migrationClock)
    {
    }

    internal ClusterRouter(
        RespireOptions options,
        RespireConnectionMultiplexer primary,
        RespireConnectionOptions commandConnectionOptions,
        Func<long>? migrationClock = null)
    {
        _options = options;
        _ownedPools = new(_nodesGate);
        _unknownReplicaDiscovery = new(RefreshReplicaRoutesAsync, HasReplicaCoverage);
        _logger = options.CreateLogger("Respire.Cluster");
        _commandConnectionOptions = commandConnectionOptions;
        _seeds = options.Endpoints.Count == 0
            ? [new RespireEndpoint("localhost")]
            : options.Endpoints.ToArray();
        _primary = primary;
        _migrations = new(migrationClock);
        _smigratedNotifications = CreateSmigratedChannel();
        _identities = new ClusterNodeIdentityIndex(options.PrimaryEndpoint, primary, CreateNode, _nodesGate);
        _topologyRefreshClock = options.ClusterTopologyRefreshClock;
        _topologyRefresh = new ClusterTopologyRefreshScheduler(options.ClusterTopologyRefreshInterval, _topologyRefreshClock);
        _discoveryClock = options.ClusterDiscoveryClock;
        _sharedRefreshCoordinator = new SharedRefreshCoordinator(_topologyRefreshClock, TopologyRefreshCoalescingWindow);
        ObserveNode(primary);
    }

    internal bool IsConnected
    {
        get
        {
            if (Volatile.Read(ref _seed)?.IsConnected == true)
            {
                return true;
            }

            foreach (var master in Volatile.Read(ref _masters))
            {
                if (master.IsConnected && !master.IsRetired)
                {
                    return true;
                }
            }

            foreach (var replica in Volatile.Read(ref _replicaNodes))
            {
                if (replica.IsConnected && !replica.IsRetired)
                {
                    return true;
                }
            }

            return false;
        }
    }

    internal event Action<RespireConnectionMultiplexer, int, RespireConnectionStateChange>? SlotStateChanged;
    internal event Action<RespireConnectionStateChange>? DedicatedStateChanged;
    internal event Action<RespireConnectionMultiplexer>? NodeRetired;
    internal event Action<RespireConnectionMultiplexer>? ReplicaNodeRetired;
    // Arguments: topology version, known primary endpoints, and whether that endpoint set is
    // authoritative. A non-authoritative set (one cached owner cleared, or a redirect onto a
    // partial map) cannot prove that an omitted primary has left the cluster.
    internal event Action<long, RespireEndpoint[], bool>? TopologyChanged;

    internal async ValueTask<RespireEndpoint> GetSlotOwnerEndpointAsync(
        int slot, CancellationToken cancellationToken)
    {
        if ((uint)slot >= ClusterHash.SlotCount) throw new ArgumentOutOfRangeException(nameof(slot));
        // A concurrent topology change can replace the owner between routing and the check
        // below. That is transient, so resolve again a few times before reporting a failure.
        for (var attempt = 1; ; attempt++)
        {
            var connection = await GetConnectionAsync(slot, cancellationToken, discovery: null).ConfigureAwait(false);
            var owner = Volatile.Read(ref _slots[slot]);
            if (owner is { IsRetired: false, IsConnected: true }
                && owner.Host == connection.Host && owner.Port == connection.Port)
                return new RespireEndpoint(connection.Host, connection.Port);
            if (attempt >= SlotOwnerResolveAttempts)
                throw new RespireConnectionException("Redis Cluster did not provide a connected owner for the notification slot.");
        }
    }

    private const int SlotOwnerResolveAttempts = 3;

    // Primaries that own slots in a freshly loaded complete map. A cached map can predate a
    // failover or an added primary, so it is refreshed first with one CLUSTER SLOTS on a node
    // that is already connected; this does not open connections to every primary. Falls back
    // to full discovery when no node is connected or the refreshed map is not usable.
    internal async ValueTask<RespireEndpoint[]> GetPrimaryEndpointsAsync(CancellationToken cancellationToken)
    {
        if (TryGetConnectedNode() is { } node
            && await TryRefreshTopologyAsync(node, cancellationToken, discovery: null).ConfigureAwait(false)
            && TryGetCachedPrimaryEndpoints() is { } endpoints)
            return endpoints;
        var connections = await GetMasterConnectionsAsync(cancellationToken, discovery: null).ConfigureAwait(false);
        return connections.Select(static connection => new RespireEndpoint(connection.Host, connection.Port))
            .Distinct().ToArray();
    }

    // Slot-owning primaries of the cached complete map, or null when the map is incomplete or
    // names a retired primary.
    private RespireEndpoint[]? TryGetCachedPrimaryEndpoints()
    {
        lock (_nodesGate)
        {
            if (!HasCompleteTopology()) return null;
            var masters = _masters;
            var counts = _masterSlotCounts;
            List<RespireEndpoint> endpoints = new(masters.Length);
            for (var index = 0; index < masters.Length; index++)
            {
                if (counts[index] == 0) continue;
                if (masters[index].IsRetired) return null;
                var endpoint = Endpoint(masters[index]);
                if (!endpoints.Contains(endpoint)) endpoints.Add(endpoint);
            }
            return endpoints.Count != 0 ? [.. endpoints] : null;
        }
    }

    // Read the published generation without connecting or taking _nodesGate. Subscription
    // topology callbacks use this while holding their own route gate.
    internal RespireConnectionMultiplexer? GetKnownSlotOwner(int slot) => Volatile.Read(ref _slots[slot]);

    // Slots in a range share an immutable snapshot. Topology changes replace the set, never its Nodes.
    private ClusterReplicaSet? GetKnownReplicas(int slot) => Volatile.Read(ref _replicasBySlot[slot]);

    // ClientCore acquires its health gate first, then this gate, through membership checks
    // and health mutation. Router callbacks must always run outside this gate.
    internal object NodeStateGate => _nodesGate;

    internal bool IsNodeObserved(RespireConnectionMultiplexer node)
    {
        lock (_nodesGate)
        {
            return _nodeStateHandlers.ContainsKey(node);
        }
    }

    internal bool IsReplicaNode(RespireConnectionMultiplexer node)
        => Array.IndexOf(Volatile.Read(ref _replicaNodes), node) >= 0;

    internal bool IsSlotConnected(int slot)
        => Volatile.Read(ref _slots[slot])?.IsConnected == true;

    internal RespireEndpoint? GetSlotOwnerEndpoint(int slot)
    {
        var owner = Volatile.Read(ref _slots[slot]);
        if (owner is null) return null;
        return Endpoint(owner);
    }

    internal RespireEndpoint SeedEndpoint
    {
        get
        {
            var seed = Volatile.Read(ref _seed) ?? _primary;
            return new RespireEndpoint(seed.Host, seed.Port);
        }
    }

    internal RespireEndpoint[] GetActiveEndpoints()
    {
        lock (_nodesGate)
        {
            return _nodeStateHandlers.Keys
                .Select(node => new RespireEndpoint(node.Host, node.Port))
                .ToArray();
        }
    }

    internal ClusterTopologyReplica[] GetReplicas()
        => Volatile.Read(ref _replicas);

    internal async ValueTask EnsureConnectedAsync(CancellationToken cancellationToken, DiscoveryRound? discovery)
    {
        if (Volatile.Read(ref _seed) is { IsConnected: true } readySeed && discovery?.HasRejected(readySeed) != true)
        {
            return;
        }

        using var scope = BeginDiscovery(discovery);
        discovery = scope.Round;
        await _seedGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (Volatile.Read(ref _seed) is { IsConnected: true } seed && discovery?.HasRejected(seed) != true)
            {
                return;
            }

            Exception? lastError = discovery?.PendingFailure;
            foreach (var endpoint in _seeds)
            {
                var node = GetOrCreateNode(endpoint);
                // Owner/master recovery may already have rejected this configured seed.
                // Do not spend another fallback attempt on the same multiplexer generation.
                if (discovery?.HasRejected(node) == true) continue;
                if (discovery is not null) await discovery.BeforeCandidateAsync(endpoint, cancellationToken).ConfigureAwait(false);
                try
                {
                    await node.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                    SetSeed(node);
                    _ = await TryLoadSlotsAsync(node, cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (CanRetryConnectionFailure(ex, cancellationToken))
                {
                    lastError = ex;
                    discovery?.FailedNode(node, ex);
                }
            }

            throw new RespireConnectionException("Unable to connect to any Redis Cluster seed.", lastError!);
        }
        catch (Exception error)
        {
            scope.SetTerminalError(error);
            throw;
        }
        finally
        {
            _seedGate.Release();
        }
    }

    internal ValueTask<RespireConnection> GetConnectionAsync(
        int? slot, CancellationToken cancellationToken, DiscoveryRound? discovery)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (discovery is null && _options.ReconnectPolicy is not null
            && TryGetReadyConnection(slot) is { } ready) return new(ready);
        return GetConnectionWithDiscoveryAsync(slot, cancellationToken, discovery);
    }

    private RespireConnection? TryGetReadyConnection(int? slot, bool? correctionIdentity = null)
    {
        var node = slot is { } value ? Volatile.Read(ref _slots[value]) : TryGetConnectedNode();
        if (node is not { IsConnected: true, IsRetired: false }) return null;
        if (correctionIdentity is { } required && !node.HasReliableCorrectionOrdering
            && (required || !node.IsReliableCorrectionOrderingUnavailable)) return null;
        try
        {
            // Identity-enabled selection uses the same round-robin choice as
            // EnableCorrectionOrderingAsync, including the optional ACL-restricted case.
            return correctionIdentity is null && slot is { } affinity
                ? node.GetConnection(affinity) : node.GetConnection();
        }
        catch (Exception error) when (error is RespireConnectionException or RespireConnectionRetiredException)
        {
            // The connection changed after the fast snapshot. Cold discovery owns any fallback.
            return null;
        }
    }

    private async ValueTask<RespireConnection> GetConnectionWithDiscoveryAsync(
        int? slot, CancellationToken cancellationToken, DiscoveryRound? discovery)
    {
        using var scope = BeginDiscovery(discovery);
        discovery = scope.Round;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                try { return await GetConnectionCoreAsync(slot, cancellationToken, discovery).ConfigureAwait(false); }
                catch (RespireConnectionRetiredException error) when (CanRetryRetirement(attempt, cancellationToken))
                {
                    // No application command was accepted during route acquisition.
                    discovery?.Failed(error);
                }
            }
        }
        catch (Exception error)
        {
            scope.SetTerminalError(error);
            throw;
        }
    }

    private async ValueTask<RespireConnection> GetConnectionCoreAsync(int? slot, CancellationToken cancellationToken, DiscoveryRound? discovery)
    {
        if (slot is null && TryGetConnectedNode() is { } connectedNode)
        {
            if (discovery is not null) await discovery.BeforeCandidateAsync(Endpoint(connectedNode), cancellationToken).ConfigureAwait(false);
            return connectedNode.GetConnection();
        }

        if (slot is null)
        {
            // A replacement generation can be known but not connected yet. Seed discovery
            // is unnecessary when a current master can serve this unkeyed command.
            foreach (var master in Volatile.Read(ref _masters))
            {
                if (discovery?.HasRejected(master) == true) continue;
                try
                {
                    await EnsureRouteNodeConnectedAsync(master, cancellationToken, discovery).ConfigureAwait(false);
                    return master.GetConnection();
                }
                catch (Exception error) when (error is not RespireConnectionRetiredException && CanRetryDiscoveryFailure(error, cancellationToken, discovery))
                {
                    discovery?.FailedNode(master, error);
                }
            }
        }

        RespireConnectionMultiplexer? failedOwner = null;
        if (slot is { } cachedSlot && Volatile.Read(ref _slots[cachedSlot]) is { } cachedNode)
        {
            try
            {
                await EnsureRouteNodeConnectedAsync(cachedNode, cancellationToken, discovery).ConfigureAwait(false);
                return cachedNode.GetConnection(cachedSlot);
            }
            catch (Exception error) when (error is not RespireConnectionRetiredException && CanRetryDiscoveryFailure(error, cancellationToken, discovery))
            {
                discovery?.FailedNode(cachedNode, error);
                ClearSlotOwner(cachedSlot, cachedNode);
                failedOwner = cachedNode;
                // Refresh through another discovered master before falling back to seeds.
            }
        }

        if (slot is { } refreshSlot
            && await TryRefreshSlotThroughKnownMastersAsync(refreshSlot, failedOwner, cancellationToken, discovery).ConfigureAwait(false)
                is { } refreshedNode)
        {
            return refreshedNode.GetConnection(refreshSlot);
        }

        await EnsureConnectedAsync(cancellationToken, discovery).ConfigureAwait(false);
        var node = slot is { } value ? Volatile.Read(ref _slots[value]) : null;
        node ??= Volatile.Read(ref _seed)!;
        await EnsureRouteNodeConnectedAsync(node, cancellationToken, discovery).ConfigureAwait(false);
        return slot is { } affinity ? node.GetConnection(affinity) : node.GetConnection();
    }

    private static RespireEndpoint Endpoint(RespireConnectionMultiplexer node) => new(node.Host, node.Port);

    private async ValueTask EnsureRouteNodeConnectedAsync(
        RespireConnectionMultiplexer node, CancellationToken cancellationToken, DiscoveryRound? discovery)
    {
        if (discovery is not null) await discovery.BeforeCandidateAsync(Endpoint(node), cancellationToken).ConfigureAwait(false);
        try
        {
            await node.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (node.IsRetired && !cancellationToken.IsCancellationRequested)
        {
            // Preserve caller cancellation; normalize only an unpublished handshake cancelled by retirement.
            var retired = new RespireConnectionRetiredException(node.Host, node.Port);
            discovery?.FailedNode(node, retired);
            throw retired;
        }
        catch (Exception error)
        {
            discovery?.FailedNode(node, error);
            throw;
        }
    }

    internal async ValueTask<RespireConnection> GetRedirectConnectionAsync(
        RespireServerException error,
        RespireConnection source,
        CancellationToken cancellationToken,
        int? commandSlot, DiscoveryRound? discovery)
    {
        using var scope = BeginDiscovery(discovery);
        discovery = scope.Round;
        if (error.Code != RespireErrorCodes.ReadOnly && discovery is { HasPendingFailure: false })
            discovery.Failed(new RespireEndpoint(source.Host, source.Port), error);
        try
        {
            if (error.Code == RespireErrorCodes.ReadOnly)
            {
                if (commandSlot is not { } readOnlySlot)
                {
                    throw error;
                }
                var replacement = await RefreshReadOnlyOwnerAsync(error, source, readOnlySlot, cancellationToken, discovery)
                    .ConfigureAwait(false);
                try { return replacement.GetConnection(readOnlySlot); }
                catch (RespireConnectionRetiredException failure) when (CanRetryRetirement(0, cancellationToken))
                {
                    discovery?.Failed(Endpoint(replacement), failure);
                    return await GetConnectionAsync(readOnlySlot, cancellationToken, discovery).ConfigureAwait(false);
                }
            }

            if (!TryParseRedirect(error, source.Host, out var slot, out var endpoint))
            {
                throw error;
            }

            for (var attempt = 0; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (error.Code == RespireErrorCodes.Moved) SignalMovedTopologyRefresh();
                var node = GetOrCreateNode(endpoint, observe: error.Code != "ASK", redirect: true);
                try
                {
                    await EnsureRouteNodeConnectedAsync(node, cancellationToken, discovery).ConfigureAwait(false);
                    if (error.Code == RespireErrorCodes.Moved) SetSlotOwner(slot, node);
                    return node.GetConnection(slot);
                }
                catch (Exception failure) when (CanRetryRetiredRedirect(failure, node, attempt, cancellationToken))
                {
                    discovery?.Failed(Endpoint(node), failure);
                    // No redirected command has been sent. Resolve the endpoint's current generation.
                }
            }
        }
        catch (Exception failure)
        {
            scope.SetTerminalError(failure);
            if (ShouldPreserveRejection(failure, cancellationToken, discovery))
                ExceptionDispatchInfo.Capture(error).Throw();
            throw;
        }
    }

    internal ValueTask<RespireConnection> GetTrackedConnectionAsync(
        int? slot, bool requireIdentity, CancellationToken cancellationToken, DiscoveryRound? discovery)
        => GetReplacementConnectionAsync(null, slot, requireIdentity, cancellationToken, discovery);

    internal bool CanRetryRetirement(int attempt, CancellationToken cancellationToken)
        => attempt < MaxRedirects && !cancellationToken.IsCancellationRequested && Volatile.Read(ref _disposed) == 0;

    // ASK and captured cluster-wide targets preserve their endpoint without changing the slot owner.
    // Callers may retry only commands rejected before acceptance, never ambiguous I/O failures.
    internal ValueTask<RespireConnection> GetReplacementConnectionAsync(
        RespireConnection? endpointSource, int? slot, bool? requireIdentity, CancellationToken cancellationToken,
        DiscoveryRound? discovery)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (endpointSource is null && discovery is null && _options.ReconnectPolicy is not null
            && TryGetReadyConnection(slot, requireIdentity) is { } ready) return new(ready);
        return GetReplacementWithDiscoveryAsync(endpointSource, slot, requireIdentity, cancellationToken, discovery);
    }

    private async ValueTask<RespireConnection> GetReplacementWithDiscoveryAsync(
        RespireConnection? endpointSource, int? slot, bool? requireIdentity, CancellationToken cancellationToken,
        DiscoveryRound? discovery)
    {
        using var scope = BeginDiscovery(discovery);
        discovery = scope.Round;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                RespireConnectionMultiplexer? node = null;
                try
                {
                    RespireConnection connection;
                    if (endpointSource is null)
                        connection = await GetConnectionAsync(slot, cancellationToken, discovery).ConfigureAwait(false);
                    else
                    {
                        node = GetOrCreateNode(new(endpointSource.Host, endpointSource.Port), observe: false, redirect: true);
                        await EnsureRouteNodeConnectedAsync(node, cancellationToken, discovery).ConfigureAwait(false);
                        connection = slot is { } value ? node.GetConnection(value) : node.GetConnection();
                    }
                    node = connection.Multiplexer;
                    return requireIdentity is { } required
                        ? await EnableCorrectionOrderingAsync(connection, required, cancellationToken, observe: endpointSource is null)
                            .ConfigureAwait(false)
                        : connection;
                }
                catch (Exception error) when (CanRetryRetirement(attempt, cancellationToken)
                    && (error is RespireConnectionRetiredException || error is OperationCanceledException && node?.IsRetired == true))
                {
                    discovery?.Failed(error);
                    // Selection and identity setup have not accepted the application command.
                }
            }
        }
        catch (Exception error) { scope.SetTerminalError(error); throw; }
    }

    internal async ValueTask<RespireConnection> GetTrackedRedirectConnectionAsync(
        RespireServerException error,
        RespireConnection source,
        bool requireIdentity,
        CancellationToken cancellationToken,
        int? commandSlot, DiscoveryRound? discovery)
    {
        using var scope = BeginDiscovery(discovery);
        discovery = scope.Round;
        try
        {
            var connection = await GetRedirectConnectionAsync(error, source, cancellationToken, commandSlot, discovery)
                .ConfigureAwait(false);
            try
            {
                return await EnableCorrectionOrderingAsync(
                        connection, requireIdentity, cancellationToken, observe: error.Code != "ASK")
                    .ConfigureAwait(false);
            }
            catch (Exception failure) when (CanRetryRetirement(0, cancellationToken)
                && (failure is RespireConnectionRetiredException
                    || failure is OperationCanceledException && connection.Multiplexer?.IsRetired == true))
            {
                discovery?.Failed(new RespireEndpoint(connection.Host, connection.Port), failure);
                return await GetReplacementConnectionAsync(
                    error.Code == RespireErrorCodes.Ask ? connection : null, commandSlot, requireIdentity,
                    cancellationToken, discovery).ConfigureAwait(false);
            }
        }
        catch (Exception failure)
        {
            scope.SetTerminalError(failure);
            if (ShouldPreserveRejection(failure, cancellationToken, discovery)) ExceptionDispatchInfo.Capture(error).Throw();
            throw;
        }
    }

    private bool ShouldPreserveRejection(Exception failure, CancellationToken callerToken, DiscoveryRound? discovery)
        => discovery is not null && !callerToken.IsCancellationRequested && Volatile.Read(ref _disposed) == 0
            && failure is not RespireConfigurationException
            && (IsDiscoveryFailure(failure) || failure is RespireConnectionRetiredException);

    internal ValueTask<DedicatedConnectionPool> GetDedicatedPoolAsync(
        int? slot, CancellationToken cancellationToken, DiscoveryRound? discovery)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var node = slot is { } value ? Volatile.Read(ref _slots[value]) : Volatile.Read(ref _seed);
        if (discovery is null && _options.ReconnectPolicy is not null
            && node is { IsConnected: true, IsRetired: false })
            return new(GetOrCreateDedicatedPool(Endpoint(node)));
        return GetDedicatedPoolWithDiscoveryAsync(slot, cancellationToken, discovery);
    }

    internal async ValueTask<DedicatedConnectionPool> GetReadDedicatedPoolAsync(
        int? slot, RespireReadFrom readFrom, CancellationToken cancellationToken, DiscoveryRound? discovery)
    {
        if (readFrom == RespireReadFrom.Primary || slot is null)
            return await GetDedicatedPoolAsync(slot, cancellationToken, discovery).ConfigureAwait(false);

        var connection = await GetReadConnectionAsync(slot, readFrom, cancellationToken, discovery)
            .ConfigureAwait(false);
        return connection.Multiplexer is { } node
            ? GetOrCreateDedicatedPool(node)
            : GetOrCreateDedicatedPool(new RespireEndpoint(connection.Host, connection.Port));
    }

    private async ValueTask<DedicatedConnectionPool> GetDedicatedPoolWithDiscoveryAsync(
        int? slot, CancellationToken cancellationToken, DiscoveryRound? discovery)
    {
        using var scope = BeginDiscovery(discovery);
        discovery = scope.Round;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                try { return await GetDedicatedPoolCoreAsync(slot, cancellationToken, discovery).ConfigureAwait(false); }
                catch (RespireConnectionRetiredException error) when (CanRetryRetirement(attempt, cancellationToken))
                {
                    discovery?.Failed(error);
                }
            }
        }
        catch (Exception error) { scope.SetTerminalError(error); throw; }
    }

    private async ValueTask<DedicatedConnectionPool> GetDedicatedPoolCoreAsync(int? slot, CancellationToken cancellationToken, DiscoveryRound? discovery)
    {
        RespireConnectionMultiplexer? failedOwner = null;
        if (slot is { } cachedSlot && Volatile.Read(ref _slots[cachedSlot]) is { } cachedNode)
        {
            try
            {
                await EnsureRouteNodeConnectedAsync(cachedNode, cancellationToken, discovery).ConfigureAwait(false);
                return GetOrCreateDedicatedPool(new RespireEndpoint(cachedNode.Host, cachedNode.Port));
            }
            catch (Exception error) when (error is not RespireConnectionRetiredException && CanRetryDiscoveryFailure(error, cancellationToken, discovery))
            {
                discovery?.FailedNode(cachedNode, error);
                ClearSlotOwner(cachedSlot, cachedNode);
                failedOwner = cachedNode;
                // Refresh through another discovered master before falling back to seeds.
            }
        }

        if (slot is { } refreshSlot
            && await TryRefreshSlotThroughKnownMastersAsync(refreshSlot, failedOwner, cancellationToken, discovery).ConfigureAwait(false)
                is { } refreshedNode)
        {
            return GetOrCreateDedicatedPool(new RespireEndpoint(refreshedNode.Host, refreshedNode.Port));
        }

        await EnsureConnectedAsync(cancellationToken, discovery).ConfigureAwait(false);
        var node = slot is { } value ? Volatile.Read(ref _slots[value]) : null;
        node ??= Volatile.Read(ref _seed)!;
        await EnsureRouteNodeConnectedAsync(node, cancellationToken, discovery).ConfigureAwait(false);
        return GetOrCreateDedicatedPool(new RespireEndpoint(node.Host, node.Port));
    }

    internal async ValueTask<DedicatedConnectionPool> GetRedirectDedicatedPoolAsync(
        RespireServerException error,
        RespireConnection source,
        CancellationToken cancellationToken,
        int? commandSlot, DiscoveryRound? discovery)
    {
        using var scope = BeginDiscovery(discovery);
        discovery = scope.Round;
        if (discovery is { HasPendingFailure: false })
            discovery.Failed(new RespireEndpoint(source.Host, source.Port), error);
        try
        {
            if (error.Code == RespireErrorCodes.ReadOnly)
            {
                if (commandSlot is not { } readOnlySlot)
                {
                    throw error;
                }
                var replacement = await RefreshReadOnlyOwnerAsync(error, source, readOnlySlot, cancellationToken, discovery)
                    .ConfigureAwait(false);
                return GetOrCreateDedicatedPool(new RespireEndpoint(replacement.Host, replacement.Port));
            }

            if (!TryParseRedirect(error, source.Host, out var slot, out var endpoint))
            {
                throw error;
            }

            for (var attempt = 0; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (error.Code == RespireErrorCodes.Moved) SignalMovedTopologyRefresh();
                var node = GetOrCreateNode(endpoint, observe: error.Code != "ASK", redirect: true);
                try
                {
                    await EnsureRouteNodeConnectedAsync(node, cancellationToken, discovery).ConfigureAwait(false);
                    if (error.Code == RespireErrorCodes.Moved) SetSlotOwner(slot, node);
                    return GetOrCreateDedicatedPool(endpoint);
                }
                catch (Exception failure) when (CanRetryRetiredRedirect(failure, node, attempt, cancellationToken))
                {
                    discovery?.Failed(Endpoint(node), failure);
                    // Pool acquisition has not begun, so retrying cannot replay an accepted command.
                }
            }
        }
        catch (Exception failure)
        {
            scope.SetTerminalError(failure);
            if (ShouldPreserveRejection(failure, cancellationToken, discovery))
                ExceptionDispatchInfo.Capture(error).Throw();
            throw;
        }
    }

    private static bool CanRetryRetiredRedirect(Exception error, RespireConnectionMultiplexer node,
        int attempt, CancellationToken cancellationToken)
        => attempt < MaxRedirects && !cancellationToken.IsCancellationRequested
            && (error is RespireConnectionRetiredException || error is OperationCanceledException && node.IsRetired);

    internal DedicatedConnectionPool GetDedicatedPool(RespireEndpoint endpoint)
        => GetOrCreateDedicatedPool(endpoint);

    internal readonly record struct StreamRouteVersion(long RedirectVersion, long OwnerVersion);

    internal StreamRouteVersion CaptureSlotVersion(int? slot)
    {
        if (slot is not { } value) return default;
        // PublishSlotLocked writes the version before the owner. Do not capture the new
        // version while pool selection can still observe the previous owner.
        // Discovery preserves the redirect version but advances the owner-mutation fence.
        // Capture both so ASK cannot outlive discovery changes, including owner A -> B -> A.
        lock (_nodesGate) return new(_slotVersions[value], _slotFences.Version(value));
    }

    internal async ValueTask<(DedicatedConnectionPool Pool, StreamRouteVersion SlotVersion)> GetDedicatedStreamPoolAsync(
        int? slot, CancellationToken cancellationToken, DiscoveryRound? discovery)
    {
        // Validate this snapshot before the upload header, where the caller's bounded retry loop
        // can handle topology churn without spinning indefinitely during pool selection.
        var slotVersion = CaptureSlotVersion(slot);
        var pool = await GetDedicatedPoolAsync(slot, cancellationToken, discovery).ConfigureAwait(false);
        return (pool, slotVersion);
    }

    internal bool IsDedicatedStreamRouteCurrent(int? slot, StreamRouteVersion slotVersion, RespireConnection connection,
        DedicatedConnectionPool? askingPool = null)
    {
        if (slot is not { } value) return true;
        lock (_nodesGate)
        {
            if (_slotVersions[value] != slotVersion.RedirectVersion
                || _slotFences.Version(value) != slotVersion.OwnerVersion) return false;
            if (askingPool is not null)
            {
                // ASK bypasses the slot owner, but never the target's MOVING publication.
                // Pool replacement precedes retirement, so IsStopping alone is insufficient.
                foreach (var (node, pool) in _dedicatedPools)
                    if (ReferenceEquals(pool, askingPool))
                        return !node.IsRetired
                            && node.ActiveConnectionEndpoint == new RespireEndpoint(connection.Host, connection.Port);
                return false;
            }
            // Read the version and owner together; a half-published route must not validate.
            // With no discovered owner, the selected seed is still eligible. Learning an owner
            // changes the owner fence, so that publication invalidates this provisional route.
            return _slots[value] is not { } owner
                || owner.ActiveConnectionEndpoint == new RespireEndpoint(connection.Host, connection.Port);
        }
    }

    internal ValueTask<(DedicatedConnectionPool Pool, RespireConnection Connection)> RentDedicatedConnectionAsync(
        DedicatedConnectionPool pool, int? slot, CancellationToken cancellationToken, DiscoveryRound? discovery,
        bool reuseIdle = true, DedicatedLeaseKind kind = DedicatedLeaseKind.Ordinary)
        => RentDedicatedConnectionAsync(pool, new DedicatedRoute(slot), cancellationToken, discovery, reuseIdle, kind);

    /// <summary>
    /// Where a dedicated rent reselects its pool after topology retirement: the slot's route under
    /// a read policy, or the importing node of a pending ASK redirect.
    /// </summary>
    internal readonly record struct DedicatedRoute(
        int? Slot,
        RespireReadFrom ReadFrom = RespireReadFrom.Primary,
        RespireServerException? AskRedirect = null,
        RespireConnection? RedirectSource = null);

    private ValueTask<DedicatedConnectionPool> ReselectDedicatedPoolAsync(
        DedicatedRoute route, CancellationToken cancellationToken, DiscoveryRound? discovery)
        => route.AskRedirect is { } ask
            ? GetRedirectDedicatedPoolAsync(ask, route.RedirectSource!, cancellationToken, route.Slot, discovery)
            : GetReadDedicatedPoolAsync(route.Slot, route.ReadFrom, cancellationToken, discovery);

    internal ValueTask<(DedicatedConnectionPool Pool, RespireConnection Connection)> RentDedicatedConnectionAsync(
        DedicatedConnectionPool pool, DedicatedRoute route, CancellationToken cancellationToken, DiscoveryRound? discovery,
        bool reuseIdle = true, DedicatedLeaseKind kind = DedicatedLeaseKind.Ordinary, string? preferredZone = null)
        => DedicatedLeaseAcquisition.RentAsync(pool, new DedicatedLeaseRoute(this, route, discovery),
            cancellationToken, reuseIdle, kind, preferredZone);

    private struct DedicatedLeaseRoute(ClusterRouter owner, DedicatedRoute route, DiscoveryRound? discovery) : IDedicatedLeaseRoute
    {
        // Ordinary rents need no discovery scope. Create one only after topology retirement
        // invalidates the selected pool, then share it across every subsequent reselection.
        private DiscoveryScope _scope;
        public void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref owner._disposed) != 0, owner);
        public bool CanRetry(int attempt, CancellationToken cancellationToken) => owner.CanRetryRetirement(attempt, cancellationToken);
        public void RecordRetirement(Exception error, int attempt)
        {
            if (attempt == 0)
            {
                _scope = owner.BeginDiscovery(discovery);
                discovery = _scope.Round;
            }
            discovery?.Failed(error);
        }
        public ValueTask<DedicatedConnectionPool> SelectReplacementAsync(CancellationToken cancellationToken)
            => owner.ReselectDedicatedPoolAsync(route, cancellationToken, discovery);
        public void SetTerminalError(Exception error) => _scope.SetTerminalError(error);
        public void Dispose() => _scope.Dispose();
    }

    internal ValueTask RetireConnectionAsync(RespireEndpoint endpoint, long serverClientId)
        => GetOrCreateNode(endpoint).RetireConnectionAsync(serverClientId);

    internal RespireConnectionMultiplexer GetMultiplexer(RespireEndpoint endpoint)
        => GetOrCreateNode(endpoint);

    internal bool HasReliableCorrectionOrdering(RespireConnection connection)
        => connection.Multiplexer?.HasReliableCorrectionOrdering == true;

    // Learn routing for a new attempt without sending any part of the rejected watched transaction.
    internal void LearnWatchedRoute(
        RespireServerException error, RespireConnection source, int? watchedSlot)
    {
        if (error.Code == RespireErrorCodes.Moved
            && TryParseRedirect(error, source.Host, out var slot, out var endpoint))
        {
            SignalMovedTopologyRefresh();
            SetSlotOwner(slot, GetOrCreateNode(endpoint, redirect: true));
        }
        else if (error.Code == RespireErrorCodes.ReadOnly && watchedSlot is { } value)
        {
            var owner = Volatile.Read(ref _slots[value]);
            if (owner is not null && IsSameEndpoint(owner, source)) ClearSlotOwner(value, owner);
        }
        // ASK is temporary: leave the permanent route unchanged and let a fresh attempt retry later.
    }

    internal static bool IsRedirect(RespireServerException error)
        => error.Code is RespireErrorCodes.Moved or RespireErrorCodes.Ask;

    internal static bool CanRecover(RespireServerException error, int? commandSlot)
        => IsRedirect(error) || (commandSlot is not null && error.Code == RespireErrorCodes.ReadOnly);

    private async ValueTask<RespireConnectionMultiplexer> RefreshReadOnlyOwnerAsync(
        RespireServerException error, RespireConnection source, int slot, CancellationToken cancellationToken, DiscoveryRound? discovery)
    {
        try
        {
            // Every command that joins another READONLY flight still needs its own
            // retry round seeded with this rejected source before fallback recovery.
            if (discovery is { HasPendingFailure: false })
                discovery.Failed(new RespireEndpoint(source.Host, source.Port), error);
            var join = JoinReadOnlyRefresh(error, source, slot, discovery);
            _ = await AwaitSharedRefreshAsync(join.Flight, cancellationToken, discovery).ConfigureAwait(false);
            var owner = Volatile.Read(ref _slots[slot]);
            if ((owner is null || IsSameEndpoint(owner, source)) && join.NeedsOwnSlotRecovery)
            {
                // A shared flight repairs its initiating slot, or performs full discovery.
                // Recheck this rejected route with the slot-specific recovery when it remains stale.
                owner = await RefreshReadOnlyOwnerCoreAsync(error, source, slot, cancellationToken, discovery)
                    .ConfigureAwait(false);
            }
            if (owner is null || IsSameEndpoint(owner, source))
                ExceptionDispatchInfo.Capture(error).Throw();
            // The shared flight already waited its backoff and connected the repaired owner.
            // Waiting this caller's pending retry again would delay, or cancel, a finished recovery.
            if (!owner.IsConnected)
                await EnsureRouteNodeConnectedAsync(owner, cancellationToken, discovery).ConfigureAwait(false);
            return owner;
        }
        catch (Exception failure) when (!cancellationToken.IsCancellationRequested
            && (ReferenceEquals(failure, error) || failure is OperationCanceledException || IsDiscoveryFailure(failure)))
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }
        throw new InvalidOperationException("Unreachable after rethrowing the original READONLY error.");
    }

    private async ValueTask<RespireConnectionMultiplexer> RefreshReadOnlyOwnerCoreAsync(
        RespireServerException error, RespireConnection source, int slot, CancellationToken cancellationToken, DiscoveryRound? discovery)
    {
        var owner = Volatile.Read(ref _slots[slot]);
        if (owner is not null && IsSameEndpoint(owner, source))
        {
            ClearSlotOwner(slot, owner);
        }
        var (primaries, fallbacks) = BuildReadOnlyCandidates(source);
        using var budget = new ClusterRecoveryBudget(cancellationToken, _options.ConnectTimeout);
        try
        {
            // Cached-owner probing and all discovered primaries share one half-round phase.
            // Their count cannot consume the time reserved for configured seeds.
            var cached = await TryConnectReadOnlyOwnerAsync(slot, source, budget.PrimaryToken, budget.Token, discovery)
                .ConfigureAwait(false);
            var replacement = cached.Replacement;
            if (replacement is not null)
            {
                return replacement;
            }
            replacement = await TryReadOnlyCandidatesAsync(primaries, source, slot, budget,
                primaryPhase: true, cached.Unavailable, discovery).ConfigureAwait(false);
            replacement ??= await TryReadOnlyCandidatesAsync(fallbacks, source, slot, budget,
                primaryPhase: false, cached.Unavailable, discovery).ConfigureAwait(false);
            if (replacement is not null)
            {
                return replacement;
            }
        }
        catch (OperationCanceledException cancelled) when (budget.IsCallerCancellation(cancelled, cancellationToken))
        {
            // Recovery phase tokens are private links; expose the initiating deadline or caller token.
            throw new OperationCanceledException(cancelled.Message, cancelled, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested
            && (exception is OperationCanceledException || IsDiscoveryFailure(exception)))
        {
            // Preserve the original rejection and its stack when discovery expires or fails.
        }

        ExceptionDispatchInfo.Capture(error).Throw();
        throw new InvalidOperationException("Unreachable after rethrowing the original READONLY error.");
    }

    private async ValueTask<RespireConnectionMultiplexer?> TryReadOnlyCandidatesAsync(
        List<RespireConnectionMultiplexer> candidates, RespireConnection source, int slot,
        ClusterRecoveryBudget budget, bool primaryPhase, RespireConnectionMultiplexer? unavailableOwner,
        DiscoveryRound? discovery)
    {
        for (var index = 0; index < candidates.Count; index++)
        {
            if (primaryPhase && budget.PrimaryToken.IsCancellationRequested)
            {
                break;
            }
            var candidate = candidates[index];
            if (ReferenceEquals(candidate, unavailableOwner) || discovery?.HasRejected(candidate) == true)
            {
                continue;
            }
            budget.Token.ThrowIfCancellationRequested();
            // Reserve the remainder for the final usable seed, excluding every generation
            // already rejected in this round. The demoted source is only a best-effort fallback.
            var lastUsableSeed = candidates.Count - 1;
            while (lastUsableSeed >= 0
                && (ReferenceEquals(candidates[lastUsableSeed], unavailableOwner)
                    || discovery?.HasRejected(candidates[lastUsableSeed]) == true
                    || IsSameEndpoint(candidates[lastUsableSeed], source)))
            {
                lastUsableSeed--;
            }
            var attemptToken = primaryPhase ? budget.PrimaryToken
                : budget.GetFallbackToken(index >= lastUsableSeed);
            if (attemptToken.IsCancellationRequested)
            {
                continue;
            }
            var snapshotBatch = new object();
            if (!await TryDiscoverReadOnlyOwnerAsync(candidate, attemptToken, budget.Token, discovery, snapshotBatch)
                    .ConfigureAwait(false))
            {
                continue;
            }
            var discovered = await TryConnectReadOnlyOwnerAsync(slot, source, attemptToken, budget.Token, discovery)
                .ConfigureAwait(false);
            if (discovered.Replacement is { } replacement)
            {
                return replacement;
            }
            if (discovery is not null && !discovery.HasPendingFailure)
                discovery.Failed(Endpoint(candidate), new RespireConnectionException("Cluster candidate did not provide a replacement owner."));
            unavailableOwner = discovered.Unavailable ?? unavailableOwner;
        }
        return null;
    }

    private (List<RespireConnectionMultiplexer> Primaries, List<RespireConnectionMultiplexer> Fallbacks)
        BuildReadOnlyCandidates(RespireConnection source)
    {
        var fallbacks = new List<RespireConnectionMultiplexer>();
        var seeds = new HashSet<RespireConnectionMultiplexer>();
        foreach (var seed in _seeds)
        {
            var node = GetOrCreateNode(seed);
            if (!IsSameEndpoint(node, source) && seeds.Add(node))
            {
                fallbacks.Add(node);
            }
        }
        var primaries = new List<RespireConnectionMultiplexer>(Volatile.Read(ref _masters));
        primaries.RemoveAll(node => IsSameEndpoint(node, source) || seeds.Contains(node));
        fallbacks.Add(GetOrCreateNode(new RespireEndpoint(source.Host, source.Port)));
        return (primaries, fallbacks);
    }

    private async ValueTask<bool> TryDiscoverReadOnlyOwnerAsync(
        RespireConnectionMultiplexer candidate, CancellationToken attemptToken, CancellationToken roundToken,
        DiscoveryRound? discovery, object snapshotBatch)
    {
        try
        {
            await EnsureRouteNodeConnectedAsync(candidate, attemptToken, discovery).ConfigureAwait(false);
            if (!(await TryLoadSlotsAsync(candidate, attemptToken, snapshotBatch: snapshotBatch).ConfigureAwait(false)).Loaded)
                discovery?.Failed(Endpoint(candidate), new RespireConnectionException("Cluster candidate did not provide topology."));
            return true;
        }
        catch (Exception exception) when (discovery?.Exhaustion is null && !roundToken.IsCancellationRequested
            && (IsDiscoveryFailure(exception) || exception is OperationCanceledException))
        {
            discovery?.Failed(Endpoint(candidate), exception);
            return false;
        }
    }

    private async ValueTask<(RespireConnectionMultiplexer? Replacement, RespireConnectionMultiplexer? Unavailable)>
        TryConnectReadOnlyOwnerAsync(int slot, RespireConnection source,
            CancellationToken attemptToken, CancellationToken roundToken, DiscoveryRound? discovery)
    {
        var current = Volatile.Read(ref _slots[slot]);
        if (current is null)
        {
            return default;
        }
        RespireConnectionMultiplexer? unavailable = null;
        if (!IsSameEndpoint(current, source))
        {
            try
            {
                await EnsureRouteNodeConnectedAsync(current, attemptToken, discovery).ConfigureAwait(false);
                return (current, null);
            }
            catch (Exception exception) when (discovery?.Exhaustion is null && !roundToken.IsCancellationRequested
                && (IsDiscoveryFailure(exception) || exception is OperationCanceledException))
            {
                // An unavailable cached replacement must not prevent seed discovery.
                discovery?.Failed(Endpoint(current), exception);
                unavailable = current;
            }
        }
        ClearSlotOwner(slot, current);
        return (null, unavailable);
    }

    private static bool IsSameEndpoint(RespireConnectionMultiplexer node, RespireConnection source)
        => node.Port == source.Port && string.Equals(node.Host, source.Host, StringComparison.OrdinalIgnoreCase);

    // Configuration failures remain actionable; cancellation belongs to the caller.
    private static bool CanRetryConnectionFailure(Exception error, CancellationToken cancellationToken)
        => error is not (RespireConfigurationException or DiscoveryRoundUsageException)
            && !cancellationToken.IsCancellationRequested;

    private bool CanRetryDiscoveryFailure(Exception error, CancellationToken cancellationToken, DiscoveryRound? discovery)
        => discovery?.Exhaustion is null && Volatile.Read(ref _disposed) == 0
            && CanRetryConnectionFailure(error, cancellationToken);

    private static bool IsDiscoveryFailure(Exception exception)
        => exception is (RespireException and not RespireConfigurationException)
            or IOException or System.Net.Sockets.SocketException
            or System.Security.Authentication.AuthenticationException or TimeoutException;

    internal static bool TryParseRedirect(
        RespireServerException error,
        string sourceHost,
        out int slot,
        out RespireEndpoint endpoint)
    {
        slot = 0;
        endpoint = default;
        if (!IsRedirect(error))
        {
            return false;
        }

        var message = error.Message.AsSpan();
        var firstSpace = message.IndexOf(' ');
        if (firstSpace < 0)
        {
            return false;
        }

        message = message[(firstSpace + 1)..];
        var secondSpace = message.IndexOf(' ');
        if (secondSpace < 0 || !int.TryParse(message[..secondSpace], out slot)
            || (uint)slot >= ClusterHash.SlotCount)
        {
            return false;
        }

        var address = message[(secondSpace + 1)..].Trim();
        var colon = address.LastIndexOf(':');
        if (colon < 0 || !int.TryParse(address[(colon + 1)..], out var port))
        {
            return false;
        }

        var host = address[..colon].Trim();
        if (host.Length >= 2 && host[0] == '[' && host[^1] == ']')
        {
            host = host[1..^1];
        }

        // Redis uses ? when hostname routing is configured but the target has no announced
        // hostname. It is not a DNS name and must not use the empty-host fallback.
        if (host.SequenceEqual("?"))
        {
            return false;
        }

        endpoint = new RespireEndpoint(host.IsEmpty ? sourceHost : host.ToString(), port);
        return true;
    }

    internal static int RedirectLimit => MaxRedirects;

    internal static ValueTask<Respire.Protocol.RespValue> SendAskingAsync<TCommand>(
        RespireConnection connection,
        in TCommand command,
        CancellationToken cancellationToken,
        string? commandName = null,
        CommandDeadline commandDeadline = default,
        bool allowStreamingConnectionReroute = true,
        DedicatedStreamRoute streamingRoute = default)
        where TCommand : struct, Respire.Protocol.IRespCommand
    {
        if (command is StreamedSetCommand streamedSet)
            return connection.SendAskingStreamedSetAsync(in Asking, streamedSet, cancellationToken, commandDeadline,
                streamingRoute);

        return connection.SendPrefixedCheckedAsync(in Asking, in command, cancellationToken, commandName,
            commandDeadline, allowStreamingConnectionReroute);
    }

    internal static ValueTask<Respire.Protocol.RespValue> SendTrackedAskingAsync<TCommand>(
        RespireConnection connection,
        in TCommand command,
        CancellationToken cancellationToken,
        string commandName = "(command)")
        where TCommand : struct, Respire.Protocol.IRespCommand
    {
        if (command is StreamedSetCommand)
            // Streamed SET is a write; CLIENT CACHING only applies to a subsequent read.
            return SendAskingAsync(connection, in command, cancellationToken, commandName);

        var caching = new ClientCachingCommand();
        return connection.SendValidatedPrefixedAsync(
            in Asking, in caching, in command, cancellationToken, commandName);
    }

    internal static ValueTask<Stream?> SendAskingBulkStreamAsync<TCommand>(
        RespireConnection connection,
        in TCommand command,
        CancellationToken cancellationToken,
        string? commandName = null,
        Action<Exception?>? onFrameCompleted = null)
        where TCommand : struct, Respire.Protocol.IRespCommand
         => connection.SendPrefixedBulkStreamAsync(
             in Asking, in command, cancellationToken, commandName, onFrameCompleted);

    internal static ValueTask<Respire.Protocol.RespValue> SendAskingUncheckedAsync<TCommand>(
        RespireConnection connection,
        in TCommand command,
        CancellationToken cancellationToken,
        bool armCommandDeadline = true,
        bool pinToConnection = false)
        where TCommand : struct, Respire.Protocol.IRespCommand
        => connection.SendPrefixedAsync(
            in Asking, in command, throwOnError: false, cancellationToken,
            commandName: null, armCommandDeadline, pinToConnection);

    internal static ValueTask<Respire.Protocol.RespValue> SendBlockingAskingUncheckedAsync<TCommand>(
        RespireConnection connection,
        in TCommand command,
        CancellationToken cancellationToken)
        where TCommand : struct, Respire.Protocol.IRespCommand
        => connection.SendPrefixedWithoutResponseTimeoutAsync(
            Asking, command, throwOnError: false, cancellationToken);

    internal async ValueTask<RespireConnection[]> GetMasterConnectionsAsync(
        CancellationToken cancellationToken, DiscoveryRound? discovery)
    {
        using var scope = BeginDiscovery(discovery);
        discovery = scope.Round;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                try { return await GetMasterConnectionsCoreAsync(cancellationToken, discovery).ConfigureAwait(false); }
                catch (RespireConnectionRetiredException error) when (CanRetryRetirement(attempt, cancellationToken))
                {
                    discovery?.Failed(error);
                }
            }
        }
        catch (Exception error)
        {
            scope.SetTerminalError(error);
            throw;
        }
    }

    private async ValueTask<RespireConnection[]> GetMasterConnectionsCoreAsync(CancellationToken cancellationToken, DiscoveryRound? discovery)
    {
        var snapshotBatch = new object();
        var expectedTopologyVersion = CaptureTopologyVersion();
        var masters = new HashSet<RespireConnectionMultiplexer>(ReferenceEqualityComparer.Instance);
        AddKnownMasters(masters);

        var refreshed = false;
        RespireConnectionMultiplexer? attemptedSeed = null;
        if (Volatile.Read(ref _seed) is { IsConnected: true, IsRetired: false } seed
            && discovery?.HasRejected(seed) != true)
        {
            attemptedSeed = seed;
            refreshed = await TryRefreshTopologyAsync(seed, cancellationToken, discovery, expectedTopologyVersion,
                    snapshotBatch)
                .ConfigureAwait(false);
        }

        if (!refreshed)
        {
            foreach (var master in masters)
            {
                // The connected seed is also a known master. Its failed query has already
                // seeded the round; reserve the fallback budget for a different candidate.
                if (ReferenceEquals(master, attemptedSeed) || discovery?.HasRejected(master) == true) continue;
                if (await TryRefreshTopologyAsync(master, cancellationToken, discovery, expectedTopologyVersion,
                        snapshotBatch)
                    .ConfigureAwait(false))
                {
                    SetSeed(master);
                    refreshed = true;
                    break;
                }
            }
        }

        if (!refreshed)
        {
            await EnsureConnectedAsync(cancellationToken, discovery).ConfigureAwait(false);
            var fallbackSeed = Volatile.Read(ref _seed)!;
            await EnsureRouteNodeConnectedAsync(fallbackSeed, cancellationToken, discovery).ConfigureAwait(false);
            var loaded = (await TryLoadSlotsAsync(fallbackSeed, cancellationToken,
                expectedTopologyVersion: expectedTopologyVersion, snapshotBatch: snapshotBatch).ConfigureAwait(false)).Loaded;
            if (!loaded || !HasCompleteTopology())
            {
                throw new RespireConnectionException(
                    "Unable to load a complete Redis Cluster topology for a cluster-wide command.");
            }
        }

        masters.Clear();
        AddKnownMasters(masters);
        if (masters.Count == 0)
        {
            masters.Add(Volatile.Read(ref _seed)!);
        }

        var connections = new RespireConnection[masters.Count];
        var index = 0;
        foreach (var master in masters)
        {
            // TryLoadSlotsAsync replaces stale owners after a successful refresh. Any owner
            // still present is current; omitting it would make SCAN and cluster-wide server
            // commands silently return incomplete results.
            await EnsureRouteNodeConnectedAsync(master, cancellationToken, discovery).ConfigureAwait(false);
            connections[index++] = master.GetConnection();
        }

        return connections;
    }

    internal ValueTask<RespireConnectionMultiplexer[]> GetKnownMastersAsync(
        CancellationToken cancellationToken, DiscoveryRound? discovery)
    {
        if (!HasCompleteTopology())
        {
            return RefreshKnownMastersAsync(cancellationToken, discovery);
        }

        var masters = Volatile.Read(ref _masters);
        foreach (var master in masters)
        {
            if (!master.IsConnected)
            {
                return ConnectKnownMastersAsync(masters, cancellationToken, discovery);
            }
        }

        return new ValueTask<RespireConnectionMultiplexer[]>(masters);
    }

    private async ValueTask<RespireConnectionMultiplexer[]> ConnectKnownMastersAsync(
        RespireConnectionMultiplexer[] masters,
        CancellationToken cancellationToken, DiscoveryRound? discovery)
    {
        using var scope = BeginDiscovery(discovery);
        discovery = scope.Round;
        try
        {
            foreach (var master in masters)
            {
                await EnsureRouteNodeConnectedAsync(master, cancellationToken, discovery).ConfigureAwait(false);
            }

            return masters;
        }
        catch (Exception error) when (CanRetryDiscoveryFailure(error, cancellationToken, discovery))
        {
            try { return await RefreshKnownMastersAsync(cancellationToken, discovery).ConfigureAwait(false); }
            catch (Exception failure) { scope.SetTerminalError(failure); throw; }
        }
        catch (Exception error) { scope.SetTerminalError(error); throw; }
    }

    private async ValueTask<RespireConnectionMultiplexer[]> RefreshKnownMastersAsync(
        CancellationToken cancellationToken, DiscoveryRound? discovery)
    {
        _ = await GetMasterConnectionsAsync(cancellationToken, discovery).ConfigureAwait(false);
        return Volatile.Read(ref _masters);
    }

    private async ValueTask<bool> TryRefreshTopologyAsync(
        RespireConnectionMultiplexer node,
        CancellationToken cancellationToken, DiscoveryRound? discovery,
        long? expectedTopologyVersion = null, object? snapshotBatch = null, bool keepUncoveredOwners = false,
        int? requiredSlot = null, ReplicaRefreshRound? replicaRefresh = null)
    {
        try
        {
            await EnsureRouteNodeConnectedAsync(node, cancellationToken, discovery).ConfigureAwait(false);
            var load = await TryLoadSlotsAsync(node, cancellationToken,
                expectedTopologyVersion: expectedTopologyVersion, snapshotBatch: snapshotBatch,
                keepUncoveredOwners: keepUncoveredOwners, requiredSlot: requiredSlot,
                replicaRefresh: replicaRefresh).ConfigureAwait(false);
            // Retained owners are not evidence that this reply revalidated the requested slot.
            var complete = load.Loaded && (requiredSlot.HasValue ? load.CoversRequiredSlot : HasCompleteTopology());
            if (!complete) discovery?.FailedNode(node, new RespireConnectionException("Cluster candidate did not provide a complete topology."));
            return complete;
        }
        catch (Exception error) when (CanRetryDiscoveryFailure(error, cancellationToken, discovery))
        {
            discovery?.FailedNode(node, error);
            return false;
        }
    }

    private async ValueTask<RespireConnectionMultiplexer?> TryRefreshSlotThroughKnownMastersAsync(
        int slot,
        RespireConnectionMultiplexer? failedOwner,
        CancellationToken cancellationToken, DiscoveryRound? discovery, bool keepUncoveredOwners = false)
    {
        var snapshotBatch = new object();
        var expectedTopologyVersion = CaptureTopologyVersion();
        foreach (var master in Volatile.Read(ref _masters))
        {
            // A failed owner can still own other slots. Spend fallback budget on a distinct
            // generation instead of immediately retrying the already rejected connection.
            if (ReferenceEquals(master, failedOwner) || discovery?.HasRejected(master) == true) continue;
            var refreshed = await TryRefreshTopologyAsync(master, cancellationToken, discovery, expectedTopologyVersion, snapshotBatch,
                    keepUncoveredOwners, requiredSlot: keepUncoveredOwners ? slot : null).ConfigureAwait(false);
            if (!refreshed)
            {
                continue;
            }

            var owner = Volatile.Read(ref _slots[slot]);
            if (owner is null)
            {
                continue;
            }

            try
            {
                await EnsureRouteNodeConnectedAsync(owner, cancellationToken, discovery).ConfigureAwait(false);
                SetSeed(master);
                return owner;
            }
            catch (Exception error) when (CanRetryDiscoveryFailure(error, cancellationToken, discovery))
            {
                discovery?.FailedNode(owner, error);
                ClearSlotOwner(slot, owner);
            }
        }

        return null;
    }

    internal async ValueTask<RespireEndpoint> GetPubSubEndpointAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var scope = BeginDiscovery(null);
        var discovery = scope.Round;
        try
        {
            foreach (var master in Volatile.Read(ref _masters))
            {
                try
                {
                    await EnsureRouteNodeConnectedAsync(master, cancellationToken, discovery).ConfigureAwait(false);
                    return Endpoint(master);
                }
                catch (Exception error) when (CanRetryDiscoveryFailure(error, cancellationToken, discovery))
                {
                    discovery?.FailedNode(master, error);
                }
            }
            await EnsureConnectedAsync(cancellationToken, discovery).ConfigureAwait(false);
            var endpoint = SeedEndpoint;
            if (discovery is not null) await discovery.BeforeCandidateAsync(endpoint, cancellationToken).ConfigureAwait(false);
            return endpoint;
        }
        catch (Exception error)
        {
            scope.SetTerminalError(error);
            throw;
        }
    }

    private bool HasCompleteTopology()
        => Volatile.Read(ref _hasCompleteTopology) != 0;

    private void AddKnownMasters(HashSet<RespireConnectionMultiplexer> masters)
    {
        foreach (var node in Volatile.Read(ref _masters))
        {
            masters.Add(node);
        }
    }

    private RespireConnectionMultiplexer? TryGetConnectedNode()
    {
        if (Volatile.Read(ref _seed) is { IsConnected: true, IsRetired: false } seed)
        {
            return seed;
        }

        foreach (var master in Volatile.Read(ref _masters))
        {
            if (master.IsConnected && !master.IsRetired)
            {
                return master;
            }
        }

        return null;
    }

    internal RespireConnectionMultiplexer GetOrCreateNode(RespireEndpoint endpoint, bool observe = true, bool redirect = false)
    {
        lock (_nodesGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var node = _identities.GetOrCreate(endpoint);
            if (redirect)
            {
                // ASK protects its endpoint mapping without changing permanent slot ownership.
                _redirectVersions[node] = ++_topologyVersion;
            }
            if (observe)
            {
                ObserveNode(node);
            }

            return node;
        }
    }

    internal void ApplyTopology(List<ClusterTopologyRange> ranges, long expectedVersion, long discoveryGeneration)
        => ApplyTopologyCore(ranges, expectedVersion, discoveryGeneration, keepUncoveredOwners: false,
            snapshotBatch: null);

    // keepUncoveredOwners: a slot the reply does not cover keeps its current owner, protected like
    // a slot a newer redirect changed. Background refresh uses this so a partial map (a lost shard
    // with cluster-require-full-coverage no, or a node with an incomplete view) still publishes the
    // ownership it does report without dropping the rest. Within one fallback batch, the first
    // reply covering a slot also stays authoritative while later candidates fill uncovered slots.
    // Replica metadata is retained as well.
    // Primary and replica publication share _nodesGate and the same slot/version fences.
    // Replica sets keep immutable membership; their cursor and refresh throttle are separate
    // mutable coordination state. Unchanged membership must retain that state. A future shared
    // topology snapshot (issue #738) must preserve these fences and uncovered-slot ownership.
    private void ApplyTopologyCore(List<ClusterTopologyRange> ranges, long expectedVersion, long discoveryGeneration,
        bool keepUncoveredOwners, object? snapshotBatch)
    {
        List<RespireConnectionMultiplexer>? retiredNodes;
        List<RetiredGeneration> retirements;
        RespireEndpoint[] publishedEndpoints;
        long publishedTopologyVersion;
        bool topologyChanged;
        HashSet<RespireConnectionMultiplexer>? retiredReplicaNodes = null;
        lock (_nodesGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            // Slot fences protect MOVED updates independently. A completed discovery also
            // fences the identity index and seed against older in-flight discoveries.
            if (discoveryGeneration <= _publishedDiscoveryGeneration)
            {
                return;
            }
            HashSet<RespireConnectionMultiplexer>? protectedNodes = null;
            for (var slot = 0; slot < _slots.Length; slot++)
            {
                if (_slotVersions[slot] > expectedVersion && _slots[slot] is { } node)
                {
                    (protectedNodes ??= []).Add(node);
                }
            }
            foreach (var (node, version) in _redirectVersions)
            {
                if (version > expectedVersion)
                {
                    (protectedNodes ??= []).Add(node);
                }
            }
            var coveredSlots = new bool[ClusterHash.SlotCount];
            foreach (var range in ranges)
            {
                coveredSlots.AsSpan(range.Start, range.End - range.Start + 1).Fill(true);
            }
            bool[]? keptSlots = null;
            if (keepUncoveredOwners)
            {
                for (var slot = 0; slot < coveredSlots.Length; slot++)
                {
                    if (!coveredSlots[slot] && _slots[slot] is { } owner)
                    {
                        keptSlots ??= new bool[ClusterHash.SlotCount];
                        keptSlots[slot] = true;
                        (protectedNodes ??= []).Add(owner);
                    }
                }
            }
            var resolved = _identities.ApplySnapshot(ranges, protectedNodes);
            var replicas = MergeReplicas(ranges);
            if (keptSlots is not null)
            {
                // Replicas are not tied to their primary here; keeping the previous entries is
                // harmless because they are only refresh fallbacks.
                var current = new HashSet<RespireEndpoint>(RespireEndpointComparer.Instance);
                foreach (var replica in replicas) current.Add(replica.Endpoint);
                var keptReplicas = new List<ClusterTopologyReplica>(replicas);
                foreach (var previous in Volatile.Read(ref _replicas))
                {
                    if (current.Add(previous.Endpoint)) keptReplicas.Add(previous);
                }
                replicas = keptReplicas.ToArray();
            }
            Volatile.Write(ref _replicas, replicas);
            var refreshedSlots = new RespireConnectionMultiplexer?[ClusterHash.SlotCount];
            var refreshedReplicas = new ClusterReplicaSet?[ClusterHash.SlotCount];
            foreach (var (range, node) in resolved)
            {
                var replicaNodes = range.Replicas
                    .Where(replica => !MatchesPrimary(replica, range.Preferred, range.NodeId, range.Aliases))
                    .Select(_identities.GetOrCreateReplica).Distinct().ToArray();
                // Unchanged routes keep their set, preserving its cursor and refresh throttle,
                // and this refresh has just confirmed them.
                ClusterReplicaSet replicaSet;
                if (_replicasBySlot[range.Start] is { } current
                    && !ReferenceEquals(current, _unknownReplicaRoutes) && current.HasSameNodes(replicaNodes))
                {
                    replicaSet = current;
                    replicaSet.MarkValidated(_options.ReplicaRouteRevalidationInterval);
                }
                else
                {
                    replicaSet = new ClusterReplicaSet(replicaNodes, _options.ReplicaRouteRevalidationInterval);
                }
                for (var slot = range.Start; slot <= range.End; slot++)
                {
                    refreshedSlots[slot] = node;
                    refreshedReplicas[slot] = replicaSet;
                }
            }
            if (snapshotBatch is not null)
            {
                for (var slot = 0; slot < _slotSnapshotBatches.Length; slot++)
                {
                    if (ReferenceEquals(_slotSnapshotBatches[slot], snapshotBatch))
                    {
                        refreshedSlots[slot] = _slots[slot];
                        refreshedReplicas[slot] = Volatile.Read(ref _replicasBySlot[slot]);
                    }
                }
            }
            if (keptSlots is not null)
            {
                for (var slot = 0; slot < keptSlots.Length; slot++)
                {
                    if (keptSlots[slot])
                    {
                        refreshedSlots[slot] = _slots[slot];
                        refreshedReplicas[slot] = Volatile.Read(ref _replicasBySlot[slot]);
                    }
                }
            }

            var previousReplicaNodes = Volatile.Read(ref _replicaNodes);
            retiredNodes = ReplaceSlotOwnersLocked(
                refreshedSlots, refreshedReplicas, coveredSlots, expectedVersion, snapshotBatch,
                out topologyChanged, out var activeReplicas);
            if (retiredNodes is not null && previousReplicaNodes.Length != 0)
                retiredReplicaNodes = new HashSet<RespireConnectionMultiplexer>(retiredNodes.Where(previousReplicaNodes.Contains));
            publishedTopologyVersion = topologyChanged ? ++_topologyVersion : _topologyVersion;
            // Discovery publishes the primaries that own slots in the reply. A primary omitted
            // from a partial map has lost its slots (usually mid-failover) and is dropped; its
            // promoted replica appears in a later discovery.
            publishedEndpoints = Enumerable.Range(0, _masters.Length)
                .Where(index => _masterSlotCounts[index] != 0 && !_masters[index].IsRetired)
                .Select(index => Endpoint(_masters[index])).Distinct().ToArray();
            Volatile.Write(ref _replicaNodes, activeReplicas.ToArray());
            retirements = RetireInactiveLocked(protectedNodes);
            _publishedDiscoveryGeneration = discoveryGeneration;
            // Older discoveries can no longer publish; later requests capture these versions.
            foreach (var (node, version) in _redirectVersions)
            {
                if (version <= expectedVersion)
                {
                    _redirectVersions.Remove(node);
                }
            }
        }

        // Launch cleanup before callbacks: a callback may synchronously dispose the client.
        foreach (var retirement in retirements) _ = DrainGenerationAsync(retirement);
        if (retiredNodes is not null)
        {
            foreach (var node in retiredNodes)
            {
                if (retiredReplicaNodes?.Contains(node) == true) ReplicaNodeRetired?.Invoke(node);
                else NodeRetired?.Invoke(node);
            }
        }
        if (topologyChanged) TopologyChanged?.Invoke(publishedTopologyVersion, publishedEndpoints, true);
    }

    // A replica that serves several slot ranges is listed once per range. Merge those entries by
    // endpoint: aliases are combined, and the first advertised node ID wins because every entry for
    // one endpoint describes the same Redis node. This runs once per published topology.
    private static ClusterTopologyReplica[] MergeReplicas(List<ClusterTopologyRange> ranges)
    {
        var merged = new Dictionary<RespireEndpoint, (string? NodeId, List<RespireEndpoint> Aliases)>();
        var order = new List<RespireEndpoint>();
        foreach (var range in ranges)
        {
            foreach (var replica in range.Replicas)
            {
                if (MatchesPrimary(replica, range.Preferred, range.NodeId, range.Aliases)) continue;
                if (!merged.TryGetValue(replica.Endpoint, out var entry))
                {
                    entry = (replica.NodeId, []);
                    order.Add(replica.Endpoint);
                }
                entry.NodeId ??= replica.NodeId;
                foreach (var alias in replica.Aliases)
                    if (!entry.Aliases.Contains(alias)) entry.Aliases.Add(alias);
                merged[replica.Endpoint] = entry;
            }
        }
        var result = new ClusterTopologyReplica[order.Count];
        for (var index = 0; index < order.Count; index++)
        {
            var (nodeId, aliases) = merged[order[index]];
            result[index] = new ClusterTopologyReplica(order[index], nodeId, aliases);
        }
        return result;
    }

    // Caller holds _nodesGate. Shared by discovery and SMIGRATED so their cleanup cannot drift:
    // detach every transport that no longer owns slots, except protected ones (ASK targets and
    // routes newer than the discovery), then keep the seed on a live transport.
    // The seed is resolved to its current identity before detaching, because detaching prunes
    // the old reverse mapping that resolution needs. If detaching retired the seed itself, the
    // second call moves it to a remaining master (or the first configured seed).
    private List<RetiredGeneration> RetireInactiveLocked(IEnumerable<RespireConnectionMultiplexer>? protectedNodes)
    {
        if (Volatile.Read(ref _seed) is { } previousSeed) SetSeedLocked(previousSeed);
        var retained = new HashSet<RespireConnectionMultiplexer>(_masters);
        retained.UnionWith(_replicaNodes);
        if (protectedNodes is not null) retained.UnionWith(protectedNodes);
        var retirements = DetachGenerationsLocked(_identities.DetachInactive(retained, _seeds));
        if (Volatile.Read(ref _seed) is { } seed) SetSeedLocked(seed);
        return retirements;
    }

    internal RespireConnectionMultiplexer? Seed => Volatile.Read(ref _seed);

    internal long TopologyVersion
    {
        get
        {
            lock (_nodesGate) return _topologyVersion;
        }
    }

    // Publish the current identity, even when discovery completed on a superseded transport.
    // Every caller has just connected to, or loaded a topology from, a cluster node. The first call
    // is the router's "connected" transition, so the background refresh worker starts here and
    // nowhere else.
    internal void SetSeed(RespireConnectionMultiplexer node)
    {
        lock (_nodesGate)
        {
            SetSeedLocked(node);
        }
        StartTopologyRefreshWorker();
    }

    // Caller holds _nodesGate, including topology publication.
    private void SetSeedLocked(RespireConnectionMultiplexer node)
    {
        var current = _identities.GetCurrent(node);
        if (!_identities.IsActive(current) || current.IsRetired)
            current = _masters.FirstOrDefault() ?? _identities.GetOrCreate(_seeds[0]);
        Volatile.Write(ref _seed, current);
    }

    // Called under _nodesGate: create a lazy transport without connecting or raising state events.
    private RespireConnectionMultiplexer CreateNode(RespireEndpoint endpoint, bool readOnly)
    {
        var connectionOptions = readOnly
            ? _commandConnectionOptions with
            {
                ReadOnly = true,
                EnableClientTracking = false,
                PushHandler = null,
                CredentialCacheInvalidation = null,
                CredentialCacheRetirementFence = null,
            }
            : _commandConnectionOptions;
        var category = readOnly
            ? $"Respire.Cluster.Replica.{endpoint.Host}:{endpoint.Port}"
            : $"Respire.Cluster.{endpoint.Host}:{endpoint.Port}";
        return RespireConnectionMultiplexer.Create(
            endpoint.Host, endpoint.Port, _options.Connections, connectionOptions, _options.CreateLogger(category));
    }

    private void ObserveNode(RespireConnectionMultiplexer node)
    {
        if (_nodeStateHandlers.ContainsKey(node))
        {
            return;
        }

        Action<int, RespireConnectionStateChange> handler = (slot, change) =>
        {
            if (change.State == RespireConnectionState.Reconnecting)
            {
                lock (_nodesGate) _migrations.ForgetSequence(node);
            }
            SlotStateChanged?.Invoke(node, slot, change);
            // A primary reports Disconnected for every slot it owns, on every reconnect attempt.
            // Once a forced refresh is queued, skip the master scan for the rest of the burst.
            if (change.State == RespireConnectionState.Disconnected
                && !_topologyRefresh.HasPendingForcedRequest
                && Array.IndexOf(Volatile.Read(ref _masters), node) >= 0) SignalPrimaryDisconnectRefresh();
        };
        _nodeStateHandlers.Add(node, handler);
        node.SlotStateChanged += handler;
        if (!_nodeMaintenanceHandlers.ContainsKey(node))
        {
            MaintenanceNotificationHandler maintenanceHandler = QueueSmigratedNotification;
            _nodeMaintenanceHandlers.Add(node, maintenanceHandler);
            node.MaintenanceNotificationReceived += maintenanceHandler;
        }
    }

    internal void SetSlotOwner(int slot, RespireConnectionMultiplexer node)
    {
        RespireConnectionMultiplexer? retiredNode = null;
        long topologyVersion;
        RespireEndpoint[]? topologyEndpoints;
        bool topologyAuthoritative;
        lock (_nodesGate)
        {
            if (_retiringNodes.ContainsKey(node) || node.IsRetired)
                throw new RespireConnectionRetiredException(node.Host, node.Port);
            ObserveNode(node);
            var previous = Volatile.Read(ref _slots[slot]);
            // A same-owner redirect still advances the discovery fence, so an older in-flight
            // discovery cannot overwrite it. It is not an owner mutation, so it does not fence
            // queued SMIGRATED notifications.
            // Stamp a changed owner before publication so a push observing it gets a newer token.
            if (!ReferenceEquals(previous, node)) MarkSlotMutatedLocked(slot);
            PublishSlotLocked(slot, node, ++_topologyVersion);
            // MOVED has no replica coverage. Unknown slots share work, while redundant
            // corrections preserve their independent throttle and changed owners reset it.
            if (!ReferenceEquals(previous, node)) _unknownReplicaDiscovery.Invalidate(slot);
            Volatile.Write(ref _replicasBySlot[slot], _unknownReplicaRoutes);
            if (ReferenceEquals(previous, node))
            {
                return;
            }
            AddSlot(node);
            if (previous is not null && RemoveSlot(previous))
            {
                retiredNode = previous;
            }
            topologyVersion = _topologyVersion;
            topologyEndpoints = _masters.Where((master, index) => _masterSlotCounts[index] != 0 && !master.IsRetired)
                .Select(static master => Endpoint(master)).Append(Endpoint(node)).Distinct().ToArray();
            topologyAuthoritative = HasCompleteTopology();
        }

        if (retiredNode is not null)
        {
            NodeRetired?.Invoke(retiredNode);
        }
        if (topologyEndpoints is not null) TopologyChanged?.Invoke(topologyVersion, topologyEndpoints, topologyAuthoritative);
    }

    internal void ClearSlotOwner(int slot, RespireConnectionMultiplexer node)
    {
        RespireConnectionMultiplexer? retiredNode = null;
        long topologyVersion;
        RespireEndpoint[] topologyEndpoints;
        lock (_nodesGate)
        {
            if (!ReferenceEquals(Volatile.Read(ref _slots[slot]), node))
            {
                return;
            }

            MarkSlotMutatedLocked(slot);
            PublishSlotLocked(slot, null, ++_topologyVersion);
            topologyVersion = _topologyVersion;
            Volatile.Write(ref _hasCompleteTopology, 0);
            if (RemoveSlot(node))
            {
                retiredNode = node;
            }
            // Clearing one cached owner proves nothing about other primaries. Publish the
            // remaining known set as non-authoritative so healthy routes are kept.
            topologyEndpoints = _masters.Where((master, index) => _masterSlotCounts[index] != 0 && !master.IsRetired)
                .Select(static master => Endpoint(master)).Distinct().ToArray();
        }

        if (retiredNode is not null)
        {
            NodeRetired?.Invoke(retiredNode);
        }
        TopologyChanged?.Invoke(topologyVersion, topologyEndpoints, false);
    }

    // Every slot publication carries its discovery-order fence under _nodesGate.
    private void PublishSlotLocked(int slot, RespireConnectionMultiplexer? node, long version)
    {
        Volatile.Write(ref _slotVersions[slot], version);
        Volatile.Write(ref _slots[slot], node);
    }

    private void AddSlot(RespireConnectionMultiplexer node, int count = 1)
    {
        var masters = Volatile.Read(ref _masters);
        var index = Array.IndexOf(masters, node);
        if (index >= 0)
        {
            _masterSlotCounts[index] += count;
            return;
        }

        var expanded = new RespireConnectionMultiplexer[masters.Length + 1];
        var expandedCounts = new int[expanded.Length];
        masters.CopyTo(expanded, 0);
        _masterSlotCounts.CopyTo(expandedCounts, 0);
        expanded[^1] = node;
        expandedCounts[^1] = count;
        _masterSlotCounts = expandedCounts;
        Volatile.Write(ref _masters, expanded);
    }

    private bool RemoveSlot(RespireConnectionMultiplexer node, int count = 1,
        bool preserveMaintenanceHandlerForRetirement = false)
    {
        var masters = Volatile.Read(ref _masters);
        var index = Array.IndexOf(masters, node);
        if (index < 0 || (_masterSlotCounts[index] -= count) > 0)
        {
            return false;
        }

        var contracted = new RespireConnectionMultiplexer[masters.Length - 1];
        var contractedCounts = new int[contracted.Length];
        masters.AsSpan(0, index).CopyTo(contracted);
        masters.AsSpan(index + 1).CopyTo(contracted.AsSpan(index));
        _masterSlotCounts.AsSpan(0, index).CopyTo(contractedCounts);
        _masterSlotCounts.AsSpan(index + 1).CopyTo(contractedCounts.AsSpan(index));
        _masterSlotCounts = contractedCounts;
        Volatile.Write(ref _masters, contracted);

        // Zero-slot nodes no longer affect command health or routed slot state, so stale state
        // callbacks must not invalidate the client cache. Keep maintenance handlers on redirect-
        // protected nodes and configured seeds, which may still send useful SMIGRATED pushes.
        // SMIGRATED retirement also keeps its handler until the PING barrier drains unread pushes.
        if (_nodeStateHandlers.Remove(node, out var handler))
            node.SlotStateChanged -= handler;
        if (!preserveMaintenanceHandlerForRetirement
            && !_redirectVersions.ContainsKey(node)
            && !_seeds.Any(seed => ClusterNodeIdentityIndex.EndpointsEqual(seed, Endpoint(node)))
            && _nodeMaintenanceHandlers.Remove(node, out var maintenanceHandler))
            node.MaintenanceNotificationReceived -= maintenanceHandler;

        return true;
    }

    private List<RespireConnectionMultiplexer>? ReplaceSlotOwnersLocked(
        RespireConnectionMultiplexer?[] refreshedSlots,
        ClusterReplicaSet?[] refreshedReplicas,
        bool[] coveredSlots, long expectedVersion, object? snapshotBatch, out bool topologyChanged,
        out HashSet<RespireConnectionMultiplexer> activeReplicas)
    {
        topologyChanged = false;
        // Preserve only slots changed by a redirect since this request began. Snapshot order
        // has its own generation fence, so publishing a snapshot must not look like a later
        // redirect to another discovery that began from the same topology.
        var activeNodes = new HashSet<RespireConnectionMultiplexer>();
        activeReplicas = [];
        // Every slot in a range shares one set, so only a change of set needs another union.
        ClusterReplicaSet? previousReplicas = null;
        for (var slot = 0; slot < refreshedSlots.Length; slot++)
        {
            if (_slotVersions[slot] > expectedVersion)
            {
                refreshedSlots[slot] = Volatile.Read(ref _slots[slot]);
                refreshedReplicas[slot] = Volatile.Read(ref _replicasBySlot[slot]);
            }
            if (refreshedSlots[slot] is { } node)
            {
                activeNodes.Add(node);
            }
            if (refreshedReplicas[slot] is { } replicas && !ReferenceEquals(replicas, previousReplicas))
            {
                activeReplicas.UnionWith(replicas.Nodes);
                previousReplicas = replicas;
            }
        }
        foreach (var node in activeNodes)
        {
            ObserveNode(node);
        }
        foreach (var node in activeReplicas)
        {
            ObserveNode(node);
        }
        List<RespireConnectionMultiplexer>? retiredNodes = null;
        var masters = activeNodes.ToArray();
        var masterSlotCounts = new int[masters.Length];
        var complete = true;
        for (var slot = 0; slot < refreshedSlots.Length; slot++)
        {
            var node = refreshedSlots[slot];
            if (_slotVersions[slot] <= expectedVersion)
            {
                topologyChanged |= !SameReplicaRoutes(_replicasBySlot[slot], refreshedReplicas[slot]);
                // Topology replies are ordered by discovery generation. Leave the point-route
                // version unchanged so a later discovery can replace this snapshot.
                if (!ReferenceEquals(Volatile.Read(ref _slots[slot]), node))
                {
                    topologyChanged = true;
                    MarkSlotMutatedLocked(slot);
                }
                PublishSlotLocked(slot, node, _slotVersions[slot]);
                // Published under the same slot fence as the owner, so the two never disagree.
                Volatile.Write(ref _replicasBySlot[slot], refreshedReplicas[slot]);
                if (snapshotBatch is not null && coveredSlots[slot])
                    _slotSnapshotBatches[slot] = snapshotBatch;
            }
            complete &= node is not null;
            if (node is not null)
            {
                masterSlotCounts[Array.IndexOf(masters, node)]++;
            }
        }
        _unknownReplicaDiscovery.ForgetCoveredSlots();
        if (!complete)
        {
            // An incomplete slot map cannot prove that an omitted primary has left the
            // cluster, so keep prior primaries as active identities (with no slots) until full
            // discovery instead of retiring their transports. They are not published to
            // TopologyChanged, which lists only slot owners; see ApplyTopology.
            var retainedMasters = new List<RespireConnectionMultiplexer>(masters);
            foreach (var prior in Volatile.Read(ref _masters))
            {
                if (activeNodes.Add(prior)) retainedMasters.Add(prior);
            }
            if (retainedMasters.Count != masters.Length)
            {
                Array.Resize(ref masterSlotCounts, retainedMasters.Count);
                masters = [.. retainedMasters];
            }
        }
        _masterSlotCounts = masterSlotCounts;
        Volatile.Write(ref _masters, masters);
        Volatile.Write(ref _hasCompleteTopology, complete ? 1 : 0);
        foreach (var node in _nodeStateHandlers.Keys)
        {
            if (!activeNodes.Contains(node) && !activeReplicas.Contains(node))
            {
                (retiredNodes ??= []).Add(node);
            }
        }
        if (retiredNodes is not null)
        {
            foreach (var node in retiredNodes)
            {
                node.SlotStateChanged -= _nodeStateHandlers[node];
                _nodeStateHandlers.Remove(node);
                // RetireInactiveLocked decides whether this is a retained seed/ASK node.
                // Actual retirement detaches maintenance handlers after its receive barrier.
            }
        }
        return retiredNodes;
    }

    private static bool SameReplicaRoutes(ClusterReplicaSet? current, ClusterReplicaSet? refreshed)
    {
        if (ReferenceEquals(current, refreshed)) return true;
        return current is not null && refreshed is not null && current.HasSameNodes(refreshed.Nodes);
    }

    private async ValueTask<RespireConnection> EnableCorrectionOrderingAsync(
        RespireConnection connection,
        bool required,
        CancellationToken cancellationToken,
        bool observe = true)
    {
        var node = connection.Multiplexer
            ?? GetOrCreateNode(new RespireEndpoint(connection.Host, connection.Port), observe);
        try
        {
            await node.EnsureReliableCorrectionOrderingAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (RespireServerException) when (!required)
        {
            // Non-cancellable cache access remains compatible with restricted ACLs.
        }

        return node.GetConnection();
    }

    private DedicatedConnectionPool GetOrCreateDedicatedPool(RespireEndpoint endpoint)
        => GetOrCreateDedicatedPool(GetOrCreateNode(endpoint, observe: false));

    private DedicatedConnectionPool GetOrCreateDedicatedPool(RespireConnectionMultiplexer node)
        => RefreshDedicatedPool(node)!;

    private DedicatedConnectionPool? RefreshDedicatedPool(RespireConnectionMultiplexer node, bool fromNotification = false)
    {
        DedicatedConnectionPool? previous;
        DedicatedConnectionPool pool;
        lock (_nodesGate)
        {
            if (fromNotification && (_disposed != 0 || node.IsRetired || !_dedicatedPools.ContainsKey(node))) return null;
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (node.IsRetired || _retiringNodes.ContainsKey(node))
                throw new RespireConnectionRetiredException(node.Host, node.Port);
            var publication = node.CaptureMovingPublication();
            var endpoint = publication.Endpoint;
            if (_dedicatedPools.TryGetValue(node, out previous)
                && ReferenceEquals(previous.MovingPublication, publication.Publication)) return previous;
            if (!_dedicatedMovingHandlers.ContainsKey(node))
            {
                Action handler = () => RefreshDedicatedPool(node, fromNotification: true);
                _dedicatedMovingHandlers.Add(node, handler);
                node.MovingHandoffPublished += handler;
            }
            DedicatedConnectionPool? created = null;
            created = new DedicatedConnectionPool(
                endpoint.Host,
                endpoint.Port,
                _options.ToConnectionOptions(enableMaintenanceNotifications: true) with { ReadOnly = node.Options.ReadOnly },
                _options.CreateLogger($"Respire.Cluster.Blocking.{node.Host}:{node.Port}"),
                change => DedicatedStateChanged?.Invoke(change),
                connection =>
                {
                    void OnMoving(MovingAnnouncement announcement)
                        => node.QueueDedicatedMovingHandoff(connection, announcement, () =>
                        {
                            lock (_nodesGate) return _disposed == 0 && !node.IsRetired
                                && _dedicatedPools.TryGetValue(node, out var current) && ReferenceEquals(current, created);
                        });
                    connection.MovingNotification += OnMoving;
                    if (connection.LastMovingAnnouncement is { } announcement) OnMoving(announcement);
                }) { MovingOwner = node, MovingPublication = publication.Publication };
            pool = created;
            _dedicatedPools[node] = pool;
            _ownedPools.Add(pool);
        }
        if (previous is not null) _ = RetirePoolAsync(previous);
        return pool;
    }

    // CLUSTER SLOTS node entry: [host, port, id?, metadata?]. Metadata supplies host names
    // only; all aliases share the node's advertised client port. No NAT port mapping can be
    // inferred here; reuse requires an established alias transport.
    private static List<RespireEndpoint> ParseEndpointAliases(ReadOnlySpan<Respire.Protocol.RespValue> node, int port)
    {
        List<RespireEndpoint> aliases = [];
        for (var metadataIndex = 3; metadataIndex < node.Length; metadataIndex++)
        {
            var metadata = node[metadataIndex].AsArray();
            for (var pairIndex = 0; pairIndex + 1 < metadata.Length; pairIndex += 2)
            {
                var name = metadata[pairIndex].AsSpan();
                if ((!name.SequenceEqual("hostname"u8) && !name.SequenceEqual("ip"u8))
                    || metadata[pairIndex + 1].IsNull)
                {
                    continue;
                }

                var alias = metadata[pairIndex + 1].AsString();
                if (!string.IsNullOrEmpty(alias) && alias != "?")
                {
                    aliases.Add(new RespireEndpoint(alias, port));
                }
            }
        }
        return aliases;
    }

    private long CaptureTopologyVersion()
    {
        lock (_nodesGate) return _topologyVersion;
    }

    // Replica entries use the primary's layout. Unknown ('?') or malformed entries are skipped.
    private static ClusterTopologyReplica? TryParseReplica(in Respire.Protocol.RespValue entry, string fallbackHost)
    {
        var replica = entry.AsArray();
        if (replica.Length < 2) return null;
        var port = replica[1].AsInteger();
        if (port is <= 0 or > 65_535) return null;
        var host = replica[0].IsNull ? fallbackHost : replica[0].AsString();
        string? hostname = null;
        string? ip = null;
        for (var metadataIndex = 3; metadataIndex < replica.Length; metadataIndex++)
        {
            var metadata = replica[metadataIndex].AsArray();
            for (var pairIndex = 0; pairIndex + 1 < metadata.Length; pairIndex += 2)
            {
                var name = metadata[pairIndex].AsSpan();
                if ((!name.SequenceEqual("hostname"u8) && !name.SequenceEqual("ip"u8))
                    || metadata[pairIndex + 1].IsNull) continue;
                var alias = metadata[pairIndex + 1].AsString();
                if (string.IsNullOrEmpty(alias) || alias == "?") continue;
                if (name.SequenceEqual("hostname"u8)) hostname = alias;
                else ip = alias;
            }
        }
        if (string.IsNullOrEmpty(host)) host = hostname ?? ip ?? fallbackHost;
        else if (host == "?") host = hostname ?? ip;
        if (string.IsNullOrEmpty(host) || host == "?") return null;
        var endpoint = new RespireEndpoint(host, (int)port);
        var nodeId = replica.Length > 2 && !replica[2].IsNull ? replica[2].AsString() : null;
        return new ClusterTopologyReplica(endpoint, string.IsNullOrEmpty(nodeId) ? null : nodeId,
            ParseEndpointAliases(replica, (int)port));
    }

    // keepUncoveredOwners is true for background refresh: slots the reply does not cover keep their
    // current owner instead of being cleared (see ApplyTopologyCore).
    private async ValueTask<(bool Loaded, bool CoversAllSlots, bool CoversRequiredSlot)> TryLoadSlotsAsync(
        RespireConnectionMultiplexer seed,
        CancellationToken cancellationToken,
        bool keepUncoveredOwners = false,
        long? expectedTopologyVersion = null,
        object? snapshotBatch = null,
        int? requiredSlot = null, ReplicaRefreshRound? replicaRefresh = null,
        RespireConnection? queryConnection = null)
    {
        long topologyVersion;
        long discoveryGeneration;
        lock (_nodesGate)
        {
            topologyVersion = expectedTopologyVersion ?? _topologyVersion;
            discoveryGeneration = ++_nextDiscoveryGeneration;
        }
        using var timeoutSource = CommandTimeoutCancellation.Create(
            cancellationToken,
            _options.CommandTimeout ?? _options.ConnectTimeout);
        try
        {
            var reply = await (queryConnection ?? seed.GetConnection()).SendAsync(
                new Cmd(Verbs.ClusterSlots), timeoutSource.Token).ConfigureAwait(false);
            try
            {
                if (reply.IsError)
                {
                    return (false, false, false);
                }

                var ranges = reply.AsArray();
                List<ClusterTopologyRange> topology = [];
                foreach (ref readonly var range in ranges)
                {
                    var values = range.AsArray();
                    if (values.Length < 3)
                    {
                        continue;
                    }

                    var start = values[0].AsInteger();
                    var end = values[1].AsInteger();
                    var primary = values[2].AsArray();
                    if (start < 0 || end < start || end >= ClusterHash.SlotCount || primary.Length < 2)
                    {
                        continue;
                    }

                    var host = primary[0].AsString();
                    var port = primary[1].AsInteger();
                    if (port is <= 0 or > 65_535)
                    {
                        continue;
                    }

                    if (host == "?")
                    {
                        _logger?.LogDebug(
                            "Skipping Redis Cluster slots {Start}-{End}: the preferred endpoint is unknown ('?').",
                            start, end);
                        continue;
                    }

                    var preferred = new RespireEndpoint(string.IsNullOrEmpty(host) ? seed.Host : host, (int)port);
                    var nodeId = primary.Length > 2 && !primary[2].IsNull ? primary[2].AsString() : null;
                    if (string.IsNullOrEmpty(nodeId))
                    {
                        nodeId = null;
                    }

                    var aliases = ParseEndpointAliases(primary, (int)port);
                    List<ClusterTopologyReplica> replicas = [];
                    for (var replicaIndex = 3; replicaIndex < values.Length; replicaIndex++)
                    {
                        if (TryParseReplica(values[replicaIndex], seed.Host) is not { } replica
                            || MatchesPrimary(replica, preferred, nodeId, aliases)
                            || replicas.Any(existing => RespireEndpointComparer.Instance.Equals(existing.Endpoint, replica.Endpoint)))
                        {
                            continue;
                        }
                        replicas.Add(replica);
                    }

                    topology.Add(new ClusterTopologyRange((int)start, (int)end, preferred, nodeId, aliases)
                    {
                        Replicas = replicas,
                    });
                }

                if (topology.Count == 0)
                {
                    return (false, false, false);
                }

                var coversAllSlots = CoversAllSlots(topology);
                var coversRequiredSlot = requiredSlot is not { } slot
                    || topology.Any(range => range.Start <= slot && slot <= range.End);
                if (replicaRefresh is not null && !replicaRefresh.Accept(topology, topologyVersion, discoveryGeneration))
                    return (false, coversAllSlots, coversRequiredSlot);
                ApplyTopologyCore(topology, topologyVersion, discoveryGeneration, keepUncoveredOwners, snapshotBatch);
                return (true, coversAllSlots, coversRequiredSlot);
            }
            finally
            {
                reply.Dispose();
            }
        }
        catch (Exception error) when (CanRetryConnectionFailure(error, cancellationToken))
        {
            // ACLs and Redis-compatible servers may hide CLUSTER SLOTS. MOVED/ASK learning
            // remains sufficient for correctness, so topology discovery is opportunistic for
            // connection/server failures. Incompatible configuration must still propagate.
            return (false, false, false);
        }
    }

    private static bool MatchesPrimary(ClusterTopologyReplica replica, RespireEndpoint primary,
        string? primaryId, List<RespireEndpoint> primaryAliases)
    {
        if (primaryId is not null && primaryId == replica.NodeId) return true;
        var comparer = RespireEndpointComparer.Instance;
        if (comparer.Equals(replica.Endpoint, primary)) return true;
        foreach (var alias in primaryAliases)
            if (comparer.Equals(replica.Endpoint, alias)) return true;
        foreach (var replicaAlias in replica.Aliases)
        {
            if (comparer.Equals(replicaAlias, primary)) return true;
            foreach (var alias in primaryAliases)
                if (comparer.Equals(replicaAlias, alias)) return true;
        }
        return false;
    }

    private static bool CoversAllSlots(List<ClusterTopologyRange> topology)
    {
        var ranges = topology.OrderBy(static range => range.Start).ToArray();
        var nextUncoveredSlot = 0;
        foreach (var range in ranges)
        {
            if (range.Start > nextUncoveredSlot) return false;
            if (range.End >= nextUncoveredSlot) nextUncoveredSlot = range.End + 1;
            if (nextUncoveredSlot >= ClusterHash.SlotCount) return true;
        }
        return false;
    }

    public ValueTask DisposeAsync() => DisposeAsync(IsOnSmigratedWorker);

    internal async ValueTask DisposeAsync(bool isOnSmigratedWorker)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        RespireConnectionMultiplexer[] nodes;
        KeyValuePair<RespireConnectionMultiplexer, Action<int, RespireConnectionStateChange>>[] stateHandlers;
        KeyValuePair<RespireConnectionMultiplexer, MaintenanceNotificationHandler>[] maintenanceHandlers;
        Task[] retirements;
        lock (_nodesGate)
        {
            nodes = _identities.All.ToArray();
            stateHandlers = [.. _nodeStateHandlers, .. _correctionStateHandlers];
            maintenanceHandlers = [.. _nodeMaintenanceHandlers];
            retirements = _retiringNodes.Values.Select(entry => entry.Completion.Task).ToArray();
            _nodeStateHandlers.Clear();
            _nodeMaintenanceHandlers.Clear();
            _correctionStateHandlers.Clear();
            _dedicatedPools.Clear();
            foreach (var (node, handler) in _dedicatedMovingHandlers) node.MovingHandoffPublished -= handler;
            _dedicatedMovingHandlers.Clear();
            _correctionPools.Clear();
            _migrations.ClearDeferred();
        }

        _smigratedNotifications.Writer.TryComplete();
        // Claims the lazily started worker slot, so no worker starts after this point.
        var smigratedWorker = CloseSmigratedWorker();
        _stopRetirement.Cancel();
        await _stopDiscovery.CancelAsync().ConfigureAwait(false);
        if (Volatile.Read(ref NearestLatency) is { } latency) await latency.DisposeAsync().ConfigureAwait(false);
        _smigratedNotifications.Writer.TryComplete();
        Task? refreshWorker;
        lock (_topologyRefreshWorkerGate) refreshWorker = Volatile.Read(ref _topologyRefreshWorker);
        if (refreshWorker is not null)
        {
            // Every wait and refresh in the worker observes _stopDiscovery, so this returns promptly
            // even while a CLUSTER SLOTS request is outstanding. The worker logs and survives other
            // failures, so only cancellation can surface here.
            try { await refreshWorker.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        foreach (var (node, handler) in stateHandlers) node.SlotStateChanged -= handler;
        foreach (var (node, handler) in maintenanceHandlers) node.MaintenanceNotificationReceived -= handler;
        // Abort all owned work before awaiting either drain. The primary may itself be a
        // superseded generation; ClientCore's later disposal of it is idempotent.
        // A NodeRetired handler on the worker can dispose the client; joining the worker from
        // inside it would deadlock. The completed channel ends the worker after that handler.
        // Otherwise this waits for any in-flight NodeRetired/TopologyChanged callback, so a
        // handler that blocks also delays disposal.
        await CleanupTasks.WhenAllAsync(nodes.Select(node => node.DisposeAsync().AsTask())
            .Append(_ownedPools.DisposeAllAsync())
            .Concat(retirements)
            .Append(isOnSmigratedWorker ? Task.CompletedTask : smigratedWorker)).ConfigureAwait(false);
    }
}

