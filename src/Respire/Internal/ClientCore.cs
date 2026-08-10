using Microsoft.Extensions.Logging;
using Respire.Infrastructure;

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
    private RespireEndpoint? _subscriptionEndpoint;
    private RespireConnectionState _subscriptionState = RespireConnectionState.Connected;
    private bool _publishingState;

    public readonly RespireConnectionMultiplexer Multiplexer;
    public readonly RespireOptions Options;
    public readonly ILogger? Logger;
    public readonly DedicatedConnectionPool DedicatedPool;
    public readonly ClusterRouter? Cluster;
    public volatile bool Disposed;

    public ClientCore(RespireOptions options)
    {
        if (options.Cluster && options.Database != 0)
        {
            throw new ArgumentException("Redis Cluster supports database 0 only.", nameof(options));
        }

        Options = options;
        Logger = options.CreateLogger("Respire.RespireClient");
        var endpoint = options.PrimaryEndpoint;
        var connectionOptions = options.ToConnectionOptions();
        Multiplexer = RespireConnectionMultiplexer.Create(
            endpoint.Host, endpoint.Port, options.Connections, connectionOptions, Logger);
        DedicatedPool = new DedicatedConnectionPool(
            endpoint.Host, endpoint.Port, connectionOptions, Logger);
        Cluster = options.Cluster ? new ClusterRouter(options, Multiplexer) : null;
        if (Cluster is { } cluster)
        {
            cluster.SlotStateChanged += NotifyCommandStateChanged;
            cluster.NodeRetired += NotifyCommandNodeRetired;
        }
        else
        {
            Multiplexer.SlotStateChanged += NotifyCommandStateChanged;
        }
    }

    public ValueTask EnsureConnectedAsync(CancellationToken cancellationToken)
        => Cluster is { } cluster
            ? cluster.EnsureConnectedAsync(cancellationToken)
            : Multiplexer.EnsureConnectedAsync(cancellationToken);

    public event Action<RespireConnectionStateChange>? ConnectionStateChanged;

    internal void NotifySubscriptionStateChanged(
        RespireConnectionState state,
        Exception? error = null)
        => NotifySubscriptionStateChanged(new RespireConnectionStateChange(
            Options.PrimaryEndpoint, state, error));

    internal void NotifySubscriptionStateChanged(RespireConnectionStateChange change)
    {
        lock (_stateGate)
        {
            var previousEndpoint = _subscriptionEndpoint;
            _subscriptionEndpoint = change.Endpoint;
            _subscriptionState = change.State;
            if (previousEndpoint is { } previous && previous != change.Endpoint)
            {
                QueueEndpointStateLocked(new RespireConnectionStateChange(
                    previous, RespireConnectionState.Connected, null));
            }

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
        lock (_stateGate)
        {
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

    internal void NotifyCommandNodeRetired(RespireConnectionMultiplexer node)
    {
        lock (_stateGate)
        {
            _reconnectingCommandSlots.RemoveWhere(
                commandSlot => ReferenceEquals(commandSlot.Node, node));
            _disconnectedCommandSlots.RemoveWhere(
                commandSlot => ReferenceEquals(commandSlot.Node, node));
            QueueEndpointStateLocked(new RespireConnectionStateChange(
                new RespireEndpoint(node.Host, node.Port), RespireConnectionState.Connected, null));
        }

        PublishQueuedStates();
    }

    private void QueueEndpointStateLocked(RespireConnectionStateChange source)
    {
        var state = GetEndpointStateLocked(source.Endpoint);
        var publishedState = _publishedEndpointStates.GetValueOrDefault(
            source.Endpoint,
            RespireConnectionState.Connected);
        if (state == publishedState)
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
        var isSubscriptionEndpoint = endpoint == _subscriptionEndpoint;
        if (_disconnectedCommandSlots.Any(commandSlot => IsEndpoint(commandSlot.Node, endpoint))
            || isSubscriptionEndpoint && _subscriptionState == RespireConnectionState.Disconnected)
        {
            return RespireConnectionState.Disconnected;
        }

        return _reconnectingCommandSlots.Any(commandSlot => IsEndpoint(commandSlot.Node, endpoint))
               || isSubscriptionEndpoint && _subscriptionState == RespireConnectionState.Reconnecting
            ? RespireConnectionState.Reconnecting
            : RespireConnectionState.Connected;
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

                handlers = ConnectionStateChanged;
            }

            try
            {
                handlers?.Invoke(change);
            }
            catch (Exception ex)
            {
                Logger?.LogWarning(ex, "Connection state-change handler threw");
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

    public async ValueTask DisposeAsync()
    {
        if (Disposed)
        {
            return;
        }

        Disposed = true;
        lock (_stateGate)
        {
            var primaryEndpoint = Options.PrimaryEndpoint;
            var subscriptionEndpoint = _subscriptionEndpoint ?? primaryEndpoint;
            _subscriptionEndpoint = subscriptionEndpoint;
            _subscriptionState = RespireConnectionState.Disconnected;
            QueueEndpointStateLocked(new RespireConnectionStateChange(
                subscriptionEndpoint, RespireConnectionState.Disconnected, null));
            if (subscriptionEndpoint != primaryEndpoint)
            {
                _subscriptionEndpoint = primaryEndpoint;
                QueueEndpointStateLocked(new RespireConnectionStateChange(
                    primaryEndpoint, RespireConnectionState.Disconnected, null));
            }
        }

        PublishQueuedStates();
        SubscriptionHub? hub;
        lock (_hubGate)
        {
            hub = _hub;
        }

        if (hub is not null)
        {
            await hub.DisposeAsync().ConfigureAwait(false);
        }

        await DedicatedPool.DisposeAsync().ConfigureAwait(false);
        if (Cluster is { } cluster)
        {
            cluster.SlotStateChanged -= NotifyCommandStateChanged;
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
