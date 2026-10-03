namespace Respire.Tests.Networking;

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
            var timer = new ManualTimer(this, callback, state, _ticks + dueTime.Ticks);
            _timers.Add(timer);
            return timer;
        }
    }

    internal void Advance(TimeSpan elapsed)
    {
        ManualTimer[] due;
        lock (_gate)
        {
            _ticks += elapsed.Ticks;
            due = _timers.Where(timer => timer.Due <= _ticks).OrderBy(timer => timer.Due).ToArray();
            foreach (var timer in due) _timers.Remove(timer);
        }
        foreach (var timer in due) timer.Fire();
    }

    private sealed class ManualTimer(ClusterRecoveryTestClock clock, TimerCallback callback, object? state, long due) : ITimer
    {
        private int _disposed;
        internal long Due { get; private set; } = due;
        internal void Fire() { if (Interlocked.Exchange(ref _disposed, 1) == 0) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate)
            {
                if (Volatile.Read(ref _disposed) != 0) return false;
                Due = clock._ticks + dueTime.Ticks;
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
