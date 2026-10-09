using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Respire.Infrastructure;
using Respire.Networking;

namespace Respire.Internal;

/// <summary>
/// The state one logical client owns: the multiplexed connection set, the dedicated-connection
/// pool for blocking commands, and the lazily created pub/sub hub. Key-prefixed views created
/// by <see cref="RespireClient.WithKeyPrefix(string)"/> share one core; only the root client disposes it.
/// </summary>
internal sealed class ClientCore : IAsyncDisposable
{
    private readonly Lock _hubGate = new();
    private readonly Lock _stateGate = new();
    private readonly Queue<RespireConnectionStateChange> _pendingStates = [];
    private readonly HashSet<(RespireConnectionMultiplexer Node, int Slot)> _reconnectingCommandSlots = [];
    private readonly HashSet<(RespireConnectionMultiplexer Node, int Slot)> _disconnectedCommandSlots = [];
    private readonly Dictionary<RespireEndpoint, RespireConnectionState> _publishedEndpointStates = [];
    private readonly Dictionary<RespireEndpoint, RespireConnectionState> _clusterSubscriptionStates = [];
    private SubscriptionHub? _hub;
    private readonly DedicatedPoolLedger _ownedPools;
    internal DedicatedPoolLedger OwnedPools => _ownedPools;
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
    internal readonly StandaloneCircuitRegistry? Circuits;
    internal readonly HedgedReadBudget? HedgedReads;
    public readonly ILogger? Logger;
    private CorrectionCoordinator? _corrections;
    internal CorrectionCoordinator Corrections
    {
        get { lock (_hubGate) return _corrections ??= new(this); }
    }
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
    // Tests own this override's lifetime and restore it before disposing the client.
    internal DedicatedConnectionPool? TestingDedicatedPoolOverride { get; set; }
    public DedicatedConnectionPool DedicatedPool => TestingDedicatedPoolOverride ?? Sentinel?.Current?.Pool ?? Volatile.Read(ref _dedicatedPool);
    internal readonly SentinelRouter? Sentinel;
    internal readonly ReadEndpointRouter ReadRouter;

    /// <summary>Preserves the logical cache owner on every data, replica and dedicated transport.</summary>
    internal RespireConnectionOptions CreateConnectionOptions(RespirePushHandler? pushHandler = null,
        bool enableClientTracking = false, bool enableMaintenanceNotifications = false)
        => Options.ToConnectionOptions(pushHandler, enableClientTracking, enableMaintenanceNotifications) with
        {
            CacheMutationAdmission = ClientCache,
        };
    public readonly ClusterRouter? Cluster;
    public readonly ClientSideCacheCoordinator? ClientCache;
    public volatile bool Disposed;

