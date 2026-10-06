using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Respire.Infrastructure;
using Respire.Networking;

namespace Respire.Internal;

internal enum DedicatedLeaseKind { Ordinary, Streaming }

/// <summary>
/// A small pool of dedicated (non-multiplexed) connections for commands that occupy a
/// connection for their whole duration: BLPOP-style blocking waits, blocking stream reads, and
/// streamed uploads whose source or socket can stall while writing a frame.
/// Multiplexed connections must never run these — one blocking command would stall every
/// pipelined command behind it — so they rent from here instead. Connections are created on
/// demand and a few idle ones are kept for reuse. Rented connections are tracked so client
/// disposal can abort a command blocked server-side (even a BLPOP with an infinite wait).
/// </summary>
/// <remarks>
/// A renter owns its lease until returning or discarding it. Retirement rejects new rentals and
/// drains accepted leases; disposal aborts every owned connection, including closing entries.
/// Failed application sends discard their leases. The router may retry an upload before its
/// header is accepted, preserving the consumed prefix and original deadline. After acceptance,
/// only an explicit server redirect permits replay, and only when the source is replayable.
/// Streaming leases negotiate maintenance independently of ordinary blocking/correction leases;
/// both kinds share the idle-capacity bound and the connection ownership ledger.
/// </remarks>
internal sealed partial class DedicatedConnectionPool(
    string host, int port, RespireConnectionOptions options, ILogger? logger,
    Action<RespireConnectionStateChange>? stateChanged = null,
    Action<RespireConnection>? streamingConnectionCreated = null) : IAsyncDisposable
{
    private const int MaxIdle = 4;
    internal RespireEndpoint Endpoint => new(host, port);

    // Identity changes on every MOVING publication, including a handoff to the same address.
    internal RespireConnectionMultiplexer? MovingOwner { get; init; }
    internal object? MovingPublication { get; init; }
    internal bool IsMovingPublicationCurrent => MovingOwner is null
        || ReferenceEquals(MovingPublication, MovingOwner.MovingPublication);

    // Cluster diagnostics acquire the router's _nodesGate before this gate. Never call
    // back into the router or invoke user callbacks while holding this gate.
    private readonly Lock _gate = new();
    private readonly List<Entry> _idle = new(MaxIdle);
    private readonly RespireConnectionOptions _ordinaryOptions = options with
        { MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled, IsDedicatedConnection = true };
    private readonly RespireConnectionOptions _streamingOptions = options with { IsDedicatedConnection = true };
    // Keep closing entries registered until socket and receive/flush cleanup actually completes.
    private readonly Dictionary<RespireConnection, Entry> _connections = [];
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private TaskCompletionSource? _completion;
    private Exception? _closeError;
    private int _connecting;
    private bool _stopping;
    private bool _movingHandoffsCaptured;
    private bool _cancellationComplete;

    /// <summary>True for a Cluster replica pool, whose connections enter READONLY mode.</summary>
    internal bool IsReadOnly => options.ReadOnly;

    internal bool IsStopping
    {
        get { lock (_gate) return _stopping; }
    }

    /// <summary>
    /// Classifies acquisition errors eligible for route-specific reselection when the caller observes
    /// a stopping pool. A CONNECT timeout may predate retirement; no causal ordering is inferred.
    /// The route, not this classifier, determines whether retirement retries have an attempt limit.
    /// This does not classify Redis handshake timeouts or authorize replay of an admitted command.
    /// </summary>
    internal static bool IsRetirementFailure(Exception error)
        => error is ObjectDisposedException or OperationCanceledException
            or RespireTimeoutException { CommandName: "CONNECT", Diagnostics.Stage: RespireCommandStage.Connecting };

    internal (int Borrowed, int Connecting, bool CleanupFailed) CaptureRetirementState()
    {
        lock (_gate)
        {
            var borrowed = 0;
            foreach (var entry in _connections.Values)
                if (entry.State == State.Rented) borrowed++;
            return (borrowed, _connecting, _closeError is not null);
        }
    }

    private enum State { Idle, Rented, Closing }

    private sealed class Entry(RespireConnection connection, DedicatedLeaseKind kind)
    {
        internal readonly RespireConnection Connection = connection;
        internal readonly DedicatedLeaseKind Kind = kind;
        internal State State = State.Rented;
        internal TaskCompletionSource? Closed;
    }

    public async ValueTask<RespireConnection> RentAsync(
        CancellationToken cancellationToken, bool armHandshakeDeadline = true, bool reuseIdle = true,
        DedicatedLeaseKind kind = DedicatedLeaseKind.Ordinary, string? preferredZone = null)
    {
        // Maintenance negotiation is connection state. Keep these leases separate from blocking
        // and corrective leases, while retaining one ownership/drain ledger and idle bound.
        var useStreamingMaintenance = kind == DedicatedLeaseKind.Streaming && options.MaintenanceNotifications != RespireMaintenanceNotificationMode.Disabled;
        var connectionOptions = useStreamingMaintenance ? _streamingOptions : _ordinaryOptions;
        var compatibleKind = useStreamingMaintenance ? DedicatedLeaseKind.Streaming : DedicatedLeaseKind.Ordinary;
        while (true)
        {
            Entry stale;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_stopping, this);
                if (reuseIdle && TryTakeIdle(compatibleKind, preferredZone, out var entry))
                {
                    if (entry.Connection.IsConnected)
                    {
                        entry.State = State.Rented;
                        entry.Connection.SetLeaseRented(true);
                        return entry.Connection;
                    }
                    BeginCloseLocked(entry);
                    stale = entry;
                }
                else
                {
                    // Reserve acquisition before reading the lifetime token. Completion cannot
                    // dispose its source until this acquisition has finished all cleanup.
                    _connecting++;
                    break;
                }
            }
            _ = CloseAsync(stale);
        }

        var waitStarted = Stopwatch.GetTimestamp();
        try
        {
            using var connectCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _lifetimeCancellation.Token);
            RespireConnection connection;
            try
            {
                // Corrective fences own their retry/deadline rules and must not inherit an
                // application acquisition limit. Healthy idle rentals never enter this path.
                connection = connectionOptions.ReconnectPolicy is { } policy && armHandshakeDeadline
                    ? await ConnectWithRecoveryAsync(policy, connectionOptions, connectCancellation.Token).ConfigureAwait(false)
                    : await RespireConnection.ConnectAsync(
                        host, port, connectionOptions, logger, connectCancellation.Token, armHandshakeDeadline).ConfigureAwait(false);
            }
            catch (OperationCanceledException error) when (CommandTimeoutCancellation.IsFromLinkedToken(
                error, cancellationToken, connectCancellation.Token))
            {
                // Unwrap only our own lifetime link, preserving independent retirement cancellation.
                throw new OperationCanceledException(error.Message, error, cancellationToken);
            }
            var entry = new Entry(connection, compatibleKind);
            bool accepted;
            lock (_gate)
            {
                _connections.Add(connection, entry);
                accepted = !_stopping;
                if (!accepted) BeginCloseLocked(entry);
            }
            if (accepted)
            {
                try
                {
                    // Observers may publish a handoff and retire this pool. Never call them
                    // under the ownership gate; the borrower still owns any returned lease.
                    if (useStreamingMaintenance) streamingConnectionCreated?.Invoke(connection);
                    connection.RecordConnectionWait(waitStarted);
                    return connection;
                }
                catch
                {
                    await DiscardAsync(connection).ConfigureAwait(false);
                    throw;
                }
            }
            await CloseAsync(entry).ConfigureAwait(false);
            throw new ObjectDisposedException(nameof(DedicatedConnectionPool));
        }
        finally
        {
            lock (_gate)
            {
                _connecting--;
                CompleteIfDrainedLocked();
            }
        }
    }

    // Called under _gate. The bounded list acts as a stack for each compatible lease kind.
    private bool TryTakeIdle(DedicatedLeaseKind kind, string? preferredZone, out Entry entry)
    {
        var selected = -1;
        for (var index = _idle.Count - 1; index >= 0; index--)
        {
            if (_idle[index].Kind != kind) continue;
            if (selected < 0) selected = index;
            var connection = _idle[index].Connection;
            if (preferredZone is null || connection.IsConnected && ReadFallbackPolicy.IsSameZone(connection, preferredZone))
            {
                selected = index;
                break;
            }
        }
        if (selected >= 0)
        {
            entry = _idle[selected];
            _idle.RemoveAt(selected);
            return true;
        }
        entry = null!;
        return false;
    }

    /// <summary>Returns a healthy connection for reuse; anything else (or overflow) is closed.</summary>
    public void Return(RespireConnection connection)
    {
        Debug.Assert(!connection.IsStreamingWriteActive, "A streaming send must release its write path before pool return.");
        Entry? closing = null;
        lock (_gate)
        {
            if (!_connections.TryGetValue(connection, out var entry) || entry.State != State.Rented) return;
            if (!_stopping && connection.IsConnected)
            {
                if (_idle.Count == MaxIdle)
                {
                    var sameKind = 0;
                    foreach (var idle in _idle) if (idle.Kind == entry.Kind) sameKind++;
                    // A lone kind may use all four slots. Under mixed demand, each can reclaim
                    // half by evicting the other kind's oldest idle entry, avoiding reconnect churn.
                    if (sameKind < MaxIdle / 2)
                    {
                        var other = 0;
                        while (other < _idle.Count && _idle[other].Kind == entry.Kind) other++;
                        Debug.Assert(other < _idle.Count,
                            "A full pool below this kind's reserved share must contain another lease kind.");
                        closing = _idle[other];
                        closing.Connection.RequestMetricCloseReason(ConnectionTelemetry.CloseReason.IdleEviction);
                        _idle.RemoveAt(other);
                        BeginCloseLocked(closing);
                    }
                }
                if (_idle.Count < MaxIdle)
                {
                    entry.State = State.Idle;
                    entry.Connection.SetLeaseRented(false);
                    _idle.Add(entry);
                }
                else
                {
                    closing = entry;
                    connection.RequestMetricCloseReason(ConnectionTelemetry.CloseReason.IdleEviction);
                }
            }
            else closing = entry;
            if (ReferenceEquals(closing, entry)) BeginCloseLocked(entry);
        }
        if (closing is not null) _ = CloseAsync(closing);
    }

    /// <summary>Removes a failed or abandoned lease and waits for its cleanup.</summary>
    public ValueTask DiscardAsync(RespireConnection connection)
    {
        Entry entry;
        lock (_gate)
        {
            if (!_connections.TryGetValue(connection, out entry!)) return ValueTask.CompletedTask;
            if (entry.State == State.Closing) return new ValueTask(entry.Closed!.Task);
            // Cleanup may run again after a lease has already been returned.
            if (entry.State != State.Rented) return ValueTask.CompletedTask;
            BeginCloseLocked(entry);
        }
        _ = CloseAsync(entry);
        return new ValueTask(entry.Closed!.Task);
    }

    /// <summary>
    /// Stops rentals and pending handshakes, closes idle sockets, and waits for borrowed leases
    /// to return. Accepted operations can finish; DisposeAsync can still abort them later.
    /// </summary>
    internal ValueTask RetireAsync(bool moving = false) => Stop(abortBorrowed: false, moving);

    /// <summary>Stops the pool and aborts borrowed operations, including an existing retirement.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await Stop(abortBorrowed: true).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // Preserve best-effort client disposal. Retirement owners can still observe
            // the fault on the shared completion returned by RetireAsync.
            logger?.DedicatedPoolDisposalFailed(host, port, error);
        }
    }

    internal List<RespireConnection>? CaptureMovingHandoffs()
    {
        lock (_gate)
        {
            if (_stopping || _movingHandoffsCaptured) return null;
            List<RespireConnection>? handedOff = null;
            foreach (var entry in _connections.Values)
                if (entry.State != State.Closing && entry.Connection.IsConnected)
                    (handedOff ??= []).Add(entry.Connection);
            // Even an empty publication excludes handshakes that finish afterwards.
            _movingHandoffsCaptured = true;
            return handedOff;
        }
    }

    private ValueTask Stop(bool abortBorrowed, bool moving = false)
    {
        List<Entry>? closing = null;
        List<RespireConnection>? handedOff = null;
        bool cancel;
        Task completion;
        lock (_gate)
        {
            cancel = !_stopping;
            _stopping = true;
            _completion ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            completion = _completion.Task;
            foreach (var entry in _connections.Values)
            {
                // Count only live members of this retired publication. Closing entries and
                // connections whose handshake finishes after retirement were not handed off.
                if (cancel && moving && !_movingHandoffsCaptured && entry.State != State.Closing && entry.Connection.IsConnected)
                    (handedOff ??= []).Add(entry.Connection);
                if (entry.State == State.Idle || (abortBorrowed && entry.State == State.Rented))
                {
                    BeginCloseLocked(entry);
                    (closing ??= []).Add(entry);
                }
            }
            _idle.Clear();
        }

        if (cancel)
        {
            try
            {
                // Outside the gate: cancellation may synchronously finish an acquisition.
#pragma warning disable CA1849 // Stop publishes _cancellationComplete only after these callbacks finish, before scheduling entry cleanup.
                _lifetimeCancellation.Cancel();
#pragma warning restore CA1849
            }
            catch (Exception error)
            {
                lock (_gate) _closeError ??= error;
            }
            finally
            {
                lock (_gate)
                {
                    _cancellationComplete = true;
                    CompleteIfDrainedLocked();
                }
            }
        }
        if (closing is not null)
        {
            foreach (var entry in closing) _ = CloseAsync(entry);
        }
        // Admission is closed and idle cleanup is running before any listener can block.
        // Borrowed operations still drain normally; callbacks run outside ownership gates.
        if (handedOff is not null)
            foreach (var connection in handedOff) connection.RecordConnectionHandoff();
        return new ValueTask(completion);
    }

    private static void BeginCloseLocked(Entry entry)
    {
        entry.State = State.Closing;
        entry.Closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private async Task CloseAsync(Entry entry)
    {
        Exception? failure = null;
        try
        {
            await entry.Connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            failure = error;
        }
        lock (_gate)
        {
            if (_stopping) _closeError ??= failure;
            _connections.Remove(entry.Connection);
            if (failure is null) entry.Closed!.TrySetResult();
            else entry.Closed!.TrySetException(failure);
            CompleteIfDrainedLocked();
        }
        if (failure is not null)
            logger?.DedicatedConnectionCloseFailed(host, port, failure);
    }

    private void CompleteIfDrainedLocked()
    {
        if (!_stopping || !_cancellationComplete || _connecting != 0 || _connections.Count != 0
            || _completion!.Task.IsCompleted) return;
        _lifetimeCancellation.Dispose();
        if (_closeError is null) _completion.TrySetResult();
        else _completion.TrySetException(_closeError);
    }
}
