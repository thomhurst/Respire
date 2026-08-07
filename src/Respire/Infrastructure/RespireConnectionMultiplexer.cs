using Microsoft.Extensions.Logging;
using Respire.Networking;
using Respire.Protocol;

namespace Respire.Infrastructure;

/// <summary>
/// Round-robins commands across a fixed set of fully multiplexed <see cref="RespireConnection"/>s.
/// Every connection pipelines concurrent commands, so there is no per-command checkout — a dead
/// connection is skipped and replaced in the background. Supports lazy start: create unconnected,
/// then <see cref="EnsureConnectedAsync"/> before first use (idempotent, thread-safe).
/// </summary>
public sealed class RespireConnectionMultiplexer : IAsyncDisposable
{
    private readonly RespireConnection?[] _connections;
    private readonly int[] _reconnecting;
    private readonly RespireConnectionOptions _options;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private uint _next;
    private int _disposed;
    private volatile bool _connected;

    public string Host { get; }
    public int Port { get; }
    public int ConnectionCount => _connections.Length;

    /// <summary>The options every connection (and any subscriber) is built from.</summary>
    public RespireConnectionOptions Options => _options;

    /// <summary>Raised when a dead connection is noticed and when its replacement lands.</summary>
    public event Action<RespireConnectionState>? StateChanged;

