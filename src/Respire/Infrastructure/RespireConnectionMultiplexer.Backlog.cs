using Respire.Networking;

namespace Respire.Infrastructure;

internal sealed partial class RespireConnectionMultiplexer
{
    private ReconnectBacklog? _backlog;
    private TaskCompletionSource? _backlogConnectionChanged;

    internal bool IsBacklogDraining => Volatile.Read(ref _backlog)?.IsRunning == true;

    // Dormant until a dispatch owner opts in. One queue per endpoint preserves arrival
    // order across command kinds. The owner must carry its original deadline in the token
    // and keep all commands arriving during a drain on this same queue.
    internal ReconnectBacklog GetReconnectBacklog(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        lock (_lifecycleGate)
        {
            ThrowIfUnavailable();
            if (!_connected) throw new InvalidOperationException("Initialize the multiplexer before using its reconnect backlog.");
            if (_backlog is null) _backlog = new ReconnectBacklog(this, capacity);
            else if (_backlog.Capacity != capacity)
                throw new InvalidOperationException("A multiplexer has one fixed reconnect backlog capacity.");
            return _backlog;
        }
    }

    private void SignalBacklogConnectionChanged()
    {
        lock (_lifecycleGate)
        {
            _backlogConnectionChanged?.TrySetResult();
            _backlogConnectionChanged = null;
        }
    }

