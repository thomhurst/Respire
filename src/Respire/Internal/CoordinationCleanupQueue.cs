using System.Diagnostics;
using System.Threading.Channels;

namespace Respire.Internal;

internal enum CleanupAttemptResult { Succeeded, Failed, Abandoned }

/// <summary>Runs bounded, retryable coordination cleanup for one client core.</summary>
internal sealed class CoordinationCleanupQueue : IAsyncDisposable
{
    internal const int Capacity = 256;
    internal const int WorkerCount = 4;

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
    private readonly object _workerGate = new();
    private readonly object _scheduledGate = new();
    private readonly List<Task> _scheduledRetries = [];
    private Task[]? _workers;
    private int _disposed;

    internal CoordinationCleanupQueue() => _stoppingToken = _stopping.Token;
    internal int StartedWorkerCount => _workers?.Length ?? 0;

    /// <summary>
    /// Enqueues one retrying cleanup. A full queue applies asynchronous backpressure so cleanup
    /// work is never discarded.
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
        try { await _outstanding.WaitAsync(_stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            onAbandoned("client_disposed");
            return false;
        }
        var cleanup = new Cleanup(attempt, shouldContinue, retryLimit, initialDelay, maximumDelay, onAbandoned,
            () => _outstanding.Release(),
            Stopwatch.GetTimestamp());
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
        _stopping.Cancel();
        Task[] workers;
        lock (_workerGate) workers = _workers ?? [];
        try { await Task.WhenAll(workers).ConfigureAwait(false); }
        catch (OperationCanceledException) { }

        Task[] retries;
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
            { cleanup.Complete(false); return; }
            if (cleanup.HasAttempted && Stopwatch.GetElapsedTime(cleanup.EnqueuedAt) >= cleanup.RetryLimit)
            { cleanup.Report("exhausted"); cleanup.Complete(false); return; }
            cleanup.HasAttempted = true;
            var outcome = await cleanup.Attempt(_stoppingToken).ConfigureAwait(false);
            if (outcome == CleanupAttemptResult.Succeeded)
            { cleanup.Complete(true); return; }
            if (outcome == CleanupAttemptResult.Abandoned)
            { cleanup.Report("client_disposed"); cleanup.Complete(false); return; }
            if (Stopwatch.GetElapsedTime(cleanup.EnqueuedAt) >= cleanup.RetryLimit)
            { cleanup.Report("exhausted"); cleanup.Complete(false); return; }

            var remaining = cleanup.RetryLimit - Stopwatch.GetElapsedTime(cleanup.EnqueuedAt);
            var delay = WithJitter(TimeSpan.FromTicks(Math.Min(cleanup.NextDelay.Ticks, remaining.Ticks)));
            cleanup.NextDelay = TimeSpan.FromTicks(Math.Min(
                cleanup.NextDelay.Ticks * 2, cleanup.MaximumDelay.Ticks));
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
        var retry = RequeueAfterDelayAsync(cleanup, delay);
        lock (_scheduledGate) _scheduledRetries.Add(retry);
        _ = retry.ContinueWith(completed =>
        {
            lock (_scheduledGate) _scheduledRetries.Remove(completed);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task RequeueAfterDelayAsync(Cleanup cleanup, TimeSpan delay)
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
    }

    private static TimeSpan WithJitter(TimeSpan delay)
        => TimeSpan.FromTicks((long)(delay.Ticks * (0.75 + Random.Shared.NextDouble() * 0.5)));

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
        internal TimeSpan InitialDelay { get; } = initialDelay;
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
