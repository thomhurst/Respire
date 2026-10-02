namespace Respire.Internal;

/// <summary>Shares unknown-slot probes without treating another slot's partial reply as coverage.</summary>
internal sealed class ClusterReplicaDiscovery(
    Func<int, Task> refresh, Func<int, bool> hasCoverage, Func<long>? clock = null)
{
    private readonly object _gate = new();
    // Only timestamps scale with uncovered slots; there is one in-flight task per router.
    private readonly Dictionary<int, long> _notBefore = new();
    private Task<int>? _current;
    private int _currentSlot;
    private bool _invalidated;

    internal async ValueTask DiscoverAsync(int slot, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task<int> current;
            TaskCompletionSource<int>? start = null;
            lock (_gate)
            {
                if (hasCoverage(slot))
                {
                    _notBefore.Remove(slot);
                    return;
                }
                if (_current is { IsCompleted: false } pending) current = pending;
                else
                {
                    var now = clock?.Invoke() ?? Environment.TickCount64;
                    if (_notBefore.TryGetValue(slot, out var next) && now < next) return;
                    _notBefore[slot] = now + ClusterReplicaSet.RefreshIntervalMilliseconds;
                    start = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    current = _current = start.Task;
                    _currentSlot = slot;
                    _invalidated = false;
                }
            }
            if (start is not null) _ = RunAsync(slot, start);
            // Caller cancellation only detaches this waiter. The router owns probe cancellation.
            var attemptedSlot = await current.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (attemptedSlot == slot) return;
            // Recheck this slot after shared work. A partial reply for another slot neither
            // completes this discovery nor consumes this slot's refresh interval.
        }
    }

    internal void Invalidate(int slot)
    {
        lock (_gate)
        {
            _notBefore.Remove(slot);
            if (_currentSlot == slot && _current is { IsCompleted: false }) _invalidated = true;
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

    private async Task RunAsync(int slot, TaskCompletionSource<int> completion)
    {
        try { await refresh(slot).ConfigureAwait(false); }
        catch { /* The router logs probe failures; callers inspect their slot's routes. */ }
        finally
        {
            lock (_gate)
            {
                // A changed owner makes the old probe insufficient even for its target slot.
                completion.TrySetResult(_invalidated ? -1 : slot);
            }
        }
    }
}
