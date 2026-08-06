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
    private SubscriptionHub? _hub;

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
        DedicatedPool = new DedicatedConnectionPool(
            endpoint.Host, endpoint.Port, options.ToConnectionOptions(), Logger);
    }

    public SubscriptionHub Hub
    {
        get
        {
            if (_hub is { } hub)
            {
                return hub;
            }

            lock (_hubGate)
            {
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
        if (_hub is not null)
        {
            await _hub.DisposeAsync().ConfigureAwait(false);
        }

        await DedicatedPool.DisposeAsync().ConfigureAwait(false);
        await Multiplexer.DisposeAsync().ConfigureAwait(false);
    }
}
