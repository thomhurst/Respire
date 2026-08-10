using System.Text;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class CollectionScanTests
{
    [Test]
    public async Task HashScan_PaginatesWithMatchAndCountHint()
    {
        await using var server = new FakeRespServer(
            "*2\r\n$2\r\n17\r\n*2\r\n$5\r\nfirst\r\n$3\r\none\r\n"u8.ToArray(),
            "*2\r\n$1\r\n0\r\n*2\r\n$6\r\nsecond\r\n$3\r\ntwo\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var entries = await CollectAsync(client.Hashes.ScanAsync("hash", "f*", countHint: 5));

        await Assert.That(entries).IsEquivalentTo(new[]
        {
            new KeyValuePair<string, string>("first", "one"),
            new KeyValuePair<string, string>("second", "two"),
        });
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "HSCAN hash 0 MATCH f* COUNT 5",
            "HSCAN hash 17 MATCH f* COUNT 5",
        });
    }

    [Test]
    public async Task SetScan_ReturnsMembersAndUsesDefaultCountHint()
    {
        await using var server = new FakeRespServer(
            "*2\r\n$1\r\n0\r\n*2\r\n$3\r\none\r\n$3\r\ntwo\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var members = await CollectAsync(client.Sets.ScanAsync("set"));

        await Assert.That(members).IsEquivalentTo(new[] { "one", "two" });
        await Assert.That(server.ReceivedCommands[0]).IsEqualTo("SSCAN set 0 COUNT 250");
    }

    [Test]
    public async Task SortedSetScan_ReturnsMembersWithScores()
    {
        await using var server = new FakeRespServer(
            "*2\r\n$1\r\n0\r\n*4\r\n$3\r\none\r\n$3\r\n1.5\r\n$3\r\ntwo\r\n$1\r\n2\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var entries = await CollectAsync(client.SortedSets.ScanAsync("scores", match: "*o", countHint: 10));

        await Assert.That(entries).IsEquivalentTo(new[]
        {
            new SortedSetEntry("one", 1.5),
            new SortedSetEntry("two", 2),
        });
        await Assert.That(server.ReceivedCommands[0]).IsEqualTo("ZSCAN scores 0 MATCH *o COUNT 10");
    }

    [Test]
    public async Task CollectionScan_AppliesKeyPrefixes()
    {
        await using var server = new FakeRespServer("*2\r\n$1\r\n0\r\n*0\r\n"u8.ToArray());
        await using var owner = await FakeRespServer.ConnectClientAsync(server.Port);
        var client = owner.WithKeyPrefix("tenant:");

        _ = await CollectAsync(client.Hashes.ScanAsync("hash"));

        await Assert.That(server.ReceivedCommands[0]).IsEqualTo("HSCAN tenant:hash 0 COUNT 250");
    }

    [Test]
    public async Task CollectionScan_RoutesByKeyInClusterMode()
    {
        const string key = "scores";
        await using var target = new FakeRespServer("*2\r\n$1\r\n0\r\n*0\r\n"u8.ToArray());
        var slot = ClusterHash.GetSlot(key);
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:{slot}\r\n:{slot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{target.Port}\r\n");
        await using var seed = new FakeRespServer(topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });

        _ = await CollectAsync(client.SortedSets.ScanAsync(key));

        await Assert.That(target.ReceivedCommands[0]).IsEqualTo("ZSCAN scores 0 COUNT 250");
    }

    [Test]
    public async Task CollectionScan_RejectsNonPositiveCountHintBeforeSending()
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await Assert.That(async () => await CollectAsync(client.Sets.ScanAsync("set", countHint: 0)))
            .ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    private static async Task<T[]> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        var items = new List<T>();
        await foreach (var item in source)
        {
            items.Add(item);
        }

        return [.. items];
    }
}
