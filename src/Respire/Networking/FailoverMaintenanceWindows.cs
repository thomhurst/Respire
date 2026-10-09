namespace Respire.Networking;

/// <summary>Retains bounded maintenance state across a failover member's transport replacements.</summary>
internal sealed class FailoverMaintenanceWindows
{
    private const int MaximumStates = 256;
    private readonly Lock _gate = new();
    private readonly List<MaintenanceTimeoutState> _states = [];
    private long _overflowUntil;
    private long _generation;

    internal void Observe(MaintenanceTimeoutState state, bool started)
    {
        var now = Environment.TickCount64;
        lock (_gate)
        {
            Prune(now);
            // Retain new-start overlap after completion or expiry, including zero-grace MOVING.
            // Ignored replays and completion-only notifications cannot suppress probe failures.
            if (started) _generation++;
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
