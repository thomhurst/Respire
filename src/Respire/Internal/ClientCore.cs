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
    private SubscriptionHub? _hub;
    private RespireConnectionState _commandState = RespireConnectionState.Connected;
    private RespireConnectionState _subscriptionState = RespireConnectionState.Connected;
    private RespireConnectionState _publishedState = RespireConnectionState.Connected;

    public readonly RespireConnectionMultiplexer Multiplexer;
    public readonly RespireOptions Options;
    public readonly ILogger? Logger;
    public readonly DedicatedConnectionPool DedicatedPool;
    public volatile bool Disposed;

    public ClientCore(RespireOptions options)
    {
        Options = options;
        Logger = options.CreateLogger("Respire.RespireClient");
        var endpoint = options.PrimaryEndpoint;
        Multiplexer = RespireConnectionMultiplexer.Create(
            endpoint.Host, endpoint.Port, options.Connections, options.ToConnectionOptions(), Logger);
        Multiplexer.StateChanged += OnCommandStateChanged;
        DedicatedPool = new DedicatedConnectionPool(
            endpoint.Host, endpoint.Port, options.ToConnectionOptions(), Logger);
    }

    public event Action<RespireConnectionState>? ConnectionStateChanged;

    internal void NotifySubscriptionStateChanged(RespireConnectionState state)
        => NotifyComponentState(state, isSubscription: true);

    internal void NotifyCommandStateChanged(RespireConnectionState state)
        => NotifyComponentState(state, isSubscription: false);

    private void OnCommandStateChanged(RespireConnectionState state)
        => NotifyCommandStateChanged(state);

    private void NotifyComponentState(RespireConnectionState state, bool isSubscription)
    {
        Action<RespireConnectionState>? handlers;
        RespireConnectionState aggregate;
        lock (_stateGate)
        {
            if (isSubscription)
            {
                _subscriptionState = state;
            }
            else
            {
                _commandState = state;
            }

            aggregate = _commandState == RespireConnectionState.Connected
                && _subscriptionState == RespireConnectionState.Connected
                    ? RespireConnectionState.Connected
                    : RespireConnectionState.Reconnecting;
            if (aggregate == _publishedState)
            {
                return;
            }

            _publishedState = aggregate;
            handlers = ConnectionStateChanged;
        }

        try
        {
            handlers?.Invoke(aggregate);
        }
        catch (Exception ex)
        {
            Logger?.LogWarning(ex, "Connection state-change handler threw");
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
        Multiplexer.StateChanged -= OnCommandStateChanged;
        await Multiplexer.DisposeAsync().ConfigureAwait(false);
    }
}
