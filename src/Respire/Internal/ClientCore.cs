using Microsoft.Extensions.Logging;
using Respire.Infrastructure;
using Respire.Networking;

namespace Respire.Internal;

/// <summary>
/// The state one logical client owns: the multiplexed connection set, the dedicated-connection
/// pool for blocking commands, and the lazily created pub/sub hub. Key-prefixed views created
/// by <see cref="RespireClient.WithKeyPrefix"/> share one core; only the root client disposes it.
/// </summary>
internal sealed class ClientCore : IAsyncDisposable
{
    private readonly object _hubGate = new();
    private readonly object _stateGate = new();
    private readonly Queue<RespireConnectionStateChange> _pendingStates = [];
    private readonly HashSet<(RespireConnectionMultiplexer Node, int Slot)> _reconnectingCommandSlots = [];
    private readonly HashSet<(RespireConnectionMultiplexer Node, int Slot)> _disconnectedCommandSlots = [];
    private readonly Dictionary<RespireEndpoint, RespireConnectionState> _publishedEndpointStates = [];
    private readonly Dictionary<RespireEndpoint, RespireConnectionState> _clusterSubscriptionStates = [];
    private SubscriptionHub? _hub;
    private HashSet<DedicatedConnectionPool>? _serverPools;
    private Dictionary<(bool Sharded, RespireEndpoint Endpoint), RespireConnectionState>? _subscriptionStates;
    private RespireEndpoint? _regularSubscriptionEndpoint;
    private bool _publishingState;
    private IDisposable? _threadPoolMonitor;

    private readonly RespireConnectionMultiplexer _multiplexer;
    public RespireConnectionMultiplexer Multiplexer => Sentinel?.Current?.Multiplexer ?? _multiplexer;
    internal RespireEndpoint Endpoint
    {
        get
        {
            var multiplexer = Multiplexer;
            return new(multiplexer.Host, multiplexer.Port);
        }
    }
    public readonly RespireOptions Options;
    public readonly ILogger? Logger;
    private CoordinationCleanupQueue? _coordinationCleanupQueue;
    internal CoordinationCleanupQueue? CoordinationCleanupQueue
    {
        get
        {
            lock (_hubGate)
                return Disposed ? null : _coordinationCleanupQueue ??= new();
        }
    }
    private DedicatedConnectionPool _dedicatedPool;
    private RespireEndpoint _dedicatedEndpoint;
    public DedicatedConnectionPool DedicatedPool => Sentinel?.Current?.Pool ?? Volatile.Read(ref _dedicatedPool);
    internal readonly SentinelRouter? Sentinel;
    internal readonly ReadEndpointRouter ReadRouter;
    public readonly ClusterRouter? Cluster;
    public readonly ClientSideCacheCoordinator? ClientCache;
    public volatile bool Disposed;

    public ClientCore(RespireOptions options)
    {
        Options = options;
        Logger = options.CreateLogger("Respire.RespireClient");
        var endpoint = options.PrimaryEndpoint;
        ClientCache = options.ClientSideCache is { } cacheOptions
            ? new ClientSideCacheCoordinator(cacheOptions)
            : null;
        var clientCache = ClientCache;
        RespirePushHandler? pushHandler = clientCache is null ? null : clientCache.HandlePush;
        var connectionOptions = options.ToConnectionOptions(
            pushHandler,
            enableClientTracking: ClientCache is not null, enableMaintenanceNotifications: true) with
        {
            CredentialCacheInvalidation = clientCache is null ? null : clientCache.FlushForContinuityLossWithoutMetrics,
            CredentialCacheRetirementFence = clientCache is null ? null : clientCache.FlushForMovingRetirementFence,
        };
        _multiplexer = RespireConnectionMultiplexer.Create(
            endpoint.Host, endpoint.Port, options.Connections, connectionOptions, Logger);
        ReadRouter = new ReadEndpointRouter(this);
        _dedicatedPool = new DedicatedConnectionPool(
            endpoint.Host, endpoint.Port, options.ToConnectionOptions(enableMaintenanceNotifications: true), Logger, NotifyRecoveryStateChanged);
        _dedicatedEndpoint = endpoint;
        Cluster = options.UseCluster
            ? new ClusterRouter(options, Multiplexer, connectionOptions)
            : null;
        Sentinel = string.IsNullOrWhiteSpace(options.SentinelPrimaryName) ? null : new SentinelRouter(this);
        if (Cluster is { } cluster)
        {
            cluster.SlotStateChanged += NotifyCommandStateChanged;
            cluster.DedicatedStateChanged += NotifyRecoveryStateChanged;
            cluster.DiscoveryStateChanged += NotifyRecoveryStateChanged;
            cluster.NodeRetired += NotifyCommandNodeRetired;
            cluster.ReplicaNodeRetired += NotifyReadReplicaNodeRetired;
            cluster.TopologyChanged += NotifySubscriptionTopologyChanged;
        }
        else if (Sentinel is null)
        {
            Multiplexer.SlotStateChanged += NotifyCommandStateChanged;
            Multiplexer.MovingHandoffPublished += RefreshStandaloneDedicatedPool;
        }
        if (options.ThreadPoolMonitoring)
            _threadPoolMonitor = ThreadPoolMonitor.Acquire(options.CreateLogger("Respire.ThreadPool"), options.ThreadPoolWarningThreshold);
    }

