using System.Collections.Concurrent;
using System.Diagnostics;
using Respire.Testing.Containers;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.IntegrationTests;

public class AvailabilityZoneIntegrationTests
{
    [Test]
    [Arguments(RespProtocol.Resp2)]
    [Arguments(RespProtocol.Resp3)]
    public async Task ValkeyClusterPrefersLocalPrimaryOnlyWhenRequested(RespProtocol protocol)
    {
        await using var cluster = await RedisReadReplicaClusterTestContainer.StartAsync(RespireContainerServer.Valkey);
        for (var node = 0; node < 6; node++)
            await cluster.SetAvailabilityZoneAsync(node, node < 3 ? "local" : "remote");
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            UseCluster = true, Protocol = protocol, Connections = 1,
            Endpoints = [new(cluster.Host, cluster.Port(0))], ClientAvailabilityZone = "local",
        });
        const string key = "{zone-integration}:key";
        await client.SetAsync(key, "value");
        var selected = new ConcurrentQueue<int>();
        using var listener = Listen(selected, Enumerable.Range(0, 6).Select(cluster.Port));
        await using var replicaReads = client.WithReadFrom(RespireReadFrom.AzAffinity);
        await replicaReads.GetStringAsync(key);
        await Assert.That(selected.TryDequeue(out var replica)).IsTrue();
        await Assert.That(Enumerable.Range(3, 3).Select(cluster.Port).Contains(replica)).IsTrue();
        await using var zoneReads = client.WithReadFrom(RespireReadFrom.AzAffinityReplicasAndPrimary);
        await zoneReads.GetStringAsync(key);
        await Assert.That(selected.TryDequeue(out var primary)).IsTrue();
        await Assert.That(Enumerable.Range(0, 3).Select(cluster.Port).Contains(primary)).IsTrue();
    }

    [Test]
    [Arguments(false, RespProtocol.Resp2)]
    [Arguments(false, RespProtocol.Resp3)]
    [Arguments(true, RespProtocol.Resp2)]
    [Arguments(true, RespProtocol.Resp3)]
    public async Task ValkeyReplicaAndSentinelRoutingUsesZoneAndFallsBack(bool sentinel, RespProtocol protocol)
    {
        await using var fixture = await RespireContainerFixture.StartAsync(new()
        {
            Server = RespireContainerServer.Valkey, Topology = RespireContainerTopology.Sentinel,
        });
        for (var index = 0; index < 2; index++)
        {
            await using var admin = await RespireClient.ConnectAsync(new RespireOptions
            {
                Endpoints = [fixture.DataEndpoints[index]], AllowAdmin = true,
            });
            using var reply = await admin.ExecuteAsync(RespireCommands.Server.CONFIG_SET,
                "availability-zone", index == 0 ? "remote" : "local");
        }
        var options = sentinel ? fixture.CreateOptions() : new RespireOptions
        {
            Endpoints = [fixture.DataEndpoints[0]], ReplicaEndpoints = [fixture.DataEndpoints[1]],
        };
        await using var client = await RespireClient.ConnectAsync(options with
        {
            Protocol = protocol, Connections = 1, ClientAvailabilityZone = "local",
        });
        const string key = "zone-integration:key";
        await client.SetAsync(key, "value");
        await using var reader = client.WithReadFrom(RespireReadFrom.AzAffinityReplicasAndPrimary);
        var selected = new ConcurrentQueue<int>();
        using var listener = Listen(selected, fixture.DataEndpoints.Select(endpoint => endpoint.Port));
        await reader.GetStringAsync(key);
        await Assert.That(selected.TryDequeue(out var replica)).IsTrue();
        await Assert.That(replica).IsEqualTo(fixture.DataEndpoints[1].Port);
        await fixture.StopDataNodeAsync(1);
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            while (client.Core.ReadRouter.IsConnected) await Task.Delay(10, deadline.Token);
        await reader.GetStringAsync(key);
        await Assert.That(selected.TryDequeue(out var primary)).IsTrue();
        await Assert.That(primary).IsEqualTo(fixture.DataEndpoints[0].Port);
    }

    private static ActivityListener Listen(ConcurrentQueue<int> selected, IEnumerable<int> ports)
    {
        var ownedPorts = ports.ToHashSet();
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Respire",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.GetTagItem("db.operation.name") is "GET"
                    && activity.GetTagItem("server.port") is { } port && ownedPorts.Contains(Convert.ToInt32(port)))
                    selected.Enqueue(Convert.ToInt32(port));
            },
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
