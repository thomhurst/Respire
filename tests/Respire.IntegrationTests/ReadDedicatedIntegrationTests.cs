using System.Collections.Concurrent;
using System.Diagnostics;
using Respire.Testing.Containers;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
public class ReadDedicatedIntegrationTests
{
    [Test]
    [Arguments(false, RespProtocol.Resp2)]
    [Arguments(false, RespProtocol.Resp3)]
    [Arguments(true, RespProtocol.Resp2)]
    [Arguments(true, RespProtocol.Resp3)]
    public async Task ConfiguredAndSentinelBlockingReadsUseReplicaAndFallback(bool sentinel, RespProtocol protocol)
    {
        await using var fixture = await RespireContainerFixture.StartAsync(new()
        {
            Server = RespireContainerServer.Redis, Topology = RespireContainerTopology.Sentinel,
        });
        var options = sentinel ? fixture.CreateOptions() : new RespireOptions
        {
            Endpoints = [fixture.DataEndpoints[0]], ReplicaEndpoints = [fixture.DataEndpoints[1]],
        };
        await using var client = await RespireClient.ConnectAsync(options with
        {
            Protocol = protocol, Connections = 1,
        });
        const string key = "dedicated-integration:key";
        await client.SetAsync(key, "value");
        const string stream = "dedicated-integration:stream";
        using (await client.ExecuteAsync(RespireCommands.Stream.XADD, stream, "1-0", "field", "value")) { }
        await using (var replicaReader = client.WithReadFrom(RespireReadFrom.Replica))
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            while (await replicaReader.Streams.CountAsync(stream, deadline.Token) == 0)
                await Task.Delay(10, deadline.Token);
        await using var reader = client.WithReadFrom(RespireReadFrom.ReplicaPreferred);
        var selected = new ConcurrentQueue<int>();
        using var listener = Listen(selected, fixture.DataEndpoints.Select(endpoint => endpoint.Port));
        await reader.GetStringAsync(key);
        await Assert.That(selected.TryDequeue(out var replica)).IsTrue();
        await Assert.That(replica).IsEqualTo(fixture.DataEndpoints[1].Port);
        using (var reply = await reader.ExecuteAsync(RespireCommands.Stream.XREAD, "BLOCK", 1, "STREAMS", stream, "0"))
            await Assert.That(reply.IsNull).IsFalse();
        await Assert.That(selected.TryDequeue(out var blockingReplica)).IsTrue();
        await Assert.That(blockingReplica).IsEqualTo(fixture.DataEndpoints[1].Port);
        await fixture.StopDataNodeAsync(1);
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            while (client.Core.ReadRouter.IsConnected) await Task.Delay(10, deadline.Token);
        await reader.GetStringAsync(key);
        await Assert.That(selected.TryDequeue(out var primary)).IsTrue();
        await Assert.That(primary).IsEqualTo(fixture.DataEndpoints[0].Port);
        using (var reply = await reader.ExecuteAsync(RespireCommands.Stream.XREAD, "BLOCK", 1, "STREAMS", stream, "0"))
            await Assert.That(reply.IsNull).IsFalse();
        await Assert.That(selected.TryDequeue(out var blockingPrimary)).IsTrue();
        await Assert.That(blockingPrimary).IsEqualTo(fixture.DataEndpoints[0].Port);
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
                if (activity.GetTagItem("db.operation.name") is "GET" or "XREAD"
                    && activity.GetTagItem("server.port") is { } port && ownedPorts.Contains(Convert.ToInt32(port)))
                    selected.Enqueue(Convert.ToInt32(port));
            },
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
