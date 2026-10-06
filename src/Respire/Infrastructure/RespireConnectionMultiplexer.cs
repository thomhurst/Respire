using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;

namespace Respire.Infrastructure;

// The scope is the receiving physical connection. The token orders the push against slot
// owner mutations (see ClusterSlotMutationClock).
internal delegate void MaintenanceNotificationHandler(
    RespireConnectionMultiplexer sender, object sequenceScope, MaintenanceNotification notification,
    long slotMutationToken);

/// <summary>
/// Round-robins commands across a fixed set of fully multiplexed <see cref="RespireConnection"/>s.
/// Every connection pipelines concurrent commands, so there is no per-command checkout — a dead
/// connection is skipped and replaced in the background. Supports lazy start: create unconnected,
/// then <see cref="EnsureConnectedAsync"/> before first use (idempotent, thread-safe).
/// </summary>
internal sealed partial class RespireConnectionMultiplexer : IAsyncDisposable
{
    private readonly RespireConnection?[] _connections;
    private readonly long[] _movingPublicationGenerations;
    private readonly int _connectionMask;
    private readonly int[] _reconnecting;
    private readonly int[]? _reconnectAttempts;
    private readonly RespireConnectionOptions _options;
    private ActiveEndpoint _activeEndpoint;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly SemaphoreSlim _correctionIdentityGate = new(1, 1);
    private readonly SemaphoreSlim _retiredFenceGate = new(1, 1);
    private readonly ConcurrentDictionary<RetiredClientIdentity, byte> _retiredServerClientIds = new();
    private readonly Lock _stateNotificationGate = new();
    private readonly Queue<StateNotification> _stateNotifications = [];
    private uint _next;
    private int _disposed;
    private int _retired;
    private bool _retirementDrained;
    // Cold lifecycle transitions share this gate; normal selection reads only volatile state.
    // A reconnect reserves ownership before starting so shutdown also awaits unpublished work.
    // Lock order: _lifecycleGate, then MovingHandoffCoordinator.Gate.
    private readonly Lock _lifecycleGate = new();
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
    private bool IsOperational => !IsRetired && Volatile.Read(ref _disposed) == 0;
    internal bool HasPendingCorrectionFences => !_retiredServerClientIds.IsEmpty;
    internal int PendingCorrectionFenceCount => _retiredServerClientIds.Count;
    // Published only after accepted work drained and every failed-socket identity was collected.
    internal bool RetirementDrained => Volatile.Read(ref _retirementDrained);
    internal bool IsInitialized => _connected;
    internal bool HasReliableCorrectionOrdering => _correctionOrderingReady;
    internal bool IsReliableCorrectionOrderingUnavailable =>
        Volatile.Read(ref _correctionOrderingFailure) is not null;

    /// <summary>The definitive CLIENT ID / CLIENT KILL denial recorded for this node, if any.</summary>
    internal string? CorrectionOrderingFailure => Volatile.Read(ref _correctionOrderingFailure);

    /// <summary>The options every connection (and any subscriber) is built from.</summary>
    public RespireConnectionOptions Options => _options;

    /// <summary>
    /// Raised when any client-owned connection begins reconnecting, reconnects, or disconnects
    /// because of failure or disposal.
    /// </summary>
    public event Action<RespireConnectionStateChange>? StateChanged;
    internal event Action<int, RespireConnectionStateChange>? SlotStateChanged;
    // Raised for SMIGRATED pushes only; other maintenance kinds have no topology consumer.
    // The scope is the receiving physical connection; SMIGRATED sequence IDs are scoped to it.
    // Subscription changes record handler epochs using ClusterSlotMutationClock, so a receive
    // token selects the handlers that were eligible before attach, detach, retirement or disposal.
    internal event MaintenanceNotificationHandler? MaintenanceNotificationReceived
    {
        add
        {
            lock (_maintenanceHandlersGate)
            {
                var boundary = ClusterSlotMutationClock.Next();
                CloseMaintenanceHandlerEpoch(boundary);
                _maintenanceNotificationReceived += value;
                if (IsOperational && _maintenanceNotificationReceived is { } handlers)
                {
                    ClusterSlotMutationClock.Track(this);
                    _maintenanceHandlerEpochs.Add((boundary, long.MaxValue, handlers));
                }
            }
        }
        remove
        {
            lock (_maintenanceHandlersGate)
            {
                var boundary = ClusterSlotMutationClock.Next();
                CloseMaintenanceHandlerEpoch(boundary);
                _maintenanceNotificationReceived -= value;
                if (IsOperational && _maintenanceNotificationReceived is { } handlers)
                {
                    ClusterSlotMutationClock.Track(this);
                    _maintenanceHandlerEpochs.Add((boundary, long.MaxValue, handlers));
                }
            }
        }
    }

