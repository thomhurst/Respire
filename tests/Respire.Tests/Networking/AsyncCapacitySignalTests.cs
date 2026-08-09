using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class AsyncCapacitySignalTests
{
    [Test]
    public async Task Signal_WakesAllCurrentWaiters()
    {
        var signal = new AsyncCapacitySignal();
        var waiters = Enumerable.Range(0, 16)
            .Select(_ => signal.WaitAsync(CancellationToken.None))
            .ToArray();

        await Assert.That(waiters.All(static task => !task.IsCompleted)).IsTrue();

        signal.Signal();
        await Task.WhenAll(waiters);
    }

    [Test]
    public async Task CancelledWaiter_DoesNotCancelSharedSignal()
    {
        var signal = new AsyncCapacitySignal();
        using var cancellation = new CancellationTokenSource();
        var cancelled = signal.WaitAsync(cancellation.Token);
        var survivor = signal.WaitAsync(CancellationToken.None);

        cancellation.Cancel();
        await Assert.That(async () => await cancelled).Throws<OperationCanceledException>();
        await Assert.That(survivor.IsCompleted).IsFalse();

        signal.Signal();
        await survivor;
    }
}
