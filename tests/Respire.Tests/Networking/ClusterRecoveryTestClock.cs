namespace Respire.Tests.Networking;

/// <summary>Advances deadlines only when the test has observed the relevant network operation.</summary>
internal sealed class ClusterRecoveryTestClock : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() { lock (_gate) return _ticks; }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_gate)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }
    }

    internal void Advance(TimeSpan elapsed)
    {
        ManualTimer[] due;
        lock (_gate)
        {
            _ticks += elapsed.Ticks;
            // Keep registration order: when both deadlines have elapsed, the round
            // cancels first so a resumed phase cannot race an undelivered round timer.
            due = _timers.Where(timer => timer.Due <= _ticks).ToArray();
            foreach (var timer in due) _timers.Remove(timer);
        }
        foreach (var timer in due) timer.Fire();
    }

    private sealed class ManualTimer(ClusterRecoveryTestClock clock, TimerCallback callback, object? state) : ITimer
    {
        private int _disposed;
        internal long Due { get; private set; }
        internal void Fire() { if (Volatile.Read(ref _disposed) == 0) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            // Recovery uses one-shot cancellation timers. Reject unsupported periodic use
            // explicitly instead of silently simulating a different timer contract.
            if (period != Timeout.InfiniteTimeSpan && period != TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(period));
            if (dueTime < TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan)
                throw new ArgumentOutOfRangeException(nameof(dueTime));
            lock (clock._gate)
            {
                if (Volatile.Read(ref _disposed) != 0) return false;
                clock._timers.Remove(this);
                if (dueTime == Timeout.InfiniteTimeSpan) return true;
                Due = clock._ticks + dueTime.Ticks;
                clock._timers.Add(this);
                return true;
            }
        }
        public void Dispose()
        {
            Interlocked.Exchange(ref _disposed, 1);
            lock (clock._gate) clock._timers.Remove(this);
        }
        public ValueTask DisposeAsync() { Dispose(); return default; }
    }
}
