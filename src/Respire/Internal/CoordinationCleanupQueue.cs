using System.Diagnostics;
using System.Threading.Channels;

namespace Respire.Internal;

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
    private readonly object _workerGate = new();
    private readonly object _scheduledGate = new();
    private readonly List<Task> _scheduledRetries = [];
    private Task[]? _workers;
    private int _disposed;

    internal CoordinationCleanupQueue() { }
    internal int StartedWorkerCount => _workers?.Length ?? 0;

    /// <summary>
    /// Enqueues one retrying cleanup. A full queue applies asynchronous backpressure so cleanup
    /// work is never discarded.
    /// </summary>
    internal async Task<bool> EnqueueAsync(
        Func<CancellationToken, ValueTask<bool>> attempt,
        Func<bool>? shouldContinue,
        TimeSpan retryLimit,
        TimeSpan initialDelay,
        TimeSpan maximumDelay,
        Action<string> onAbandoned)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(onAbandoned);
        var cleanup = new Cleanup(attempt, shouldContinue, retryLimit, initialDelay, maximumDelay, onAbandoned,
            Stopwatch.GetTimestamp());
        if (Volatile.Read(ref _disposed) != 0)
        {
            cleanup.Report("client_disposed");
            cleanup.Completion.TrySetResult(false);
        }
        else
        {
            try
            {
                await _queue.Writer.WriteAsync(cleanup, _stopping.Token).ConfigureAwait(false);
                StartWorkers();
            }
            catch (Exception error) when (error is OperationCanceledException or ChannelClosedException)
            {
                cleanup.Report("client_disposed");
                cleanup.Completion.TrySetResult(false);
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
            cleanup.Completion.TrySetResult(false);
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
            await foreach (var cleanup in _queue.Reader.ReadAllAsync(_stopping.Token).ConfigureAwait(false))
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
                cleanup.Completion.TrySetResult(false);
                return;
            }
            if (cleanup.ShouldContinue is not null && !cleanup.ShouldContinue())
            { cleanup.Completion.TrySetResult(false); return; }
            if (cleanup.HasAttempted && Stopwatch.GetElapsedTime(cleanup.EnqueuedAt) >= cleanup.RetryLimit)
            { cleanup.Report("exhausted"); cleanup.Completion.TrySetResult(false); return; }
            cleanup.HasAttempted = true;
            if (await cleanup.Attempt(_stopping.Token).ConfigureAwait(false))
            { cleanup.Completion.TrySetResult(true); return; }
            if (Stopwatch.GetElapsedTime(cleanup.EnqueuedAt) >= cleanup.RetryLimit)
            { cleanup.Report("exhausted"); cleanup.Completion.TrySetResult(false); return; }

            var delay = WithJitter(cleanup.NextDelay);
            cleanup.NextDelay = TimeSpan.FromTicks(Math.Min(
                cleanup.NextDelay.Ticks * 2, cleanup.MaximumDelay.Ticks));
            ScheduleRetry(cleanup, delay);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            cleanup.Report("client_disposed");
            cleanup.Completion.TrySetResult(false);
        }
        catch (ObjectDisposedException)
        {
            cleanup.Report("client_disposed");
            cleanup.Completion.TrySetResult(false);
        }
        catch
        {
            cleanup.Report("failed");
            cleanup.Completion.TrySetResult(false);
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
            await Task.Delay(delay, _stopping.Token).ConfigureAwait(false);
            await _queue.Writer.WriteAsync(cleanup, _stopping.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or ChannelClosedException)
        {
            cleanup.Report("client_disposed");
            cleanup.Completion.TrySetResult(false);
        }
    }

    private static TimeSpan WithJitter(TimeSpan delay)
        => TimeSpan.FromTicks((long)(delay.Ticks * (0.75 + Random.Shared.NextDouble() * 0.5)));

    private sealed class Cleanup(
        Func<CancellationToken, ValueTask<bool>> attempt,
        Func<bool>? shouldContinue,
        TimeSpan retryLimit,
        TimeSpan initialDelay,
        TimeSpan maximumDelay,
        Action<string> onAbandoned,
        long enqueuedAt)
    {
        internal Func<CancellationToken, ValueTask<bool>> Attempt { get; } = attempt;
        internal Func<bool>? ShouldContinue { get; } = shouldContinue;
        internal TimeSpan RetryLimit { get; } = retryLimit;
        internal TimeSpan InitialDelay { get; } = initialDelay;
        internal TimeSpan MaximumDelay { get; } = maximumDelay;
        internal long EnqueuedAt { get; } = enqueuedAt;
        internal TimeSpan NextDelay { get; set; } = initialDelay;
        internal bool HasAttempted { get; set; }
        internal TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Report(string reason)
        {
            try { onAbandoned(reason); }
            catch { /* Diagnostics must not stop cleanup workers. */ }
        }
    }
}