    public ClientCore(RespireOptions options)
    {
        _ownedPools = new(_hubGate);
        Options = options;
        HedgedReads = options.HedgedReads is { } hedging ? new(hedging.MaximumExtraLoadPercent) : null;
        Logger = options.CreateLogger("Respire.RespireClient");
        var endpoint = options.PrimaryEndpoint;
        ClientCache = options.ClientSideCache is { } cacheOptions
            ? new ClientSideCacheCoordinator(cacheOptions)
            : null;
        var clientCache = ClientCache;
        RespirePushHandler? pushHandler = clientCache is null ? null : clientCache.HandlePush;
        var connectionOptions = CreateConnectionOptions(
            pushHandler,
            enableClientTracking: ClientCache is not null, enableMaintenanceNotifications: true) with
        {
            CredentialCacheInvalidation = clientCache is null ? null : clientCache.FlushForContinuityLossWithoutMetrics,
            CredentialCacheRetirementFence = clientCache is null ? null : clientCache.FlushForMovingRetirementFence,
        };
        _multiplexer = RespireConnectionMultiplexer.Create(
            endpoint.Host, endpoint.Port, options.Connections, connectionOptions, Logger);
        ReadRouter = new ReadEndpointRouter(this);
        Circuits = options.CircuitBreaker is { } circuitOptions
            ? new(circuitOptions, () => Multiplexer.ActiveConnectionEndpoint,
                endpoint => Cluster is { } cluster
                    ? cluster.IsCircuitEndpointCurrent(endpoint)
                    : Sentinel is not null && ReadRouter.IsCurrentReplicaEndpoint(endpoint)) : null;
        _dedicatedPool = CreateStandaloneDedicatedPool(_multiplexer.CaptureMovingPublication());
        _ownedPools.Add(_dedicatedPool);
        Cluster = options.UseCluster
            ? new ClusterRouter(options, Multiplexer, connectionOptions, circuits: Circuits)
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

    public ValueTask EnsureConnectedAsync(CancellationToken cancellationToken, bool observeEstablishmentErrors = false)
        => Sentinel is not null
            ? EnsureSentinelConnectedAsync(cancellationToken, observeEstablishmentErrors)
            : Cluster is { } cluster
            ? cluster.EnsureConnectedAsync(cancellationToken, discovery: null)
            : Multiplexer.EnsureConnectedAsync(cancellationToken, observeEstablishmentErrors);

    /// <summary>Captures an initialized primary route without starting discovery or reconnecting.</summary>
    /// <remarks>
    /// Readiness is an observation, not a lease. Connection selection or admission can still
    /// throw if socket loss, retirement, or disposal races this check.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetReadyPrimaryMultiplexer(out RespireConnectionMultiplexer multiplexer)
    {
        if (Sentinel is null)
        {
            multiplexer = _multiplexer;
            return multiplexer.IsInitialized;
        }

        var generation = Sentinel.Current;
        if (generation is not null && generation.Multiplexer.IsInitialized
            && generation.Multiplexer.IsConnected && !generation.IsRetired)
        {
            // Keep selection on this validated generation. Admission checks retirement again.
            multiplexer = generation.Multiplexer;
            return true;
        }

        multiplexer = null!;
        return false;
    }

    private async ValueTask EnsureSentinelConnectedAsync(CancellationToken cancellationToken, bool observeEstablishmentErrors)
        => await Sentinel!.GetGenerationAsync(cancellationToken, observeEstablishmentErrors: observeEstablishmentErrors).ConfigureAwait(false);

    internal async ValueTask<DedicatedConnectionPool> GetDedicatedPoolAsync(CancellationToken cancellationToken)
    {
        if (TestingDedicatedPoolOverride is { } testingPool) return testingPool;
        if (Sentinel is { } sentinel)
            return (await sentinel.GetGenerationAsync(cancellationToken).ConfigureAwait(false)).Pool;
        // Read the published endpoint here too: a new upload can race the handoff callback.
        RefreshStandaloneDedicatedPool();
        return Volatile.Read(ref _dedicatedPool);
    }

    internal ValueTask<(DedicatedConnectionPool Pool, RespireConnection Connection)> RentDedicatedConnectionAsync(
        DedicatedConnectionPool pool, CancellationToken cancellationToken, bool reuseIdle = true,
        DedicatedLeaseKind kind = DedicatedLeaseKind.Ordinary, string? preferredZone = null)
        => DedicatedLeaseAcquisition.RentAsync(pool, new DedicatedLeaseRoute(this),
            cancellationToken, reuseIdle, kind, preferredZone);

    private readonly struct DedicatedLeaseRoute(ClientCore owner) : IDedicatedLeaseRoute
    {
        public void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(owner.Disposed, owner);
        public bool CanRetry(int attempt, CancellationToken cancellationToken) => !owner.Disposed;
        public void RecordRetirement(Exception error, int attempt) { }
        public ValueTask<DedicatedConnectionPool> SelectReplacementAsync(CancellationToken cancellationToken)
            => owner.GetDedicatedPoolAsync(cancellationToken);
        public void SetTerminalError(Exception error) { }
        public void Dispose() { }
    }

    internal bool IsDedicatedStreamRouteCurrent(DedicatedConnectionPool pool, RespireConnection connection)
        => !pool.IsStopping && pool.IsMovingPublicationCurrent && ReferenceEquals(pool, DedicatedPool)
            && Multiplexer.ActiveConnectionEndpoint == new RespireEndpoint(connection.Host, connection.Port);

    internal sealed class CorrectionLease(ClientCore owner, DedicatedConnectionPool pool) : IAsyncDisposable
    {
        internal DedicatedConnectionPool Pool => pool;
        public ValueTask DisposeAsync() => owner.ReleaseServerPoolAsync(pool);
    }

    internal CorrectionLease GetCorrectionLease(RespireEndpoint endpoint, RespireConnection? original)
    {
        var options = CreateConnectionOptions();
        if (options.UseTls)
            options = options with { TlsOptions = RespireConnection.CreateTlsOptions(options.TlsOptions, original?.Host ?? endpoint.Host) };
        // Client IDs belong to the original physical server, not the current MOVING destination.
        lock (_hubGate)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            var pool = new DedicatedConnectionPool(original?.NetworkPeerAddress ?? endpoint.Host,
                original?.NetworkPeerPort ?? endpoint.Port, options, Logger);
            _ownedPools.Add(pool);
            return new(this, pool);
        }
    }

