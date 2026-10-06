using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class AsyncLifecycleCancellationTests
{
    [Test]
    public async Task CleanupDisposalYieldsWhileCancellationCallbackRuns()
    {
        await using var queue = new CoordinationCleanupQueue();
        var attemptStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposalReturned = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = queue.EnqueueAsync(async token =>
        {
            using var registration = token.Register(() =>
            {
                callbackStarted.TrySetResult();
                releaseCallback.Task.GetAwaiter().GetResult();
            });
            attemptStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return CleanupAttemptResult.Succeeded;
        }, null, TimeSpan.FromMinutes(1), TimeSpan.Zero, TimeSpan.Zero, _ => { });

        await attemptStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var invokeDisposal = Task.Run(() => disposalReturned.TrySetResult(queue.DisposeAsync().AsTask()));
        try
        {
            await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var disposal = await disposalReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(disposal.IsCompleted).IsFalse();
            releaseCallback.TrySetResult();
            await disposal.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(await cleanup.WaitAsync(TimeSpan.FromSeconds(5))).IsFalse();
        }
        finally
        {
            releaseCallback.TrySetResult();
            await invokeDisposal.WaitAsync(TimeSpan.FromSeconds(5));
            var disposal = await disposalReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
