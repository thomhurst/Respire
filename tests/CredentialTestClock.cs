namespace Respire.Testing;

internal sealed class CredentialTestClock : TimeProvider
{
    private readonly object _gate = new();
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly List<Timer> _timers = [];

    public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_gate)
        {
            var timer = new Timer(this, callback, state, _now + dueTime);
            _timers.Add(timer);
            return timer;
        }
    }

    public bool HasDelay(TimeSpan delay)
    {
        lock (_gate) return _timers.Any(timer => timer.Due == _now + delay);
    }

    public void Advance(TimeSpan duration)
    {
        Timer[] due;
        lock (_gate)
        {
            _now += duration;
            due = _timers.Where(timer => timer.Due <= _now).ToArray();
            foreach (var timer in due) _timers.Remove(timer);
        }
        foreach (var timer in due) timer.Fire();
    }

    private sealed class Timer(CredentialTestClock clock, TimerCallback callback, object? state, DateTimeOffset due) : ITimer
    {
        public DateTimeOffset Due { get; private set; } = due;
        public void Fire() => callback(state);
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate) Due = clock._now + dueTime;
            return true;
        }
        public void Dispose() { lock (clock._gate) clock._timers.Remove(this); }
        public ValueTask DisposeAsync() { Dispose(); return default; }
    }
}