    public bool IsConnected
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0 || !_connected)
            {
                return false;
            }

            foreach (var connection in _connections)
            {
                if (connection is { IsConnected: true })
                {
                    return true;
                }
            }

            return false;
        }
    }

    private RespireConnectionMultiplexer(string host, int port, int connectionCount, RespireConnectionOptions options, ILogger? logger)
    {
        Host = host;
        Port = port;
        _options = options;
        _logger = logger;
        _connections = new RespireConnection?[connectionCount];
        _reconnecting = new int[connectionCount];
    }

    /// <summary>Creates an unconnected multiplexer; call <see cref="EnsureConnectedAsync"/> before use.</summary>
    public static RespireConnectionMultiplexer Create(
        string host, int port = 6379, int connectionCount = 0, RespireConnectionOptions? options = null, ILogger? logger = null)
    {
        if (connectionCount <= 0)
        {
            // One connection maximizes pipelining: concurrent commands coalesce into deep
            // batches per syscall. Spreading load across sockets divides the batch depth —
            // measured under 50-worker stress, every added connection lowered throughput
            // (small commands worst: 1 connection doubled PING ops/s over 8).
            connectionCount = 1;
        }

        return new RespireConnectionMultiplexer(host, port, connectionCount, options ?? RespireConnectionOptions.Default, logger);
    }

    public static async Task<RespireConnectionMultiplexer> CreateAsync(
        string host,
        int port = 6379,
        int connectionCount = 0,
        RespireConnectionOptions? options = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var multiplexer = Create(host, port, connectionCount, options, logger);
        try
        {
            await multiplexer.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await multiplexer.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return multiplexer;
    }

    /// <summary>Opens all connections on first call; later calls return immediately.</summary>
    public async ValueTask EnsureConnectedAsync(CancellationToken cancellationToken = default)
    {
        if (_connected)
        {
            return;
        }

        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_connected)
            {
                return;
            }

            var connectTasks = new Task<RespireConnection>[_connections.Length];
            for (var i = 0; i < connectTasks.Length; i++)
            {
                connectTasks[i] = RespireConnection.ConnectAsync(Host, Port, _options, _logger, cancellationToken);
            }

            try
            {
                var connections = await Task.WhenAll(connectTasks).ConfigureAwait(false);
                for (var i = 0; i < connections.Length; i++)
                {
                    Volatile.Write(ref _connections[i], connections[i]);
                }

                _connected = true;
            }
            catch
            {
                foreach (var task in connectTasks)
                {
                    if (task.IsCompletedSuccessfully)
                    {
                        await task.Result.DisposeAsync().ConfigureAwait(false);
                    }
                }

                throw;
            }
        }
        finally
        {
            _connectGate.Release();
        }
    }

    /// <summary>Returns the next healthy connection, scheduling replacement of any dead ones seen.</summary>
    public RespireConnection GetConnection()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_connected)
        {
            throw new RespireConnectionException(
                $"Not connected to {Host}:{Port} — call {nameof(EnsureConnectedAsync)} first.");
        }

        var count = _connections.Length;
        var startIndex = Interlocked.Increment(ref _next);
        for (var i = 0; i < count; i++)
        {
            var slot = (int)((startIndex + (uint)i) % (uint)count);
            var connection = Volatile.Read(ref _connections[slot]);
            if (connection is { IsConnected: true })
            {
                return connection;
            }

            ScheduleReconnect(slot);
        }

        throw new RespireConnectionException($"No healthy connections to {Host}:{Port}.");
    }

    public ValueTask<RespValue> SendAsync<TCommand>(in TCommand command, CancellationToken cancellationToken = default)
        where TCommand : struct, IRespCommand
        => GetConnection().SendAsync(in command, cancellationToken);

    public ValueTask SendFireAndForgetAsync<TCommand>(in TCommand command, CancellationToken cancellationToken = default)
        where TCommand : struct, IRespCommand
        => GetConnection().SendFireAndForgetAsync(in command, cancellationToken);

    /// <summary>
    /// Sends a command on every currently healthy connection and awaits all replies. Each
    /// connection is FIFO, so the copy sharing a connection with any earlier still-buffered
    /// command is guaranteed to execute after it — the ordering primitive a corrective command
    /// needs when the connection that carried the original is unknowable (round-robin). The
    /// command must therefore be idempotent and safe to run out of order on the other
    /// connections. Dead connections are skipped: anything buffered on them died with the
    /// socket. Throws only when every send failed; partial failure is success, because a
    /// connection that died also killed whatever the correction was ordering against.
    /// </summary>
    internal async ValueTask SendToAllConnectionsAsync<TCommand>(TCommand command, CancellationToken cancellationToken = default)
        where TCommand : struct, IRespCommand
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_connected)
        {
            // Never connected: nothing can be buffered anywhere, so there is nothing to order
            // against and nothing to correct.
            return;
        }

        List<ValueTask<RespValue>>? sends = null;
        for (var slot = 0; slot < _connections.Length; slot++)
        {
            if (Volatile.Read(ref _connections[slot]) is { IsConnected: true } connection)
            {
                (sends ??= new List<ValueTask<RespValue>>(_connections.Length))
                    .Add(connection.SendAsync(in command, cancellationToken));
            }
        }

        if (sends is null)
        {
            return;
        }

        Exception? firstFailure = null;
        var failures = 0;
        foreach (var send in sends)
        {
            try
            {
                (await send.ConfigureAwait(false)).Dispose();
            }
            catch (Exception ex)
            {
                failures++;
                firstFailure ??= ex;
            }
        }

        if (failures == sends.Count && firstFailure is not null)
        {
            throw firstFailure;
        }
    }

    /// <summary>Runs a MULTI/EXEC block on one connection; see <see cref="RespireConnection.SendTransactionAsync"/>.</summary>
    public ValueTask<RespValue> SendTransactionAsync(
        ReadOnlyMemory<byte> serializedCommands, int commandCount, CancellationToken cancellationToken = default)
        => GetConnection().SendTransactionAsync(serializedCommands, commandCount, cancellationToken);

    private void ScheduleReconnect(int slot)
    {
        if (Interlocked.CompareExchange(ref _reconnecting[slot], 1, 0) != 0)
        {
            return;
        }

        NotifyStateChanged(RespireConnectionState.Reconnecting);
        _ = ReconnectAsync(slot);
    }

    private async Task ReconnectAsync(int slot)
    {
        try
        {
            var replacement = await RespireConnection.ConnectAsync(Host, Port, _options, _logger).ConfigureAwait(false);
            if (Volatile.Read(ref _disposed) != 0)
            {
                await replacement.DisposeAsync().ConfigureAwait(false);
                return;
            }

            var old = Interlocked.Exchange(ref _connections[slot], replacement);
            _logger?.LogInformation("Replaced dead connection {Slot} to {Host}:{Port}", slot, Host, Port);
            if (old is not null)
            {
                await old.DisposeAsync().ConfigureAwait(false);
            }

            // Disposal may have swept the array between the pre-check above and the exchange,
            // missing the just-published replacement. DisposeAsync sets _disposed before it
            // sweeps, so if the flag is still clear here the sweep is guaranteed to see the
            // replacement; otherwise take it back out and dispose it ourselves (double
            // dispose is idempotent).
            if (Volatile.Read(ref _disposed) != 0)
            {
                Interlocked.Exchange(ref _connections[slot], old);
                await replacement.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                NotifyStateChanged(RespireConnectionState.Connected);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Reconnect to {Host}:{Port} failed; will retry on next use", Host, Port);
        }
        finally
        {
            Volatile.Write(ref _reconnecting[slot], 0);
        }
    }

    private void NotifyStateChanged(RespireConnectionState state)
    {
        try
        {
            StateChanged?.Invoke(state);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Connection state-change handler threw");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Wait out any in-flight EnsureConnectedAsync so connections it publishes are swept
        // here instead of leaked, and so the gate is never disposed while held.
        await _connectGate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var connection in _connections)
            {
                if (connection is not null)
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _connectGate.Release();
            _connectGate.Dispose();
        }
    }
}
