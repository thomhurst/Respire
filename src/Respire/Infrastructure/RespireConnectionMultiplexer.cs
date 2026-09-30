using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;

namespace Respire.Infrastructure;

/// <summary>
/// Round-robins commands across a fixed set of fully multiplexed <see cref="RespireConnection"/>s.
/// Every connection pipelines concurrent commands, so there is no per-command checkout — a dead
/// connection is skipped and replaced in the background. Supports lazy start: create unconnected,
/// then <see cref="EnsureConnectedAsync"/> before first use (idempotent, thread-safe).
/// </summary>
internal sealed class RespireConnectionMultiplexer : IAsyncDisposable
{
    private readonly RespireConnection?[] _connections;
    private readonly int _connectionMask;
    private readonly int[] _reconnecting;
    private readonly RespireConnectionOptions _options;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly SemaphoreSlim _correctionIdentityGate = new(1, 1);
    private readonly SemaphoreSlim _retiredFenceGate = new(1, 1);
    private readonly ConcurrentDictionary<long, byte> _retiredServerClientIds = new();
    private readonly object _stateNotificationGate = new();
    private readonly Queue<StateNotification> _stateNotifications = [];
    private uint _next;
    private int _disposed;
    private int _retired;
    // Cold lifecycle transitions share this gate; normal selection reads only volatile state.
    // A reconnect reserves ownership before starting so shutdown also awaits unpublished work.
    private readonly object _lifecycleGate = new();
    private readonly CancellationTokenSource _stopConnecting = new();
    private readonly CancellationTokenSource _abortCancellation = new();
    private int _activeReconnects;
    private TaskCompletionSource? _reconnectsDrained;
    private TaskCompletionSource? _retirementCompletion;
    private TaskCompletionSource? _disposeCompletion;
    private int _trackServerClientIds;
    private string? _correctionOrderingFailure;
    private volatile bool _correctionOrderingReady;
    private volatile bool _connected;
    private bool _publishingStateNotifications;

    public string Host { get; }
    public int Port { get; }
    public int ConnectionCount => _connections.Length;
    internal bool IsRetired => Volatile.Read(ref _retired) != 0;
    internal bool HasPendingCorrectionFences => !_retiredServerClientIds.IsEmpty;
    internal bool IsInitialized => _connected;
    internal bool HasReliableCorrectionOrdering => _correctionOrderingReady;
    internal bool IsReliableCorrectionOrderingUnavailable =>
        Volatile.Read(ref _correctionOrderingFailure) is not null;

    /// <summary>The options every connection (and any subscriber) is built from.</summary>
    public RespireConnectionOptions Options => _options;

    /// <summary>
    /// Raised when any client-owned connection begins reconnecting, reconnects, or disconnects
    /// because of failure or disposal.
    /// </summary>
    public event Action<RespireConnectionStateChange>? StateChanged;
    internal event Action<int, RespireConnectionStateChange>? SlotStateChanged;

    internal bool IsReconnecting
    {
        get
        {
            for (var i = 0; i < _reconnecting.Length; i++)
            {
                if (Volatile.Read(ref _reconnecting[i]) != 0) return true;
            }
            return false;
        }
    }

    internal bool? GetReconnectState(RespireConnection connection)
    {
        var slot = FindSlot(connection);
        if (slot < 0) return null;
        var reconnecting = Volatile.Read(ref _reconnecting[slot]) != 0;
        // The slot may have been replaced while its reconnect flag was sampled.
        return ReferenceEquals(connection, Volatile.Read(ref _connections[slot])) ? reconnecting : null;
    }

    internal RespireTimeoutDiagnostics CaptureConnectionWait()
        => RespireTimeoutDiagnostics.Capture(RespireCommandStage.Connecting,
            new RespireEndpoint(Host, Port), isConnected: IsConnected, isReconnecting: IsReconnecting);

