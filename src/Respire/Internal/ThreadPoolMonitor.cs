using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace Respire.Internal;

// One dedicated sampling thread per process, independent of the pool it observes. At most
// one probe is queued, even during a prolonged stall. Commands never touch this monitor.
internal sealed class ThreadPoolMonitor
{
    private static readonly object Gate = new();
    private static readonly Probe SharedProbe = new();
    private static ThreadPoolMonitor? _current;
    private static RespireThreadPoolSnapshot? _latest;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan WarningInterval = TimeSpan.FromSeconds(30);
    private readonly object _stopGate = new();
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly Thread _thread;
    private Lease[] _leases = [];
    private bool _stopDisposed;

    private ThreadPoolMonitor()
        => _thread = new Thread(Run) { IsBackground = true, Name = "Respire thread-pool probe" };

    internal static RespireThreadPoolSnapshot? Latest => Volatile.Read(ref _latest);

    internal static IDisposable Acquire(ILogger? logger, TimeSpan warningThreshold)
    {
        // Ensure observable instruments exist even before the first Redis command.
        _ = RespireTelemetry.ThreadPoolSchedulingDelay;
        lock (Gate)
        {
            var start = _current is null;
            var monitor = _current ??= new ThreadPoolMonitor();
            var lease = new Lease(monitor, logger, warningThreshold);
            monitor._leases = [.. monitor._leases, lease];
            if (start)
            {
                try
                {
                    // This process-wide thread must not retain the creating request's AsyncLocals.
                    if (ExecutionContext.IsFlowSuppressed()) monitor._thread.Start();
                    else
                    {
                        using (ExecutionContext.SuppressFlow()) monitor._thread.Start();
                    }
                }
                catch
                {
                    _current = null;
                    monitor._leases = [];
                    monitor._stop.Dispose();
                    throw;
                }
            }
            return lease;
        }
    }

    private void QueueProbe()
    {
        lock (Gate)
        {
            if (ReferenceEquals(_current, this)) SharedProbe.QueueIfCompleted();
        }
    }

    // Keep the queued item independent of monitor lifetimes. Repeated client creation and
    // disposal during starvation must not accumulate abandoned work items in the pool.
    private sealed class Probe : IThreadPoolWorkItem
    {
        // Reads and requeue are serialized by Gate; only the current sampler can requeue.
        internal long QueuedAt { get; private set; }
        private long _completedAt;
        internal long CompletedAt => Volatile.Read(ref _completedAt);

        internal void QueueIfCompleted()
        {
            // Preserve the original timestamp across monitor restarts while this work
            // item is pending: resetting it would hide the actual scheduling delay.
            if (QueuedAt != 0 && CompletedAt == 0) return;
            QueuedAt = Stopwatch.GetTimestamp();
            Volatile.Write(ref _completedAt, 0);
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
        }

        public void Execute() => Volatile.Write(ref _completedAt, Stopwatch.GetTimestamp());
    }

    private void Run()
    {
        try
        {
            QueueProbe();
            while (!_stop.Wait(Interval))
            {
                ThreadPool.GetAvailableThreads(out var available, out _);
                ThreadPool.GetMaxThreads(out var maximum, out _);
                ThreadPool.GetMinThreads(out var minimum, out _);
                long now;
                long completed;
                RespireThreadPoolSnapshot sample;
                Lease[] observers;
                lock (Gate)
                {
                    // A replacement sampler can start before this thread exits; only
                    // the current instance may publish a sample or requeue the probe.
                    if (!ReferenceEquals(_current, this)) return;
                    now = Stopwatch.GetTimestamp();
                    completed = SharedProbe.CompletedAt;
                    sample = new RespireThreadPoolSnapshot(DateTimeOffset.UtcNow,
                        Stopwatch.GetElapsedTime(SharedProbe.QueuedAt, completed == 0 ? now : completed), completed == 0,
                        Math.Max(0, maximum - available), minimum, ThreadPool.PendingWorkItemCount);
                    Volatile.Write(ref _latest, sample);
                    observers = _leases;
                }
                foreach (var observer in observers) observer.Observe(sample, now);
                if (completed != 0 && !_stop.IsSet) QueueProbe();
            }
        }
        finally
        {
            lock (_stopGate)
            {
                _stopDisposed = true;
                _stop.Dispose();
            }
        }
    }

    private void Release(Lease lease)
    {
        lock (Gate)
        {
            var index = Array.IndexOf(_leases, lease);
            if (index < 0) return;
            var remaining = new Lease[_leases.Length - 1];
            _leases.AsSpan(0, index).CopyTo(remaining);
            _leases.AsSpan(index + 1).CopyTo(remaining.AsSpan(index));
            _leases = remaining;
            if (_leases.Length != 0) return;
            if (ReferenceEquals(_current, this))
            {
                _current = null;
                Volatile.Write(ref _latest, null);
            }
        }
        // Do not wait for a starved pool callback or a user logger during client disposal.
        lock (_stopGate)
        {
            if (!_stopDisposed) _stop.Set();
        }
    }

    private sealed class Lease(ThreadPoolMonitor owner, ILogger? logger, TimeSpan threshold) : IDisposable
    {
        private int _disposed;
        // Only this lease's sampler thread reads or writes the warning timestamp.
        private long _lastWarning;

        internal void Observe(RespireThreadPoolSnapshot sample, long now)
        {
            if (Volatile.Read(ref _disposed) != 0 || logger is null || sample.SchedulingDelay < threshold
                || _lastWarning != 0 && Stopwatch.GetElapsedTime(_lastWarning, now) < WarningInterval) return;
            _lastWarning = now;
            try
            {
                logger.LogWarning(
                    "Thread-pool scheduling delayed by {SchedulingDelayMs} ms (probe pending: {ProbePending}); workers busy/min: {BusyWorkers}/{MinWorkers}; queued work: {PendingWorkItems}. " +
                    "Inspect synchronous blocking and long-running work; use asynchronous I/O and diagnose runtime counters before changing minimum worker threads.",
                    sample.SchedulingDelay.TotalMilliseconds, sample.IsPending, sample.BusyWorkerThreads,
                    sample.MinWorkerThreads, sample.PendingWorkItems);
            }
            catch (Exception)
            {
                // A diagnostic logger cannot terminate the background thread or the process.
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Release(this);
        }
    }

    // Observable callbacks allocate only when a metrics consumer collects a sample.
    internal static IEnumerable<Measurement<double>> ObserveDelay()
        => Latest is { } sample ? [new(sample.SchedulingDelay.TotalSeconds)] : [];
    internal static IEnumerable<Measurement<long>> ObserveBusyWorkers()
        => Latest is { } sample ? [new(sample.BusyWorkerThreads)] : [];
    internal static IEnumerable<Measurement<long>> ObserveMinimumWorkers()
        => Latest is { } sample ? [new(sample.MinWorkerThreads)] : [];
    internal static IEnumerable<Measurement<long>> ObservePendingWork()
        => Latest is { } sample ? [new(sample.PendingWorkItems)] : [];
}
