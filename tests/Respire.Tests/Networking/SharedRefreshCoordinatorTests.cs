using Respire.Internal;
using TUnit.Assertions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class SharedRefreshCoordinatorTests
{
    [Test]
    public async Task ReadOnlyJoinsShareFlightAndCancelOnlyAfterLastWaiterLeaves()
    {
        var coordinator = new SharedRefreshCoordinator(TimeProvider.System, TimeSpan.FromSeconds(5));
        var source = new RespireEndpoint("127.0.0.1", 6379);
        using var cancellation = new CancellationTokenSource();
        var resourceFactoryCalls = 0;
        var first = coordinator.JoinReadOnly(12, source, () =>
        {
            resourceFactoryCalls++;
            return (cancellation, (IDisposable?)null);
        });
        var second = coordinator.JoinReadOnly(12, source, () =>
        {
            resourceFactoryCalls++;
            throw new InvalidOperationException("Joining an active flight must not create starter resources.");
        });

        await Assert.That(first.Started).IsTrue();
        await Assert.That(second.Started).IsFalse();
        await Assert.That(second.Flight).IsSameReferenceAs(first.Flight);
        await Assert.That(second.NeedsOwnSlotRecovery).IsFalse();
        await Assert.That(resourceFactoryCalls).IsEqualTo(1);
        await Assert.That(first.Flight.Waiters).IsEqualTo(2);
        await Assert.That(coordinator.ReleaseWaiter(first.Flight)).IsNull();
        await Assert.That(coordinator.IsPublished(first.Flight)).IsTrue();

        var abandoned = coordinator.ReleaseWaiter(first.Flight);
        await Assert.That(abandoned).IsSameReferenceAs(cancellation);
        await Assert.That(first.Flight.Abandoned).IsTrue();
        await Assert.That(coordinator.IsPublished(first.Flight)).IsFalse();
        abandoned!.Dispose();
    }

    [Test]
    public async Task TopologyFlightCoalescesAndSuccessfulRefreshUsesRecentWindow()
    {
        var clock = new ManualClock();
        var coordinator = new SharedRefreshCoordinator(clock, TimeSpan.FromSeconds(5));
        await Assert.That(coordinator.JoinTopology(allowRecentSuccessfulResult: true, out var first, out var started)).IsTrue();
        await Assert.That(started).IsTrue();
        await Assert.That(coordinator.JoinTopology(allowRecentSuccessfulResult: true, out var joined, out started)).IsTrue();
        await Assert.That(started).IsFalse();
        await Assert.That(joined).IsSameReferenceAs(first);

        coordinator.Complete(first, result: true, failure: null);
        await Assert.That(first.Task.Result).IsTrue();
        await Assert.That(coordinator.IsPublished(first)).IsFalse();
        await Assert.That(coordinator.JoinTopology(allowRecentSuccessfulResult: true, out _, out _)).IsFalse();

        clock.Advance(TimeSpan.FromSeconds(5));
        await Assert.That(coordinator.JoinTopology(allowRecentSuccessfulResult: true, out var next, out started)).IsTrue();
        await Assert.That(started).IsTrue();
        coordinator.Complete(next, result: false, failure: null);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Volatile.Read(ref _timestamp);
        internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref _timestamp, elapsed.Ticks);
    }
}
