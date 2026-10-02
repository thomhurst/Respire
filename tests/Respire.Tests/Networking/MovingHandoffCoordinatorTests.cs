using Respire.Infrastructure;
using TUnit.Assertions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class MovingHandoffCoordinatorTests
{
    [Test]
    public async Task NewestRequestSupersedesActiveAndPendingRequests()
    {
        var coordinator = new MovingHandoffCoordinator();
        using var stop = new CancellationTokenSource();
        var peer = ("source", 6379);
        MovingHandoffCoordinator.Request active;
        MovingHandoffCoordinator.QueueResult firstQueue;
        Task? workerCompletion;
        lock (coordinator.Gate)
        {
            firstQueue = Queue(coordinator, peer, sequence: 1, "first-target", stop.Token, Environment.TickCount64);
            active = coordinator.TakeNext(operational: true)!;
            workerCompletion = coordinator.WorkerCompletion;
        }
        await Assert.That(firstQueue.StartWorker).IsTrue();

        MovingHandoffCoordinator.QueueResult secondQueue;
        MovingHandoffCoordinator.QueueResult latestQueue;
        lock (coordinator.Gate)
        {
            secondQueue = Queue(coordinator, peer, sequence: 2, "second-target", stop.Token, Environment.TickCount64);
            latestQueue = Queue(coordinator, peer, sequence: 3, "latest-target", stop.Token, Environment.TickCount64);
        }
        await Assert.That(secondQueue.StartWorker).IsFalse();
        await Assert.That(latestQueue.StartWorker).IsFalse();
        await Assert.That(active.Cancellation.IsCancellationRequested).IsTrue();

        MovingHandoffCoordinator.Request latest;
        bool workerCompleted;
        lock (coordinator.Gate)
        {
            coordinator.Complete(active);
            latest = coordinator.TakeNext(operational: true)!;
            coordinator.Complete(latest);
            _ = coordinator.TakeNext(operational: true);
            workerCompleted = coordinator.WorkerCompletion is null;
        }
        await Assert.That(latest.Endpoint.Host).IsEqualTo("latest-target");
        await Assert.That(workerCompleted).IsTrue();
        await Assert.That(workerCompletion is { IsCompleted: true }).IsTrue();
    }

    [Test]
    public async Task PeerSequenceFenceDeduplicatesUntilGraceDeadline()
    {
        var coordinator = new MovingHandoffCoordinator();
        using var stop = new CancellationTokenSource();
        var peer = ("source", 6379);
        var now = Environment.TickCount64;
        MovingHandoffCoordinator.QueueResult first;
        MovingHandoffCoordinator.QueueResult duplicate;
        MovingHandoffCoordinator.QueueResult afterExpiry;
        MovingHandoffCoordinator.Request next;
        lock (coordinator.Gate)
        {
            first = Queue(coordinator, peer, sequence: 5, "target", stop.Token, now,
                now + 30_000);
            duplicate = Queue(coordinator, peer, sequence: 5, "target", stop.Token, now);
            afterExpiry = Queue(coordinator, peer, sequence: 1, "restarted-target", stop.Token,
                now + 30_001, now + 30_002);
            next = coordinator.TakeNext(operational: true)!;
            coordinator.Complete(next);
            _ = coordinator.TakeNext(operational: true);
        }
        await Assert.That(first.MarkConnectionSequence).IsTrue();
        await Assert.That(first.StartWorker).IsTrue();
        await Assert.That(duplicate.MarkConnectionSequence).IsTrue();
        await Assert.That(duplicate.StartWorker).IsFalse();
        await Assert.That(afterExpiry.MarkConnectionSequence).IsTrue();
        await Assert.That(afterExpiry.StartWorker).IsFalse();
        await Assert.That(next.Endpoint.Host).IsEqualTo("restarted-target");
    }

    [Test]
    public async Task PublicationEpochAndDrainTrackingStayIndependent()
    {
        var coordinator = new MovingHandoffCoordinator();
        var epoch = coordinator.HandoffEpoch;
        await Assert.That(coordinator.IsCurrent(4, 4, epoch)).IsTrue();
        await Assert.That(coordinator.IsCurrent(-1, 4, epoch)).IsFalse();
        coordinator.PublishHandoffEpoch();
        await Assert.That(coordinator.IsCurrent(4, 4, epoch)).IsTrue();
        coordinator.PublishHandoffEpoch();
        await Assert.That(coordinator.IsCurrent(4, 4, epoch)).IsFalse();

        Task idle;
        Task completed;
        bool idleBeforeEnd;
        lock (coordinator.Gate)
        {
            coordinator.BeginDrain();
            idle = coordinator.WaitForDrains();
            idleBeforeEnd = idle.IsCompleted;
            coordinator.EndDrain();
            completed = coordinator.WaitForDrains();
        }
        await Assert.That(idleBeforeEnd).IsFalse();
        await Assert.That(completed.IsCompleted).IsTrue();
    }

    [Test]
    public async Task DrainWaiterTracksLaterDrainCycles()
    {
        var coordinator = new MovingHandoffCoordinator();
        Task first;
        Task second;
        bool secondInitiallyCompleted;
        lock (coordinator.Gate)
        {
            coordinator.BeginDrain();
            first = coordinator.WaitForDrains();
            coordinator.EndDrain();
            coordinator.BeginDrain();
            second = coordinator.WaitForDrains();
            secondInitiallyCompleted = second.IsCompleted;
            coordinator.EndDrain();
        }

        await Assert.That(first.IsCompleted).IsTrue();
        await Assert.That(secondInitiallyCompleted).IsFalse();
        await Assert.That(second.IsCompleted).IsTrue();
    }

    [Test]
    public async Task ForgetSequencesKeepsOnlyPeersStillPublished()
    {
        var coordinator = new MovingHandoffCoordinator();
        using var stop = new CancellationTokenSource();
        var retiredPeer = ("retired", 6379);
        var livePeer = ("live", 6379);
        bool retiredFence;
        bool liveFence;
        bool hasFencesBeforeForget;
        lock (coordinator.Gate)
        {
            _ = Queue(coordinator, retiredPeer, 1, "target", stop.Token, Environment.TickCount64);
            _ = Queue(coordinator, livePeer, 1, "target", stop.Token, Environment.TickCount64);
            hasFencesBeforeForget = coordinator.HasSequenceFences;
            coordinator.ForgetSequences(new HashSet<(string Host, int Port)> { livePeer });
            retiredFence = coordinator.HasSequenceFence(retiredPeer);
            liveFence = coordinator.HasSequenceFence(livePeer);
            _ = coordinator.TakeNext(operational: false);
        }

        await Assert.That(hasFencesBeforeForget).IsTrue();
        await Assert.That(retiredFence).IsFalse();
        await Assert.That(liveFence).IsTrue();
    }

    private static MovingHandoffCoordinator.QueueResult Queue(MovingHandoffCoordinator coordinator,
        (string Host, int Port) peer, long sequence, string target, CancellationToken stop,
        long now, long? deadline = null)
        => coordinator.Queue(operational: true, current: true, long.MinValue, peer, sequence,
            new RespireEndpoint(target, 6379), deadline ?? now + 30_000, now, stop);
}
