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
    private readonly DedicatedConnectionPool _dedicatedPool;
    public DedicatedConnectionPool DedicatedPool => Sentinel?.Current?.Pool ?? _dedicatedPool;
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
            endpoint.Host, endpoint.Port, options.ToConnectionOptions(), Logger, NotifyRecoveryStateChanged);
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
        }
        else if (Sentinel is null)
        {
            Multiplexer.SlotStateChanged += NotifyCommandStateChanged;
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
        => Sentinel is { } sentinel
            ? (await sentinel.GetGenerationAsync(cancellationToken).ConfigureAwait(false)).Pool
            : _dedicatedPool;

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

        if (_disconnectedCommandSlots.Any(commandSlot => IsEndpoint(commandSlot.Node, endpoint))
            || HasSubscriptionState(endpoint, RespireConnectionState.Disconnected))
        {
            return RespireConnectionState.Disconnected;
        }

        return _reconnectingCommandSlots.Any(commandSlot => IsEndpoint(commandSlot.Node, endpoint))
               || HasSubscriptionState(endpoint, RespireConnectionState.Reconnecting)
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
            var pool = new DedicatedConnectionPool(endpoint.Host, endpoint.Port, Options.ToConnectionOptions(), Logger,
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
        if (Disposed)
        {
            return;
        }

        Disposed = true;
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
            await cluster.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            Multiplexer.SlotStateChanged -= NotifyCommandStateChanged;
        }

        await _multiplexer.DisposeAsync().ConfigureAwait(false);
    }
}