    public bool IsConnected
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0 || IsRetired || !_connected)
            {
                return false;
            }

            foreach (var connection in _connections)
            {
                if (connection is { IsAcceptingCommands: true })
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
        _connectionMask = BitOperations.IsPow2((uint)connectionCount) ? connectionCount - 1 : -1;
        _reconnecting = new int[connectionCount];
    }

    /// <summary>Creates an unconnected multiplexer; call <see cref="EnsureConnectedAsync"/> before use.</summary>
    public static RespireConnectionMultiplexer Create(
        string host, int port = 6379, int connectionCount = 1, RespireConnectionOptions? options = null, ILogger? logger = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(connectionCount);

        return new RespireConnectionMultiplexer(host, port, connectionCount, options ?? RespireConnectionOptions.Default, logger);
    }

    public static async Task<RespireConnectionMultiplexer> CreateAsync(
        string host,
        int port = 6379,
        int connectionCount = 1,
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
        ThrowIfUnavailable();
        if (_connected)
        {
            return;
        }

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopConnecting.Token);
        try
        {
            await InitializeConnectionsAsync(lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (CommandTimeoutCancellation.IsFromLinkedToken(
            error, cancellationToken, lifetime.Token))
        {
            // Retirement cancellation keeps its own identity; caller/deadline cancellation crosses our link.
            throw new OperationCanceledException(error.Message, error, cancellationToken);
        }
    }

    private async ValueTask InitializeConnectionsAsync(CancellationToken cancellationToken)
    {
        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
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
                lock (_lifecycleGate)
                {
                    ThrowIfUnavailable();
                    for (var i = 0; i < connections.Length; i++)
                    {
                        connections[i].Multiplexer = this;
                        Volatile.Write(ref _connections[i], connections[i]);
                    }
                    _connected = true;
                }
                for (var i = 0; i < connections.Length; i++)
                    ObserveConnectionFailure(i, connections[i]);
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
        => _connections.Length == 1
            ? GetSingleConnection()
            : GetConnection(Interlocked.Increment(ref _next));

    /// <summary>
    /// Returns a stable healthy connection for an affinity value, probing replacements in a
    /// deterministic order when its preferred connection is unavailable.
    /// </summary>
    internal RespireConnection GetConnection(int affinity) => GetConnection(unchecked((uint)affinity));

    private RespireConnection GetConnection(uint startIndex)
    {
        ThrowIfUnavailable();
        if (!_connected)
        {
            throw new RespireConnectionException(
                $"Not connected to {Host}:{Port} — call {nameof(EnsureConnectedAsync)} first.");
        }

        var count = _connections.Length;
        for (var i = 0; i < count; i++)
        {
            var offset = startIndex + (uint)i;
            var slot = _connectionMask >= 0
                ? (int)(offset & (uint)_connectionMask)
                : (int)(offset % (uint)count);
            var connection = Volatile.Read(ref _connections[slot]);
            if (connection is { IsAcceptingCommands: true })
            {
                return connection;
            }

            ScheduleReconnect(slot);
        }

        ThrowIfUnavailable();
        throw new RespireConnectionException($"No healthy connections to {Host}:{Port}.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private RespireConnection GetSingleConnection()
    {
        ThrowIfUnavailable();
        if (!_connected)
        {
            throw new RespireConnectionException(
                $"Not connected to {Host}:{Port} — call {nameof(EnsureConnectedAsync)} first.");
        }

        var connection = Volatile.Read(ref _connections[0]);
        if (connection is { IsAcceptingCommands: true })
        {
            return connection;
        }

        ScheduleReconnect(0);
        ThrowIfUnavailable();
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
        ThrowIfUnavailable();
        if (_correctionOrderingReady)
        {
            return;
        }

        ThrowIfCorrectionOrderingUnavailable();

        await _correctionIdentityGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            if (_correctionOrderingReady)
            {
                return;
            }

            ThrowIfCorrectionOrderingUnavailable();

            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _trackServerClientIds, 1);

            while (true)
            {
                ThrowIfUnavailable();
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
        catch (RespireServerException ex) when (IsDefinitiveCorrectionOrderingFailure(ex))
        {
            Volatile.Write(ref _trackServerClientIds, 0);
            Volatile.Write(ref _correctionOrderingFailure, ex.Message);
            throw;
        }
        finally
        {
            if (!_correctionOrderingReady)
            {
                // A failed bootstrap must not make reconnect publication depend on a CLIENT ID
                // permission the client may not have.
                Volatile.Write(ref _trackServerClientIds, 0);
            }

            _correctionIdentityGate.Release();
        }
    }

    internal static bool IsDefinitiveCorrectionOrderingFailure(RespireServerException exception)
        => exception.Code == RespireErrorCodes.NoPerm ||
           exception.Code == RespireErrorCodes.Err &&
           (exception.Message.Contains("unknown command", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("unknown subcommand", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("wrong number of arguments", StringComparison.OrdinalIgnoreCase));

    private void ThrowIfCorrectionOrderingUnavailable()
    {
        if (Volatile.Read(ref _correctionOrderingFailure) is { } failure)
        {
            throw new RespireServerException(failure, "CLIENT KILL");
        }
    }

    private static async ValueTask ValidateClientKillPermissionAsync(
        RespireConnection connection,
        CancellationToken cancellationToken)
    {
        // Target this connection's valid ID but explicitly exclude the caller. Redis performs
        // CLIENT KILL ACL validation, then returns 0 without disconnecting anything. Identity
        // setup is bounded by its callers' own timeout tokens, not the per-command deadline.
        var reply = await connection.SendAsync(
                new ClientKillIdCommand(connection.ServerClientId, skipMe: true), cancellationToken,
                armCommandDeadline: false)
            .ConfigureAwait(false);
        if (reply.IsError)
        {
            var error = new RespireServerException(reply.GetErrorMessage(), "CLIENT KILL");
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
    /// When <paramref name="sendAsking"/> is true, each copy is atomically prefixed with ASKING.
    /// A slot dying during the broadcast is fenced and the broadcast retried.
    /// </summary>
    internal async ValueTask SendToAllConnectionsAsync<TCommand>(
        TCommand command,
        bool sendAsking = false,
        CancellationToken cancellationToken = default)
        where TCommand : struct, IRespCommand
    {
        ThrowIfUnavailable();
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
                    // Corrections, once owed, must not be abandonable: no command deadline.
                    var send = sendAsking
                        ? Respire.Internal.ClusterRouter.SendAskingUncheckedAsync(
                            connection, in command, cancellationToken, armCommandDeadline: false)
                        : connection.SendAsync(in command, cancellationToken, armCommandDeadline: false);
                    sends.Add((connection, send));
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
                var reply = await send.ConfigureAwait(false);
                try
                {
                    return reply.IsError
                        ? new RespireServerException(reply.GetErrorMessage())
                        : null;
                }
                finally
                {
                    reply.Dispose();
                }
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
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _retiredFenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
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
                    if (IsRetired)
                    {
                        await FenceUsingControlConnectionAsync(cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    var connection = await GetHealthyConnectionAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        // The kill is an owed ordering barrier; it must not be abandonable,
                        // so no command deadline applies.
                        var reply = await connection.SendAsync(
                                new ClientKillIdCommand(clientId), cancellationToken,
                                armCommandDeadline: false)
                            .ConfigureAwait(false);
                        if (reply.IsError)
                        {
                            var error = new RespireServerException(reply.GetErrorMessage(), "CLIENT KILL");
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
        catch (RespireConnectionRetiredException) when (IsRetired)
        {
            await FenceUsingControlConnectionAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _retiredFenceGate.Release();
        }
    }

    // Called under the fence gate. These idempotent safety commands are the only sends
    // permitted after retirement; the control connection is never published or reconnected.
    private async Task FenceUsingControlConnectionAsync(CancellationToken cancellationToken)
    {
        if (_retiredServerClientIds.IsEmpty) return;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _abortCancellation.Token);
        await using var connection = await RespireConnection.ConnectAsync(Host, Port,
            _options with { EnableClientTracking = false, PushHandler = null, SubscriptionConfirmationHandler = null },
            _logger, lifetime.Token).ConfigureAwait(false);
        foreach (var clientId in _retiredServerClientIds.Keys)
        {
            using var reply = await connection.SendAsync(new ClientKillIdCommand(clientId), lifetime.Token,
                armCommandDeadline: false).ConfigureAwait(false);
            if (reply.IsError) throw new RespireServerException(reply.GetErrorMessage(), "CLIENT KILL");
            _retiredServerClientIds.TryRemove(clientId, out _);
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

    internal async ValueTask<RespireConnection> GetHealthyConnectionAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                return GetConnection();
            }
            catch (RespireConnectionException)
            {
                ThrowIfUnavailable();
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void RetireConnection(RespireConnection? connection)
    {
        // An identity already obtained remains an obligation even if interrupted bootstrap
        // clears the flag that requests identities on future replacement connections.
        if (connection is { ServerClientId: > 0, DrainedSuccessfully: false })
        {
            _retiredServerClientIds.TryAdd(connection.ServerClientId, 0);
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
        var connection = Volatile.Read(ref _connections[slot]);
        // An individually draining connection must finish before replacement can dispose it.
        if (connection is { IsConnected: true, IsAcceptingCommands: false }) return;
        var error = connection?.CloseError;
        RetireConnection(connection);
        bool publish;
        lock (_lifecycleGate)
        {
            if (IsRetired || Volatile.Read(ref _disposed) != 0
                || Interlocked.CompareExchange(ref _reconnecting[slot], 1, 0) != 0)
                return;
            _activeReconnects++;
            publish = QueueLifecycleNotificationUnderLock(new StateNotification(slot, RespireConnectionState.Reconnecting, error));
        }
        if (publish) DrainStateNotifications();
        _ = ReconnectAsync(slot);
    }

    private async Task ReconnectAsync(int slot)
    {
        RespireConnection? replacement = null;
        var reconnectGuardReleased = false;
        try
        {
            replacement = await RespireConnection.ConnectAsync(Host, Port, _options, _logger, _stopConnecting.Token)
                .ConfigureAwait(false);
            if (Volatile.Read(ref _trackServerClientIds) != 0)
                await replacement.EnsureServerClientIdAsync(_stopConnecting.Token).ConfigureAwait(false);

            RespireConnection? old;
            RespireConnection publishedReplacement;
            lock (_lifecycleGate)
            {
                ThrowIfUnavailable();
                replacement.Multiplexer = this;
                old = Interlocked.Exchange(ref _connections[slot], replacement);
                publishedReplacement = replacement;
                replacement = null;
            }
            ObserveConnectionFailure(slot, publishedReplacement);
            RetireConnection(old);
            _logger?.LogInformation("Replaced dead connection {Slot} to {Host}:{Port}", slot, Host, Port);
            if (old is not null) await old.DisposeAsync().ConfigureAwait(false);
            bool publish;
            lock (_lifecycleGate)
            {
                publish = !IsRetired && Volatile.Read(ref _disposed) == 0
                    && QueueLifecycleNotificationUnderLock(new StateNotification(slot, RespireConnectionState.Connected, null));
            }
            if (publish) DrainStateNotifications();
        }
        catch (Exception ex)
        {
            if (replacement is not null)
            {
                try { await replacement.DisposeAsync().ConfigureAwait(false); }
                catch (Exception disposeException)
                {
                    _logger?.LogWarning(disposeException, "Failed to dispose rejected replacement connection to {Host}:{Port}", Host, Port);
                }
            }
            if (!IsRetired && Volatile.Read(ref _disposed) == 0)
            {
                _logger?.LogWarning(ex, "Reconnect to {Host}:{Port} failed; will retry on next use", Host, Port);
                var publish = EnqueueReconnectFailure(slot, ex);
                reconnectGuardReleased = true;
                if (publish) DrainStateNotifications();
            }
        }
        finally
        {
            if (!reconnectGuardReleased) Volatile.Write(ref _reconnecting[slot], 0);
            lock (_lifecycleGate)
            {
                if (--_activeReconnects == 0) _reconnectsDrained?.TrySetResult();
            }
        }
    }

    private void ObserveConnectionFailure(int slot, RespireConnection connection)
    {
        if (_options.EnableClientTracking)
        {
            connection.PendingCommandsFailing += () => HandleConnectionFailure(slot, connection);
            if (!connection.IsConnected)
            {
                HandleConnectionFailure(slot, connection);
            }
        }
    }

    private void HandleConnectionFailure(int slot, RespireConnection connection)
    {
        if (!IsRetired && Volatile.Read(ref _disposed) == 0
            && ReferenceEquals(connection, Volatile.Read(ref _connections[slot])))
        {
            ScheduleReconnect(slot);
        }
    }

    internal void NotifyStateChanged(RespireConnectionState state, Exception? error = null)
        => EnqueueStateNotification(new StateNotification(null, state, error));

    private void NotifyStateChanged(
        int slot,
        RespireConnectionState state,
        Exception? error = null)
        => EnqueueStateNotification(new StateNotification(slot, state, error));

    private bool QueueLifecycleNotificationUnderLock(StateNotification notification)
    {
        lock (_stateNotificationGate) return EnqueueStateNotificationUnderLock(notification);
    }

    private void EnqueueStateNotification(StateNotification notification)
    {
        bool publish;
        lock (_stateNotificationGate)
        {
            publish = EnqueueStateNotificationUnderLock(notification);
        }

        if (publish)
        {
            DrainStateNotifications();
        }
    }

    private bool EnqueueReconnectFailure(int slot, Exception error)
    {
        lock (_stateNotificationGate)
        {
            var publish = EnqueueStateNotificationUnderLock(
                new StateNotification(slot, RespireConnectionState.Disconnected, error));

            // The failure is ordered before the guard opens. Concurrent or synchronous retries
            // can now enqueue Reconnecting, but only behind this Disconnected notification.
            Volatile.Write(ref _reconnecting[slot], 0);
            return publish;
        }
    }

    private bool EnqueueStateNotificationUnderLock(StateNotification notification)
    {
        _stateNotifications.Enqueue(notification);
        if (_publishingStateNotifications)
        {
            return false;
        }

        _publishingStateNotifications = true;
        return true;
    }

    private void DrainStateNotifications()
    {
        while (true)
        {
            StateNotification notification;
            lock (_stateNotificationGate)
            {
                if (_stateNotifications.Count == 0)
                {
                    _publishingStateNotifications = false;
                    return;
                }

                notification = _stateNotifications.Dequeue();
            }

            PublishStateNotification(notification);
        }
    }

    private void PublishStateNotification(StateNotification notification)
    {
        var change = new RespireConnectionStateChange(
            new RespireEndpoint(Host, Port), notification.State, notification.Error);
        try
        {
            StateChanged?.Invoke(change);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Connection state-change handler threw");
        }

        if (notification.Slot is { } slot)
        {
            try
            {
                SlotStateChanged?.Invoke(slot, change);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Connection slot state-change handler threw");
            }
        }
    }

    private readonly record struct StateNotification(
        int? Slot,
        RespireConnectionState State,
        Exception? Error);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (IsRetired) throw new RespireConnectionRetiredException(Host, Port);
    }

    /// <summary>Stops selection and reconnects, drains accepted work, and fences failed sockets.</summary>
    /// <remarks>A failed fence leaves its IDs retained and faults retirement. Owners must retain
    /// this generation and retry FenceRetiredConnectionsAsync before dropping correction ownership.</remarks>
    internal Task RetireAsync()
    {
        TaskCompletionSource completion;
        bool publish;
        lock (_lifecycleGate)
        {
            if (_retirementCompletion is not null) return _retirementCompletion.Task;
            completion = _retirementCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _retired, 1);
            publish = QueueLifecycleNotificationUnderLock(new StateNotification(null, RespireConnectionState.Disconnected, null));
        }
        _stopConnecting.Cancel();
        foreach (var connection in _connections) _ = connection?.RetireAsync();
        _ = RetireCoreAsync(completion);
        if (publish) DrainStateNotifications();
        return completion.Task;
    }

    /// <summary>Retires gracefully unless the owner explicitly cancels, then completes abortive cleanup.</summary>
    /// <remarks>Cancellation does not prove correction ordering. Pending fence IDs remain observable.</remarks>
    internal async Task RetireAsync(CancellationToken abortOnCancellation)
    {
        var retirement = RetireAsync();
        try
        {
            await retirement.WaitAsync(abortOnCancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (abortOnCancellation.IsCancellationRequested)
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task WaitForCorrectionIdentityAsync()
    {
        // A dequeued CLIENT ID can still be awaiting its continuation. No new bootstrap can
        // enter after retirement; wait for the existing owner to publish before collecting IDs.
        await _correctionIdentityGate.WaitAsync().ConfigureAwait(false);
        _correctionIdentityGate.Release();
    }

    private async Task WaitForPublicationAsync()
    {
        await _connectGate.WaitAsync().ConfigureAwait(false);
        _connectGate.Release();
        Task reconnects;
        lock (_lifecycleGate)
        {
            reconnects = _activeReconnects == 0 ? Task.CompletedTask
                : (_reconnectsDrained ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
        await reconnects.ConfigureAwait(false);
    }

    private async Task RetireCoreAsync(TaskCompletionSource completion)
    {
        try
        {
            await WaitForPublicationAsync().ConfigureAwait(false);
            await Task.WhenAll(_connections.OfType<RespireConnection>().Select(connection => connection.RetireAsync()))
                .ConfigureAwait(false);
            await WaitForCorrectionIdentityAsync().ConfigureAwait(false);
            foreach (var connection in _connections) RetireConnection(connection);
            if (Volatile.Read(ref _disposed) == 0)
                await FenceRetiredConnectionsAsync(_abortCancellation.Token).ConfigureAwait(false);
            else if (HasPendingCorrectionFences)
                throw new OperationCanceledException(
                    "Disposal prevented retirement from fencing failed Redis connections.", _abortCancellation.Token);
            completion.TrySetResult();
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        bool publish;
        lock (_lifecycleGate)
        {
            if (_disposeCompletion is not null) return new ValueTask(_disposeCompletion.Task);
            completion = _disposeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _disposed, 1);
            Volatile.Write(ref _retired, 1);
            publish = QueueLifecycleNotificationUnderLock(new StateNotification(null, RespireConnectionState.Disconnected, null));
        }
        _stopConnecting.Cancel();
        _abortCancellation.Cancel();
        foreach (var connection in _connections)
            if (connection is not null) _ = connection.DisposeAsync();
        _ = DisposeCoreAsync(completion);
        if (publish) DrainStateNotifications();
        return new ValueTask(completion.Task);
    }

    private async Task DisposeCoreAsync(TaskCompletionSource completion)
    {
        try
        {
            await WaitForPublicationAsync().ConfigureAwait(false);
            await Task.WhenAll(_connections.OfType<RespireConnection>().Select(connection => connection.DisposeAsync().AsTask()))
                .ConfigureAwait(false);
            await WaitForCorrectionIdentityAsync().ConfigureAwait(false);
            if (_retirementCompletion is { } retirement)
            {
                // Disposal escalates a drain and cancels control fencing. A failed fence is
                // still visible to the retirement caller, but cannot prevent explicit disposal.
                try { await retirement.Task.ConfigureAwait(false); }
                catch (Exception error)
                {
                    _logger?.LogDebug(error, "Retirement did not finish cleanly before disposal of {Host}:{Port}", Host, Port);
                }
            }
            // A caller may have started an explicit fence retry independently of retirement.
            // Its linked token observes the abort; await its control-connection cleanup too.
            await _retiredFenceGate.WaitAsync().ConfigureAwait(false);
            _retiredFenceGate.Release();
            // Late entrants and retirement actions outside _lifecycleGate may still read or
            // cancel these sources. They have no timers/wait handles; cancellation has removed
            // registrations, so leave the cancelled managed sources for GC rather than race disposal.
            completion.TrySetResult();
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
    }
}
