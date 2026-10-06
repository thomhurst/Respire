using System.Diagnostics;
using System.Threading.Channels;

namespace Respire.Internal;

internal enum CleanupAttemptResult { Succeeded, Failed, Abandoned }

/// <summary>Runs bounded, retryable coordination cleanup for one client core.</summary>
internal sealed class CoordinationCleanupQueue : IAsyncDisposable
{
    internal const int Capacity = 256;
    internal const int WorkerCount = 4;
    internal const int MaximumAdmissionWaiters = Capacity;

    private readonly Channel<Cleanup> _queue = Channel.CreateBounded<Cleanup>(new BoundedChannelOptions(Capacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = false,
        SingleWriter = false,
        AllowSynchronousContinuations = false,
    });
    private readonly CancellationTokenSource _stopping = new();
    private readonly CancellationToken _stoppingToken;
    private readonly SemaphoreSlim _outstanding = new(Capacity + WorkerCount);
    private readonly Lock _workerGate = new();
    private readonly Lock _scheduledGate = new();
    private readonly HashSet<Task> _scheduledRetries = [];
    private Task[]? _workers;
    private int _disposed;
    private int _admissionWaiters;

    internal CoordinationCleanupQueue() => _stoppingToken = _stopping.Token;
    internal int StartedWorkerCount => _workers?.Length ?? 0;

    /// <summary>
    /// Enqueues one retrying cleanup. A full queue applies asynchronous backpressure up to the
    /// admission waiter limit; excess work is reported as overloaded.
    /// </summary>
    internal async Task<bool> EnqueueAsync(
        Func<CancellationToken, ValueTask<CleanupAttemptResult>> attempt,
        Func<bool>? shouldContinue,
        TimeSpan retryLimit,
        TimeSpan initialDelay,
        TimeSpan maximumDelay,
        Action<string> onAbandoned)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(onAbandoned);
        var enqueuedAt = Stopwatch.GetTimestamp();
        if (Interlocked.Increment(ref _admissionWaiters) > MaximumAdmissionWaiters)
        {
            Interlocked.Decrement(ref _admissionWaiters);
            try { onAbandoned("overloaded"); }
            catch { /* Diagnostics must not stop cleanup callers. */ }
            return false;
        }
        try { await _outstanding.WaitAsync(_stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { onAbandoned("client_disposed"); }
            catch { /* Diagnostics must not stop cleanup callers. */ }
            return false;
        }
        finally { Interlocked.Decrement(ref _admissionWaiters); }
        var cleanup = new Cleanup(attempt, shouldContinue, retryLimit, initialDelay, maximumDelay, onAbandoned,
            () => _outstanding.Release(),
            enqueuedAt);
        if (Volatile.Read(ref _disposed) != 0)
        {
            cleanup.Report("client_disposed");
            cleanup.Complete(false);
        }
        else
        {
            try
            {
                await _queue.Writer.WriteAsync(cleanup, _stoppingToken).ConfigureAwait(false);
                StartWorkers();
            }
            catch (Exception error) when (error is OperationCanceledException or ChannelClosedException)
            {
                cleanup.Report("client_disposed");
                cleanup.Complete(false);
            }
        }

        return await cleanup.Completion.Task.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _queue.Writer.TryComplete();
        await _stopping.CancelAsync().ConfigureAwait(false);
        Task[] workers;
        lock (_workerGate) workers = _workers ?? [];
        try { await Task.WhenAll(workers).ConfigureAwait(false); }
        catch (OperationCanceledException) { }

        Task[] retries;
        // Only workers schedule retries. Awaiting them first closes additions before this snapshot.
        lock (_scheduledGate) retries = [.. _scheduledRetries];
        try { await Task.WhenAll(retries).ConfigureAwait(false); }
        catch (OperationCanceledException) { }

        while (_queue.Reader.TryRead(out var cleanup))
        {
            cleanup.Report("client_disposed");
            cleanup.Complete(false);
        }

        _stopping.Dispose();
    }

