using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Dashboard.Tests;

public class DashboardCheckpointConcurrencyTests
{
    [Test]
    public async Task ReadsOverlapWithinBoundAndRetainSuccessfulEntriesAfterFailure()
    {
        var ids = Enumerable.Range(1, 12).Select(i => new RespireStreamId($"{i}-0")).ToArray();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var exceededBound = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reading = DashboardState.ReadCheckpointsAsync(ids, async (id, token) =>
        {
            var count = Interlocked.Increment(ref active);
            if (count > 4) Interlocked.Exchange(ref exceededBound, 1);
            if (count == 4) started.TrySetResult();
            try
            {
                await release.Task.WaitAsync(token);
                if (id == ids[1]) throw new RespireException("unavailable");
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
        var result = await reading;
        await Assert.That(exceededBound).IsEqualTo(0);
        await Assert.That(result.Unavailable).IsTrue();
        await Assert.That(result.Checkpoints.Count).IsEqualTo(ids.Length - 1);
        for (var i = 0; i < ids.Length; i++)
            await Assert.That(result.Checkpoints.ContainsKey(ids[i])).IsEqualTo(i != 1);
    }

    [Test]
    public async Task CancellationPropagatesInsteadOfBecomingUnavailable()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reading = DashboardState.ReadCheckpointsAsync([new("1-0"), new("2-0")], async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return null;
        }, cancellation.Token);
        await started.Task.WaitAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.That(async () => await reading).Throws<OperationCanceledException>();
    }
}
