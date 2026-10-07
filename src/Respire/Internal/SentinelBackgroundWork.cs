namespace Respire.Internal;

internal enum SentinelWorkKind { Supervisor, Monitor, Rediscovery, SourceResolution, MonitorRemoval }

/// <summary>
/// Shares the publication/disposal gate. Start never runs application or transport code
/// inline, and Stop closes registration before returning the complete shutdown snapshot.
/// </summary>
/// <remarks>
/// <list type="table">
/// <listheader><term>Work</term><description>Owner and shutdown contract</description></listheader>
/// <item><term>Monitor supervisor, endpoint monitors, removed-monitor cleanup</term><description>Registered here
/// under the router gate; <see cref="SentinelMonitoring"/> owns the subscription resources. Removed endpoints get
/// individual cancellation; their cleanup stays registered until it completes.</description></item>
/// <item><term>Notification rediscovery, switch-source DNS</term><description>Registered here while the router keeps
/// generation-sensitive evidence. Router disposal sets <c>_disposed</c> and calls <see cref="Stop"/> atomically, then
/// cancels <c>_lifetime</c>. All tasks and cancellation callbacks share one ten-second shutdown bound; a straggler
/// must recheck disposal and its endpoint cancellation before publication or retirement.</description></item>
/// <item><term>Generation retirement, correction-fence drainage</term><description>Not registered here: each owned
/// generation keeps its retirement task. Disposal starts cleanup for every owned connection/pool, then joins
/// retirement and propagates aggregated failures. Failed cleanup stays owned until disposal.</description></item>
/// <item><term>State/health observer callbacks</term><description>Not registered here: serialized on the router's
/// <c>_notifications</c> chain outside publication locks and never joined, because an observer may synchronously
/// dispose the client. Pending application callbacks are suppressed after disposal; explicitly retained telemetry
/// callbacks may still run.</description></item>
/// </list>
/// <para>
/// Successful tasks are released. The last <see cref="MaximumRetainedFailuresPerKind"/> completed failures per
/// <see cref="SentinelWorkKind"/> are kept for aggregate shutdown reporting, with a count of omitted earlier
/// failures. Active tasks are never evicted by this limit. A timed-out shutdown join still observes late faults.
/// </para>
/// </remarks>
internal sealed class SentinelBackgroundWork(Lock gate)
{
    internal const int MaximumRetainedFailuresPerKind = 8;
    private readonly Lock _gate = gate;
    private readonly Dictionary<Task, SentinelWorkKind> _tasks = [];
    private readonly Dictionary<SentinelWorkKind, Queue<Task>> _failures = [];
    private long _discardedFailures;
    private bool _stopped;

    internal Task? TryStart(SentinelWorkKind kind, Func<Task> action)
    {
        lock (_gate)
        {
            if (_stopped) return null;
            var task = Task.Run(action);
            _tasks.Add(task, kind);
            _ = task.ContinueWith(static (completed, state) =>
            {
                var owner = (SentinelBackgroundWork)state!;
                lock (owner._gate)
                {
                    // Observe every failure, retaining bounded evidence for shutdown.
                    // Active work remains owned regardless of the completed history cap.
                    if (completed.Exception is null)
                    {
                        owner._tasks.Remove(completed);
                        return;
                    }
                    var kind = owner._tasks[completed];
                    if (!owner._failures.TryGetValue(kind, out var failures))
                        owner._failures.Add(kind, failures = new(MaximumRetainedFailuresPerKind));
                    if (failures.Count == MaximumRetainedFailuresPerKind)
                    {
                        owner._tasks.Remove(failures.Dequeue());
                        owner._discardedFailures++;
                    }
                    failures.Enqueue(completed);
                }
            }, this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
    }

    internal int Count(SentinelWorkKind kind)
    {
        lock (_gate) return _tasks.Count(pair => pair.Value == kind && !pair.Key.IsCompleted);
    }

    internal Task[] Stop()
    {
        lock (_gate)
        {
            _stopped = true;
            if (_discardedFailures == 0) return _tasks.Keys.ToArray();
            return [.. _tasks.Keys, Task.FromException(new InvalidOperationException(
                $"{_discardedFailures} earlier Sentinel background failures were omitted from bounded shutdown history; "
                + $"retaining up to {MaximumRetainedFailuresPerKind} per work kind."))];
        }
    }
}
