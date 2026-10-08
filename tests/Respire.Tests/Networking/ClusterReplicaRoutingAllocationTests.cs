using System.Runtime.CompilerServices;
using System.Text;
using Respire.Infrastructure;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

[NotInParallel]
public class ClusterReplicaRoutingAllocationTests
{
    [Test]
    [Arguments("Replica")]
    [Arguments("ReplicaPreferred")]
    [Arguments("Nearest")]
    [Arguments("Pinned")]
    [Arguments("Primary")]
    public async Task PreparedHealthyRoutesReturnInlineWithoutRefresh(string selection)
    {
        await using var primary = new FakeRespServer(32, FakeRespServer.OkReply);
        await using var replica = new FakeRespServer(32, FakeRespServer.OkReply);
        var topology = Encoding.ASCII.GetBytes($"*1\r\n*4\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{primary.Port}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{replica.Port}\r\n");
        primary.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? topology : null;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            UseCluster = true,
            Endpoints = [new("127.0.0.1", primary.Port)],
            ClusterTopologyRefreshInterval = null,
        });
        var router = client.Core.Cluster!;
        var slot = ClusterHash.GetSlot("ready-replica");
        await router.GetReadConnectionAsync(slot, RespireReadFrom.Replica, default);
        var route = router.RoutingSnapshot[slot];
        var replicaNode = route.Replicas!.Nodes.Single();
        // Cache deterministic samples; route revalidation keeps its normal production interval.
        router.NearestLatency = new ReadLatencySampler<RespireConnection>(
            (connection, _) => ValueTask.FromResult(ReferenceEquals(connection.Multiplexer, replicaNode) ? 10L : 100L),
            static () => 0L);
        var pinned = selection == "Pinned";
        var policy = pinned ? RespireReadFrom.Replica : Enum.Parse<RespireReadFrom>(selection);
        var expectedNode = policy == RespireReadFrom.Primary ? route.Primary! : replicaNode;
        _ = Measure(router, slot, expectedNode, policy, pinned, false);
        _ = Measure(router, slot, expectedNode, policy, pinned, true);
        var topologyReads = primary.ReceivedCommands.Count(command => command == "CLUSTER SLOTS");
        var result = AllocationMeasurement.WithoutConcurrentGc(() =>
            (Measure(router, slot, expectedNode, policy, pinned, false),
                Measure(router, slot, expectedNode, policy, pinned, true)));

        await Assert.That(result.Item2 - result.Item1).IsGreaterThanOrEqualTo(37_000L);
        await Assert.That(primary.ReceivedCommands.Count(command => command == "CLUSTER SLOTS")).IsEqualTo(topologyReads);
        await Assert.That(replica.ReceivedCommands).Contains("READONLY");
#if !DEBUG
        // Debug builds allocate compiler-generated async state-machine objects. The
        // exact zero-byte contract applies to optimized Release selection; routing
        // correctness and the escaping allocation control run in both configurations.
        await Assert.That(result.Item1).IsEqualTo(0L);
#endif
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Measure(ClusterRouter router, int slot, RespireConnectionMultiplexer expectedNode,
        RespireReadFrom policy, bool pinned, bool allocate)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
        {
            var pending = pinned
                ? router.GetPinnedReadConnectionAsync(slot, expectedNode, default, revalidate: true, readFrom: policy)
                : router.GetReadConnectionAsync(slot, policy, default);
            // A prepared route must complete inline; no task allocation may hide in the measurement.
            if (!pending.IsCompletedSuccessfully)
                throw new InvalidOperationException("The prepared cluster route did not complete synchronously.");
            var connection = pending.GetAwaiter().GetResult();
            if (!ReferenceEquals(connection.Multiplexer, expectedNode))
                throw new InvalidOperationException("The prepared cluster route selected a different node.");
            if (allocate) GC.KeepAlive(new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
