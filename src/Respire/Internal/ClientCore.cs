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

    public readonly RespireConnectionMultiplexer Multiplexer;
    public readonly RespireOptions Options;
    public readonly ILogger? Logger;
    public readonly DedicatedConnectionPool DedicatedPool;
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
        RespirePushHandler? pushHandler = ClientCache is null ? null : ClientCache.HandlePush;
        var connectionOptions = options.ToConnectionOptions(
            pushHandler,
            enableClientTracking: ClientCache is not null);
        Multiplexer = RespireConnectionMultiplexer.Create(
            endpoint.Host, endpoint.Port, options.Connections, connectionOptions, Logger);
        DedicatedPool = new DedicatedConnectionPool(
            endpoint.Host, endpoint.Port, options.ToConnectionOptions(), Logger, NotifyRecoveryStateChanged);
        Cluster = options.UseCluster
            ? new ClusterRouter(options, Multiplexer, connectionOptions)
            : null;
        if (Cluster is { } cluster)
        {
            cluster.SlotStateChanged += NotifyCommandStateChanged;
            cluster.DedicatedStateChanged += NotifyRecoveryStateChanged;
            cluster.DiscoveryStateChanged += NotifyRecoveryStateChanged;
            cluster.NodeRetired += NotifyCommandNodeRetired;
        }
        else
        {
            Multiplexer.SlotStateChanged += NotifyCommandStateChanged;
        }
        if (options.ThreadPoolMonitoring)
            _threadPoolMonitor = ThreadPoolMonitor.Acquire(options.CreateLogger("Respire.ThreadPool"), options.ThreadPoolWarningThreshold);
    }

    public ValueTask EnsureConnectedAsync(CancellationToken cancellationToken)
        => Cluster is { } cluster
            ? cluster.EnsureConnectedAsync(cancellationToken, discovery: null)
            : Multiplexer.EnsureConnectedAsync(cancellationToken);

    public event Action<RespireConnectionStateChange>? ConnectionStateChanged;

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
            if (clusterSharded && change.State == RespireConnectionState.Connected)
                _subscriptionStates.Remove((true, change.Endpoint));
            else
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
        => NotifyCommandStateChanged(
            Multiplexer,
            slot,
            new RespireConnectionStateChange(
                new RespireEndpoint(Multiplexer.Host, Multiplexer.Port), state, error));

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
        var commandEndpoints = Cluster?.GetActiveEndpoints() ?? [Options.PrimaryEndpoint];
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

        await DedicatedPool.DisposeAsync().ConfigureAwait(false);
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

        await Multiplexer.DisposeAsync().ConfigureAwait(false);
    }
}
