namespace Respire.Networking;

/// <summary>Retains bounded maintenance state across a failover member's transport replacements.</summary>
internal sealed class FailoverMaintenanceWindows
{
    private const int MaximumStates = 256;
    private readonly Lock _gate = new();
    private readonly List<MaintenanceTimeoutState> _states = [];
    private long _overflowUntil;
    private long _generation;

    internal void Observe(MaintenanceTimeoutState state)
    {
        var now = Environment.TickCount64;
        lock (_gate)
        {
            Prune(now);
            // Record every applied notification, including a zero-grace MOVING handoff that
            // never opens a window, and retain overlap history after completion or expiry.
            _generation++;
            if (state.GetWindow(now) is not { } window) return;
            if (_states.Contains(state)) return;
            if (_states.Count < MaximumStates) _states.Add(state);
            // At capacity, retain only a finite expiry rather than another socket's state.
            else _overflowUntil = Math.Max(_overflowUntil, window.Expires);
        }
    }

    internal long Generation { get { lock (_gate) return _generation; } }

    internal bool IsActive
    {
        get
        {
            lock (_gate)
            {
                var now = Environment.TickCount64;
                Prune(now);
                return _states.Count != 0 || _overflowUntil > now;
            }
        }
    }

    private void Prune(long now)
    {
        for (var index = _states.Count - 1; index >= 0; index--)
            if (_states[index].GetWindow(now) is null) _states.RemoveAt(index);
    }
}
