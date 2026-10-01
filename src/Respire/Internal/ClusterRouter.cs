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
    private readonly Dictionary<CorrectionPoolIdentity, CorrectionPoolEntry> _correctionPools = [];
    private readonly object _nodesGate = new();
    private readonly RespireConnectionMultiplexer?[] _slots = new RespireConnectionMultiplexer?[ClusterHash.SlotCount];
    private RespireConnectionMultiplexer[] _masters = [];
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
    private int _disposed;

    internal ClusterRouter(RespireOptions options, RespireConnectionMultiplexer primary)
        : this(options, primary, options.ToConnectionOptions(enableMaintenanceNotifications: true))
    {
    }

    internal ClusterRouter(
        RespireOptions options,
        RespireConnectionMultiplexer primary,
        RespireConnectionOptions commandConnectionOptions)
    {
        _options = options;
        _logger = options.CreateLogger("Respire.Cluster");
        _commandConnectionOptions = commandConnectionOptions;
        _seeds = options.Endpoints.Count == 0
            ? [new RespireEndpoint("localhost")]
            : options.Endpoints.ToArray();
        _primary = primary;
        _identities = new ClusterNodeIdentityIndex(options.PrimaryEndpoint, primary, CreateNode, _nodesGate);
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

            return false;
        }
    }

    internal event Action<RespireConnectionMultiplexer, int, RespireConnectionStateChange>? SlotStateChanged;
    internal event Action<RespireConnectionStateChange>? DedicatedStateChanged;
    internal event Action<RespireConnectionMultiplexer>? NodeRetired;

    internal event Action? TopologyChanged;

    // Read the published generation without connecting or taking _nodesGate. Subscription
    // topology callbacks use this while holding their own route gate.
    internal RespireConnectionMultiplexer? GetKnownSlotOwner(int slot) => Volatile.Read(ref _slots[slot]);

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
            StartTopologyRefreshWorker();
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
                StartTopologyRefreshWorker();
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
                    StartTopologyRefreshWorker();
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
            && TryGetReadyConnection(slot) is { } ready)
        {
            StartTopologyRefreshWorker();
            return new(ready);
        }
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
            StartTopologyRefreshWorker();
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
            StartTopologyRefreshWorker();
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
                if (error.Code == RespireErrorCodes.Moved)
                    SignalTopologyRefresh(delayMilliseconds: MovedTopologyRefreshDelayMilliseconds);
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

    internal async ValueTask<(DedicatedConnectionPool Pool, RespireConnection Connection)> RentDedicatedConnectionAsync(
        DedicatedConnectionPool pool, int? slot, CancellationToken cancellationToken, DiscoveryRound? discovery,
        bool reuseIdle = true, RespireServerException? askRedirect = null, RespireConnection? redirectSource = null)
    {
        // Ordinary rents need no discovery scope. Create one only after topology retirement
        // invalidates the selected pool, then share it across every subsequent reselection.
        DiscoveryScope scope = default;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var connection = await pool.RentAsync(cancellationToken, reuseIdle: reuseIdle).ConfigureAwait(false);
                    return (pool, connection);
                }
                catch (Exception error) when (CanRetryRetirement(attempt, cancellationToken) && pool.IsStopping
                    && error is ObjectDisposedException or OperationCanceledException)
                {
                    if (attempt == 0)
                    {
                        scope = BeginDiscovery(discovery);
                        discovery = scope.Round;
                    }
                    discovery?.Failed(error);
                    // Retirement can cancel a pending handshake; no application command was sent.
                    pool = askRedirect is null
                        ? await GetDedicatedPoolAsync(slot, cancellationToken, discovery).ConfigureAwait(false)
                        : await GetRedirectDedicatedPoolAsync(askRedirect, redirectSource!, cancellationToken, slot, discovery)
                            .ConfigureAwait(false);
                }
            }
        }
        catch (Exception error) { scope.SetTerminalError(error); throw; }
        finally { scope.Dispose(); }
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
            var sharedRefresh = RefreshReadOnlySharedAsync(
                error, source, slot, cancellationToken, discovery, out var joinedOtherSlot);
            var sharedRefreshSucceeded = await sharedRefresh.ConfigureAwait(false);
            var owner = Volatile.Read(ref _slots[slot]);
            if ((owner is null || IsSameEndpoint(owner, source)) && sharedRefreshSucceeded && joinedOtherSlot)
            {
                // A concurrent READONLY on another slot can join this flight. The flight
                // repairs its initiating slot; discover this slot before failing its write.
                owner = await RefreshReadOnlyOwnerCoreAsync(error, source, slot, cancellationToken, discovery)
                    .ConfigureAwait(false);
            }
            if (owner is null || IsSameEndpoint(owner, source))
                ExceptionDispatchInfo.Capture(error).Throw();
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
        ClusterRecoveryBudget budget, bool primaryPhase, RespireConnectionMultiplexer? unavailableOwner, DiscoveryRound? discovery)
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
            if (!await TryDiscoverReadOnlyOwnerAsync(candidate, attemptToken, budget.Token, discovery).ConfigureAwait(false))
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
        RespireConnectionMultiplexer candidate, CancellationToken attemptToken, CancellationToken roundToken, DiscoveryRound? discovery)
    {
        try
        {
            await EnsureRouteNodeConnectedAsync(candidate, attemptToken, discovery).ConfigureAwait(false);
            if (!await TryLoadSlotsAsync(candidate, attemptToken).ConfigureAwait(false))
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
        string? commandName = null)
        where TCommand : struct, Respire.Protocol.IRespCommand
        => connection.SendPrefixedCheckedAsync(in Asking, in command, cancellationToken, commandName);

    internal static ValueTask<Respire.Protocol.RespValue> SendTrackedAskingAsync<TCommand>(
        RespireConnection connection,
        in TCommand command,
        CancellationToken cancellationToken,
        string commandName = "(command)")
        where TCommand : struct, Respire.Protocol.IRespCommand
    {
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
        bool armCommandDeadline = true)
        where TCommand : struct, Respire.Protocol.IRespCommand
        => connection.SendPrefixedAsync(
            in Asking, in command, throwOnError: false, cancellationToken,
            commandName: null, armCommandDeadline);

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
        var masters = new HashSet<RespireConnectionMultiplexer>(ReferenceEqualityComparer.Instance);
        AddKnownMasters(masters);

        var refreshed = false;
        RespireConnectionMultiplexer? attemptedSeed = null;
        if (Volatile.Read(ref _seed) is { IsConnected: true, IsRetired: false } seed
            && discovery?.HasRejected(seed) != true)
        {
            attemptedSeed = seed;
            refreshed = await TryRefreshTopologyAsync(seed, cancellationToken, discovery).ConfigureAwait(false);
        }

        if (!refreshed)
        {
            foreach (var master in masters)
            {
                // The connected seed is also a known master. Its failed query has already
                // seeded the round; reserve the fallback budget for a different candidate.
                if (ReferenceEquals(master, attemptedSeed) || discovery?.HasRejected(master) == true) continue;
                if (await TryRefreshTopologyAsync(master, cancellationToken, discovery).ConfigureAwait(false))
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
            var loaded = await TryLoadSlotsAsync(fallbackSeed, cancellationToken).ConfigureAwait(false);
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
        CancellationToken cancellationToken, DiscoveryRound? discovery)
    {
        try
        {
            await EnsureRouteNodeConnectedAsync(node, cancellationToken, discovery).ConfigureAwait(false);
            var complete = await TryLoadSlotsAsync(node, cancellationToken).ConfigureAwait(false) && HasCompleteTopology();
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
        CancellationToken cancellationToken, DiscoveryRound? discovery)
    {
        foreach (var master in Volatile.Read(ref _masters))
        {
            // A failed owner can still own other slots. Spend fallback budget on a distinct
            // generation instead of immediately retrying the already rejected connection.
            if (ReferenceEquals(master, failedOwner) || discovery?.HasRejected(master) == true) continue;
            if (!await TryRefreshTopologyAsync(master, cancellationToken, discovery).ConfigureAwait(false))
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

    private RespireConnectionMultiplexer GetOrCreateNode(RespireEndpoint endpoint, bool observe = true, bool redirect = false)
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

    private void ApplyTopology(List<ClusterTopologyRange> ranges, long expectedVersion, long discoveryGeneration)
    {
        List<RespireConnectionMultiplexer>? retiredNodes;
        List<RetiredGeneration> retirements;
        bool topologyChanged;
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
            var resolved = _identities.ApplySnapshot(ranges, protectedNodes);
            Volatile.Write(ref _replicas, ranges.SelectMany(static range => range.Replicas)
                .GroupBy(static replica => replica.Endpoint)
                .Select(static group => new ClusterTopologyReplica(group.Key,
                    group.Select(static replica => replica.NodeId).FirstOrDefault(static nodeId => nodeId is not null),
                    group.SelectMany(static replica => replica.Aliases).Distinct().ToList()))
                .ToArray());
            var refreshedSlots = new RespireConnectionMultiplexer?[ClusterHash.SlotCount];
            foreach (var (range, node) in resolved)
            {
                for (var slot = range.Start; slot <= range.End; slot++)
                {
                    refreshedSlots[slot] = node;
                }
            }

            retiredNodes = ReplaceSlotOwnersLocked(refreshedSlots, expectedVersion, out topologyChanged);
            // Resolve stable node identity before pruning the old reverse mapping.
            if (Volatile.Read(ref _seed) is { } previousSeed) SetSeedLocked(previousSeed);
            var retained = new HashSet<RespireConnectionMultiplexer>(_masters);
            if (protectedNodes is not null) retained.UnionWith(protectedNodes);
            retirements = DetachGenerationsLocked(_identities.DetachInactive(retained, _seeds));
            if (Volatile.Read(ref _seed) is { } seed) SetSeedLocked(seed);
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
                NodeRetired?.Invoke(node);
            }
        }
        if (topologyChanged) TopologyChanged?.Invoke();
    }

    // Publish the current identity, even when discovery completed on a superseded transport.
    private void SetSeed(RespireConnectionMultiplexer node)
    {
        lock (_nodesGate)
        {
            SetSeedLocked(node);
        }
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
    private RespireConnectionMultiplexer CreateNode(RespireEndpoint endpoint)
        => RespireConnectionMultiplexer.Create(
            endpoint.Host, endpoint.Port, _options.Connections, _commandConnectionOptions,
            _options.CreateLogger($"Respire.Cluster.{endpoint.Host}:{endpoint.Port}"));

    private void ObserveNode(RespireConnectionMultiplexer node)
    {
        if (_nodeStateHandlers.ContainsKey(node))
        {
            return;
        }

        Action<int, RespireConnectionStateChange> handler =
            (slot, change) =>
            {
                SlotStateChanged?.Invoke(node, slot, change);
                if (change.State == RespireConnectionState.Disconnected
                    && Volatile.Read(ref _masters).Contains(node)) SignalTopologyRefresh(force: true);
            };
        _nodeStateHandlers.Add(node, handler);
        node.SlotStateChanged += handler;
    }

    internal void SetSlotOwner(int slot, RespireConnectionMultiplexer node)
    {
        RespireConnectionMultiplexer? retiredNode = null;
        lock (_nodesGate)
        {
            if (_retiringNodes.ContainsKey(node) || node.IsRetired)
                throw new RespireConnectionRetiredException(node.Host, node.Port);
            ObserveNode(node);
            var previous = Volatile.Read(ref _slots[slot]);
            PublishSlotLocked(slot, node, ++_topologyVersion);
            if (ReferenceEquals(previous, node))
            {
                return;
            }

            AddSlot(node);
            if (previous is not null && RemoveSlot(previous))
            {
                retiredNode = previous;
            }
        }

        if (retiredNode is not null)
        {
            NodeRetired?.Invoke(retiredNode);
        }
        TopologyChanged?.Invoke();
    }

    private void ClearSlotOwner(int slot, RespireConnectionMultiplexer node)
    {
        RespireConnectionMultiplexer? retiredNode = null;
        lock (_nodesGate)
        {
            if (!ReferenceEquals(Volatile.Read(ref _slots[slot]), node))
            {
                return;
            }

            PublishSlotLocked(slot, null, ++_topologyVersion);
            Volatile.Write(ref _hasCompleteTopology, 0);
            if (RemoveSlot(node))
            {
                retiredNode = node;
            }
        }

        if (retiredNode is not null)
        {
            NodeRetired?.Invoke(retiredNode);
        }
        TopologyChanged?.Invoke();
    }

    // Every slot publication carries its discovery-order fence under _nodesGate.
    private void PublishSlotLocked(int slot, RespireConnectionMultiplexer? node, long version)
    {
        _slotVersions[slot] = version;
        Volatile.Write(ref _slots[slot], node);
    }

    private void AddSlot(RespireConnectionMultiplexer node)
    {
        var masters = Volatile.Read(ref _masters);
        var index = Array.IndexOf(masters, node);
        if (index >= 0)
        {
            _masterSlotCounts[index]++;
            return;
        }

        var expanded = new RespireConnectionMultiplexer[masters.Length + 1];
        var expandedCounts = new int[expanded.Length];
        masters.CopyTo(expanded, 0);
        _masterSlotCounts.CopyTo(expandedCounts, 0);
        expanded[^1] = node;
        expandedCounts[^1] = 1;
        _masterSlotCounts = expandedCounts;
        Volatile.Write(ref _masters, expanded);
    }

    private bool RemoveSlot(RespireConnectionMultiplexer node)
    {
        var masters = Volatile.Read(ref _masters);
        var index = Array.IndexOf(masters, node);
        if (index < 0 || --_masterSlotCounts[index] > 0)
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

        if (_nodeStateHandlers.Remove(node, out var handler))
        {
            node.SlotStateChanged -= handler;
        }

        return true;
    }

    private List<RespireConnectionMultiplexer>? ReplaceSlotOwnersLocked(
        RespireConnectionMultiplexer?[] refreshedSlots,
        long expectedVersion, out bool topologyChanged)
    {
        topologyChanged = false;
        // Preserve only slots changed since this request began. An unrelated MOVED
        // must not discard useful discovery for a READONLY command's slot.
        // Discovery order has its own fence. Publishing an older request must not advance
        // slot mutation versions past a newer request that is already in flight.
        var activeNodes = new HashSet<RespireConnectionMultiplexer>();
        for (var slot = 0; slot < refreshedSlots.Length; slot++)
        {
            if (_slotVersions[slot] > expectedVersion)
            {
                refreshedSlots[slot] = Volatile.Read(ref _slots[slot]);
            }
            if (refreshedSlots[slot] is { } node)
            {
                activeNodes.Add(node);
            }
        }
        foreach (var node in activeNodes)
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
                topologyChanged |= !ReferenceEquals(_slots[slot], node);
                PublishSlotLocked(slot, node, _slotVersions[slot]);
            }
            complete &= node is not null;
            if (node is not null)
            {
                masterSlotCounts[Array.IndexOf(masters, node)]++;
            }
        }
        _masterSlotCounts = masterSlotCounts;
        Volatile.Write(ref _masters, masters);
        Volatile.Write(ref _hasCompleteTopology, complete ? 1 : 0);
        foreach (var node in _nodeStateHandlers.Keys)
        {
            if (!activeNodes.Contains(node))
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
            }
        }
        return retiredNodes;
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
    {
        lock (_nodesGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var node = GetOrCreateNode(endpoint, observe: false);
            if (_dedicatedPools.TryGetValue(node, out var existing))
            {
                return existing;
            }

            var pool = new DedicatedConnectionPool(
                node.Host,
                node.Port,
                _options.ToConnectionOptions(),
                _options.CreateLogger($"Respire.Cluster.Blocking.{node.Host}:{node.Port}"),
                change => DedicatedStateChanged?.Invoke(change));
            _dedicatedPools.Add(node, pool);
            _ownedPools.Add(pool);
            return pool;
        }
    }

    private async ValueTask<bool> TryLoadSlotsAsync(
        RespireConnectionMultiplexer seed,
        CancellationToken cancellationToken,
        bool requireComplete = false)
    {
        long topologyVersion;
        long discoveryGeneration;
        lock (_nodesGate)
        {
            topologyVersion = _topologyVersion;
            discoveryGeneration = ++_nextDiscoveryGeneration;
        }
        using var timeoutSource = CommandTimeoutCancellation.Create(
            cancellationToken,
            _options.CommandTimeout ?? _options.ConnectTimeout);
        try
        {
            var reply = await seed.GetConnection().SendAsync(
                new Cmd(Verbs.ClusterSlots), timeoutSource.Token).ConfigureAwait(false);
            try
            {
                if (reply.IsError)
                {
                    return false;
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

                    List<RespireEndpoint> aliases = [];
                    for (var metadataIndex = 3; metadataIndex < primary.Length; metadataIndex++)
                    {
                        var metadata = primary[metadataIndex].AsArray();
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
                                // CLUSTER SLOTS metadata supplies host names only; all aliases
                                // share the node's advertised client port. No NAT port mapping
                                // can be inferred here; reuse requires an established alias transport.
                                aliases.Add(new RespireEndpoint(alias, (int)port));
                            }
                        }
                    }

                    List<ClusterTopologyReplica> replicas = [];
                    for (var replicaIndex = 3; replicaIndex < values.Length; replicaIndex++)
                    {
                        var replica = values[replicaIndex].AsArray();
                        if (replica.Length < 2) continue;
                        var replicaHost = replica[0].AsString();
                        var replicaPort = replica[1].AsInteger();
                        if (replicaPort is <= 0 or > 65_535 || replicaHost == "?") continue;
                        var replicaEndpoint = new RespireEndpoint(
                            string.IsNullOrEmpty(replicaHost) ? seed.Host : replicaHost, (int)replicaPort);
                        var replicaId = replica.Length > 2 && !replica[2].IsNull ? replica[2].AsString() : null;
                        if (string.IsNullOrEmpty(replicaId)) replicaId = null;
                        List<RespireEndpoint> replicaAliases = [];
                        for (var metadataIndex = 3; metadataIndex < replica.Length; metadataIndex++)
                        {
                            var metadata = replica[metadataIndex].AsArray();
                            for (var pairIndex = 0; pairIndex + 1 < metadata.Length; pairIndex += 2)
                            {
                                var name = metadata[pairIndex].AsSpan();
                                if ((!name.SequenceEqual("hostname"u8) && !name.SequenceEqual("ip"u8))
                                    || metadata[pairIndex + 1].IsNull) continue;
                                var alias = metadata[pairIndex + 1].AsString();
                                if (!string.IsNullOrEmpty(alias) && alias != "?")
                                    replicaAliases.Add(new RespireEndpoint(alias, (int)replicaPort));
                            }
                        }
                        replicas.Add(new ClusterTopologyReplica(replicaEndpoint, replicaId, replicaAliases));
                    }

                    topology.Add(new ClusterTopologyRange((int)start, (int)end, preferred, nodeId, aliases)
                    {
                        Replicas = replicas,
                    });
                }

                if (topology.Count == 0)
                {
                    return false;
                }

                if (requireComplete)
                {
                    var nextSlot = 0;
                    foreach (var range in topology.OrderBy(static range => range.Start))
                    {
                        if (range.Start != nextSlot) return false;
                        nextSlot = range.End + 1;
                    }
                    if (nextSlot != ClusterHash.SlotCount) return false;
                }

                ApplyTopology(topology, topologyVersion, discoveryGeneration);
                return true;
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
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        RespireConnectionMultiplexer[] nodes;
        KeyValuePair<RespireConnectionMultiplexer, Action<int, RespireConnectionStateChange>>[] stateHandlers;
        DedicatedConnectionPool[] dedicatedPools;
        Task retirements;
        lock (_nodesGate)
        {
            nodes = _identities.All.ToArray();
            stateHandlers = [.. _nodeStateHandlers, .. _correctionStateHandlers];
            dedicatedPools = _ownedPools.ToArray();
            retirements = Task.WhenAll(_retiringNodes.Values.Select(entry => entry.Completion.Task));
            _nodeStateHandlers.Clear();
            _correctionStateHandlers.Clear();
            _dedicatedPools.Clear();
            _correctionPools.Clear();
        }

        _stopRetirement.Cancel();
        await _stopDiscovery.CancelAsync().ConfigureAwait(false);
        if (_topologyRefreshWorker is { } refreshWorker)
        {
            try { await refreshWorker.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        foreach (var (node, handler) in stateHandlers) node.SlotStateChanged -= handler;
        // Abort all owned work before awaiting either drain. The primary may itself be a
        // superseded generation; ClientCore's later disposal of it is idempotent.
        await Task.WhenAll(dedicatedPools.Select(pool => pool.DisposeAsync().AsTask())
            .Concat(nodes.Select(node => node.DisposeAsync().AsTask()))).ConfigureAwait(false);
        await retirements.ConfigureAwait(false);
    }
}
