using System.Runtime.CompilerServices;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class DedicatedStreamRouteTests
{
    [Test, NotInParallel]
    [Arguments("standalone")]
    [Arguments("cluster")]
    [Arguments("ask")]
    public async Task CreatingAndValidatingRoutesAllocatesNothing(string mode)
    {
        await using var server = new FakeRespServer(16, FakeRespServer.OkReply);
        server.ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
            ? System.Text.Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{server.Port}\r\n")
            : FakeRespServer.OkReply;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, UseCluster = mode != "standalone",
            ClusterTopologyRefreshInterval = null, Endpoints = [new("127.0.0.1", server.Port)],
        });
        var slot = ClusterHash.GetSlot("key");
        var cluster = client.Core.Cluster;
        var pool = cluster is null ? client.Core.DedicatedPool
            : await cluster.GetDedicatedPoolAsync(slot, default, discovery: null);
        var connection = await pool.RentAsync(default, kind: DedicatedLeaseKind.Streaming);
        try
        {
            var version = cluster?.CaptureSlotVersion(slot) ?? default;
            var asking = mode == "ask";
            for (var i = 0; i < 20; i++)
            {
                Measure(client.Core, cluster, pool, connection, slot, version, asking, false);
                Measure(client.Core, cluster, pool, connection, slot, version, asking, true);
            }
            var measured = AllocationMeasurement.WithoutConcurrentGc(() => (
                Bytes: Measure(client.Core, cluster, pool, connection, slot, version, asking, false),
                Closures: Measure(client.Core, cluster, pool, connection, slot, version, asking, true)));
            await Assert.That(measured.Bytes).IsEqualTo(0L);
            await Assert.That(measured.Closures).IsGreaterThan(0L);
        }
        finally { pool.Return(connection); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Measure(ClientCore core, ClusterRouter? cluster, DedicatedConnectionPool pool,
        RespireConnection connection, int slot, ClusterRouter.StreamRouteVersion version, bool asking, bool control)
    {
        var start = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
        {
            var route = cluster is null ? new DedicatedStreamRoute(core, pool, connection)
                : new DedicatedStreamRoute(cluster, pool, connection, slot, version, asking);
            if (!route.IsCurrent()) throw new InvalidOperationException("The measured route must remain current.");
            if (control)
            {
                var legacy = CreateLegacyValidator(core, cluster, pool, connection, slot, version, asking);
                if (!legacy()) throw new InvalidOperationException("The legacy route must remain current.");
                GC.KeepAlive(legacy);
            }
        }
        return GC.GetAllocatedBytesForCurrentThread() - start;
    }

    // Positive control retains the previous per-attempt capturing-delegate shape.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Func<bool> CreateLegacyValidator(ClientCore core, ClusterRouter? cluster,
        DedicatedConnectionPool pool, RespireConnection connection, int slot,
        ClusterRouter.StreamRouteVersion version, bool asking)
        => () => cluster is null ? core.IsDedicatedStreamRouteCurrent(pool, connection)
            : !pool.IsStopping && pool.IsMovingPublicationCurrent
                && cluster.IsDedicatedStreamRouteCurrent(slot, version, connection, asking ? pool : null);
}
