namespace Respire.Internal;

/// <summary>Shares unknown-slot probes without treating another slot's partial reply as coverage.</summary>
/// <remarks>
/// The coverage callback runs under this coordinator's gate and must remain a lock-free read.
/// Topology publication takes the router node gate before calling ForgetCoveredSlots, so taking
/// that node gate from the callback would invert the lock order.
/// </remarks>
internal sealed class ClusterReplicaDiscovery(
    Func<int, Task> refresh, Func<int, bool> hasCoverage, Func<long>? clock = null)
{
    private readonly Lock _gate = new();
    // Only value-type throttle/attempt records scale with uncovered slots; one probe runs per router.
    private readonly Dictionary<int, (long NotBefore, long Version)> _notBefore = new();
    private (int Slot, Task<(int Slot, long Version)>? Completion) _current;
    private long _nextVersion;

    // Deterministic completion/interleaving seam for the coordinator's tests.
    internal Lock TestingGate => _gate;
    internal Task? TestingCurrentProbe => _current.Completion;

    internal async ValueTask<long> DiscoverAsync(int slot, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task<(int Slot, long Version)> current;
            TaskCompletionSource<(int Slot, long Version)>? start = null;
            long version = 0;
            lock (_gate)
            {
                if (hasCoverage(slot))
                {
                    _notBefore.Remove(slot);
                    return 0;
                }
                var pending = _current.Completion is { IsCompleted: false };
                var now = clock?.Invoke() ?? Environment.TickCount64;
                // A completed attempt remains usable while another slot probes. The current
                // slot must still join its own pending probe, whose throttle starts at launch.
                if ((!pending || _current.Slot != slot)
                    && _notBefore.TryGetValue(slot, out var next) && now < next.NotBefore) return next.Version;
                if (pending) current = _current.Completion!;
                else
                {
                    version = ++_nextVersion;
                    _notBefore[slot] = (now + ClusterReplicaSet.RefreshIntervalMilliseconds, version);
                    start = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    current = start.Task;
                    _current = (slot, current);
                }
            }
            if (start is not null) _ = RunAsync(slot, version, start);
            // Caller cancellation only detaches this waiter. The router owns probe cancellation.
            var attempt = await current.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                // Another slot starting a probe does not invalidate this completed attempt.
                if (attempt.Slot == slot && IsCurrentLocked(slot, attempt.Version)) return attempt.Version;
            }
            // Recheck this slot after shared work. A partial reply for another slot neither
            // completes this discovery nor consumes this slot's refresh interval.
            // Persistently uncovered distinct slots intentionally probe sequentially: each
            // needs its own coverage attempt, at the cost of one probe round per missing slot.
        }
    }

    internal bool IsCurrent(int slot, long version)
    {
        lock (_gate) return IsCurrentLocked(slot, version);
    }

    private bool IsCurrentLocked(int slot, long version)
        => version != 0 && _notBefore.TryGetValue(slot, out var attempt) && attempt.Version == version;

    internal void Invalidate(int slot)
    {
        lock (_gate)
        {
            _notBefore.Remove(slot);
        }
    }

    internal void ForgetCoveredSlots()
    {
        lock (_gate)
        {
            // Dictionary permits removal during enumeration on the supported runtimes.
            foreach (var slot in _notBefore.Keys)
                if (hasCoverage(slot)) _notBefore.Remove(slot);
        }
    }

    private async Task RunAsync(int slot, long version, TaskCompletionSource<(int Slot, long Version)> completion)
    {
        try { await refresh(slot).ConfigureAwait(false); }
        catch
        {
            // RefreshReplicaRoutesAsync reports failed rounds through its rate-limited warning
            // (excluding expected router disposal). Waiters report unavailable routes themselves.
        }
        finally
        {
            completion.TrySetResult((slot, version));
        }
    }
}
