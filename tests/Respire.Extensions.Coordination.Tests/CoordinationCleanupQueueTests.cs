using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Respire.Internal;

namespace Respire.Extensions.Coordination.Tests;

public class CoordinationCleanupQueueTests
{
    [Test]
    public async Task InvalidRetryDelayCompletesCleanupAndDisposal()
    {
        await using var queue = new CoordinationCleanupQueue();
        string? abandoned = null;
        var completion = queue.EnqueueAsync(_ => new(CleanupAttemptResult.Failed), null,
            TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(1),
            reason => abandoned = reason);

        await Assert.That(await completion.WaitAsync(TimeSpan.FromSeconds(5))).IsFalse();
        await Assert.That(abandoned).IsEqualTo("failed");
        await queue.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task DisposedClientRejectsCleanupWithoutStartingAttempt()
    {
        await using var client = RespireClient.Create(new RespireOptions { Endpoints = [new("unused.invalid")] });
        await client.DisposeAsync();
        var attempts = 0;
        string? abandoned = null;
        await RespireSemaphore.EnqueueCleanupAsync(client, _ =>
        {
            attempts++;
            return new(CleanupAttemptResult.Succeeded);
        }, null, reason => abandoned = reason);

        await Assert.That(client.Core.CoordinationCleanupQueue).IsNull();
        await Assert.That(attempts).IsEqualTo(0);
        await Assert.That(abandoned).IsEqualTo("client_disposed");
    }

    [Test]
    public async Task CancelledReleaseIsAbandonedWithoutRetry()
    {
        await using var client = RespireClient.Create(new RespireOptions { Endpoints = [new("unused.invalid")] });
        var outcome = await RespireSemaphore.TryReleaseOnceAsync(
            client, "semaphore", RespireLock.NewToken(), new CancellationToken(canceled: true));

        await Assert.That(outcome).IsEqualTo(CleanupAttemptResult.Abandoned);
    }

    [Test]
    public async Task DisposeCancelsDelayedRetryAndCompletesCleanup()
    {
        await using var queue = new CoordinationCleanupQueue();
        var attempts = 0;
        string? abandoned = null;
        // The synchronous first attempt schedules its delay before EnqueueAsync returns.
        var completion = queue.EnqueueAsync(_ =>
        {
            attempts++;
            return new(CleanupAttemptResult.Failed);
        }, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1),
            reason => abandoned = reason);

        await Assert.That(attempts).IsEqualTo(1);
        await Assert.That(completion.IsCompleted).IsFalse();
        await queue.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(await completion.WaitAsync(TimeSpan.FromSeconds(5))).IsFalse();
        await Assert.That(abandoned).IsEqualTo("client_disposed");
        await Assert.That(attempts).IsEqualTo(1);
    }

    [Test]
    public async Task AbandonedAttemptStopsWithoutRetrying()
    {
        await using var queue = new CoordinationCleanupQueue();
        var attempts = 0;
        string? abandoned = null;
        var completion = queue.EnqueueAsync(_ =>
        {
            attempts++;
            return new(CleanupAttemptResult.Abandoned);
        }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2),
            reason => abandoned = reason);

