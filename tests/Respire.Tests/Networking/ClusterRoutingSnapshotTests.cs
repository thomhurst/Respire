using System.Runtime.CompilerServices;
using System.Reflection;
using Respire.Infrastructure;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClusterRoutingSnapshotTests
{
    [Test]
    public async Task IncrementalPublicationsMatchFullStagingRebuild()
    {
        await using var client = CreateClient();
        var router = client.Core.Cluster!;
        router.ApplyTopology(Topology(6379, 6380), 0, 1);
        AssertMatchesFullRebuild(router);
        var target = router.GetMultiplexer(new("localhost", 6381));
        int[] slots = [0, 255, 256, 511, 512, 16383];
        foreach (var slot in slots)
        {
            router.SetSlotOwner(slot, target);
            AssertMatchesFullRebuild(router);
        }
        foreach (var slot in slots)
        {
            router.ClearSlotOwner(slot, target);
            AssertMatchesFullRebuild(router);
        }
        router.ApplyTopology(Topology(6382, 6383), router.TopologyVersion, 2);
        AssertMatchesFullRebuild(router);
    }

    // Test-only oracle runs in both build configurations, without adding work to publication.
    private static void AssertMatchesFullRebuild(ClusterRouter router)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object Field(string name) => typeof(ClusterRouter).GetField(name, flags)!.GetValue(router)!;
        lock (Field("_nodesGate"))
        {
            var owners = (RespireConnectionMultiplexer?[])Field("_slots");
            var routes = (ClusterReplicaSet?[])Field("_replicasBySlot");
            var published = router.RoutingSnapshot;
            var rebuilt = ClusterRoutingSnapshot.Empty.Publish(owners, routes, ulong.MaxValue,
                published.Masters, published.ReplicaNodes, published.Replicas,
                published.MasterSlotCounts, published.IsComplete);
            for (var slot = 0; slot < ClusterHash.SlotCount; slot++)
                if (published[slot] != rebuilt[slot])
                    throw new InvalidOperationException($"Incremental publication differs from staging at slot {slot}.");
        }
    }

    [Test]
    public async Task UnchangedPublicationReusesSnapshotButChangedCountsAndCompletenessDoNot()
    {
        await using var client = CreateClient();
        var router = client.Core.Cluster!;
        router.ApplyTopology(Topology(6379, 6380), 0, 1);
        var before = router.RoutingSnapshot;
        var counts = (int[])before.MasterSlotCounts.Clone();
        var unchanged = before.Publish([], [], 0, before.Masters, before.ReplicaNodes, before.Replicas, counts, true);
        await Assert.That(unchanged).IsSameReferenceAs(before);

        counts[0]--;
        var changed = before.Publish([], [], 0, before.Masters, before.ReplicaNodes, before.Replicas, counts, true);
        await Assert.That(changed.MasterSlotCounts[0]).IsEqualTo(16383);
        await Assert.That(before.MasterSlotCounts[0]).IsEqualTo(16384);
        counts[0]--;
        await Assert.That(changed.MasterSlotCounts[0]).IsEqualTo(16383);
        var incomplete = changed.Publish([], [], 0, changed.Masters, changed.ReplicaNodes,
            changed.Replicas, changed.MasterSlotCounts, false);
        await Assert.That(incomplete.IsComplete).IsFalse();
        await Assert.That(changed.IsComplete).IsTrue();
    }

    [Test]
    [NotInParallel]
    public async Task SnapshotSelectionDoesNotAllocate()
    {
        await using var client = CreateClient();
        var router = client.Core.Cluster!;
        router.ApplyTopology(Topology(6379, 6380), 0, 1);
        _ = MeasureSelection(router, false);
        _ = MeasureSelection(router, true);
        var (allocated, control) = AllocationMeasurement.WithoutConcurrentGc(() =>
            (MeasureSelection(router, false), MeasureSelection(router, true)));
        await Assert.That(allocated).IsEqualTo(0);
        await Assert.That(control).IsGreaterThanOrEqualTo(37_000);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureSelection(ClusterRouter router, bool allocate)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1_000; index++)
        {
            var route = router.RoutingSnapshot[index];
            GC.KeepAlive(route.Primary);
            var selector = new ClusterReplicaSelector(route.Replicas!);
            selector.TryNext(out var replica);
            GC.KeepAlive(replica);
            if (allocate) GC.KeepAlive(AllocateControl());
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object AllocateControl() => new byte[37];

    private static RespireClient CreateClient() => RespireClient.Create(new RespireOptions
    {
        UseCluster = true,
        Endpoints = [new("localhost", 6379)],
    });

    private static List<ClusterTopologyRange> Topology(int primary, int replica) =>
    [
        new(0, 16383, new("localhost", primary), $"primary-{primary}", [])
        {
            Replicas = [new(new("localhost", replica), $"replica-{replica}", [])],
        },
    ];

    [Test]
    public async Task CapturedSnapshotKeepsPrimaryAndReplicaMembershipAfterRedirectAndDiscovery()
    {
        await using var client = CreateClient();
        var router = client.Core.Cluster!;
        router.ApplyTopology(Topology(6379, 6380), 0, 1);
        var before = router.RoutingSnapshot;
        var owner = before[0].Primary;
        var replicas = before[0].Replicas;
        var target = router.GetMultiplexer(new("localhost", 6381));
        router.SetSlotOwner(0, target);
        var redirected = router.RoutingSnapshot;
        await Assert.That(redirected[0].Primary).IsSameReferenceAs(target);
        await Assert.That(redirected[0].Replicas!.Nodes).IsEmpty();
        await Assert.That(before[0].Primary).IsSameReferenceAs(owner);
        await Assert.That(before[0].Replicas).IsSameReferenceAs(replicas);
        await Assert.That(redirected[1].Replicas).IsSameReferenceAs(replicas);
        await Assert.That(replicas!.Nodes.Length).IsEqualTo(1);

        // A reply started before MOVED can replace other slots, but not either half of slot 0.
        router.ApplyTopology(Topology(6382, 6383), 0, 2);
        var refreshed = router.RoutingSnapshot;
        await Assert.That(refreshed[0]).IsEqualTo(redirected[0]);
        await Assert.That(refreshed[1].Primary!.Port).IsEqualTo(6382);
        await Assert.That(refreshed[1].Replicas!.Nodes[0].Port).IsEqualTo(6383);
        await Assert.That(before[16383].Primary).IsSameReferenceAs(owner);
        await Assert.That(before[16383].Replicas).IsSameReferenceAs(replicas);
    }

    [Test]
    public async Task RedirectRetiresReplicaOnlyAfterItsLastCoveredSlotMoves()
    {
        await using var client = CreateClient();
        var router = client.Core.Cluster!;
        var topology = Topology(6379, 6380);
        topology[0] = topology[0] with { End = 1 };
        router.ApplyTopology(topology, 0, 1);
        var replica = router.RoutingSnapshot[0].Replicas!.Nodes[0];
        var target = router.GetMultiplexer(new("localhost", 6381));
        router.SetSlotOwner(0, target);
        await Assert.That(replica.IsRetired).IsFalse();
        await Assert.That(router.RoutingSnapshot.ReplicaNodes).Contains(replica);
        router.SetSlotOwner(1, target);
        await Assert.That(router.RoutingSnapshot.ReplicaNodes).IsEmpty();
        await Assert.That(replica.IsRetired).IsTrue();
    }

    [Test]
    public async Task ConcurrentReadersNeverPairOwnersAndReplicasFromDifferentPublications()
    {
        await using var client = CreateClient();
        var router = client.Core.Cluster!;
        router.ApplyTopology(Topology(6379, 6380), 0, 1);
        using var stop = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = Task.Run(() =>
        {
            int[] slots = [0, 255, 256, 16383];
            var samples = 0;
            while (!stop.IsCancellationRequested)
            {
                var snapshot = router.RoutingSnapshot;
                foreach (var slot in slots)
                {
                    var route = snapshot[slot];
                    if (route.Primary!.Port + 1 != route.Replicas!.Nodes[0].Port)
                        throw new InvalidOperationException("Mixed topology publication.");
                }
                samples++;
                if (samples == 1) started.SetResult();
            }
            return samples;
        });
        try
        {
            await started.Task;
            for (var generation = 2; generation <= 100; generation++)
            {
                var primary = generation % 2 == 0 ? 6381 : 6379;
                router.ApplyTopology(Topology(primary, primary + 1), router.TopologyVersion, generation);
            }
        }
        finally { stop.Cancel(); }
        await Assert.That(await reader).IsGreaterThan(0);
    }
}
