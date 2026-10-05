namespace Respire.Internal;

internal enum SentinelWorkKind { Supervisor, Monitor, Rediscovery, SourceResolution, MonitorRemoval }

// Shares the publication/disposal gate. Start never runs application or transport code
// inline, and Stop closes registration before returning the complete shutdown snapshot.
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
