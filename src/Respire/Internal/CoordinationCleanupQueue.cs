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
    private readonly Task[] _workers;
    private int _disposed;

    internal CoordinationCleanupQueue()
        => _workers = Enumerable.Range(0, WorkerCount).Select(_ => RunWorkerAsync()).ToArray();

    /// <summary>
    /// Enqueues one retrying cleanup. A full queue reports and rejects new work so its size and
    /// worker count remain bounded.
    /// </summary>
    internal Task<bool> EnqueueAsync(
        Func<CancellationToken, ValueTask<bool>> attempt,
        Func<bool>? shouldContinue,
        TimeSpan retryLimit,
        TimeSpan initialDelay,
        TimeSpan maximumDelay,
        Action<string> onAbandoned)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(onAbandoned);
        var cleanup = new Cleanup(attempt, shouldContinue, retryLimit, initialDelay, maximumDelay, onAbandoned);
        if (Volatile.Read(ref _disposed) != 0)
        {
            cleanup.Report("client_disposed");
            cleanup.Completion.TrySetResult(false);
        }
        else if (!_queue.Writer.TryWrite(cleanup))
        {
            cleanup.Report("queue_full");
            cleanup.Completion.TrySetResult(false);
        }

        return cleanup.Completion.Task;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _queue.Writer.TryComplete();
        _stopping.Cancel();
        try { await Task.WhenAll(_workers).ConfigureAwait(false); }
        catch (OperationCanceledException) { }

        while (_queue.Reader.TryRead(out var cleanup))
        {
            cleanup.Report("client_disposed");
            cleanup.Completion.TrySetResult(false);
        }

        _stopping.Dispose();
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
        var started = Stopwatch.GetTimestamp();
        var delay = cleanup.InitialDelay;
        try
        {
            while (!_stopping.IsCancellationRequested
                && (cleanup.ShouldContinue is null || cleanup.ShouldContinue()))
            {
                if (await cleanup.Attempt(_stopping.Token).ConfigureAwait(false))
                {
                    cleanup.Completion.TrySetResult(true);
                    return;
                }

                if (Stopwatch.GetElapsedTime(started) >= cleanup.RetryLimit)
                {
                    cleanup.Report("exhausted");
                    cleanup.Completion.TrySetResult(false);
                    return;
                }

                await Task.Delay(WithJitter(delay), _stopping.Token).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, cleanup.MaximumDelay.Ticks));
            }

            if (_stopping.IsCancellationRequested) cleanup.Report("client_disposed");
            cleanup.Completion.TrySetResult(false);
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

    private static TimeSpan WithJitter(TimeSpan delay)
        => TimeSpan.FromTicks((long)(delay.Ticks * (0.75 + Random.Shared.NextDouble() * 0.5)));

    private sealed class Cleanup(
        Func<CancellationToken, ValueTask<bool>> attempt,
        Func<bool>? shouldContinue,
        TimeSpan retryLimit,
        TimeSpan initialDelay,
        TimeSpan maximumDelay,
        Action<string> onAbandoned)
    {
        internal Func<CancellationToken, ValueTask<bool>> Attempt { get; } = attempt;
        internal Func<bool>? ShouldContinue { get; } = shouldContinue;
        internal TimeSpan RetryLimit { get; } = retryLimit;
        internal TimeSpan InitialDelay { get; } = initialDelay;
        internal TimeSpan MaximumDelay { get; } = maximumDelay;
        internal TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Report(string reason)
        {
            try { onAbandoned(reason); }
            catch { /* Diagnostics must not stop cleanup workers. */ }
        }
    }
}
