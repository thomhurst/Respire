using System.Text;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ServerClusterInspectionCommandTests
{
    [Test]
    public async Task NodeAndIdentityCommandsHaveInitializedVerbs()
    {
        await using var server = new FakeRespServer(Bulk("id :0@0 myself,master - 0 0 0 connected 0-16383\n"), Bulk("id"), Bulk("shard"));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var nodes = await client.Server.ClusterNodesAsync();
        await Assert.That(nodes.Single().Id).IsEqualTo("id");
        await Assert.That(await client.Server.ClusterMyIdAsync()).IsEqualTo("id");
        await Assert.That(await client.Server.ClusterMyShardIdAsync()).IsEqualTo("shard");
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(["CLUSTER NODES", "CLUSTER MYID", "CLUSTER MYSHARDID"]);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task StructuredRepliesOwnResp2AndResp3Fields(bool resp3)
    {
        byte[] binary = [255, 0];
        var member = Map(resp3, Bulk("id"), Bulk("member"), Bulk("role"), Bulk("replica"), Bulk("health"), Bulk("loading"),
            Bulk("replication-offset"), Integer(12), Bulk("endpoint"), Bulk("?"), Bulk("port"), Integer(0), Bulk("future"), Bulk(binary));
        var shard = Map(resp3, Bulk("slots"), Array(Integer(1), Integer(3)), Bulk("nodes"), Array(member));
        var link = Map(resp3, Bulk("direction"), Bulk("from"), Bulk("node"), Bulk("peer"), Bulk("create-time"), Integer(1234),
            Bulk("events"), Bulk("r"), Bulk("send-buffer-allocated"), Integer(512), Bulk("send-buffer-used"), Integer(4), Bulk("future"), Bulk(binary));
        var stats = Array(Integer(2), Map(resp3, Bulk("key-count"), Integer(1), Bulk("memory-bytes"), Integer(42)));
        await using var server = new FakeRespServer(Array(shard), Array(link), Array(stats));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var shards = await client.Server.ClusterShardsAsync();
        var links = await client.Server.ClusterLinksAsync();
        var slots = await client.Server.ClusterSlotStatsAsync(1, 3);
        await client.DisposeAsync();
        await Assert.That(shards[0].Nodes[0].Role).IsEqualTo("replica");
        await Assert.That(shards[0].Nodes[0].Endpoint).IsEqualTo("?");
        await Assert.That(shards[0].Nodes[0].AdditionalFields["future"].AsBytes()).IsEquivalentTo(binary);
        await Assert.That(links[0].NodeId).IsEqualTo("peer");
        await Assert.That(links[0].CreatedUnixMilliseconds).IsEqualTo(1234);
        await Assert.That(links[0].SendBufferUsed).IsEqualTo(4);
        await Assert.That(links[0].AdditionalFields["future"].AsBytes()).IsEquivalentTo(binary);
        await Assert.That(slots[0].Slot).IsEqualTo(2);
        await Assert.That(slots[0].MemoryBytes).IsEqualTo(42);
    }

    [Test]
    public async Task KeySlotAppliesPrefixButSlotArgumentsRemainLiteral()
    {
        await using var server = new FakeRespServer(Integer(3), Integer(2), Array(), Array(), Array());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var facet = client.WithKeyPrefix("tenant:").Server;
        await Assert.That(await facet.ClusterKeySlotAsync((byte[])[255, 0, 13, 10])).IsEqualTo(3);
        await Assert.That(await facet.ClusterCountKeysInSlotAsync(7)).IsEqualTo(2);
        await facet.ClusterSlotStatsAsync(0, 16383);
        await facet.ClusterSlotStatsByMetricAsync(RespireClusterSlotMetric.NetworkBytesOut, 4, descending: false);
        await facet.ClusterSlotStatsByMetricAsync(RespireClusterSlotMetric.KeyCount);
        await Assert.That(server.ReceivedArguments[0][2]).IsEquivalentTo((byte[])[.. "tenant:"u8, 255, 0, 13, 10]);
        await Assert.That(server.ReceivedCommands.Skip(1)).IsEquivalentTo([
            "CLUSTER COUNTKEYSINSLOT 7", "CLUSTER SLOT-STATS SLOTSRANGE 0 16383",
            "CLUSTER SLOT-STATS ORDERBY NETWORK-BYTES-OUT LIMIT 4 ASC", "CLUSTER SLOT-STATS ORDERBY KEY-COUNT DESC"]);
    }

    [Test]
    public async Task InvalidRangesLimitsAndCancellationDoNotSend()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await Assert.That(async () => await client.Server.ClusterCountKeysInSlotAsync(-1)).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Server.ClusterCountKeysInSlotOnAllNodesAsync(16384)).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Server.ClusterSlotStatsAsync(1, 0)).ThrowsExactly<ArgumentException>();
        await Assert.That(async () => await client.Server.ClusterSlotStatsOnAllNodesAsync(0, 16384)).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Server.ClusterSlotStatsByMetricAsync(RespireClusterSlotMetric.KeyCount, 0)).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Server.ClusterSlotStatsByMetricOnAllNodesAsync(RespireClusterSlotMetric.KeyCount, 16385)).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Server.ClusterSlotStatsByMetricAsync((RespireClusterSlotMetric)99)).ThrowsExactly<ArgumentOutOfRangeException>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(async () => await client.Server.ClusterInfoAsync(cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(async () => await client.Server.ClusterShardsOnAllNodesAsync(cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task FanOutKeepsReplicaErrorsAndNeverPublishesInspectedTopology()
    {
        await using var replica = new FakeRespServer("-NOPERM denied on replica\r\n"u8.ToArray());
        await using var seed = new FakeRespServer(2, Bulk("view-of-seed"));
        seed.SuppressReply = command =>
        {
            if (command == "CLUSTER SLOTS") { _ = seed.SendRawAsync(Slots(seed.Port)); return true; }
            if (command == "CLUSTER NODES")
            {
                var nodes = $"self 127.0.0.1:{seed.Port}@2 myself,master - 0 0 1 connected 0-16383\nreplica 127.0.0.1:{replica.Port}@2 slave self 0 0 1 connected\n";
                _ = seed.SendRawAsync(Bulk(nodes), seed.ReceivedConnectionIds[^1]);
                return true;
            }
            if (command != "CLUSTER SHARDS") return false;
            // Deliberately different from the active slot map; inspection must not install it.
            var node = Map(true, Bulk("id"), Bulk("unreachable"), Bulk("role"), Bulk("master"), Bulk("health"), Bulk("online"),
                Bulk("replication-offset"), Integer(0), Bulk("endpoint"), Bulk("not-a-real-host.invalid"), Bulk("port"), Integer(1));
            _ = seed.SendRawAsync(Array(Map(true, Bulk("slots"), Array(Integer(0), Integer(16383)), Bulk("nodes"), Array(node))), seed.ReceivedConnectionIds[^1]);
            return true;
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true, Connections = 1, Endpoints = [new("127.0.0.1", seed.Port)],
        });
        var results = await client.Server.ClusterShardsOnAllNodesAsync();
        await Assert.That(results.Length).IsEqualTo(2);
        await Assert.That(results.Single(x => x.Endpoint.Port == seed.Port).Value[0].Nodes[0].Endpoint).IsEqualTo("not-a-real-host.invalid");
        await Assert.That(results.Single(x => x.Endpoint.Port == replica.Port).Error).IsTypeOf<RespireServerException>();
        await Assert.That(await client.GetStringAsync("still-on-seed")).IsEqualTo("view-of-seed");
        await Assert.That(replica.ReceivedCommands).IsEquivalentTo(["CLUSTER SHARDS"]);
    }

    [Test]
    public async Task CancellationAfterDiscoveryRemainsAttributed()
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply)
        {
            SuppressReply = command => { if (command != "CLUSTER LINKS") return false; received.TrySetResult(); return true; },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var cancellation = new CancellationTokenSource();
        var operation = client.Server.ClusterLinksOnAllNodesAsync(cancellation.Token).AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var results = await operation.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(results.Single().Endpoint.Port).IsEqualTo(server.Port);
        await Assert.That(results[0].Error).IsAssignableTo<OperationCanceledException>();
    }

    [Test]
    public async Task InspectionPreservesClientCacheAndParsesResp3VerbatimInfo()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        server.SuppressReply = command =>
        {
            byte[]? reply = command switch
            {
                "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
                "CLUSTER INFO" => "=22\r\ntxt:cluster_state:ok\r\n\r\n"u8.ToArray(),
                "CLUSTER SHARDS" => Array(),
                "CLUSTER KEYSLOT key" => Integer(12539),
                _ => null,
            };
            if (reply is null) return false;
            _ = server.SendRawAsync(reply, server.ReceivedConnectionIds[^1]);
            return true;
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = [new("127.0.0.1", server.Port)], Connections = 1, ClientSideCache = new(),
        });
        var cache = client.Core.ClientCache!;
        RespireKey key = "cached";
        var token = cache.BeginRead(in key);
        var value = RespValue.BulkString("old");
        cache.CompleteRead(in token, in value, allowInsert: true);
        await Assert.That((await client.Server.ClusterInfoAsync()).State).IsEqualTo("ok");
        await client.Server.ClusterShardsAsync();
        await client.Server.ClusterKeySlotAsync("key");
        await Assert.That(cache.Count).IsEqualTo(1);
    }

    private static byte[] Bulk(string value) => Bulk(Encoding.UTF8.GetBytes(value));
    private static byte[] Bulk(byte[] value) => [.. Encoding.ASCII.GetBytes($"${value.Length}\r\n"), .. value, 13, 10];
    private static byte[] Integer(long value) => Encoding.ASCII.GetBytes($":{value}\r\n");
    private static byte[] Array(params byte[][] values) => [.. Encoding.ASCII.GetBytes($"*{values.Length}\r\n"), .. values.SelectMany(x => x)];
    private static byte[] Map(bool resp3, params byte[][] values)
        => [.. Encoding.ASCII.GetBytes(resp3 ? $"%{values.Length / 2}\r\n" : $"*{values.Length}\r\n"), .. values.SelectMany(x => x)];
    private static byte[] Slots(int port) => Array(Array(Integer(0), Integer(16383), Array(Bulk("127.0.0.1"), Integer(port))));
}