    private readonly Lock _maintenanceHandlersGate = new();
    private MaintenanceNotificationHandler? _maintenanceNotificationReceived;
    // A receive loop stamps and registers its fence before waiting for this gate. Keep only
    // epochs that an in-flight receive can still select; older closed epochs are pruned.
    private readonly List<(long Start, long End, MaintenanceNotificationHandler Handlers)> _maintenanceHandlerEpochs = [];
    internal int MaintenanceHandlerEpochCount
    {
        get { lock (_maintenanceHandlersGate) return _maintenanceHandlerEpochs.Count; }
    }

    // Receive loop: the fence is stamped as soon as the push is identified. Select its matching
    // subscription epoch under the same gate used to publish epoch boundaries.
    internal MaintenanceNotificationHandler? CaptureMaintenanceHandlers(long slotMutationToken)
    {
        lock (_maintenanceHandlersGate)
        {
            for (var i = _maintenanceHandlerEpochs.Count - 1; i >= 0; i--)
            {
                var epoch = _maintenanceHandlerEpochs[i];
                if (slotMutationToken >= epoch.Start && slotMutationToken < epoch.End)
                    return epoch.Handlers;
            }
            return null;
        }
    }

    internal MaintenanceNotificationHandler? CaptureMaintenanceHandlers()
    {
        using var capture = ClusterSlotMutationClock.BeginCapture(this);
        return CaptureMaintenanceHandlers(capture.Token);
    }

    internal void PruneMaintenanceHandlerEpochs()
    {
        lock (_maintenanceHandlersGate) PruneMaintenanceHandlerEpochsLocked();
    }

    private void CloseMaintenanceHandlerEpoch(long boundary)
    {
        if (_maintenanceHandlerEpochs.Count is > 0)
        {
            var current = _maintenanceHandlerEpochs[^1];
            if (current.End == long.MaxValue)
                _maintenanceHandlerEpochs[^1] = (current.Start, boundary, current.Handlers);
        }
        PruneMaintenanceHandlerEpochsLocked();
    }

    private void PruneMaintenanceHandlerEpochsLocked()
    {
        var removable = 0;
        while (removable < _maintenanceHandlerEpochs.Count)
        {
            var epoch = _maintenanceHandlerEpochs[removable];
            if (epoch.End == long.MaxValue
                || ClusterSlotMutationClock.HasActiveCapture(this, epoch.Start, epoch.End)) break;
            removable++;
        }
        if (removable > 0) _maintenanceHandlerEpochs.RemoveRange(0, removable);
    }

    // The caller captures handlers for the token recorded by the receive loop.
    internal void PublishMaintenanceNotification(MaintenanceNotificationHandler? handlers,
        object sequenceScope, MaintenanceNotification notification, long slotMutationToken)
        => handlers?.Invoke(this, sequenceScope, notification, slotMutationToken);

    // Test convenience: delivers the notification as if it was received with this token now.
    internal void PublishMaintenanceNotification(
        object sequenceScope, MaintenanceNotification notification, long slotMutationToken)
        => PublishMaintenanceNotification(CaptureMaintenanceHandlers(slotMutationToken), sequenceScope, notification, slotMutationToken);

    // Test convenience: stamps the notification as received now.
    internal void PublishMaintenanceNotification(object sequenceScope, MaintenanceNotification notification)
        => PublishMaintenanceNotification(sequenceScope, notification, ClusterSlotMutationClock.Next());

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