    public ValueTask EnsureConnectedAsync(CancellationToken cancellationToken)
        => Sentinel is not null
            ? EnsureSentinelConnectedAsync(cancellationToken)
            : Cluster is { } cluster
            ? cluster.EnsureConnectedAsync(cancellationToken, discovery: null)
            : Multiplexer.EnsureConnectedAsync(cancellationToken);

    private async ValueTask EnsureSentinelConnectedAsync(CancellationToken cancellationToken)
        => await Sentinel!.GetGenerationAsync(cancellationToken).ConfigureAwait(false);

    internal async ValueTask<DedicatedConnectionPool> GetDedicatedPoolAsync(CancellationToken cancellationToken)
    {
        if (Sentinel is { } sentinel)
            return (await sentinel.GetGenerationAsync(cancellationToken).ConfigureAwait(false)).Pool;
        // Read the published endpoint here too: a new upload can race the handoff callback.
        RefreshStandaloneDedicatedPool();
        return Volatile.Read(ref _dedicatedPool);
    }

    internal async ValueTask<(DedicatedConnectionPool Pool, RespireConnection Connection)> RentDedicatedConnectionAsync(
        DedicatedConnectionPool pool, CancellationToken cancellationToken, bool reuseIdle = true,
        DedicatedLeaseKind kind = DedicatedLeaseKind.Ordinary)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Disposed, this);
            try
            {
                var connection = await pool.RentAsync(cancellationToken, reuseIdle: reuseIdle, kind: kind).ConfigureAwait(false);
                return (pool, connection);
            }
            catch (Exception error) when (!Disposed && !cancellationToken.IsCancellationRequested
                && pool.IsStopping && error is ObjectDisposedException or OperationCanceledException)
            {
                // Publication can retire the selected pool before rental or during its handshake.
                // No application command has been sent. Keep the caller's acquisition deadline
                // and return the replacement owner together with its lease.
                var replacement = await GetDedicatedPoolAsync(cancellationToken).ConfigureAwait(false);
                if (ReferenceEquals(replacement, pool)) throw;
                pool = replacement;
            }
        }
    }

    internal bool IsDedicatedStreamRouteCurrent(DedicatedConnectionPool pool, RespireConnection connection)
        => !pool.IsStopping && ReferenceEquals(pool, DedicatedPool)
            && (Sentinel is not null || _multiplexer.ActiveConnectionEndpoint == new RespireEndpoint(connection.Host, connection.Port));

    internal sealed class CorrectionLease(ClientCore owner, DedicatedConnectionPool pool) : IAsyncDisposable
    {
        internal DedicatedConnectionPool Pool => pool;
        public ValueTask DisposeAsync() => owner.ReleaseServerPoolAsync(pool);
    }

    internal CorrectionLease GetCorrectionLease(RespireEndpoint endpoint, RespireConnection? original)
    {
        var options = Options.ToConnectionOptions();
        if (options.UseTls)
            options = options with { TlsOptions = RespireConnection.CreateTlsOptions(options.TlsOptions, original?.Host ?? endpoint.Host) };
        // Client IDs belong to the original physical server, not the current MOVING destination.
        lock (_hubGate)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            var pool = new DedicatedConnectionPool(original?.NetworkPeerAddress ?? endpoint.Host,
                original?.NetworkPeerPort ?? endpoint.Port, options, Logger);
            (_serverPools ??= []).Add(pool);
            return new(this, pool);
        }
    }

    private void RefreshStandaloneDedicatedPool()
    {
        DedicatedConnectionPool previous;
        lock (_hubGate)
        {
            if (Disposed || Cluster is not null || Sentinel is not null) return;
            var endpoint = _multiplexer.ActiveConnectionEndpoint;
            if (endpoint == _dedicatedEndpoint) return;
            previous = _dedicatedPool;
            var replacement = new DedicatedConnectionPool(endpoint.Host, endpoint.Port,
                Options.ToConnectionOptions(enableMaintenanceNotifications: true), Logger, NotifyRecoveryStateChanged);
            (_serverPools ??= []).Add(previous);
            _dedicatedEndpoint = endpoint;
            Volatile.Write(ref _dedicatedPool, replacement);
        }
        // Keep borrowed uploads and blocking calls alive, while rejecting new rentals on the
        // old endpoint. Client disposal retains ownership until the final borrower returns.
        _ = RetireMovedDedicatedPoolAsync(previous);
    }

    private async Task RetireMovedDedicatedPoolAsync(DedicatedConnectionPool pool)
    {
        try { await pool.RetireAsync().ConfigureAwait(false); }
        catch (Exception error)
        {
            try { Logger?.LogWarning(error, "Dedicated connection cleanup after MOVING failed"); }
            catch { /* Logging cannot fault the background retirement. */ }
        }
        finally { lock (_hubGate) _serverPools!.Remove(pool); }
    }

    public event Action<RespireConnectionStateChange>? ConnectionStateChanged;

    internal void NotifySentinelDisconnected(RespireConnectionMultiplexer node, Exception? error = null)
    {
        lock (_stateGate)
        {
            if (Disposed) return;
            _disconnectedCommandSlots.Add((node, 0));
            QueueEndpointStateLocked(new RespireConnectionStateChange(
                new RespireEndpoint(node.Host, node.Port), RespireConnectionState.Disconnected, error));
        }
        PublishQueuedStates();
    }

    internal void NotifySentinelPrimaryChanged(RespireConnectionMultiplexer? previous, RespireConnectionMultiplexer current)
    {
        lock (_stateGate)
        {
            if (Disposed) return;
            if (previous is not null)
            {
                _reconnectingCommandSlots.RemoveWhere(slot => ReferenceEquals(slot.Node, previous));
                _disconnectedCommandSlots.RemoveWhere(slot => ReferenceEquals(slot.Node, previous));
                _publishedEndpointStates.Remove(new(previous.Host, previous.Port));
            }
            // Publication itself is a connection event, including the first discovery.
            // Do not synthesize a continuity loss after new-generation reads can start.
            var endpoint = new RespireEndpoint(current.Host, current.Port);
            _publishedEndpointStates.Remove(endpoint);
            var state = GetEndpointStateLocked(endpoint);
            if (state == RespireConnectionState.Connected)
            {
                _pendingStates.Enqueue(new RespireConnectionStateChange(endpoint, state, null));
            }
            else
            {
                QueueEndpointStateLocked(new RespireConnectionStateChange(endpoint, state, null));
            }
        }
        PublishQueuedStates();
    }

    private void NotifySubscriptionTopologyChanged(long version, RespireEndpoint[] endpoints, bool authoritative)
    {
        SubscriptionHub? hub;
        lock (_hubGate) hub = _hub;
        hub?.NotifyTopologyChanged(version, endpoints, authoritative);
    }

    internal void NotifyRecoveryStateChanged(RespireConnectionStateChange change)
    {
        lock (_stateGate)
        {
            if (Disposed) return;
            // Dedicated rents and discovery rounds are not required command slots. Forward
            // source metadata without adding them to command/subscription health sets.
            QueueEndpointStateLocked(change);
        }
        PublishQueuedStates();
    }

    internal void NotifySubscriptionStateChanged(
        RespireConnectionState state,
        Exception? error = null)
        => NotifySubscriptionStateChanged(new RespireConnectionStateChange(
            Options.PrimaryEndpoint, state, error));

    internal void NotifySubscriptionStateChanged(RespireConnectionStateChange change, bool clusterSharded = false)
    {
        lock (_stateGate)
        {
            if (Disposed) return;
            _subscriptionStates ??= [];
            // Keep recovered sharded endpoints (as Connected) so disposal still reports a
            // terminal state for them. ASK targets are not active Cluster command endpoints.
            _subscriptionStates[(clusterSharded, change.Endpoint)] = change.State;
            // Regular subscriptions move as one group; sharded primaries recover independently.
            if (!clusterSharded && _regularSubscriptionEndpoint is { } previous && previous != change.Endpoint)
            {
                _subscriptionStates.Remove((false, previous));
                QueueEndpointStateLocked(new RespireConnectionStateChange(
                    previous, RespireConnectionState.Connected, null));
            }
            if (!clusterSharded) _regularSubscriptionEndpoint = change.Endpoint;

            QueueEndpointStateLocked(change);
        }

        PublishQueuedStates();
    }

    internal void NotifyClusterSubscriptionStateChanged(RespireConnectionStateChange change)
    {
        lock (_stateGate)
        {
            if (Disposed) return;
            if (change.State == RespireConnectionState.Connected)
                _clusterSubscriptionStates.Remove(change.Endpoint);
            else
                _clusterSubscriptionStates[change.Endpoint] = change.State;
            QueueEndpointStateLocked(change with
            {
                ReconnectSource = RespireReconnectSource.PubSub,
                SourceState = change.State,
            });
        }
        ThreadPool.UnsafeQueueUserWorkItem(static core => core.PublishQueuedStates(), this, preferLocal: false);
    }

    // Forgets the cluster notification state of an endpoint whose notification connection was
    // retired, or that left the topology. This does not claim a new connection: an event is
    // queued only when the endpoint had recorded notification state, and the aggregated state
    // it carries is Connected only if no command or Pub/Sub source still reports it degraded.
    internal void ClearClusterSubscriptionState(RespireEndpoint endpoint)
    {
        lock (_stateGate)
        {
            if (Disposed || !_clusterSubscriptionStates.Remove(endpoint)) return;
            QueueEndpointStateLocked(new RespireConnectionStateChange(endpoint, RespireConnectionState.Connected, null)
            {
                ReconnectSource = RespireReconnectSource.PubSub,
                SourceState = RespireConnectionState.Connected,
            });
        }
        ThreadPool.UnsafeQueueUserWorkItem(static core => core.PublishQueuedStates(), this, preferLocal: false);
    }

    internal void NotifyCommandStateChanged(
        int slot,
        RespireConnectionState state,
        Exception? error = null)
    {
        var multiplexer = Multiplexer;
        NotifyCommandStateChanged(multiplexer, slot,
            new RespireConnectionStateChange(new(multiplexer.Host, multiplexer.Port), state, error));
    }

    internal void NotifyCommandStateChanged(int slot, RespireConnectionStateChange change)
        => NotifyCommandStateChanged(Multiplexer, slot, change);

    internal void NotifyCommandStateChanged(
        RespireConnectionMultiplexer node,
        int slot,
        RespireConnectionState state,
        Exception? error = null)
        => NotifyCommandStateChanged(
            node,
            slot,
            new RespireConnectionStateChange(new RespireEndpoint(node.Host, node.Port), state, error));

    internal void NotifyCommandStateChanged(
        RespireConnectionMultiplexer node,
        int slot,
        RespireConnectionStateChange change)
    {
        if (Cluster?.IsReplicaNode(node) == true)
        {
            NotifyReadReplicaStateChanged(node, slot, change);
            return;
        }
        int? cacheEvictions = null;

        lock (_stateGate)
        {
            // Keep observer membership stable through cache and health mutation.
            lock (Cluster?.NodeStateGate ?? _stateGate)
            {
                if (Cluster is { } cluster && !cluster.IsNodeObserved(node))
                {
                    return;
                }
                if (change.State != RespireConnectionState.Connected)
                {
                    cacheEvictions = ClientCache?.FlushForContinuityLossWithoutMetrics();
                }
                var commandSlot = (node, slot);
                switch (change.State)
                {
                    case RespireConnectionState.Reconnecting:
                        _disconnectedCommandSlots.Remove(commandSlot);
                        _reconnectingCommandSlots.Add(commandSlot);
                        break;
                    case RespireConnectionState.Disconnected:
                        _reconnectingCommandSlots.Remove(commandSlot);
                        _disconnectedCommandSlots.Add(commandSlot);
                        break;
                    default:
                        _reconnectingCommandSlots.Remove(commandSlot);
                        _disconnectedCommandSlots.Remove(commandSlot);
                        break;
                }

                QueueEndpointStateLocked(change);
            }
        }

        // Metrics listeners and health subscribers can run user code, outside both gates.
        if (cacheEvictions is { } removed)
        {
            ClientSideCacheCoordinator.PublishContinuityFlushMetrics(removed);
        }
        PublishQueuedStates();
    }

    internal void NotifyCommandNodeRetired(RespireConnectionMultiplexer node)
    {
        if (Cluster?.IsReplicaNode(node) == true)
        {
            lock (_stateGate)
            {
                _reconnectingCommandSlots.RemoveWhere(item => ReferenceEquals(item.Node, node));
                _disconnectedCommandSlots.RemoveWhere(item => ReferenceEquals(item.Node, node));
                QueueEndpointStateLocked(new RespireConnectionStateChange(
                    new RespireEndpoint(node.Host, node.Port), RespireConnectionState.Connected, null));
            }
            PublishQueuedStates();
            return;
        }
        int? cacheEvictions = null;

        lock (_stateGate)
        {
            // Keep observer membership stable through cache and health mutation.
            lock (Cluster?.NodeStateGate ?? _stateGate)
            {
                // Topology publication and callbacks are separate. A node reactivated before
                // this callback acquired the health lock must retain its current health state.
                if (Cluster?.IsNodeObserved(node) == true)
                {
                    return;
                }
                cacheEvictions = ClientCache?.FlushForContinuityLossWithoutMetrics();
                _reconnectingCommandSlots.RemoveWhere(
                    commandSlot => ReferenceEquals(commandSlot.Node, node));
                _disconnectedCommandSlots.RemoveWhere(
                    commandSlot => ReferenceEquals(commandSlot.Node, node));
                QueueEndpointStateLocked(new RespireConnectionStateChange(
                    new RespireEndpoint(node.Host, node.Port), RespireConnectionState.Connected, null));
            }
        }

        // Metrics listeners and health subscribers can run user code, outside both gates.
        if (cacheEvictions is { } removed)
        {
            ClientSideCacheCoordinator.PublishContinuityFlushMetrics(removed);
        }
        PublishQueuedStates();
    }

    internal void NotifyReadReplicaNodeRetired(RespireConnectionMultiplexer node)
    {
        lock (_stateGate)
        {
            _reconnectingCommandSlots.RemoveWhere(item => ReferenceEquals(item.Node, node));
            _disconnectedCommandSlots.RemoveWhere(item => ReferenceEquals(item.Node, node));
            QueueEndpointStateLocked(new RespireConnectionStateChange(
                new RespireEndpoint(node.Host, node.Port), RespireConnectionState.Connected, null));
        }
        PublishQueuedStates();
    }

    /// <summary>
    /// Tracks a read replica's command slot health. Replica reads never populate the client-side
    /// cache, so a replica outage does not flush entries tracked against the primary.
    /// </summary>
    internal void NotifyReadReplicaStateChanged(
        RespireConnectionMultiplexer node,
        int slot,
        RespireConnectionStateChange change)
    {
        lock (_stateGate)
        {
            if (Disposed) return;
            var commandSlot = (node, slot);
            switch (change.State)
            {
                case RespireConnectionState.Reconnecting:
                    _disconnectedCommandSlots.Remove(commandSlot);
                    _reconnectingCommandSlots.Add(commandSlot);
                    break;
                case RespireConnectionState.Disconnected:
                    _reconnectingCommandSlots.Remove(commandSlot);
                    _disconnectedCommandSlots.Add(commandSlot);
                    break;
                default:
                    _reconnectingCommandSlots.Remove(commandSlot);
                    _disconnectedCommandSlots.Remove(commandSlot);
                    break;
            }

            QueueEndpointStateLocked(change);
        }

        PublishQueuedStates();
    }

    /// <summary>
    /// Forgets a closed read replica's slot health, so a later replica at the same host and port
    /// does not inherit a stale Reconnecting or Disconnected state.
    /// </summary>
    internal void NotifyReadReplicaRetired(RespireConnectionMultiplexer node)
    {
        lock (_stateGate)
        {
            if (Disposed) return;
            var removed = _reconnectingCommandSlots.RemoveWhere(
                    commandSlot => ReferenceEquals(commandSlot.Node, node))
                + _disconnectedCommandSlots.RemoveWhere(
                    commandSlot => ReferenceEquals(commandSlot.Node, node));
            if (removed != 0)
            {
                QueueEndpointStateLocked(new RespireConnectionStateChange(
                    new RespireEndpoint(node.Host, node.Port), RespireConnectionState.Connected, null));
            }
        }

        PublishQueuedStates();
    }

    private void QueueEndpointStateLocked(RespireConnectionStateChange source)
    {
        var state = GetEndpointStateLocked(source.Endpoint);
        var publishedState = _publishedEndpointStates.GetValueOrDefault(
            source.Endpoint,
            RespireConnectionState.Connected);
        if (state == publishedState && source.ReconnectAttempt == 0)
        {
            return;
        }

        if (state == RespireConnectionState.Connected)
        {
            _publishedEndpointStates.Remove(source.Endpoint);
        }
        else
        {
            _publishedEndpointStates[source.Endpoint] = state;
        }

        _pendingStates.Enqueue(source with { State = state });
    }

    private RespireConnectionState GetEndpointStateLocked(RespireEndpoint endpoint)
    {
        if (Disposed)
        {
            return RespireConnectionState.Disconnected;
        }

        var hasClusterSubscriptionState = _clusterSubscriptionStates.TryGetValue(endpoint, out var clusterSubscriptionState);
        if (_disconnectedCommandSlots.Any(commandSlot => IsEndpoint(commandSlot.Node, endpoint))
            || HasSubscriptionState(endpoint, RespireConnectionState.Disconnected)
            || hasClusterSubscriptionState && clusterSubscriptionState == RespireConnectionState.Disconnected)
        {
            return RespireConnectionState.Disconnected;
        }

        return _reconnectingCommandSlots.Any(commandSlot => IsEndpoint(commandSlot.Node, endpoint))
               || HasSubscriptionState(endpoint, RespireConnectionState.Reconnecting)
               || hasClusterSubscriptionState && clusterSubscriptionState == RespireConnectionState.Reconnecting
            ? RespireConnectionState.Reconnecting
            : RespireConnectionState.Connected;
    }

    private bool HasSubscriptionState(RespireEndpoint endpoint, RespireConnectionState state)
    {
        if (_subscriptionStates is null) return false;
        return (_subscriptionStates.TryGetValue((false, endpoint), out var regular) && regular == state)
            || (_subscriptionStates.TryGetValue((true, endpoint), out var sharded) && sharded == state);
    }

    private static bool IsEndpoint(RespireConnectionMultiplexer node, RespireEndpoint endpoint)
        => node.Host == endpoint.Host && node.Port == endpoint.Port;

    private void PublishQueuedStates()
    {
        lock (_stateGate)
        {
            if (_publishingState || _pendingStates.Count == 0)
            {
                return;
            }

            _publishingState = true;
        }

        while (true)
        {
            Action<RespireConnectionStateChange>? handlers;
            RespireConnectionStateChange change;
            lock (_stateGate)
            {
                if (!_pendingStates.TryDequeue(out change))
                {
                    _publishingState = false;
                    return;
                }

                // Recovery can have been queued before disposal started while an earlier
                // observer held the dispatcher. The terminal client event has no recovery source.
                if (Disposed && change.ReconnectSource is RespireReconnectSource.Dedicated
                    or RespireReconnectSource.PubSub or RespireReconnectSource.ClusterDiscovery) continue;

                handlers = ConnectionStateChanged;
            }

            try
            {
                handlers?.Invoke(change);
            }
            catch (Exception ex)
            {
                try { Logger?.LogWarning(ex, "Connection state-change handler threw"); }
                catch (Exception) { /* A user logger must not strand queued connection events. */ }
            }
        }
    }

    public SubscriptionHub Hub
    {
        get
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            if (_hub is { } hub)
            {
                return hub;
            }

            lock (_hubGate)
            {
                // Re-checked under the gate: disposal must not be revivable through a
                // freshly created hub (and its dedicated connection).
                ObjectDisposedException.ThrowIf(Disposed, this);
                return _hub ??= new SubscriptionHub(this);
            }
        }
    }

    internal DedicatedConnectionPool CreateServerPool(RespireEndpoint endpoint)
    {
        lock (_hubGate)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            var pool = new DedicatedConnectionPool(endpoint.Host, endpoint.Port, Options.ToConnectionOptions(enableMaintenanceNotifications: true), Logger,
                NotifyRecoveryStateChanged);
            (_serverPools ??= []).Add(pool);
            return pool;
        }
    }

    internal async ValueTask ReleaseServerPoolAsync(DedicatedConnectionPool pool)
    {
        try
        {
            await pool.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            // Keep the pool visible to concurrent client disposal until its cleanup finishes.
            lock (_hubGate) _serverPools!.Remove(pool);
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Preserve worker ownership through client cleanup awaits without flowing it into
        // unrelated tasks created by callbacks.
        var disposeStartedOnSmigratedWorker = Cluster?.IsOnSmigratedWorker == true;
        if (Disposed)
        {
            return;
        }

        CoordinationCleanupQueue? cleanupQueue;
        lock (_hubGate)
        {
            Disposed = true;
            cleanupQueue = _coordinationCleanupQueue;
        }
        // A closed client cannot send queued releases, so cancel its background cleanup before
        // tearing down the transports. Dispose semaphore permits before the client when possible.
        if (cleanupQueue is not null)
            await cleanupQueue.DisposeAsync().ConfigureAwait(false);
        Interlocked.Exchange(ref _threadPoolMonitor, null)?.Dispose();
        ClientCache?.StopInvalidationObservers();
        ClientCache?.StopSharedReads();
        ClientCache?.Clear();
        RespireEndpoint[] commandEndpoints;
        if (Cluster is { } clusterRouter) commandEndpoints = clusterRouter.GetActiveEndpoints();
        // A lazy Sentinel client has no data endpoint until a validated generation is published.
        else if (Sentinel is { } sentinelRouter)
            commandEndpoints = sentinelRouter.Current is { } generation ? [generation.Endpoint] : [];
        else commandEndpoints = [Endpoint];
        // Replica endpoints used by read views are command endpoints too; report their terminal state.
        commandEndpoints = commandEndpoints.Concat(ReadRouter.GetOpenEndpoints()).Distinct().ToArray();
        lock (_stateGate)
        {
            if (_subscriptionStates is not null)
                foreach (var subscription in _subscriptionStates.Keys)
                    QueueEndpointStateLocked(new RespireConnectionStateChange(
                        subscription.Endpoint, RespireConnectionState.Disconnected, null));

            foreach (var endpoint in _clusterSubscriptionStates.Keys)
                QueueEndpointStateLocked(new RespireConnectionStateChange(
                    endpoint, RespireConnectionState.Disconnected, null));

            foreach (var endpoint in commandEndpoints)
            {
                QueueEndpointStateLocked(new RespireConnectionStateChange(
                    endpoint, RespireConnectionState.Disconnected, null));
            }
        }

        PublishQueuedStates();
        SubscriptionHub? hub;
        DedicatedConnectionPool[] serverPools;
        lock (_hubGate)
        {
            hub = _hub;
            serverPools = _serverPools?.ToArray() ?? [];
        }

        foreach (var pool in serverPools)
            await pool.DisposeAsync().ConfigureAwait(false);

        if (hub is not null)
        {
            await hub.DisposeAsync().ConfigureAwait(false);
        }

        if (Sentinel is { } sentinel) await sentinel.DisposeAsync().ConfigureAwait(false);
        await ReadRouter.DisposeAsync().ConfigureAwait(false);
        await _dedicatedPool.DisposeAsync().ConfigureAwait(false);
        if (Cluster is { } cluster)
        {
            cluster.SlotStateChanged -= NotifyCommandStateChanged;
            cluster.DedicatedStateChanged -= NotifyRecoveryStateChanged;
            cluster.DiscoveryStateChanged -= NotifyRecoveryStateChanged;
            cluster.NodeRetired -= NotifyCommandNodeRetired;
            cluster.ReplicaNodeRetired -= NotifyReadReplicaNodeRetired;
            cluster.TopologyChanged -= NotifySubscriptionTopologyChanged;
            await cluster.DisposeAsync(disposeStartedOnSmigratedWorker).ConfigureAwait(false);
        }
        else
        {
            Multiplexer.SlotStateChanged -= NotifyCommandStateChanged;
            Multiplexer.MovingHandoffPublished -= RefreshStandaloneDedicatedPool;
        }

        await _multiplexer.DisposeAsync().ConfigureAwait(false);
    }
}
