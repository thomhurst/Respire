namespace Respire.Internal;

internal enum SentinelWorkKind { Supervisor, Monitor, Rediscovery, SourceResolution, MonitorRemoval }

// Shares the publication/disposal gate. Start never runs application or transport code
// inline, and Stop closes registration before returning the complete shutdown snapshot.
internal sealed class SentinelBackgroundWork(object gate)
{
    private readonly object _gate = gate;
    private readonly Dictionary<Task, SentinelWorkKind> _tasks = [];
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
                    // Retain and observe failures until disposal can report all of them.
                    // Successful work must not accumulate for the client's lifetime.
                    if (completed.Exception is null) owner._tasks.Remove(completed);
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
            return _tasks.Keys.ToArray();
        }
    }
}