    // Metadata belongs to each physical connection and changes on replacement. A known
    // remote primary need not reconnect merely to lose an AZ-affinity comparison.
    internal bool MayBeInAvailabilityZone(string? clientZone)
    {
        if (IsRetired) return true;
        for (var slot = 0; slot < _connections.Length; slot++)
        {
            var connection = Volatile.Read(ref _connections[slot]);
            if (connection?.AvailabilityZone is not { } zone
                || string.Equals(zone, clientZone, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    internal bool HasConnection(Func<RespireConnection, bool> predicate)
    {
        if (Volatile.Read(ref _disposed) != 0 || IsRetired || !_connected) return false;
        foreach (var connection in _connections)
        {
            if (connection is { IsAcceptingCommands: true } && predicate(connection)) return true;
        }
        return false;
    }

    private RespireConnectionMultiplexer(string host, int port, int connectionCount, RespireConnectionOptions options, ILogger? logger)
    {
        Host = host;
        Port = port;
        _activeEndpoint = new ActiveEndpoint(host, port);
        _options = options;
        _logger = logger;
        _connections = new RespireConnection?[connectionCount];
        _movingPublicationGenerations = new long[connectionCount];
        _connectionMask = BitOperations.IsPow2((uint)connectionCount) ? connectionCount - 1 : -1;
        _reconnecting = new int[connectionCount];
        _reconnectAttempts = options.ReconnectPolicy is null ? null : new int[connectionCount];
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
                var endpoint = Volatile.Read(ref _activeEndpoint);
                connectTasks[i] = RespireConnection.ConnectAsync(endpoint.Host, endpoint.Port, _options, _logger, cancellationToken);
            }

            try
            {
                var connections = await Task.WhenAll(connectTasks).ConfigureAwait(false);
                lock (_lifecycleGate)
                {
                    ThrowIfUnavailable();
                    for (var i = 0; i < connections.Length; i++)
                    {
                        connections[i].MultiplexerSlot = i;
                        connections[i].MovingPublicationGeneration = Volatile.Read(ref _movingPublicationGenerations[i]);
                        connections[i].Multiplexer = this;
                        Volatile.Write(ref _connections[i], connections[i]);
                    }
                    _connected = true;
                }
                for (var i = 0; i < connections.Length; i++)
                    ObservePublishedConnection(i, connections[i]);
            }
            catch
            {
                foreach (var task in connectTasks)
                {
                    if (task.IsCompletedSuccessfully)
                    {
                        var connection = await task.ConfigureAwait(false);
                        await connection.DisposeAsync().ConfigureAwait(false);
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

    // Diagnostics must not schedule reconnects or initialize an unused connection slot.
    internal RespireConnection? GetExistingHealthConnection()
    {
        if (!IsOperational) return null;
        for (var index = 0; index < _connections.Length; index++)
        {
            var slot = Volatile.Read(ref _connections[index]);
            if (slot is { IsAcceptingCommands: true }) return slot;
        }
        return null;
    }

    /// <summary>
    /// Returns a stable healthy connection for an affinity value, probing replacements in a
    /// deterministic order when its preferred connection is unavailable.
    /// </summary>
    internal RespireConnection GetConnection(int affinity) => GetConnection(unchecked((uint)affinity));

    /// <summary>Prefers a matching physical connection while retaining round-robin or slot-affinity order.</summary>
    internal RespireConnection GetConnectionForZone(string zone, int? affinity = null)
    {
        if (_connections.Length == 1) return GetSingleConnection();
        var start = affinity is { } value ? unchecked((uint)value) : Interlocked.Increment(ref _next);
        return GetConnection(start, zone);
    }

    private RespireConnection GetConnection(uint startIndex, string? preferredZone = null)
    {
        ThrowIfUnavailable();
        if (!_connected)
        {
            throw new RespireConnectionException(
                $"Not connected to {Host}:{Port} — call {nameof(EnsureConnectedAsync)} first.");
        }

        RespireConnection? fallback = null;
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
                if (preferredZone is null || string.Equals(connection.AvailabilityZone, preferredZone, StringComparison.Ordinal))
                    return connection;
                fallback ??= connection;
            }
            else ScheduleReconnect(slot);
        }

        if (fallback is not null) return fallback;
        ThrowIfUnavailable();
        ThrowIfRecoveryExhausted();
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
        ThrowIfRecoveryExhausted();
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
                        RecordRetiredConnectionIdentity(connection);
                        ScheduleReconnect(slot);
                        ThrowIfRecoveryExhausted(slot);
                        ready = false;
                        continue;
                    }

                    try
                    {
                        await connection.EnsureServerClientIdAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (IsConnectionLoss(ex) && Volatile.Read(ref _disposed) == 0)
                    {
                        RecordRetiredConnectionIdentity(connection);
                        ScheduleReconnect(slot);
                        ThrowIfRecoveryExhausted(slot);
                        ready = false;
                    }
                    catch (RespireConnectionRetiredException)
                    {
                        // A handoff unpublished this socket before admitting CLIENT ID; its
                        // replacement is examined on the next pass.
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
                        RecordRetiredConnectionIdentity(connection);
                        var slot = FindSlot(connection);
                        if (slot >= 0)
                        {
                            ScheduleReconnect(slot);
                            ThrowIfRecoveryExhausted(slot);
                        }

                        await Task.Delay(25, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    catch (RespireConnectionRetiredException)
                    {
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
                armCommandDeadline: false, pinToConnection: true) // Probes this connection's own ID.
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
            ThrowIfUnavailable();
            await FenceRetiredConnectionsAsync(cancellationToken).ConfigureAwait(false);

            var sends = new List<(RespireConnection Connection, ValueTask<RespValue> Send)>(_connections.Length);
            var retiredBeforeAdmission = false;
            for (var slot = 0; slot < _connections.Length; slot++)
            {
                var connection = Volatile.Read(ref _connections[slot]);
                if (connection is not { IsConnected: true })
                {
                    RecordRetiredConnectionIdentity(connection);
                    ScheduleReconnect(slot);
                    continue;
                }

                try
                {
                    // Corrections, once owed, must not be abandonable: no command deadline. Each
                    // copy orders against its own socket's FIFO, so it is pinned there: moving it
                    // to another socket would void that barrier.
                    var send = sendAsking
                        ? Respire.Internal.ClusterRouter.SendAskingUncheckedAsync(
                            connection, in command, cancellationToken, armCommandDeadline: false, pinToConnection: true)
                        : connection.SendAsync(in command, cancellationToken, armCommandDeadline: false, pinToConnection: true);
                    sends.Add((connection, send));
                }
                catch (Exception ex) when (IsConnectionLoss(ex))
                {
                    RecordRetiredConnectionIdentity(connection);
                    ScheduleReconnect(slot);
                }
                catch (RespireConnectionRetiredException)
                {
                    // Never admitted, so nothing to fence; the replacement gets the next pass.
                    retiredBeforeAdmission = true;
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

            var retry = sends.Count == 0 || retiredBeforeAdmission;
            Exception? fatal = null;
            for (var i = 0; i < drains.Length; i++)
            {
                if (await drains[i].ConfigureAwait(false) is { } ex)
                {
                    if (IsConnectionLoss(ex) || ex is RespireConnectionRetiredException)
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
            // A cleanly closed slot may owe no fence. Wait for recovery rather than spinning
            // synchronously through empty broadcasts while a configured delay is pending.
            if (sends.Count == 0) await GetHealthyConnectionAsync(cancellationToken).ConfigureAwait(false);
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
                    RecordRetiredConnectionIdentity(connection);
                }

                return ex;
            }
        }
    }

    // TlsHost is the name the socket was opened with; after a MOVING handoff it can differ
    // from the multiplexer's configured host, and the fence must validate the same identity.
    private readonly record struct RetiredClientIdentity(long ClientId, string Host, int Port, string TlsHost)
    {
        internal static RetiredClientIdentity From(RespireConnection connection)
            => new(connection.ServerClientId, connection.NetworkPeerAddress ?? connection.Host,
                connection.NetworkPeerPort ?? connection.Port, connection.Host);
    }

    internal bool HasCurrentPeer(string host, int port)
    {
        if (IsRetired) return false;
        foreach (var connection in _connections)
            if (connection is { IsAcceptingCommands: true }
                && (connection.NetworkPeerAddress ?? connection.Host) == host
                && (connection.NetworkPeerPort ?? connection.Port) == port)
                return true;
        return false;
    }

    // Reusing a whole Sentinel generation needs proof for every command slot. A single
    // matching socket can coexist with an old DNS peer while slots recover independently.
    internal bool AllCurrentPeersMatch(string host, int port)
        => GetConfirmedCurrentPeer() is { } peer && peer.Host == host && peer.Port == port;

    // A snapshot is proof for the whole generation only when every command slot is
    // accepting commands and names the same physical peer at observation time.
    internal RespireEndpoint? GetConfirmedCurrentPeer()
        => CaptureCurrentPeers(null);

    internal (ImmutableArray<RespireEndpoint> Peers, RespireEndpoint? ConfirmedPeer) CaptureSentinelPeers()
    {
        var peers = ImmutableArray.CreateBuilder<RespireEndpoint>(_connections.Length);
        var confirmed = CaptureCurrentPeers(peers);
        return IsRetired ? ([], null) : (peers.ToImmutable(), confirmed);
    }

    private RespireEndpoint? CaptureCurrentPeers(ImmutableArray<RespireEndpoint>.Builder? peers)
    {
        var allReady = IsConnected;
        RespireEndpoint? confirmed = null;
        for (var slot = 0; slot < _connections.Length; slot++)
        {
            var connection = Volatile.Read(ref _connections[slot]);
            if (connection is not { IsAcceptingCommands: true }) { allReady = false; continue; }
            var peer = new RespireEndpoint(connection.NetworkPeerAddress ?? connection.Host,
                connection.NetworkPeerPort ?? connection.Port);
            peers?.Add(peer);
            if (confirmed is { } previous && (previous.Host != peer.Host || previous.Port != peer.Port)) allReady = false;
            confirmed = peer;
        }
        return allReady && !IsRetired ? confirmed : null;
    }

    // Only these fence-entry guards produce this signal. Generic disposal failures from
    // connection cleanup must remain failures even if node disposal starts afterwards.
    internal sealed class CorrectionFenceDisposedException()
        : ObjectDisposedException(typeof(RespireConnectionMultiplexer).FullName)
    {
    }

    internal async ValueTask FenceRetiredConnectionsAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0) throw new CorrectionFenceDisposedException();
        await _retiredFenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0) throw new CorrectionFenceDisposedException();
            foreach (var connection in _connections)
                if (connection is not { IsConnected: true }) RecordRetiredConnectionIdentity(connection);

            foreach (var identity in _retiredServerClientIds.Keys)
            {
                // Client IDs belong to a physical server, not a hostname. DNS can change
                // while this generation drains, and the new server can reuse the same ID.
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _abortCancellation.Token);
                lifetime.CancelAfter(_options.ConnectTimeout);
                var options = _options with
                {
                    // The lifetime above bounds the entire fence. A second connect timer
                    // could fire first and escape classification as a CLIENT KILL timeout.
                    ConnectTimeout = Timeout.InfiniteTimeSpan,
                    Generation = null,
                    EnableClientTracking = false, PushHandler = null, SubscriptionConfirmationHandler = null,
                    MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled,
                    TlsOptions = _options.UseTls ? RespireConnection.CreateTlsOptions(_options.TlsOptions, identity.TlsHost) : _options.TlsOptions,
                };
                try
                {
                    await using var control = await RespireConnection.ConnectAsync(identity.Host, identity.Port,
                        options, _logger, lifetime.Token).ConfigureAwait(false);
                    using var reply = await control.SendAsync(new ClientKillIdCommand(identity.ClientId), lifetime.Token,
                        armCommandDeadline: false).ConfigureAwait(false);
                    if (reply.IsError) throw new RespireServerException(reply.GetErrorMessage(), "CLIENT KILL");
                }
                catch (OperationCanceledException error) when (lifetime.IsCancellationRequested
                    && !cancellationToken.IsCancellationRequested && !_abortCancellation.IsCancellationRequested)
                {
                    throw new RespireTimeoutException("CLIENT KILL", _options.ConnectTimeout, error);
                }
                _retiredServerClientIds.TryRemove(identity, out _);
            }
        }
        finally
        {
            _retiredFenceGate.Release();
        }
    }

    internal async ValueTask RetireConnectionAsync(RespireConnection original)
    {
        await original.DisposeAsync().ConfigureAwait(false);
        var slot = FindSlot(original);
        if (slot >= 0) ScheduleReconnect(slot);
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
            catch (RespireConnectionException error) when (error is not RespireReconnectLimitException)
            {
                ThrowIfUnavailable();
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void RecordRetiredConnectionIdentity(RespireConnection? connection)
    {
        // An identity already obtained remains an obligation even if interrupted bootstrap
        // clears the flag that requests identities on future replacement connections.
        if (connection is { ServerClientId: > 0, DrainedSuccessfully: false })
        {
            _retiredServerClientIds.TryAdd(RetiredClientIdentity.From(connection), 0);
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

    internal void ScheduleReconnect(int slot)
    {
        if (_options.Generation?.IsRetired == true) return;
        var connection = Volatile.Read(ref _connections[slot]);
        // A stale request can arrive after a healthy replacement publishes. Do not record
        // that socket as a correction fence. A draining socket must also finish first.
        if (connection is { IsConnected: true }) return;
        var error = connection?.CloseError;
        RecordRetiredConnectionIdentity(connection);
        ForgetMovingSequences(connectedOnly: true);
        bool publish;
        var attempt = 0;
        var delay = TimeSpan.Zero;
        lock (_lifecycleGate)
        {
            // A competing caller may already have published a healthy replacement since our snapshot.
            // Keep it for both legacy and configured recovery; a stale notification must not replace it.
            if (!IsOperational || _connections[slot] is { IsAcceptingCommands: true }
                || (_options.ReconnectPolicy?.IsExhausted(_reconnectAttempts![slot]) ?? false)
                || Interlocked.CompareExchange(ref _reconnecting[slot], 1, 0) != 0)
                return;
            if (_options.ReconnectPolicy is { } policy)
            {
                attempt = _reconnectAttempts![slot] = (int)Math.Min((long)_reconnectAttempts[slot] + 1, int.MaxValue);
                delay = policy.GetDelay(attempt);
            }
            _activeReconnects++;
            publish = QueueLifecycleNotificationUnderLock(new StateNotification(slot, RespireConnectionState.Reconnecting, error,
                attempt, attempt == 0 ? null : delay));
        }
        _ = ReconnectAsync(slot, attempt, delay);
        // A handler can synchronously retire/dispose and wait for this reserved work.
        if (publish) DrainStateNotifications();
    }

    private async Task ReconnectAsync(int slot, int attempt, TimeSpan delay)
    {
        RespireConnection? replacement = null;
        var reconnectGuardReleased = false;
        var publish = false;
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, _stopConnecting.Token).ConfigureAwait(false);
            var endpoint = Volatile.Read(ref _activeEndpoint);
            replacement = await RespireConnection.ConnectAsync(endpoint.Host, endpoint.Port, _options, _logger, _stopConnecting.Token)
                .ConfigureAwait(false);
            if (Volatile.Read(ref _trackServerClientIds) != 0)
                await replacement.EnsureServerClientIdAsync(_stopConnecting.Token).ConfigureAwait(false);

            RespireConnection? old;
            RespireConnection publishedReplacement;
            lock (_lifecycleGate)
            {
                ThrowIfUnavailable();
                if (!ReferenceEquals(endpoint, Volatile.Read(ref _activeEndpoint)))
                    throw new RespireConnectionException("Connection endpoint changed during reconnect.");
                ForgetMovingSequences(connectedOnly: true);
                lock (_moving.Gate)
                {
                    var generation = Interlocked.Increment(ref _movingPublicationGenerations[slot]);
                    replacement.MultiplexerSlot = slot;
                    replacement.MovingPublicationGeneration = generation;
                    replacement.Multiplexer = this;
                    old = Interlocked.Exchange(ref _connections[slot], replacement);
                    publishedReplacement = replacement;
                    replacement = null;
                    if (_reconnectAttempts is not null) _reconnectAttempts[slot] = 0;
                }
            }
            ObservePublishedConnection(slot, publishedReplacement);
            RecordRetiredConnectionIdentity(old);
            _logger?.DeadConnectionReplaced(slot, Host, Port);
            if (old is not null) await old.DisposeAsync().ConfigureAwait(false);
            lock (_lifecycleGate)
            {
                publish = IsOperational
                    && QueueLifecycleNotificationUnderLock(new StateNotification(slot, RespireConnectionState.Connected, null, attempt));
            }
        }
        catch (Exception ex)
        {
            if (replacement is not null)
            {
                try { await replacement.DisposeAsync().ConfigureAwait(false); }
                catch (Exception disposeException)
                {
                    _logger?.RejectedReplacementDisposalFailed(Host, Port, disposeException);
                }
            }
            if (IsOperational)
            {
                lock (_lifecycleGate)
                {
                    if (IsOperational && _connections[slot] is { IsAcceptingCommands: true })
                    {
                        // Not a recovery failure: while this reconnect was failing (for example
                        // against an endpoint a MOVING handoff has just left), the handoff
                        // published a healthy socket into this slot. Report the slot as
                        // connected and reset its attempts instead of scheduling a retry.
                        _logger?.ReconnectKeptHealthyConnection(slot, ex);
                        if (_reconnectAttempts is not null) _reconnectAttempts[slot] = 0;
                        publish = QueueLifecycleNotificationUnderLock(
                            new StateNotification(slot, RespireConnectionState.Connected, null));
                        Volatile.Write(ref _reconnecting[slot], 0);
                        reconnectGuardReleased = true;
                    }
                    else if (IsOperational)
                    {
                        var exhausted = _options.ReconnectPolicy?.IsExhausted(attempt) == true;
                        if (exhausted)
                            _logger?.ReconnectExhausted(Host, Port, attempt, ex);
                        else if (_options.ReconnectPolicy is not null)
                            _logger?.ReconnectBackoffDeferred(Host, Port, ex);
                        else
                            _logger?.ReconnectDeferred(Host, Port, ex);
                        publish = EnqueueReconnectFailure(slot, ex, attempt, exhausted);
                        reconnectGuardReleased = true;
                    }
                }
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
        // Completion handlers must not wait for the reconnect that is invoking them.
        if (publish) DrainStateNotifications();
    }

    /// <summary>
    /// Wires a newly published socket's failure and MOVING callbacks. Each socket is published
    /// once, so handlers never stack; they die with the socket, and the eligibility checks in
    /// <see cref="QueueMovingHandoffUnderLock"/> ignore callbacks from sockets no longer current.
    /// </summary>
    private void ObservePublishedConnection(int slot, RespireConnection connection)
    {
        connection.ReplayUnpublishedMigrations();
        connection.MovingNotification += announcement => QueueMovingHandoff(slot, connection, announcement);
        // Replay a MOVING parsed before this handler existed (for example during the handshake).
        // The handler is attached first, so a MOVING parsed between these two lines is delivered
        // twice; the per-connection LastQueuedMovingSequence check drops the second copy.
        if (connection.LastMovingAnnouncement is { } announcement)
            QueueMovingHandoff(slot, connection, announcement.PublicationGeneration >= 0
                ? announcement
                : announcement with
                {
                    // Parsed before publication: it is current as of this publication, but keeps
                    // its receipt time so the advertised grace period is not restarted.
                    PublicationGeneration = connection.MovingPublicationGeneration,
                    HandoffEpoch = _moving.HandoffEpoch,
                });
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
        if (IsOperational
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

    private bool EnqueueReconnectFailure(int slot, Exception error, int attempt, bool exhausted)
    {
        lock (_stateNotificationGate)
        {
            var publish = EnqueueStateNotificationUnderLock(
                new StateNotification(slot, RespireConnectionState.Disconnected, error, attempt,
                    Exhausted: exhausted));

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
            new RespireEndpoint(Host, Port), notification.State, notification.Error)
        {
            ReconnectSource = RespireReconnectSource.Command,
            SourceState = notification.State,
            ReconnectAttempt = notification.Attempt,
            ConnectionSlot = notification.Slot,
            NextReconnectDelay = notification.Delay,
            ReconnectExhausted = notification.Exhausted,
        };
        try
        {
            StateChanged?.Invoke(change);
        }
        catch (Exception ex)
        {
            _logger?.ConnectionStateObserverFailed(ex);
        }

        if (notification.Slot is { } slot)
        {
            try
            {
                SlotStateChanged?.Invoke(slot, change);
            }
            catch (Exception ex)
            {
                _logger?.ConnectionSlotObserverFailed(ex);
            }
        }
        if (notification.Exhausted)
        {
            try { RespireTelemetry.RecordReconnectExhaustion(Host, Port); }
            catch (Exception ex) { _logger?.ReconnectMetricObserverFailed(ex); }
        }
        if (notification.Delay is { } delay)
        {
            try { RespireTelemetry.RecordReconnectAttempt(Host, Port, notification.Attempt, delay); }
            catch (Exception ex) { _logger?.ReconnectMetricObserverFailed(ex); }
        }
    }

    private readonly record struct StateNotification(
        int? Slot,
        RespireConnectionState State,
        Exception? Error,
        int Attempt = 0,
        TimeSpan? Delay = null,
        bool Exhausted = false);

    private void ThrowIfRecoveryExhausted(int? requiredSlot = null)
    {
        if (_options.ReconnectPolicy is not { MaxAttempts: not null } policy) return;
        lock (_lifecycleGate)
        {
            var first = requiredSlot ?? 0;
            var end = requiredSlot is { } requested ? requested + 1 : _connections.Length;
            for (var slot = first; slot < end; slot++)
                if (!policy.IsExhausted(_reconnectAttempts![slot]) || Volatile.Read(ref _reconnecting[slot]) != 0
                    || _connections[slot] is { IsAcceptingCommands: true }) return;
        }
        var scope = requiredSlot is { } exhaustedSlot ? $"required slot {exhaustedSlot}" : "all slots";
        throw new RespireReconnectLimitException($"Reconnect policy exhausted for {Host}:{Port} ({scope}) after {policy.MaxAttempts} attempts per connection slot. Recreate the client to start a new recovery episode.");
    }

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
            lock (_maintenanceHandlersGate)
            {
                Volatile.Write(ref _retired, 1);
            }
            publish = QueueLifecycleNotificationUnderLock(new StateNotification(null, RespireConnectionState.Disconnected, null));
        }
#pragma warning disable CA1849 // Settle connect callbacks before starting retirement and publishing its synchronous notification.
        _stopConnecting.Cancel();
#pragma warning restore CA1849
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
        Task? moving;
        lock (_moving.Gate) moving = _moving.WorkerCompletion;
        if (moving is not null) await moving.ConfigureAwait(false);
        // The worker has stopped, so no further drain can start after this snapshot.
        await WaitForMovingDrainsAsync().ConfigureAwait(false);
    }

    private async Task RetireCoreAsync(TaskCompletionSource completion)
    {
        try
        {
            await WaitForPublicationAsync().ConfigureAwait(false);
            var connections = _connections.OfType<RespireConnection>().ToArray();
            foreach (var connection in connections) connection.StopAcceptingCommands();
            // The PING replies fence already-sent RESP3 maintenance pushes behind the receive
            // loop before the connection retirement drain closes sockets with empty command rings.
            await CleanupTasks.WhenAllAsync(connections.Select(DrainMaintenanceNotificationsBeforeRetirementAsync))
                .ConfigureAwait(false);
            lock (_maintenanceHandlersGate)
                CloseMaintenanceHandlerEpoch(ClusterSlotMutationClock.Next());
            await CleanupTasks.WhenAllAsync(connections.Select(connection => connection.RetireAsync())).ConfigureAwait(false);
            await WaitForCorrectionIdentityAsync().ConfigureAwait(false);
            foreach (var connection in _connections) RecordRetiredConnectionIdentity(connection);
            Volatile.Write(ref _retirementDrained, true);
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

    private async Task DrainMaintenanceNotificationsBeforeRetirementAsync(RespireConnection connection)
    {
        using var deadline = new CancellationTokenSource(_options.ConnectTimeout, _options.MaintenanceDrainTimeProvider);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_abortCancellation.Token, deadline.Token);
        try
        {
            await connection.DrainPendingMaintenanceNotificationsAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // The barrier may time out behind accepted commands. Let callers finish through
            // CommandTimeout or maintenance relaxation; use the explicit fallback when disabled.
            // Then abort the connection to release the unanswered barrier.
            using var drainTimeout = CancellationTokenSource.CreateLinkedTokenSource(_abortCancellation.Token);
            var commandDrainTimeout = _options.CommandTimeout ?? _options.RetirementDrainFallbackTimeout;
            if (_options.MaintenanceNotifications != RespireMaintenanceNotificationMode.Disabled
                && _options.MaintenanceRelaxedTimeout > commandDrainTimeout)
                commandDrainTimeout = _options.MaintenanceRelaxedTimeout;
            drainTimeout.CancelAfter(commandDrainTimeout);
            try
            {
                await connection.WaitForOtherCommandsToCompleteAsync(drainTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (drainTimeout.IsCancellationRequested)
            {
                // Preserve accepted commands through their deadline, including maintenance relaxation.
            }

            // A streamed reply has left the in-flight ring. Preserve it while the reader makes
            // progress, then abort only after its idle grace expires.
            var streamIdleTimeout = _options.RetirementDrainFallbackTimeout;
            if (_options.CommandTimeout is { } streamCommandTimeout && streamCommandTimeout > streamIdleTimeout)
                streamIdleTimeout = streamCommandTimeout;
            if (_options.MaintenanceNotifications != RespireMaintenanceNotificationMode.Disabled
                && _options.MaintenanceRelaxedTimeout > streamIdleTimeout)
                streamIdleTimeout = _options.MaintenanceRelaxedTimeout;
            _ = await connection.WaitForActiveBulkStreamToCompleteAsync(streamIdleTimeout, _abortCancellation.Token)
                .ConfigureAwait(false);

            try { await connection.DisposeAsync().ConfigureAwait(false); }
            catch (Exception disposeError)
            {
                try { _logger?.MaintenanceBarrierAbortFailed(Host, Port, disposeError); }
                catch { /* Logging must not stop retirement. */ }
            }

            try { _logger?.MaintenanceBarrierFailed(Host, Port, error); }
            catch { /* Logging must not stop retirement. */ }
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
            lock (_maintenanceHandlersGate)
            {
                var boundary = ClusterSlotMutationClock.Next();
                Volatile.Write(ref _disposed, 1);
                Volatile.Write(ref _retired, 1);
                CloseMaintenanceHandlerEpoch(boundary);
            }
            publish = QueueLifecycleNotificationUnderLock(new StateNotification(null, RespireConnectionState.Disconnected, null));
        }
#pragma warning disable CA1849 // This synchronous admission boundary must finish callbacks before aborting sockets and starting shared disposal.
        _stopConnecting.Cancel();
        _abortCancellation.Cancel();
#pragma warning restore CA1849
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
            await CleanupTasks.WhenAllAsync(_connections.OfType<RespireConnection>().Select(connection => connection.DisposeAsync().AsTask()))
                .ConfigureAwait(false);
            await WaitForCorrectionIdentityAsync().ConfigureAwait(false);
            if (_retirementCompletion is { } retirement)
            {
                // Disposal escalates a drain and cancels control fencing. A failed fence is
                // still visible to the retirement caller, but cannot prevent explicit disposal.
                try { await retirement.Task.ConfigureAwait(false); }
                catch (Exception error)
                {
                    _logger?.RetirementDisposalFailed(Host, Port, error);
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