    private void StartWorkers()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        lock (_workerGate)
        {
            if (_workers is null && Volatile.Read(ref _disposed) == 0)
                _workers = Enumerable.Range(0, WorkerCount).Select(_ => RunWorkerAsync()).ToArray();
        }
    }

    private async Task RunWorkerAsync()
    {
        try
        {
            await foreach (var cleanup in _queue.Reader.ReadAllAsync(_stoppingToken).ConfigureAwait(false))
                await RunCleanupAsync(cleanup).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
    }

    private async Task RunCleanupAsync(Cleanup cleanup)
    {
        try
        {
            if (_stopping.IsCancellationRequested)
            {
                cleanup.Report("client_disposed");
                cleanup.Complete(false);
                return;
            }
            if (cleanup.ShouldContinue is not null && !cleanup.ShouldContinue())
            {
                // The caller no longer needs cleanup, so this is not abandonment.
                cleanup.Complete(false);
                return;
            }
            if (cleanup.HasAttempted && Stopwatch.GetElapsedTime(cleanup.EnqueuedAt) >= cleanup.RetryLimit)
            { cleanup.Report("exhausted"); cleanup.Complete(false); return; }
            cleanup.HasAttempted = true;
            var outcome = await cleanup.Attempt(_stoppingToken).ConfigureAwait(false);
            if (outcome == CleanupAttemptResult.Succeeded)
            { cleanup.Complete(true); return; }
            if (outcome == CleanupAttemptResult.Abandoned)
            { cleanup.Report("client_disposed"); cleanup.Complete(false); return; }
            var remaining = cleanup.RetryLimit - Stopwatch.GetElapsedTime(cleanup.EnqueuedAt);
            if (remaining <= TimeSpan.Zero)
            { cleanup.Report("exhausted"); cleanup.Complete(false); return; }
            var delay = CoordinationCleanupRetry.WithJitter(TimeSpan.FromTicks(Math.Min(cleanup.NextDelay.Ticks, remaining.Ticks)));
            if (delay > remaining) delay = remaining;
            cleanup.NextDelay = CoordinationCleanupRetry.NextDelay(cleanup.NextDelay, cleanup.MaximumDelay);
            ScheduleRetry(cleanup, delay);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            cleanup.Report("client_disposed");
            cleanup.Complete(false);
        }
        catch (ObjectDisposedException)
        {
            cleanup.Report("client_disposed");
            cleanup.Complete(false);
        }
        catch
        {
            cleanup.Report("failed");
            cleanup.Complete(false);
        }
    }

    private void ScheduleRetry(Cleanup cleanup, TimeSpan delay)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_scheduledGate) _scheduledRetries.Add(completion.Task);
        _ = RequeueAfterDelayAsync(cleanup, delay, completion);
    }

    private async Task RequeueAfterDelayAsync(Cleanup cleanup, TimeSpan delay, TaskCompletionSource completion)
    {
        try
        {
            await Task.Delay(delay, _stoppingToken).ConfigureAwait(false);
            await _queue.Writer.WriteAsync(cleanup, _stoppingToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or ChannelClosedException)
        {
            cleanup.Report("client_disposed");
            cleanup.Complete(false);
        }
        catch
        {
            cleanup.Report("failed");
            cleanup.Complete(false);
        }
        finally
        {
            lock (_scheduledGate) _scheduledRetries.Remove(completion.Task);
            completion.TrySetResult();
        }
    }

    private sealed class Cleanup(
        Func<CancellationToken, ValueTask<CleanupAttemptResult>> attempt,
        Func<bool>? shouldContinue,
        TimeSpan retryLimit,
        TimeSpan initialDelay,
        TimeSpan maximumDelay,
        Action<string> onAbandoned,
        Action onComplete,
        long enqueuedAt)
    {
        internal Func<CancellationToken, ValueTask<CleanupAttemptResult>> Attempt { get; } = attempt;
        internal Func<bool>? ShouldContinue { get; } = shouldContinue;
        internal TimeSpan RetryLimit { get; } = retryLimit;
        internal TimeSpan MaximumDelay { get; } = maximumDelay;
        internal long EnqueuedAt { get; } = enqueuedAt;
        internal TimeSpan NextDelay { get; set; } = initialDelay;
        internal bool HasAttempted { get; set; }
        internal TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Complete(bool result)
        {
            if (Completion.TrySetResult(result))
                onComplete();
        }
        internal void Report(string reason)
        {
            try { onAbandoned(reason); }
            catch { /* Diagnostics must not stop cleanup workers. */ }
        }
    }
}