    private void RefreshStandaloneDedicatedPool()
    {
        DedicatedConnectionPool previous;
        lock (_hubGate)
        {
            if (Disposed || Cluster is not null || Sentinel is not null) return;
            var publication = _multiplexer.CaptureMovingPublication();
            if (ReferenceEquals(publication.Publication, _dedicatedPool.MovingPublication)) return;
            previous = _dedicatedPool;
            var replacement = CreateStandaloneDedicatedPool(publication);
            _ownedPools.Add(replacement);
            Volatile.Write(ref _dedicatedPool, replacement);
        }
        // Keep borrowed uploads and blocking calls alive, while rejecting new rentals on the
        // old endpoint. Client disposal retains ownership until the final borrower returns.
        _ = RetireMovedDedicatedPoolAsync(previous);
    }

    private DedicatedConnectionPool CreateStandaloneDedicatedPool((RespireEndpoint Endpoint, object Publication) publication)
    {
        var endpoint = publication.Endpoint;
        DedicatedConnectionPool? pool = null;
        pool = new DedicatedConnectionPool(endpoint.Host, endpoint.Port,
            CreateConnectionOptions(enableMaintenanceNotifications: true), Logger, NotifyRecoveryStateChanged,
            connection =>
            {
                if (Cluster is not null || Sentinel is not null) return;
                void OnMoving(MovingAnnouncement announcement)
                    => _multiplexer.QueueDedicatedMovingHandoff(connection, announcement,
                        () => !Disposed && ReferenceEquals(pool, DedicatedPool) && !pool!.IsStopping);
                connection.MovingNotification += OnMoving;
                // A server may send MOVING alongside the maintenance opt-in acknowledgement.
                if (connection.LastMovingAnnouncement is { } announcement) OnMoving(announcement);
            }) { MovingOwner = _multiplexer, MovingPublication = publication.Publication };
        _multiplexer.RegisterMovingDedicatedPool(pool);
        return pool;
    }

    private async Task RetireMovedDedicatedPoolAsync(DedicatedConnectionPool pool)
    {
        try { await _ownedPools.RetireAsync(pool, moving: true).ConfigureAwait(false); }
        catch (Exception error)
        {
            try { Logger?.DedicatedMovingCleanupFailed(error); }
            catch { /* Logging cannot fault the background retirement. */ }
        }
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
                    or RespireReconnectSource.PubSub or RespireReconnectSource.ClusterDiscovery
                    or RespireReconnectSource.SentinelMonitor) continue;

