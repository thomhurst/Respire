using System.Text;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class StreamMetadataCommandTests
{
    [Test]
    public async Task MetadataCommandsWriteAllOptionsAndPrefixOnlyTheKey()
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray(), ":0\r\n"u8.ToArray(),
            FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var view = client.WithKeyPrefix("tenant:");
        await Assert.That(await view.Streams.CreateConsumerAsync("events", "workers", "alice")).IsTrue();
        await Assert.That(await view.Streams.CreateConsumerAsync("events", "workers", "alice")).IsFalse();
        await view.Streams.SetLastIdAsync("events", "9-0");
        await view.Streams.SetLastIdAsync("events", "9-0", entriesAdded: 0);
        await view.Streams.SetLastIdAsync("events", "9-0", maxDeletedId: "2-0");
        await view.Streams.SetLastIdAsync("events", "9-0", entriesAdded: long.MaxValue, maxDeletedId: "2-0");
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "XGROUP CREATECONSUMER tenant:events workers alice",
            "XGROUP CREATECONSUMER tenant:events workers alice",
            "XSETID tenant:events 9-0",
            "XSETID tenant:events 9-0 ENTRIESADDED 0",
            "XSETID tenant:events 9-0 MAXDELETEDID 2-0",
            "XSETID tenant:events 9-0 ENTRIESADDED 9223372036854775807 MAXDELETEDID 2-0",
        }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task BinaryKeysRemainIntact()
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray(), FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        byte[] key = [0xff, 0, 0x80];
        var view = client.WithKeyPrefix("p:");
        await view.Streams.CreateConsumerAsync(key, "group", "consumer");
        await view.Streams.SetLastIdAsync(key, "1-0");
        byte[] expected = [.. "p:"u8, .. key];
        await Assert.That(server.ReceivedArguments[0][2]).IsEquivalentTo(expected, CollectionOrdering.Matching);
        await Assert.That(server.ReceivedArguments[1][1]).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    public async Task InvalidCountAndPreCanceledCallsDoNotWrite()
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await Assert.That(async () => await client.Streams.SetLastIdAsync("events", "1-0", entriesAdded: -1))
            .ThrowsExactly<ArgumentOutOfRangeException>();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.That(async () => await client.Streams.SetLastIdAsync("events", "1-0", cancellationToken: canceled.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(async () => await client.Streams.CreateConsumerAsync("events", "group", "consumer", canceled.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task ServerErrorsPreserveConnectionReplyOrder()
    {
        await using var server = new FakeRespServer("-NOGROUP missing group\r\n"u8.ToArray(),
            "-ERR invalid stream ID\r\n"u8.ToArray(), FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await Assert.That(async () => await client.Streams.CreateConsumerAsync("events", "missing", "consumer"))
            .Throws<RespireServerException>();
        await Assert.That(async () => await client.Streams.SetLastIdAsync("events", "$"))
            .Throws<RespireServerException>();
        await client.Streams.SetLastIdAsync("events", "1-0");
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(3);
    }

    [Test]
    public async Task ClusterRoutesMetadataToPrefixedKeyOwner()
    {
        await using var owner = new FakeRespServer(":1\r\n"u8.ToArray(), FakeRespServer.OkReply);
        var slot = ClusterHash.GetSlot("{tenant}:events");
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:{slot}\r\n:{slot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{owner.Port}\r\n");
        await using var seed = new FakeRespServer(topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true, Connections = 1, Endpoints = [new("127.0.0.1", seed.Port)],
        });
        var view = client.WithKeyPrefix("{tenant}:");
        await Assert.That(await view.Streams.CreateConsumerAsync("events", "group", "consumer")).IsTrue();
        await view.Streams.SetLastIdAsync("events", "9-0", 3, "2-0");
        await Assert.That(seed.ReceivedCommands).IsEquivalentTo(new[] { "CLUSTER SLOTS" });
        await Assert.That(owner.ReceivedCommands).IsEquivalentTo(new[]
        {
            "XGROUP CREATECONSUMER {tenant}:events group consumer",
            "XSETID {tenant}:events 9-0 ENTRIESADDED 3 MAXDELETEDID 2-0",
        }, CollectionOrdering.Matching);
    }
}
