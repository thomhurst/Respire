using Microsoft.Extensions.Logging;
using Respire.Networking;

namespace Respire.Internal;

/// <summary>
/// A small pool of dedicated (non-multiplexed) connections for commands that occupy a
/// connection for their whole duration: BLPOP-style blocking waits and blocking stream reads.
/// Multiplexed connections must never run these — one blocking command would stall every
/// pipelined command behind it — so they rent from here instead. Connections are created on
/// demand and a few idle ones are kept for reuse.
/// </summary>
internal sealed class DedicatedConnectionPool(
    string host, int port, RespireConnectionOptions options, ILogger? logger) : IAsyncDisposable
{
    private const int MaxIdle = 4;

    private readonly LockFreeStack<RespireConnection> _idle = new(MaxIdle);
    private volatile bool _disposed;

    public async ValueTask<RespireConnection> RentAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        while (_idle.TryPop(out var connection))
        {
            if (connection.IsConnected)
            {
                return connection;
            }

            await connection.DisposeAsync().ConfigureAwait(false);
        }

        return await RespireConnection.ConnectAsync(host, port, options, logger, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns a healthy connection for reuse; anything else (or overflow) is disposed.</summary>
    public void Return(RespireConnection connection)
    {
        if (_disposed || !connection.IsConnected || !_idle.TryPush(connection))
        {
            _ = connection.DisposeAsync().AsTask();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        while (_idle.TryPop(out var connection))
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
