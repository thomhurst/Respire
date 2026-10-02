using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Respire.Internal;

namespace Respire.Extensions.Coordination.Tests;

public class CoordinationCleanupQueueTests
{
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
            return false;
        }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2),
            reason => abandoned = reason);

        await started.Task;
        await queue.DisposeAsync();

        await Assert.That(await completion).IsFalse();
        await Assert.That(abandoned).IsEqualTo("client_disposed");
    }

    [Test]
    public async Task FullQueueRejectsCleanupAndReportsQueueFull()
    {
        await using var queue = new CoordinationCleanupQueue();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var completions = new List<Task<bool>>();
        for (var i = 0; i < CoordinationCleanupQueue.WorkerCount + CoordinationCleanupQueue.Capacity; i++)
        {
            completions.Add(queue.EnqueueAsync(async cancellationToken =>
            {
                Interlocked.Increment(ref started);
                await gate.Task.WaitAsync(cancellationToken);
                return false;
            }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2), _ => { }));
        }

        var rejectedReason = string.Empty;
        var rejected = queue.EnqueueAsync(_ => new ValueTask<bool>(false), null, TimeSpan.FromMinutes(1),
            TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2), reason => rejectedReason = reason);
        await Assert.That(await rejected).IsFalse();
        await Assert.That(rejectedReason).IsEqualTo("queue_full");
        gate.TrySetResult();
    }
}
