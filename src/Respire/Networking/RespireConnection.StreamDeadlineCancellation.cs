namespace Respire.Networking;

internal sealed partial class RespireConnection
{
    // System.Threading.Timer accepts at most about 49.7 days. CommandTimeout has no upper bound,
    // so a longer deadline re-arms the timer in slices instead of failing to schedule.
    private const long StreamTimeoutTimerSliceMilliseconds = 30L * 24 * 60 * 60 * 1000;

    /// <summary>
    /// Cancels a streamed upload at its command deadline. The connection's deadline sweep only
    /// inspects commands published to <c>_inflight</c>, and a streamed SET is not published until
    /// its frame is queued (so a reply can never be matched to a partial frame), so each upload
    /// needs its own timer. It follows maintenance windows the same way the sweep does.
    /// </summary>
    /// <remarks>
    /// Lock order: <c>_scheduleGate</c>, then the connection's <c>_maintenancePublicationGate</c>.
    /// Maintenance publication takes only the publication gate and raises
    /// <c>MaintenanceStateChanged</c> after releasing it, so <see cref="Recheck"/> never takes the
    /// two gates in the opposite order.
    /// </remarks>
    private sealed class StreamDeadlineCancellation : IDisposable
    {
        private readonly RespireConnection _connection;
        private readonly CommandDeadline _deadline;
        private readonly CancellationTokenSource _source = new();
        private readonly Timer _timer;
        private readonly Action? _maintenanceChanged;
        private readonly Lock _scheduleGate = new();
        // Bumped by every maintenance change; a Schedule that read an older value recomputes
        // instead of installing (or acting on) a stale deadline.
        private int _version;
        private int _disposed;
        private long _effectiveTimeoutTicks;
        private long _committedTimeoutTicks = long.MinValue;

        internal StreamDeadlineCancellation(RespireConnection connection, CommandDeadline deadline)
        {
            _connection = connection;
            _deadline = deadline;
            _effectiveTimeoutTicks = connection._commandTimeout!.Value.Ticks;
            _timer = new Timer(static state => ((StreamDeadlineCancellation)state!).Schedule(),
                this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            if (connection._maintenanceOptions is not null) _maintenanceChanged = Recheck;
        }

        internal CancellationToken Token => _source.Token;

        internal bool IsCancellationRequested => _source.IsCancellationRequested;

        /// <summary>The timeout that applied when the deadline fired, including any maintenance relaxation.</summary>
        internal TimeSpan EffectiveTimeout
        {
            get
            {
                var committedTicks = Interlocked.Read(ref _committedTimeoutTicks);
                return committedTicks == long.MinValue
                    ? TimeSpan.FromTicks(Interlocked.Read(ref _effectiveTimeoutTicks))
                    : TimeSpan.FromTicks(committedTicks);
            }
        }

        internal void Start()
        {
            // A maintenance start or completion changes the effective deadline immediately.
            if (_maintenanceChanged is not null) _connection.MaintenanceStateChanged += _maintenanceChanged;
            Schedule();
        }

        // A synchronous stream implementation can block before its ReadAsync returns a Task for
        // WaitAsync to observe. Recompute when that call returns so a delayed timer callback cannot
        // let an already-expired command queue its header.
        internal void ThrowIfDue()
        {
            Schedule();
            _source.Token.ThrowIfCancellationRequested();
        }

        // Runs on the receive loop: never cancel inline (that would run caller continuations
        // there); fire the timer so Schedule recomputes the deadline on a pool thread.
        private void Recheck()
        {
            lock (_scheduleGate)
            {
                _version++;
                try { _timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan); }
                catch (ObjectDisposedException) { }
            }
        }

        private void Schedule()
        {
            while (true)
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                var version = Volatile.Read(ref _version);
                Task? cancellationCallbacks = null;
                lock (_scheduleGate)
                {
                    // A maintenance change raced this calculation; the newest state must win.
                    if (version != _version) continue;
                    lock (_connection._maintenancePublicationGate)
                    {
                        var delay = ComputeDelay(out var effectiveTimeout);
                        Interlocked.Exchange(ref _effectiveTimeoutTicks, effectiveTimeout.Ticks);
                        if (delay is { } next)
                        {
                            try { _timer.Change(next, Timeout.InfiniteTimeSpan); }
                            catch (ObjectDisposedException) { }
                            return;
                        }

                        // Commit cancellation while maintenance-state publication is excluded.
                        // CancelAsync only marks the token here; callbacks run asynchronously.
                        Interlocked.Exchange(ref _committedTimeoutTicks, effectiveTimeout.Ticks);
                        try { cancellationCallbacks = _source.CancelAsync(); }
                        catch (ObjectDisposedException) { }
                    }
                }

                ObserveCancellationCallbacks(cancellationCallbacks);
                return;
            }
        }

        // Null once the effective deadline has passed.
        private TimeSpan? ComputeDelay(out TimeSpan effectiveTimeout)
        {
            // The capacity wait shares this computation, and the deadline sweep applies the same
            // MaintenanceTimeoutState rule: an active maintenance window relaxes the deadline
            // measured from the command's original start, and its end restores it.
            var remaining = _connection.RemainingUntilCommandDeadline(_deadline.Ticks, Environment.TickCount64,
                out effectiveTimeout, out var window, _deadline.IsRelaxed);
            if (remaining <= 0) return null;

            var sleep = Math.Min(remaining, StreamTimeoutTimerSliceMilliseconds);
            // Recheck when the window closes so a restored, shorter deadline is enforced.
            if (window > 0) sleep = Math.Min(sleep, window);
            return TimeSpan.FromMilliseconds(sleep);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (_maintenanceChanged is not null) _connection.MaintenanceStateChanged -= _maintenanceChanged;
            _timer.Dispose();
            _source.Dispose();
        }
    }
}
