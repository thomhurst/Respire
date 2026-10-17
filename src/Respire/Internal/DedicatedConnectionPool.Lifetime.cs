namespace Respire.Internal;

internal sealed partial class DedicatedConnectionPool
{
    // Only idle selection/return/sweeps inspect age. A borrowed blocking command or upload
    // can run beyond its lifetime, and lifetime expiration never cancels that lease.
    private static bool MarkLifetimeExpired(Entry entry)
    {
        if (!entry.Connection.IsConnectionLifetimeExpired) return false;
        entry.LifetimeExpired = true;
        return true;
    }

    private void ScheduleIdleLifetimeLocked()
    {
        if (options.ConnectionMaxLifetime is null || _stopping) return;
        var due = TimeSpan.FromDays(1);
        foreach (var entry in _idle)
            if (entry.Connection.RemainingConnectionLifetime is { } remaining && remaining < due) due = remaining;
        if (_idle.Count == 0) due = Timeout.InfiniteTimeSpan;
        else if (due <= TimeSpan.Zero) due = TimeSpan.FromMilliseconds(1);
        if (_connectionLifetimeTimer is null)
            _connectionLifetimeTimer = options.ConnectionLifetimeTimeProvider.CreateTimer(
                static state => ((DedicatedConnectionPool)state!).RetireExpiredIdleConnections(), this,
                due, Timeout.InfiniteTimeSpan);
        else _connectionLifetimeTimer.Change(due, Timeout.InfiniteTimeSpan);
    }

    private void RetireExpiredIdleConnections()
    {
        List<Entry>? closing = null;
        lock (_gate)
        {
            if (_stopping) return;
            for (var index = _idle.Count - 1; index >= 0; index--)
            {
                var entry = _idle[index];
                if (!MarkLifetimeExpired(entry)) continue;
                _idle.RemoveAt(index);
                BeginCloseLocked(entry);
                (closing ??= []).Add(entry);
            }
            ScheduleIdleLifetimeLocked();
            PauseIdleTimerIfEmptyLocked();
        }
        if (closing is not null)
            foreach (var entry in closing) _ = CloseAsync(entry);
    }
}
