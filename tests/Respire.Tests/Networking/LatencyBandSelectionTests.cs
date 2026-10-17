using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class LatencyBandSelectionTests
{
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
