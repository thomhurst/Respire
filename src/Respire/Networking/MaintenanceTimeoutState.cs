namespace Respire.Networking;

/// <summary>Cold, bounded maintenance windows. The command enqueue path never touches this state.</summary>
internal sealed class MaintenanceTimeoutState(long maximumWindowMilliseconds)
{
    private const int MaximumOperations = 256;
    private readonly object _gate = new();
    private readonly Dictionary<(string Family, long Sequence), Window> _windows = [];
    // Insertion order, so capacity eviction removes the oldest finished identity deterministically.
    private readonly List<(string Family, long Sequence)> _order = new(MaximumOperations);
    private long _overflowUntil;
    private ActiveWindow? _active;
    private readonly record struct Window(long Expires, bool Completed);
    internal sealed record ActiveWindow(long Started, long Expires);

    internal void Apply(MaintenanceNotification notification, long now)
    {
        lock (_gate)
        {
            ApplyUnderLock(notification, now);
            var expires = _overflowUntil;
            foreach (var window in _windows.Values)
                if (!window.Completed) expires = Math.Max(expires, window.Expires);
            // Preserve the cutoff across overlapping operations, but never across a gap.
            // Publish the pair atomically; deadline readers neither lock nor scan the history.
            var active = expires > now
                ? new ActiveWindow(_active is { } previous && previous.Expires > now ? previous.Started : now, expires)
                : null;
            Volatile.Write(ref _active, active);
        }
    }

    private void ApplyUnderLock(MaintenanceNotification notification, long now)
    {
        var key = (notification.Family, notification.SequenceId);
        if (_windows.TryGetValue(key, out var existing))
        {
            if (notification.IsCompletion) _windows[key] = existing with { Completed = true };
            return; // Duplicate starts cannot extend an existing window or reopen a completion.
        }
        // Parse supplies grace for MOVING; an internally constructed incomplete value must
        // not accidentally acquire the maximum relaxation window.
        var duration = notification.Kind == "MOVING"
            ? Math.Min(maximumWindowMilliseconds, Math.Min(notification.Seconds.GetValueOrDefault(), maximumWindowMilliseconds / 1000 + 1) * 1000)
            : maximumWindowMilliseconds;
        if (_windows.Count == MaximumOperations)
        {
            // Retain completed/expired identities until capacity pressure requires eviction,
            // then evict the oldest finished one so recent identities keep replay suppression.
            for (var i = 0; i < _order.Count; i++)
            {
                var candidate = _order[i];
                var window = _windows[candidate];
                if (window.Expires > now && !window.Completed) continue;
                _windows.Remove(candidate);
                _order.RemoveAt(i);
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
        _order.Add(key);
    }

    internal ActiveWindow? GetWindow(long now)
        => Volatile.Read(ref _active) is { } active && active.Expires > now ? active : null;

    internal long Remaining(long now) => GetWindow(now) is { } active ? active.Expires - now : 0;
}