                handlers = ConnectionStateChanged;
            }

            try
            {
                handlers?.Invoke(change);
            }
            catch (Exception ex)
            {
                try { Logger?.ConnectionStateObserverFailed(ex); }
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

    internal DedicatedConnectionPool CreateServerPool(RespireEndpoint endpoint, bool controlConnection = false,
        TimeSpan? commandTimeout = null)
    {
        lock (_hubGate)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            var options = controlConnection ? Options.ToControlConnectionOptions() with { CacheMutationAdmission = ClientCache }
                : CreateConnectionOptions(enableMaintenanceNotifications: !endpoint.IsUnixSocket);
            if (commandTimeout is { } timeout) options = options with { CommandTimeout = timeout };
            var pool = new DedicatedConnectionPool(endpoint.Host, endpoint.Port, options, Logger,
                NotifyRecoveryStateChanged);
            _ownedPools.Add(pool);
            return pool;
        }
    }

    internal ValueTask ReleaseServerPoolAsync(DedicatedConnectionPool pool)
        => new(_ownedPools.ReleaseAsync(pool));

    /// <remarks>Observes each owner separately in disposal order; a pool, subscription hub, or router
    /// failure cannot skip a later owner. A single failure (even an owner-supplied
    /// <see cref="AggregateException"/>) is rethrown unchanged when no other phase fails; multiple
    /// failures across owners or phases become direct inner exceptions of one
    /// <see cref="AggregateException"/> after cleanup finishes, including every failure from the
    /// <see cref="DedicatedPoolLedger"/>. An exception seen through both retirement and disposal is
    /// reported once.</remarks>
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
        lock (_hubGate)
        {
            hub = _hub;
        }

        List<Exception>? disposeErrors = null;
        // Disposed already gates RefreshStandaloneDedicatedPool, so an early abort cannot publish another pool.
        await DisposeOwnerAsync(() => new(_ownedPools.DisposeAllAsync())).ConfigureAwait(false);
        if (hub is not null) await DisposeOwnerAsync(hub.DisposeAsync).ConfigureAwait(false);
        if (Sentinel is { } sentinel) await DisposeOwnerAsync(sentinel.DisposeAsync).ConfigureAwait(false);
        await DisposeOwnerAsync(ReadRouter.DisposeAsync).ConfigureAwait(false);
        if (Cluster is { } cluster)
        {
            cluster.SlotStateChanged -= NotifyCommandStateChanged;
            cluster.DedicatedStateChanged -= NotifyRecoveryStateChanged;
            cluster.DiscoveryStateChanged -= NotifyRecoveryStateChanged;
            cluster.NodeRetired -= NotifyCommandNodeRetired;
            cluster.ReplicaNodeRetired -= NotifyReadReplicaNodeRetired;
            cluster.TopologyChanged -= NotifySubscriptionTopologyChanged;
            await DisposeOwnerAsync(() => cluster.DisposeAsync(disposeStartedOnSmigratedWorker)).ConfigureAwait(false);
        }
        else
        {
            Multiplexer.SlotStateChanged -= NotifyCommandStateChanged;
            Multiplexer.MovingHandoffPublished -= RefreshStandaloneDedicatedPool;
        }

        await DisposeOwnerAsync(_multiplexer.DisposeAsync).ConfigureAwait(false);
        CleanupTasks.Rethrow(disposeErrors);

        async ValueTask DisposeOwnerAsync(Func<ValueTask> dispose)
        {
            // Keep shutdown ordered, but never let one owner's failure skip another or mask an earlier error.
            Task? disposal = null;
            try
            {
                disposal = dispose().AsTask();
                await disposal.ConfigureAwait(false);
            }
            catch (Exception error)
            {
                // Remove only the Task.Exception wrapper here. Flattening a single owner-supplied
                // aggregate would change its identity; flatten only when combining failures above.
                disposeErrors ??= [];
                if (disposal?.Exception is { } taskError)
                    disposeErrors.AddRange(taskError.InnerExceptions);
                else
                    disposeErrors.Add(error);
            }
        }
    }
}
