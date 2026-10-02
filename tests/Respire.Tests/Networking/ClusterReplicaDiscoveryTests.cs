using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClusterReplicaDiscoveryTests
{
    [Test]
    public async Task ManyUncoveredSlotsShareOneFullReply()
    {
        var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var covered = false;
        var commands = 0;
        var baselineCommands = 0;
        var baseline = new ConcurrentDictionary<int, ClusterReplicaSet>();
        var baselineReads = Enumerable.Range(0, 256).Select(slot => baseline
            .GetOrAdd(slot, static _ => new([], TimeSpan.Zero))
            .JoinOrStartRefresh(() => { baselineCommands++; return reply.Task; })!).ToArray();
        var coordinator = new ClusterReplicaDiscovery(_ => { commands++; return reply.Task; }, _ => covered);
        var reads = Enumerable.Range(0, 256).Select(slot => coordinator.DiscoverAsync(slot, default).AsTask()).ToArray();
        covered = true;
        reply.SetResult();
        await Task.WhenAll(reads).WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(baselineReads).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(commands).IsEqualTo(1);
        await Assert.That(baselineCommands).IsEqualTo(256);
    }

    [Test]
    public async Task PartialReplyDoesNotConsumeAnotherSlotsThrottle()
    {
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var covered = new bool[2];
        var commands = 0;
        var coordinator = new ClusterReplicaDiscovery(slot =>
        {
            Interlocked.Increment(ref commands);
            if (slot == 0) return first.Task;
            secondStarted.SetResult();
            return second.Task;
        }, slot => Volatile.Read(ref covered[slot]));
        var low = coordinator.DiscoverAsync(0, default).AsTask();
        var high = coordinator.DiscoverAsync(1, default).AsTask();
        Volatile.Write(ref covered[0], true);
        first.SetResult();
        await low.WaitAsync(TimeSpan.FromSeconds(5));
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(high.IsCompleted).IsFalse();
        Volatile.Write(ref covered[1], true);
        second.SetResult();
        await high.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(commands).IsEqualTo(2);
    }

    [Test]
    public async Task CallerCancellationDoesNotCancelSharedProbe()
    {
        var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commands = 0;
        var covered = false;
        var coordinator = new ClusterReplicaDiscovery(_ => { commands++; return reply.Task; }, _ => covered);
        using var cancellation = new CancellationTokenSource();
        var canceled = coordinator.DiscoverAsync(0, cancellation.Token).AsTask();
        var survivor = coordinator.DiscoverAsync(1, default).AsTask();
        cancellation.Cancel();
        await Assert.That(async () => await canceled).Throws<OperationCanceledException>();
        await Assert.That(survivor.IsCompleted).IsFalse();
        covered = true;
        reply.SetResult();
        await survivor.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(commands).IsEqualTo(1);
    }

    [Test]
    public async Task UncoveredSlotsThrottleIndependentlyAndResetAfterOwnerChange()
    {
        long now = 10;
        var commands = 0;
        var coordinator = new ClusterReplicaDiscovery(_ => { commands++; return Task.CompletedTask; }, _ => false, () => now);
        for (var repeat = 0; repeat < 3; repeat++)
            for (var slot = 0; slot < 128; slot++) await coordinator.DiscoverAsync(slot, default);
        await Assert.That(commands).IsEqualTo(128);
        coordinator.Invalidate(3);
        await coordinator.DiscoverAsync(3, default);
        await Assert.That(commands).IsEqualTo(129);
        now += ClusterReplicaSet.RefreshIntervalMilliseconds;
        await coordinator.DiscoverAsync(4, default);
        await Assert.That(commands).IsEqualTo(130);
    }

    [Test]
    public async Task ChangedOwnerDuringProbeRequiresFreshCoverage()
    {
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commands = 0;
        var covered = false;
        var coordinator = new ClusterReplicaDiscovery(_ =>
        {
            if (++commands == 1) return first.Task;
            covered = true;
            return Task.CompletedTask;
        }, _ => covered);
        var pending = coordinator.DiscoverAsync(1, default).AsTask();
        coordinator.Invalidate(1);
        var joined = coordinator.DiscoverAsync(1, default).AsTask();
        first.SetResult();
        await Task.WhenAll(pending, joined).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(commands).IsEqualTo(2);
        await Assert.That(covered).IsTrue();
    }

    [Test]
    public async Task SlowUncoveredProbeCompletesOneAttemptDespiteExpiredThrottle()
    {
        long now = 10;
        var commands = 0;
        var coordinator = new ClusterReplicaDiscovery(_ =>
        {
            commands++;
            now += ClusterReplicaSet.RefreshIntervalMilliseconds;
            return Task.CompletedTask;
        }, _ => false, () => now);
        await coordinator.DiscoverAsync(1, default);
        await Assert.That(commands).IsEqualTo(1);
        await coordinator.DiscoverAsync(1, default);
        await Assert.That(commands).IsEqualTo(2);
    }

    [Test]
    public async Task ChangedOwnerAfterProbeCompletionRequiresFreshCoverage()
    {
        // Inline probe completion lets the test hold the coordinator gate across completion
        // and invalidation, before a resumed waiter can recheck the completed attempt.
        var first = new TaskCompletionSource();
        var commands = 0;
        var covered = false;
        var coordinator = new ClusterReplicaDiscovery(_ =>
        {
            if (Interlocked.Increment(ref commands) == 1) return first.Task;
            Volatile.Write(ref covered, true);
            return Task.CompletedTask;
        }, _ => Volatile.Read(ref covered));
        var pending = coordinator.DiscoverAsync(1, default).AsTask();
        var gate = typeof(ClusterReplicaDiscovery).GetField("_gate",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(coordinator)!;
        bool completed;
        lock (gate)
        {
            first.SetResult();
            var probe = (Task)typeof(ClusterReplicaDiscovery).GetField("_current",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(coordinator)!;
            completed = probe.IsCompleted;
            coordinator.Invalidate(1);
        }
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(completed).IsTrue();
        await Assert.That(commands).IsEqualTo(2);
        await Assert.That(covered).IsTrue();
    }

    [Test]
    [NotInParallel]
    public async Task UncoveredSlotBookkeepingAllocatesLessThanPerSlotCoalescers()
    {
        _ = MeasureBookkeeping(shared: false);
        _ = MeasureBookkeeping(shared: true);
        var (baseline, shared) = AllocationMeasurement.WithoutConcurrentGc(() =>
            (MeasureBookkeeping(shared: false), MeasureBookkeeping(shared: true)));
        Console.WriteLine($"256 uncovered slots: per-slot={baseline} bytes, shared={shared} bytes; 256 probes each.");
        await Assert.That(shared).IsLessThan(baseline);
        await Assert.That(shared).IsGreaterThan(0);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureBookkeeping(bool shared)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        if (shared)
        {
            var coordinator = new ClusterReplicaDiscovery(static _ => Task.CompletedTask, static _ => false);
            for (var slot = 0; slot < 256; slot++) coordinator.DiscoverAsync(slot, default).GetAwaiter().GetResult();
            GC.KeepAlive(coordinator);
        }
        else
        {
            var routes = new ConcurrentDictionary<int, ClusterReplicaSet>();
            for (var slot = 0; slot < 256; slot++)
                routes.GetOrAdd(slot, static _ => new([], TimeSpan.Zero))
                    .JoinOrStartRefresh(static () => Task.CompletedTask)!.GetAwaiter().GetResult();
            GC.KeepAlive(routes);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Test]
    [NotInParallel]
    public async Task CoveredSlotsDoNotAllocate()
    {
        var coordinator = new ClusterReplicaDiscovery(static _ => throw new InvalidOperationException("Covered slot probed"), static _ => true);
        _ = MeasureCovered(coordinator, false);
        _ = MeasureCovered(coordinator, true);
        var (allocated, control) = AllocationMeasurement.WithoutConcurrentGc(() =>
            (MeasureCovered(coordinator, false), MeasureCovered(coordinator, true)));
        await Assert.That(allocated).IsEqualTo(0);
        await Assert.That(control).IsGreaterThanOrEqualTo(37_000);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureCovered(ClusterReplicaDiscovery coordinator, bool allocate)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var slot = 0; slot < 1_000; slot++)
        {
            coordinator.DiscoverAsync(slot, default).GetAwaiter().GetResult();
            if (allocate) GC.KeepAlive(new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
