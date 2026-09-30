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

    private readonly object _gate = new();
    private readonly Stack<Entry> _idle = new(MaxIdle);
    // Keep closing entries registered until socket and receive/flush cleanup actually completes.
    private readonly Dictionary<RespireConnection, Entry> _connections = [];
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private TaskCompletionSource? _completion;
    private Exception? _closeError;
    private int _connecting;
    private bool _stopping;
    private bool _cancellationComplete;

    internal bool IsStopping
    {
        get { lock (_gate) return _stopping; }
    }

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

    private sealed class Entry(RespireConnection connection)
    {
        internal readonly RespireConnection Connection = connection;
        internal State State = State.Rented;
        internal TaskCompletionSource? Closed;
    }

    public async ValueTask<RespireConnection> RentAsync(
        CancellationToken cancellationToken, bool armHandshakeDeadline = true, bool reuseIdle = true)
    {
        while (true)
        {
            Entry stale;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_stopping, this);
                if (reuseIdle && _idle.TryPop(out var entry))
                {
                    if (entry.Connection.IsConnected)
                    {
                        entry.State = State.Rented;
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

        try
        {
            using var connectCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _lifetimeCancellation.Token);
            RespireConnection connection;
            try
            {
                connection = await RespireConnection.ConnectAsync(
                    host, port, options, logger, connectCancellation.Token, armHandshakeDeadline).ConfigureAwait(false);
            }
            catch (OperationCanceledException error) when (CommandTimeoutCancellation.IsFromLinkedToken(
                error, cancellationToken, connectCancellation.Token))
            {
                // Unwrap only our own lifetime link, preserving independent retirement cancellation.
                throw new OperationCanceledException(error.Message, error, cancellationToken);
            }
            var entry = new Entry(connection);
            lock (_gate)
            {
                _connections.Add(connection, entry);
                if (!_stopping) return connection;
                BeginCloseLocked(entry);
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

    /// <summary>Returns a healthy connection for reuse; anything else (or overflow) is closed.</summary>
    public void Return(RespireConnection connection)
    {
        Entry entry;
        lock (_gate)
        {
            if (!_connections.TryGetValue(connection, out entry!) || entry.State != State.Rented) return;
            if (!_stopping && connection.IsConnected && _idle.Count < MaxIdle)
            {
                entry.State = State.Idle;
                _idle.Push(entry);
                return;
            }
            BeginCloseLocked(entry);
        }
        _ = CloseAsync(entry);
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
    internal ValueTask RetireAsync() => Stop(abortBorrowed: false);

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
            logger?.LogWarning(error, "Failed to dispose a dedicated pool for {Host}:{Port}", host, port);
        }
    }

    private ValueTask Stop(bool abortBorrowed)
    {
        List<Entry>? closing = null;
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
                _lifetimeCancellation.Cancel();
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
            logger?.LogWarning(failure, "Failed to close a dedicated connection to {Host}:{Port}", host, port);
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
