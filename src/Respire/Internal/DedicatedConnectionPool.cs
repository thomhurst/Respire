using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Respire.Networking;

namespace Respire.Internal;

/// <summary>
/// A small pool of dedicated (non-multiplexed) connections for commands that occupy a
/// connection for their whole duration: BLPOP-style blocking waits and blocking stream reads.
/// Multiplexed connections must never run these — one blocking command would stall every
/// pipelined command behind it — so they rent from here instead. Connections are created on
/// demand and a few idle ones are kept for reuse. Rented connections are tracked so client
/// disposal can abort a command blocked server-side (even a BLPOP with an infinite wait).
/// </summary>
internal sealed class DedicatedConnectionPool(
    string host, int port, RespireConnectionOptions options, ILogger? logger) : IAsyncDisposable
{
    private const int MaxIdle = 4;

    private readonly LockFreeStack<RespireConnection> _idle = new(MaxIdle);
    private readonly ConcurrentDictionary<RespireConnection, byte> _rented = new();
    private volatile bool _disposed;

    public async ValueTask<RespireConnection> RentAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        while (_idle.TryPop(out var pooled))
        {
            if (pooled.IsConnected)
            {
                return Track(pooled);
            }

            await pooled.DisposeAsync().ConfigureAwait(false);
        }

        var connection = await RespireConnection.ConnectAsync(host, port, options, logger, cancellationToken).ConfigureAwait(false);
        return Track(connection);
    }

    private RespireConnection Track(RespireConnection connection)
    {
        _rented[connection] = 0;

        // Disposal may have swept _rented between the entry check and this registration; if the
        // flag is set now, this connection is ours to clean up.
        if (_disposed && _rented.TryRemove(connection, out _))
        {
            _ = connection.DisposeAsync().AsTask();
            throw new ObjectDisposedException(nameof(DedicatedConnectionPool));
        }

        return connection;
    }

    /// <summary>Returns a healthy connection for reuse; anything else (or overflow) is disposed.</summary>
    public void Return(RespireConnection connection)
    {
        _rented.TryRemove(connection, out _);
        if (_disposed || !connection.IsConnected || !_idle.TryPush(connection))
        {
            _ = connection.DisposeAsync().AsTask();
            return;
        }

        // Disposal may have drained the idle stack just before the push above landed.
        if (_disposed)
        {
            DrainIdle();
        }
    }

    /// <summary>Removes a connection that must not be reused (failed or abandoned mid-block).</summary>
    public ValueTask DiscardAsync(RespireConnection connection)
    {
        _rented.TryRemove(connection, out _);
        return connection.DisposeAsync();
    }

    private void DrainIdle()
    {
        while (_idle.TryPop(out var connection))
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

        // Abort connections mid-blocking-command: closing the socket wakes the receive loop and
        // fails the in-flight wait, so a BLPOP with an infinite wait cannot outlive the client.
        foreach (var rented in _rented.Keys)
        {
            if (_rented.TryRemove(rented, out _))
            {
                await rented.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