    private async ValueTask<RespireConnection> WaitForBacklogConnectionAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task changed;
            // Capture before selection: a replacement can publish synchronously while
            // GetConnection schedules it. Capturing afterwards would lose that wakeup.
            lock (_lifecycleGate)
                changed = (_backlogConnectionChanged ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            try { return GetConnection(); }
            catch (RespireConnectionException error) when (error is not RespireReconnectLimitException)
            {
                await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
                ThrowIfUnavailable();
                ThrowIfRecoveryExhausted();
                // Null/zero-delay policies can fail synchronously (for example connection
                // refused). Bound the on-demand retry rate while the queue owns demand.
                if (!IsConnected) await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Bounded FIFO for unsent units. A unit owns the drain until its callback completes;
    /// this deliberately serializes cold-path execution, including asynchronous writes.
    /// Once dispatched, its outcome belongs to that callback and is never replayed here.
    /// </summary>
    internal sealed class ReconnectBacklog
    {
        private readonly RespireConnectionMultiplexer _owner;
        private readonly Func<CancellationToken, ValueTask<RespireConnection>> _waitForConnection;
        private readonly Lock _gate = new();
        private readonly LinkedList<Entry> _pending = new();
        private volatile bool _running;
        private Exception? _stopped;

        internal int Capacity { get; }
        // Includes the head waiting for recovery, excludes the dispatched unit.
        internal int Count { get { lock (_gate) return _pending.Count; } }
        internal bool IsRunning => _running;

        internal ReconnectBacklog(RespireConnectionMultiplexer owner, int capacity,
            Func<CancellationToken, ValueTask<RespireConnection>>? waitForConnection = null)
        {
            _owner = owner;
            _waitForConnection = waitForConnection ?? owner.WaitForBacklogConnectionAsync;
            Capacity = capacity;
            // The source and queue share the multiplexer's lifetime; cancellation removes
            // the registration. No per-command registration against the owner is retained.
            owner._stopConnecting.Token.UnsafeRegister(static state => ((ReconnectBacklog)state!).Stop(), this);
        }

        internal async ValueTask<T> EnqueueAsync<T>(
            Func<RespireConnection, CancellationToken, ValueTask<T>> dispatch,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(dispatch);
            cancellationToken.ThrowIfCancellationRequested();
            var entry = new Entry<T>(dispatch, cancellationToken);
            bool start;
            lock (_gate)
            {
                if (_stopped is { } stopped) throw stopped;
                _owner.ThrowIfUnavailable();
                if (_pending.Count >= Capacity)
                    throw new RespireConnectionException($"Reconnect backlog capacity {Capacity} reached for {_owner.ActiveConnectionEndpoint}.")
                    { Endpoint = _owner.ActiveConnectionEndpoint };
                entry.Node = _pending.AddLast(entry);
                start = !_running;
                _running = true;
            }
            using var registration = cancellationToken.UnsafeRegister(
                static state => { var (queue, pending) = ((ReconnectBacklog, Entry))state!; queue.Cancel(pending); },
                (this, (Entry)entry));
            if (start) _ = DrainAsync();
            return await entry.Completion.Task.ConfigureAwait(false);
        }

        private void Cancel(Entry entry)
        {
            lock (_gate)
            {
                if (entry.Node is null) return; // Dispatch now owns cancellation and outcome.
                _pending.Remove(entry.Node);
                entry.Node = null;
                entry.Cancel();
            }
        }

        private void Stop()
        {
            Exception error;
            try { _owner.ThrowIfUnavailable(); error = new RespireConnectionException("Connection recovery stopped."); }
            catch (Exception unavailable) { error = unavailable; }
            lock (_gate)
            {
                _stopped = error;
                FailPendingLocked(error);
            }
        }

        private void FailPendingLocked(Exception error)
        {
            while (_pending.First is { } node)
            {
                _pending.RemoveFirst();
                node.Value.Node = null;
                node.Value.Fail(error);
            }
        }

        private async Task DrainAsync()
        {
            while (true)
            {
                Entry entry;
                lock (_gate)
                {
                    if (_pending.First is not { } first) { _running = false; return; }
                    entry = first.Value;
                }
                try
                {
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(entry.Token, _owner._stopConnecting.Token);
                    var connection = await _waitForConnection(wait.Token).ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (entry.Node is null) continue;
                        _pending.Remove(entry.Node);
                        entry.Node = null;
                        if (entry.Token.IsCancellationRequested) { entry.Cancel(); continue; }
                    }
                    // No queue or lifecycle lock surrounds application/transport code.
                    // ExecuteAsync captures its own errors, so an accepted failure cannot
                    // be mistaken for a connection-selection failure or replayed.
                    await entry.ExecuteAsync(connection).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (entry.Token.IsCancellationRequested)
                {
                    Cancel(entry);
                }
                catch (OperationCanceledException) when (_owner._stopConnecting.IsCancellationRequested)
                {
                    Stop();
                }
                catch (RespireReconnectLimitException error)
                {
                    lock (_gate) FailPendingLocked(error);
                }
                catch (Exception error)
                {
                    // An unexpected selection failure belongs to this waiter. Later
                    // entries may still recover; only terminal exhaustion fails them all.
                    lock (_gate)
                    {
                        if (entry.Node is null) continue;
                        _pending.Remove(entry.Node);
                        entry.Node = null;
                        entry.Fail(error);
                    }
                }
            }
        }

        private abstract class Entry(CancellationToken token)
        {
            internal CancellationToken Token { get; } = token;
            internal LinkedListNode<Entry>? Node { get; set; }
            internal abstract void Cancel();
            internal abstract void Fail(Exception error);
            internal abstract ValueTask ExecuteAsync(RespireConnection connection);
        }

        private sealed class Entry<T>(Func<RespireConnection, CancellationToken, ValueTask<T>> dispatch,
            CancellationToken token) : Entry(token)
        {
            internal TaskCompletionSource<T> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal override void Cancel() => Completion.TrySetCanceled(Token);
            internal override void Fail(Exception error) => Completion.TrySetException(error);
            internal override async ValueTask ExecuteAsync(RespireConnection connection)
            {
                try { Completion.TrySetResult(await dispatch(connection, Token).ConfigureAwait(false)); }
                catch (OperationCanceledException error) when (error.CancellationToken == Token && Token.IsCancellationRequested)
                { Cancel(); }
                catch (Exception error) { Fail(error); }
            }
        }
    }
}
