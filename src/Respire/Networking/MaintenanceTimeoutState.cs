namespace Respire.Networking;

/// <summary>Cold, bounded maintenance windows. The command enqueue path never touches this state.</summary>
internal sealed class MaintenanceTimeoutState(long maximumWindowMilliseconds)
{
    private const int MaximumOperations = 256;
    private readonly object _gate = new();
    private readonly Dictionary<(string Family, long Sequence), Window> _windows = [];
    private long _overflowUntil;
    private readonly record struct Window(long Expires, bool Completed);

    internal void Apply(MaintenanceNotification notification, long now)
    {
        lock (_gate)
        {
            var key = (notification.Family, notification.SequenceId);
            if (_windows.TryGetValue(key, out var existing))
            {
                if (notification.IsCompletion) _windows[key] = existing with { Completed = true };
                return; // Duplicate starts cannot extend an existing window or reopen a completion.
            }
            var duration = notification.Kind == "MOVING"
                ? Math.Min(maximumWindowMilliseconds, Math.Min(notification.Seconds!.Value, maximumWindowMilliseconds / 1000 + 1) * 1000)
                : maximumWindowMilliseconds;
            if (_windows.Count == MaximumOperations)
            {
                // Retain recent completed/expired identities so replay cannot reopen them.
                // Evict only under pressure, preferring the oldest finished operation.
                foreach (var pair in _windows)
                {
                    if (pair.Value.Expires > now && !pair.Value.Completed) continue;
                    _windows.Remove(pair.Key);
                    break;
                }
            }
            if (_windows.Count == MaximumOperations)
            {
                // Keep memory bounded under a burst of unique operations. Conservatively retain
                // relaxation until this bounded window expires; no unbounded identity history.
                if (!notification.IsCompletion) _overflowUntil = Math.Max(_overflowUntil, now + duration);
                return;
            }
            _windows.Add(key, new(now + duration, notification.IsCompletion));
        }
    }

    internal long Remaining(long now)
    {
        lock (_gate)
        {
            var expires = _overflowUntil;
            foreach (var window in _windows.Values)
                if (!window.Completed) expires = Math.Max(expires, window.Expires);
            return Math.Max(0, expires - now);
        }
    }
}
