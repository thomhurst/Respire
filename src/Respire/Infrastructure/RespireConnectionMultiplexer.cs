using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Respire.Commands;
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
    private readonly SemaphoreSlim _correctionIdentityGate = new(1, 1);
    private readonly SemaphoreSlim _retiredFenceGate = new(1, 1);
    private readonly ConcurrentDictionary<long, byte> _retiredServerClientIds = new();
    private uint _next;
    private int _disposed;
    private int _trackServerClientIds;
    private volatile bool _correctionOrderingReady;
    private volatile bool _connected;

    public string Host { get; }
    public int Port { get; }
    public int ConnectionCount => _connections.Length;
    internal bool HasReliableCorrectionOrdering => _correctionOrderingReady;

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
    /// Enables server-side identities for every multiplexed connection. A correction can then
    /// fence a socket that died locally by issuing CLIENT KILL for its Redis client ID: once
    /// that reply arrives, an earlier command on the dead socket either already ran or was
    /// discarded, so a following correction cannot be overtaken by latent bytes.
    /// </summary>
    internal async ValueTask EnsureReliableCorrectionOrderingAsync(CancellationToken cancellationToken = default)
    {
        if (_correctionOrderingReady)
        {
            return;
        }

        await _correctionIdentityGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_correctionOrderingReady)
            {
                return;
            }

            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _trackServerClientIds, 1);

            while (true)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                var ready = true;
                for (var slot = 0; slot < _connections.Length; slot++)
                {
                    var connection = Volatile.Read(ref _connections[slot]);
                    if (connection is not { IsConnected: true })
                    {
                        RetireConnection(connection);
                        ScheduleReconnect(slot);
                        ready = false;
                        continue;
                    }

                    try
                    {
                        await connection.EnsureServerClientIdAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (IsConnectionLoss(ex) && Volatile.Read(ref _disposed) == 0)
                    {
                        RetireConnection(connection);
                        ScheduleReconnect(slot);
                        ready = false;
                    }
                }

                if (ready)
                {
                    var connection = GetConnection();
                    try
                    {
                        await ValidateClientKillPermissionAsync(connection, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (IsConnectionLoss(ex) && Volatile.Read(ref _disposed) == 0)
                    {
                        RetireConnection(connection);
                        var slot = FindSlot(connection);
                        if (slot >= 0)
                        {
                            ScheduleReconnect(slot);
                        }

                        await Task.Delay(25, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    _correctionOrderingReady = true;
                    return;
                }

                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _correctionIdentityGate.Release();
        }
    }

    private static async ValueTask ValidateClientKillPermissionAsync(
        RespireConnection connection,
        CancellationToken cancellationToken)
    {
        // Target this connection's valid ID but explicitly exclude the caller. Redis performs
        // CLIENT KILL ACL validation, then returns 0 without disconnecting anything.
        var reply = await connection.SendAsync(
            new ClientKillIdCommand(connection.ServerClientId, skipMe: true), cancellationToken).ConfigureAwait(false);
        if (reply.IsError)
        {
            var error = new RespireServerException(reply.GetErrorMessage());
            reply.Dispose();
            throw error;
        }

        reply.Dispose();
    }

    /// <summary>
    /// Sends a command on every connection and awaits all replies. Each
    /// connection is FIFO, so the copy sharing a connection with any earlier still-buffered
    /// command is guaranteed to execute after it — the ordering primitive a corrective command
    /// needs when the connection that carried the original is unknowable (round-robin). The
    /// command must therefore be idempotent and safe to run out of order on the other
    /// connections. A locally dead connection is first killed by its Redis client ID; that
    /// server-side barrier proves its flushed commands cannot execute after the correction.
    /// A slot dying during the broadcast is fenced and the broadcast retried.
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

        await EnsureReliableCorrectionOrderingAsync(cancellationToken).ConfigureAwait(false);

        while (true)
        {
            await FenceRetiredConnectionsAsync(cancellationToken).ConfigureAwait(false);

            var sends = new List<(RespireConnection Connection, ValueTask<RespValue> Send)>(_connections.Length);
            for (var slot = 0; slot < _connections.Length; slot++)
            {
                var connection = Volatile.Read(ref _connections[slot]);
                if (connection is not { IsConnected: true })
                {
                    RetireConnection(connection);
                    ScheduleReconnect(slot);
                    continue;
                }

                try
                {
                    sends.Add((connection, connection.SendAsync(in command, cancellationToken)));
                }
                catch (Exception ex) when (IsConnectionLoss(ex))
                {
                    RetireConnection(connection);
                    ScheduleReconnect(slot);
                }
            }

            // Each reply is drained by its own task, not awaited in sequence: a slot that never
            // replies must not stop the completed replies of later slots from being consumed
            // and disposed, since a caller that detaches this broadcast would otherwise retain
            // every undrained reply for as long as the stuck slot lives.
            var drains = new Task<Exception?>[sends.Count];
            for (var i = 0; i < sends.Count; i++)
            {
                drains[i] = DrainAsync(sends[i].Connection, sends[i].Send);
            }

            var retry = sends.Count == 0;
            Exception? fatal = null;
            for (var i = 0; i < drains.Length; i++)
            {
                if (await drains[i].ConfigureAwait(false) is { } ex)
                {
                    if (IsConnectionLoss(ex))
                    {
                        retry = true;
                    }
                    else
                    {
                        fatal ??= ex;
                    }
                }
            }

            if (!retry && _retiredServerClientIds.IsEmpty)
            {
                if (fatal is not null)
                {
                    ExceptionDispatchInfo.Capture(fatal).Throw();
                }

                return;
            }

            // Any failed copy may have left bytes executable on Redis. CLIENT KILL is the
            // ordering barrier. Establish it before surfacing an unrelated fatal reply; the
            // dead slot may be the one that carried the original command.
            await FenceRetiredConnectionsAsync(cancellationToken).ConfigureAwait(false);
            if (fatal is not null)
            {
                ExceptionDispatchInfo.Capture(fatal).Throw();
            }
        }

        async Task<Exception?> DrainAsync(RespireConnection connection, ValueTask<RespValue> send)
        {
            try
            {
                (await send.ConfigureAwait(false)).Dispose();
                return null;
            }
            catch (Exception ex)
            {
                if (IsConnectionLoss(ex))
                {
                    // Publish the dead ID as soon as this individual reply faults. Aggregation
                    // may still be waiting on another slot, but a caller abandoning that wait
                    // must already be able to fence every failure observed so far.
                    RetireConnection(connection);
                }

                return ex;
            }
        }
    }

    internal async ValueTask FenceRetiredConnectionsAsync(CancellationToken cancellationToken = default)
    {
        await _retiredFenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Catch slots that died after the broadcast started but before their reply task
            // faulted. A caller joining this safety barrier must not return merely because the
            // asynchronous drain has not published the dead ID yet.
            for (var slot = 0; slot < _connections.Length; slot++)
            {
                var connection = Volatile.Read(ref _connections[slot]);
                if (connection is not { IsConnected: true })
                {
                    RetireConnection(connection);
                    ScheduleReconnect(slot);
                }
            }

            while (!_retiredServerClientIds.IsEmpty)
            {
                foreach (var clientId in _retiredServerClientIds.Keys)
                {
                    var connection = await GetHealthyConnectionAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var reply = await connection.SendAsync(
                            new ClientKillIdCommand(clientId), cancellationToken).ConfigureAwait(false);
                        if (reply.IsError)
                        {
                            var error = new RespireServerException(reply.GetErrorMessage());
                            reply.Dispose();
                            throw error;
                        }

                        reply.Dispose();
                        _retiredServerClientIds.TryRemove(clientId, out _);
                    }
                    catch (Exception ex) when (IsConnectionLoss(ex))
                    {
                        RetireConnection(connection);
                    }
                }
            }
        }
        finally
        {
            _retiredFenceGate.Release();
        }
    }

    internal async ValueTask RetireConnectionAsync(long serverClientId)
    {
        for (var slot = 0; slot < _connections.Length; slot++)
        {
            var connection = Volatile.Read(ref _connections[slot]);
            if (connection?.ServerClientId != serverClientId)
            {
                continue;
            }

            await connection.DisposeAsync().ConfigureAwait(false);
            if (ReferenceEquals(connection, Volatile.Read(ref _connections[slot])))
            {
                ScheduleReconnect(slot);
            }

            return;
        }
    }

    private async ValueTask<RespireConnection> GetHealthyConnectionAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                return GetConnection();
            }
            catch (RespireConnectionException)
            {
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void RetireConnection(RespireConnection? connection)
    {
        var clientId = connection?.ServerClientId ?? 0;
        if (clientId > 0)
        {
            _retiredServerClientIds.TryAdd(clientId, 0);
        }
    }

    private static bool IsConnectionLoss(Exception exception)
        => exception is RespireConnectionException or ObjectDisposedException;

    private int FindSlot(RespireConnection connection)
    {
        for (var slot = 0; slot < _connections.Length; slot++)
        {
            if (ReferenceEquals(connection, Volatile.Read(ref _connections[slot])))
            {
                return slot;
            }
        }

        return -1;
    }

    /// <summary>Runs a MULTI/EXEC block on one connection; see <see cref="RespireConnection.SendTransactionAsync"/>.</summary>
    public ValueTask<RespValue> SendTransactionAsync(
        ReadOnlyMemory<byte> serializedCommands, int commandCount, CancellationToken cancellationToken = default)
        => GetConnection().SendTransactionAsync(serializedCommands, commandCount, cancellationToken);

    private void ScheduleReconnect(int slot)
    {
        RetireConnection(Volatile.Read(ref _connections[slot]));
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
            if (Volatile.Read(ref _trackServerClientIds) != 0)
            {
                await replacement.EnsureServerClientIdAsync().ConfigureAwait(false);
            }

            if (Volatile.Read(ref _disposed) != 0)
            {
                await replacement.DisposeAsync().ConfigureAwait(false);
                return;
            }

            var old = Interlocked.Exchange(ref _connections[slot], replacement);
            RetireConnection(old);
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
