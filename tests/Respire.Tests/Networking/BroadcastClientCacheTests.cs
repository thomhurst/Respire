using System.Text;
using Respire.Commands;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class BroadcastClientCacheTests
{
    private static readonly byte[] Hello = "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray();
    private static readonly byte[] Old = "$3\r\nold\r\n"u8.ToArray();
    private static readonly byte[] New = "$3\r\nnew\r\n"u8.ToArray();
    private static readonly byte[] Invalidate = ">2\r\n$10\r\ninvalidate\r\n*1\r\n$7\r\nhot:key\r\n"u8.ToArray();

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BroadcastRegistersBeforeReadsAndOmitsCachingYes(bool prefix)
    {
        await using var server = new FakeRespServer(Hello, FakeRespServer.OkReply, Old);
        await using var client = await RespireClient.ConnectAsync(Options(server,
            prefix ? ["hot:"] : []) with { MaxInflightCommands = 1 });
        await Assert.That(await client.GetStringAsync("hot:key")).IsEqualTo("old");
        await Assert.That(await client.GetStringAsync("hot:key")).IsEqualTo("old");
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "HELLO 3", prefix ? "CLIENT TRACKING ON BCAST PREFIX hot:" : "CLIENT TRACKING ON BCAST", "GET hot:key",
        });
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(1);
    }

    [Test]
    public async Task SnapshotOwnsLiteralBinaryPrefixesAndUsesPhysicalKeys()
    {
        byte[] binary = [255, 0, (byte)'*'];
        RespireKey[] prefixes = [binary, "tenant:hot:"];
        await using var server = new FakeRespServer(Hello, FakeRespServer.OkReply, Old);
        await using var client = RespireClient.Create(Options(server, prefixes));
        binary[0] = 1;
        prefixes[1] = "changed:";
        var view = client.WithKeyPrefix("tenant:");
        await Assert.That(await view.GetStringAsync("hot:key")).IsEqualTo("old");
        await Assert.That(await view.GetStringAsync("hot:key")).IsEqualTo("old");
        await view.GetStringAsync("cold:key");
        await view.GetStringAsync("cold:key");
        await client.GetStringAsync(new byte[] { 255, 0, (byte)'*', (byte)'k' });
        await client.GetStringAsync(new byte[] { 255, 0, (byte)'*', (byte)'k' });
        var tracking = server.ReceivedArguments[1];
        await Assert.That(tracking[5].AsSpan().SequenceEqual(new byte[] { 255, 0, (byte)'*' })).IsTrue();
        await Assert.That(Encoding.UTF8.GetString(tracking[7])).IsEqualTo("tenant:hot:");
        await Assert.That(server.ReceivedCommands.Count(command => command == "GET tenant:cold:key")).IsEqualTo(2);
        await Assert.That(server.ReceivedCommands.Count(command => command == "GET tenant:hot:key")).IsEqualTo(1);
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(2);
    }

    [Test]
    public async Task MixedMgetCachesOnlyCoveredKeysAndPreservesPartialHits()
    {
        await using var server = new FakeRespServer(Hello, FakeRespServer.OkReply,
            "*2\r\n$3\r\nold\r\n$3\r\nold\r\n"u8.ToArray(), "*1\r\n$3\r\nnew\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(Options(server, ["hot:"]));
        await Assert.That(await client.Strings.GetManyAsync("hot:key", "cold:key")).IsEquivalentTo((string?[])["old", "old"]);
        var values = await client.Strings.GetManyAsync("hot:key", "cold:key");
        await Assert.That(values[0]).IsEqualTo("old");
        await Assert.That(values[1]).IsEqualTo("new");
        await Assert.That(server.ReceivedCommands.Last()).IsEqualTo("MGET cold:key");
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(1);
    }

    [Test]
    public async Task MultiKeyProjectionRequiresEveryDependencyToBeCovered()
    {
        await using var server = new FakeRespServer(Hello, FakeRespServer.OkReply, ":1\r\n"u8.ToArray(), ":0\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(Options(server, ["hot:"]));
        using var first = await client.ExecuteAsync(RespireCommands.Key.EXISTS, "hot:key", "cold:key");
        using var second = await client.ExecuteAsync(RespireCommands.Key.EXISTS, "hot:key", "cold:key");
        await Assert.That(first.AsInteger()).IsEqualTo(1L);
        await Assert.That(second.AsInteger()).IsEqualTo(0L);
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);
    }

    [Test]
    public async Task InvalidationBeforeReplyPreventsStaleInsertion()
    {
        await using var server = new FakeRespServer(Hello, FakeRespServer.OkReply, New);
        await using var client = await RespireClient.ConnectAsync(Options(server, ["hot:"]));
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.SuppressReply = command =>
        {
            if (command != "GET hot:key") return false;
            received.TrySetResult();
            return true;
        };
        var pending = client.GetStringAsync("hot:key").AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await server.SendRawAsync([.. Invalidate, .. Old]);
        await Assert.That(await pending.WaitAsync(TimeSpan.FromSeconds(5))).IsEqualTo("old");
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);
        server.SuppressReply = null;
        await Assert.That(await client.GetStringAsync("hot:key")).IsEqualTo("new");
        await Assert.That(client.ClientSideCache.Count).IsEqualTo(1);
    }

    [Test]
    public async Task LocalMutationAndExternalInvalidationEvictBroadcastEntries()
    {
        await using var server = new FakeRespServer(Hello, FakeRespServer.OkReply, Old,
            FakeRespServer.OkReply, New, FakeRespServer.PongReply, New);
        await using var client = await RespireClient.ConnectAsync(Options(server, ["hot:"]));
        await client.GetStringAsync("hot:key");
        await client.SetAsync("hot:key", "new");
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);
        await Assert.That(await client.GetStringAsync("hot:key")).IsEqualTo("new");
        await server.SendRawAsync(Invalidate);
        await client.PingAsync(); // Ordered reply confirms the preceding push was parsed.
        await Assert.That(client.ClientSideCache.Count).IsEqualTo(0);
        await Assert.That(await client.GetStringAsync("hot:key")).IsEqualTo("new");
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    [Arguments(5)]
    public async Task InvalidTrackingConfigurationFailsBeforeConnecting(int scenario)
    {
        var cache = scenario switch
        {
            0 => new RespireClientSideCacheOptions { TrackingMode = (RespireClientTrackingMode)123 },
            1 => new RespireClientSideCacheOptions { BroadcastPrefixes = ["hot:"] },
            2 => Broadcast(["hot:", "hot:key"]),
            3 => Broadcast(["hot:", "hot:"]),
            4 => Broadcast([RespireKey.Empty, "hot:"]),
            _ => new RespireClientSideCacheOptions { BroadcastPrefixes = null! },
        };
        await Assert.That(() => RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("localhost", 1)], ClientSideCache = cache,
        })).ThrowsExactly<RespireConfigurationException>();
    }

    [Test]
    public async Task ClusterBroadcastRequiresTwoSlotsForAskingBeforeConnecting()
    {
        await Assert.That(() => RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("localhost", 1)], UseCluster = true,
            MaxInflightCommands = 1, ClientSideCache = Broadcast([]),
        })).ThrowsExactly<RespireConfigurationException>();
    }

    [Test]
    public async Task PrefixMatchingHandlesUnicodeAndLongKeysWithoutChangingBinaryIdentity()
    {
        var cache = new ClientSideCacheCoordinator(Broadcast(["é:", "long:"]));
        foreach (RespireKey key in new RespireKey[] { "é:键", "long:" + new string('x', 1000) })
        {
            var token = cache.BeginRead(in key);
            var response = RespValue.BulkString("value");
            cache.CompleteRead(in token, in response, allowInsert: true);
            await Assert.That(cache.TryGet(in key, out _)).IsTrue();
        }
    }

    private static RespireClientSideCacheOptions Broadcast(IReadOnlyList<RespireKey> prefixes)
        => new() { TrackingMode = RespireClientTrackingMode.Broadcast, BroadcastPrefixes = prefixes };

    private static RespireOptions Options(FakeRespServer server, IReadOnlyList<RespireKey> prefixes)
        => new() { Endpoints = [new("127.0.0.1", server.Port)], Connections = 1, ClientSideCache = Broadcast(prefixes) };
}
