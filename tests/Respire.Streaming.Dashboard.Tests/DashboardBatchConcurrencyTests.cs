using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Dashboard.Tests;

public class DashboardBatchConcurrencyTests
{
    [Test]
    public async Task ReadsOverlapWithinBoundAndPreserveOrderAndFailures()
    {
        var ids = Enumerable.Range(0, 12).Select(i => i.ToString()).ToArray();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var exceededBound = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reading = DashboardState.ReadBatchesAsync(ids, async (id, token) =>
        {
            var count = Interlocked.Increment(ref active);
            if (count > 4) Interlocked.Exchange(ref exceededBound, 1);
            if (count == 4) started.TrySetResult();
            try
            {
                await release.Task.WaitAsync(token);
                if (id == "1") throw new RespireException("unavailable");
                return null;
            }
            finally { Interlocked.Decrement(ref active); }
        }, timeout.Token);
        try
        {
            await started.Task.WaitAsync(timeout.Token);
            await Assert.That(active).IsEqualTo(4);
        }
        finally { release.TrySetResult(); }
        var batches = await reading;
        await Assert.That(exceededBound).IsEqualTo(0);
        await Assert.That(batches.Length).IsEqualTo(ids.Length);
        for (var i = 0; i < ids.Length; i++)
        {
            await Assert.That(batches[i].Id).IsEqualTo(ids[i]);
            await Assert.That(batches[i].Unavailable).IsEqualTo(i == 1);
        }
    }

    [Test]
    public async Task CancellationPropagatesInsteadOfBecomingUnavailable()
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reading = DashboardState.ReadBatchesAsync(["one", "two"], async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return null;
        }, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.That(async () => await reading).Throws<OperationCanceledException>();
    }
}
