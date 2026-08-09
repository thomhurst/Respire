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
    }

    public ValueTask EnsureConnectedAsync(CancellationToken cancellationToken)
        => Cluster is { } cluster
            ? cluster.EnsureConnectedAsync(cancellationToken)
            : Multiplexer.EnsureConnectedAsync(cancellationToken);

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
            await cluster.DisposeAsync().ConfigureAwait(false);
        }

        await Multiplexer.DisposeAsync().ConfigureAwait(false);
    }
}