        await Assert.That(await completion.WaitAsync(TimeSpan.FromSeconds(5))).IsFalse();
        await Assert.That(attempts).IsEqualTo(1);
        await Assert.That(abandoned).IsEqualTo("client_disposed");
    }

    [Test]
    public async Task UnneededCleanupSkipsAttemptAndAbandonmentReport()
    {
        await using var queue = new CoordinationCleanupQueue();
        var attempts = 0;
        string? abandoned = null;
        var completion = queue.EnqueueAsync(_ =>
        {
            attempts++;
            return new(CleanupAttemptResult.Failed);
        }, () => false, TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2),
            reason => abandoned = reason);

        await Assert.That(await completion.WaitAsync(TimeSpan.FromSeconds(5))).IsFalse();
        await Assert.That(attempts).IsEqualTo(0);
        await Assert.That(abandoned).IsNull();
    }

    [Test]
    public async Task DisposeCancelsRunningCleanupAndReportsClientDisposed()
    {
        await using var queue = new CoordinationCleanupQueue();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? abandoned = null;
        var completion = queue.EnqueueAsync(async cancellationToken =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return CleanupAttemptResult.Failed;
        }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2),
            reason => abandoned = reason);

        await started.Task;
        await queue.DisposeAsync();

        await Assert.That(await completion).IsFalse();
        await Assert.That(abandoned).IsEqualTo("client_disposed");
    }

    [Test]
    public async Task FullQueueBackpressuresWithoutDroppingCleanup()
    {
        await using var queue = new CoordinationCleanupQueue();
        await Assert.That(queue.StartedWorkerCount).IsEqualTo(0);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var allWorkersStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completions = new List<Task<bool>>();
        Task<bool> EnqueueBlockedCleanup()
        {
            return queue.EnqueueAsync(async cancellationToken =>
            {
                if (Interlocked.Increment(ref started) == CoordinationCleanupQueue.WorkerCount)
                    allWorkersStarted.TrySetResult();
                await gate.Task.WaitAsync(cancellationToken);
                return CleanupAttemptResult.Succeeded;
            }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2), _ => { });
        }
        for (var i = 0; i < CoordinationCleanupQueue.WorkerCount; i++)
            completions.Add(EnqueueBlockedCleanup());
        await allWorkersStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(started).IsEqualTo(CoordinationCleanupQueue.WorkerCount);
        for (var i = 0; i < CoordinationCleanupQueue.Capacity; i++)
            completions.Add(EnqueueBlockedCleanup());

        var accepted = queue.EnqueueAsync(_ => new ValueTask<CleanupAttemptResult>(CleanupAttemptResult.Succeeded), null, TimeSpan.FromMinutes(1),
            TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2), _ => { });
        await Assert.That(accepted.IsCompleted).IsFalse();
        gate.TrySetResult();
        await Assert.That(await accepted.WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();
        await Assert.That(queue.StartedWorkerCount).IsEqualTo(CoordinationCleanupQueue.WorkerCount);
        await Task.WhenAll(completions);
    }

    [Test]
    public async Task BackoffDoesNotOccupyWorkers()
    {
        await using var queue = new CoordinationCleanupQueue();
        var firstAttempts = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var retries = Enumerable.Range(0, CoordinationCleanupQueue.WorkerCount)
            .Select(_ => queue.EnqueueAsync(_ =>
            {
                if (Interlocked.Increment(ref started) == CoordinationCleanupQueue.WorkerCount)
                    firstAttempts.TrySetResult();
                return new ValueTask<CleanupAttemptResult>(CleanupAttemptResult.Failed);
            }, null, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), _ => { }))
            .ToArray();
        await firstAttempts.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var laterStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var later = queue.EnqueueAsync(_ =>
        {
            laterStarted.TrySetResult();
            return new ValueTask<CleanupAttemptResult>(CleanupAttemptResult.Succeeded);
        }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2), _ => { });

        await laterStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(await later).IsTrue();
        await queue.DisposeAsync();
        await Task.WhenAll(retries);
    }

    [Test]
    public async Task RetryLimitIncludesTimeWaitingInQueue()
    {
        await using var queue = new CoordinationCleanupQueue();
        var gates = Enumerable.Range(0, CoordinationCleanupQueue.WorkerCount)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var startedCount = 0;
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = gates.Select(gate => queue.EnqueueAsync(async cancellationToken =>
        {
            if (Interlocked.Increment(ref startedCount) == CoordinationCleanupQueue.WorkerCount)
                allStarted.TrySetResult();
            await gate.Task.WaitAsync(cancellationToken);
            return CleanupAttemptResult.Succeeded;
        }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2), _ => { })).ToArray();
        await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var attempts = 0;
        var abandoned = string.Empty;
        var queued = queue.EnqueueAsync(_ =>
        {
            Interlocked.Increment(ref attempts);
            return new ValueTask<CleanupAttemptResult>(CleanupAttemptResult.Failed);
        }, null, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2), reason => abandoned = reason);

        await Task.Delay(50);
        foreach (var gate in gates) gate.TrySetResult();
        await Task.WhenAll(active);
        await Assert.That(await queued.WaitAsync(TimeSpan.FromSeconds(5))).IsFalse();
        await Assert.That(attempts).IsEqualTo(1);
        await Assert.That(abandoned).IsEqualTo("exhausted");
    }
}

