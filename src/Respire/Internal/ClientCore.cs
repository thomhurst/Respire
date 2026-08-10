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
    private readonly Queue<RespireConnectionState> _pendingStates = [];
    private readonly HashSet<(RespireConnectionMultiplexer Node, int Slot)> _reconnectingCommandSlots = [];
    private SubscriptionHub? _hub;
    private bool _subscriptionReconnecting;
    private bool _publishingState;
    private RespireConnectionState _publishedState = RespireConnectionState.Connected;

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
            throw new RespireConfigurationException("Redis Cluster supports database 0 only.");
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

    public event Action<RespireConnectionState>? ConnectionStateChanged;

    internal void NotifySubscriptionStateChanged(RespireConnectionState state)
    {
        lock (_stateGate)
        {
            _subscriptionReconnecting = state == RespireConnectionState.Reconnecting;
            QueueAggregateStateLocked();
        }

        PublishQueuedStates();
    }

    internal void NotifyCommandStateChanged(int slot, RespireConnectionState state)
        => NotifyCommandStateChanged(Multiplexer, slot, state);

    internal void NotifyCommandStateChanged(
        RespireConnectionMultiplexer node,
        int slot,
        RespireConnectionState state)
    {
        lock (_stateGate)
        {
            var commandSlot = (node, slot);
            var reconnecting = state == RespireConnectionState.Reconnecting;
            if (reconnecting)
            {
                _reconnectingCommandSlots.Add(commandSlot);
            }
            else
            {
                _reconnectingCommandSlots.Remove(commandSlot);
            }

            QueueAggregateStateLocked();
        }

        PublishQueuedStates();
    }

    internal void NotifyCommandNodeRetired(RespireConnectionMultiplexer node)
    {
        lock (_stateGate)
        {
            _reconnectingCommandSlots.RemoveWhere(
                commandSlot => ReferenceEquals(commandSlot.Node, node));
            QueueAggregateStateLocked();
        }

        PublishQueuedStates();
    }

    private void QueueAggregateStateLocked()
    {
        var aggregate = _reconnectingCommandSlots.Count == 0 && !_subscriptionReconnecting
            ? RespireConnectionState.Connected
            : RespireConnectionState.Reconnecting;
        if (aggregate == _publishedState)
        {
            return;
        }

        _publishedState = aggregate;
        _pendingStates.Enqueue(aggregate);
    }

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
            Action<RespireConnectionState>? handlers;
            RespireConnectionState state;
            lock (_stateGate)
            {
                if (!_pendingStates.TryDequeue(out state))
                {
                    _publishingState = false;
                    return;
                }

                handlers = ConnectionStateChanged;
            }

            try
            {
                handlers?.Invoke(state);
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
