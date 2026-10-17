using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class LatencyBandSelectionTests
{
    [Test]
    [Arguments(2)]
    [Arguments(12)]
    public async Task SnapshotReuseRetainsImmutableObservationUntilBandChanges(int candidateCount)
    {
        RespireReadRoutingSnapshot? snapshot = null;
        for (uint start = 0; start < candidateCount; start++)
        {
            var selection = new NearestReadSelection<int>(start, candidateCount, TimeSpan.Zero);
            while (selection.TryNext(out var index))
                selection.QueueSample(index, ValueTask.FromResult(ReadLatencyResult.Measured(index % 2)));
            await Assert.That(selection.TryGet(out _)).IsTrue();
            var captured = selection.CaptureSnapshot(RespireReadFrom.Replica, null, value => new("host", value), snapshot);
            if (snapshot is not null) await Assert.That(captured).IsSameReferenceAs(snapshot);
            snapshot = captured;
        }
        var changed = new NearestReadSelection<int>(0, 1, TimeSpan.Zero);
        changed.QueueSample(99, ValueTask.FromResult(ReadLatencyResult.Measured(0)));
        await Assert.That(changed.TryGet(out _)).IsTrue();
        var replacement = changed.CaptureSnapshot(RespireReadFrom.Replica, null, value => new("host", value), snapshot);
        await Assert.That(replacement).IsNotSameReferenceAs(snapshot);
        await Assert.That(replacement.EligibleEndpoints.Single().Port).IsEqualTo(99);
        await Assert.That(snapshot!.EligibleEndpoints.Count).IsEqualTo(candidateCount / 2);
    }

    [Test, NotInParallel]
    public async Task WarmSmallBandSelectionAndUnchangedSnapshotAllocateNothing()
    {
        _ = MeasureBand(false);
        _ = MeasureBand(true);
        var result = AllocationMeasurement.WithoutConcurrentGc(() => (MeasureBand(false), MeasureBand(true)));
        await Assert.That(result.Item1).IsEqualTo(0L);
        await Assert.That(result.Item2).IsGreaterThanOrEqualTo(37_000L);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static long MeasureBand(bool allocate)
    {
        var initial = new NearestReadSelection<int>(0, 3, TimeSpan.Zero);
        initial.QueueSample(0, ValueTask.FromResult(ReadLatencyResult.Measured(10)));
        initial.QueueSample(2, ValueTask.FromResult(ReadLatencyResult.Measured(10)));
        _ = initial.TryGet(out _);
        var snapshot = initial.CaptureSnapshot(RespireReadFrom.Replica, null, static value => new("host", value));
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (uint start = 0; start < 1_000; start++)
        {
            var selection = new NearestReadSelection<int>(start, 3, TimeSpan.Zero);
            while (selection.TryNext(out var index))
                selection.QueueSample(index, ValueTask.FromResult(ReadLatencyResult.Measured(index == 1 ? 100 : 10)));
            if (!selection.TryGet(out var selected) || selected == 1
                || !ReferenceEquals(snapshot, selection.CaptureSnapshot(RespireReadFrom.Replica, null,
                    static value => new("host", value), snapshot)))
                throw new InvalidOperationException("The warm band changed unexpectedly.");
            if (allocate) GC.KeepAlive(new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Test]
    public async Task ClusterRangeTicketsRetainFullRotationAndRemainIndependent()
    {
        var firstRange = new ClusterReplicaSet([], TimeSpan.Zero);
        var secondRange = new ClusterReplicaSet([], TimeSpan.Zero);
        for (uint expected = 1; expected <= 6; expected++)
        {
            foreach (var range in new[] { firstRange, secondRange })
            {
                var selection = new NearestReadSelection<int>(range.NextLatencyStart(), 3, TimeSpan.Zero);
                while (selection.TryNext(out var index))
                    selection.QueueSample(index, ValueTask.FromResult(ReadLatencyResult.Measured(index == 0 ? 100 : 10)));
                await Assert.That(selection.TryGet(out var selected)).IsTrue();
                await Assert.That(selected).IsEqualTo((int)(expected % 2) + 1);
            }
        }
    }

    [Test]
    public async Task RotationIsUniformWithinBandDespiteGapsInTopology()
    {
        var counts = new int[5];
        for (uint start = 0; start < 20; start++)
        {
            var selection = new NearestReadSelection<int>(start, 5, TimeSpan.FromTicks(5));
            while (selection.TryNext(out var index))
                selection.QueueSample(index, ValueTask.FromResult(ReadLatencyResult.Measured(index is 1 or 2 ? 10 : 100)));
            await Assert.That(selection.TryGet(out var selected)).IsTrue();
            counts[selected]++;
        }
        await Assert.That(counts[1]).IsEqualTo(10);
        await Assert.That(counts[2]).IsEqualTo(10);
        await Assert.That(counts.Sum()).IsEqualTo(20);
    }

    [Test]
    public async Task FasterFallbackTierCannotDisplacePreferredUnknownTier()
    {
        var selection = new NearestReadSelection<string>(0, 2, TimeSpan.MaxValue);
        selection.QueueSample("fallback", ValueTask.FromResult(ReadLatencyResult.Measured(0)), priority: 1);
        selection.QueueSample("preferred", ValueTask.FromResult(ReadLatencyResult.Unknown));
        await Assert.That(selection.TryGet(out var selected)).IsTrue();
        await Assert.That(selected).IsEqualTo("preferred");
    }

    [Test]
    [Arguments(0L, "fast")]
    [Arguments(5L, "near")]
    [Arguments(10L, "slow")]
    [Arguments(long.MaxValue, "slow")]
    public async Task BandUsesFinalMinimumAndInclusiveOverflowSafeTolerance(long tolerance, string expected)
    {
        var selection = new NearestReadSelection<string>(0, 3, TimeSpan.FromTicks(tolerance));
        selection.QueueSample("slow", ValueTask.FromResult(ReadLatencyResult.Measured(20)));
        selection.QueueSample("near", ValueTask.FromResult(ReadLatencyResult.Measured(15)));
        selection.QueueSample("fast", ValueTask.FromResult(ReadLatencyResult.Measured(10)));
        await Assert.That(selection.TryGet(out var selected)).IsTrue();
        await Assert.That(selected).IsEqualTo(expected);
    }

    [Test]
    public async Task UnknownAndPendingSamplesCannotDisplaceMeasuredBand()
    {
        var selection = new NearestReadSelection<string>(0, 4, TimeSpan.MaxValue);
        selection.QueueSample("unknown", ValueTask.FromResult(ReadLatencyResult.Unknown));
        selection.QueueSample("pending", ValueTask.FromResult(ReadLatencyResult.Pending));
        selection.QueueSample("measured", ValueTask.FromResult(ReadLatencyResult.Measured(long.MaxValue - 1)));
        await Assert.That(selection.TryGet(out var selected)).IsTrue();
        await Assert.That(selected).IsEqualTo("measured");
        var snapshot = selection.CaptureSnapshot(RespireReadFrom.Replica, null, value => new(value));
        await Assert.That(snapshot.EligibleEndpoints.Single().Host).IsEqualTo("measured");
    }

    [Test]
    public async Task UnknownSamplesRotateWhenNoMeasurementExists()
    {
        for (uint start = 0; start < 3; start++)
        {
            var selection = new NearestReadSelection<int>(start, 3, TimeSpan.Zero);
            while (selection.TryNext(out var index))
                selection.QueueSample(index, ValueTask.FromResult(ReadLatencyResult.Unknown));
            await Assert.That(selection.TryGet(out var selected)).IsTrue();
            await Assert.That(selected).IsEqualTo((int)start);
        }
    }

    [Test]
    public async Task PendingCompletionRetainsRotatedOrderAndLinkPriority()
    {
        var pending = new TaskCompletionSource<ReadLatencyResult>();
        var selection = new NearestReadSelection<string>(0, 3, TimeSpan.FromTicks(5));
        selection.QueueSample("first", new(pending.Task));
        selection.QueueSample("second", ValueTask.FromResult(ReadLatencyResult.Measured(10)));
        selection.QueueSample("unlinked", ValueTask.FromResult(ReadLatencyResult.Measured(0)), linked: false);
        pending.SetResult(ReadLatencyResult.Measured(15));
        while (selection.TryNextSample(out var sample))
            selection.Consider(sample.Candidate, await sample.Latency, sample.Linked, sample.Order, sample.Priority);
        await Assert.That(selection.TryGet(out var selected)).IsTrue();
        await Assert.That(selected).IsEqualTo("first");
    }

    [Test]
    public async Task NegativeToleranceIsRejectedBeforeConnecting()
    {
        await Assert.That(() => RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("localhost")], ReplicaLatencyTolerance = TimeSpan.FromTicks(-1),
        })).ThrowsExactly<RespireConfigurationException>();
    }
}
