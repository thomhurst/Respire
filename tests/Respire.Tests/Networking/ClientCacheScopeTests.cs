using Respire.Commands;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClientCacheScopeTests
{
    private static readonly byte[] Hello = "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray();
    private static readonly byte[] Old = "$3\r\nold\r\n"u8.ToArray();
    private static readonly byte[] New = "$3\r\nnew\r\n"u8.ToArray();

    [Test]
    public async Task OptInKeyPrefixesCacheCoveredKeysAndLeaveOthersUntracked()
    {
        await using var server = new FakeRespServer(
            Hello, FakeRespServer.OkReply, FakeRespServer.OkReply, Old, New, New);
        await using var client = await RespireClient.ConnectAsync(Options(server, ["hot:"]));

        await Assert.That(await client.GetStringAsync("hot:key")).IsEqualTo("old");
        await Assert.That(await client.GetStringAsync("hot:key")).IsEqualTo("old");
        await Assert.That(await client.GetStringAsync("cold:key")).IsEqualTo("new");
        await Assert.That(await client.GetStringAsync("cold:key")).IsEqualTo("new");

        await Assert.That(server.ReceivedCommands).IsEquivalentTo([
            "HELLO 3",
            "CLIENT TRACKING ON OPTIN",
            "CLIENT CACHING YES",
            "GET hot:key",
            "GET cold:key",
            "GET cold:key",
        ]);
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(1);
    }

    [Test]
    public async Task OptInUncoveredMgetAndQueriesSkipCachingYes()
    {
        await using var server = new FakeRespServer(
            Hello, FakeRespServer.OkReply,
            "*2\r\n$1\r\na\r\n$1\r\nb\r\n"u8.ToArray(), ":1\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(Options(server, ["hot:"]));

        await Assert.That(await client.Strings.GetManyAsync("cold:a", "cold:b"))
            .IsEquivalentTo((string?[])["a", "b"]);
        using var exists = await client.ExecuteAsync(RespireCommands.Key.EXISTS, "cold:a");

        await Assert.That(exists.AsInteger()).IsEqualTo(1L);
        await Assert.That(server.ReceivedCommands.Contains("CLIENT CACHING YES")).IsFalse();
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);
    }

    [Test]
    public async Task OptInMixedMgetStaysOneTrackedCommandAndCachesOnlyCoveredKeys()
    {
        await using var server = new FakeRespServer(
            Hello, FakeRespServer.OkReply, FakeRespServer.OkReply,
            "*2\r\n$1\r\na\r\n$1\r\nb\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(Options(server, ["hot:"]));

        await Assert.That(await client.Strings.GetManyAsync("hot:a", "cold:b"))
            .IsEquivalentTo((string?[])["a", "b"]);

        await Assert.That(server.ReceivedCommands.Skip(2)).IsEquivalentTo([
            "CLIENT CACHING YES",
            "MGET hot:a cold:b",
        ]);
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(1);
    }

    [Test]
    public async Task InvalidationSubscriptionRequiresCoveredKeyInOptInMode()
    {
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", 1)],
            ClientSideCache = new() { KeyPrefixes = ["hot:"] },
        });

        await Assert.That(() => client.ClientSideCache!.SubscribeInvalidations("cold:key", static _ => { }))
            .ThrowsExactly<ArgumentException>();
        using var covered = client.ClientSideCache!.SubscribeInvalidations("hot:key", static _ => { });
    }

    [Test]
    public async Task WithoutClientCacheReadsRedisAndWritesStillInvalidate()
    {
        await using var server = new FakeRespServer(
            Hello, FakeRespServer.OkReply, FakeRespServer.OkReply, Old, New, ":1\r\n"u8.ToArray(),
            FakeRespServer.OkReply);
        await using var client = await RespireClient.ConnectAsync(Options(server, []));
        var fresh = client.WithoutClientCache();

        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("old");
        await Assert.That(await fresh.GetStringAsync("key")).IsEqualTo("new");
        using var exists = await fresh.ExecuteAsync(RespireCommands.Key.EXISTS, "key");
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(1);

        await fresh.SetAsync("key", "newer");

        await Assert.That(client.ClientSideCache.Count).IsEqualTo(0);
        await Assert.That(server.ReceivedCommands.Count(command => command == "CLIENT CACHING YES")).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(2);
        await Assert.That(server.ReceivedCommands).Contains("EXISTS key");
    }

    [Test]
    public async Task WithoutClientCacheKeepsKeyPrefixAndGetOrSetSkipsLocalCache()
    {
        await using var server = new FakeRespServer(
            Hello, FakeRespServer.OkReply, "$-1\r\n"u8.ToArray(), "$-1\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(Options(server, []));
        var fresh = client.WithKeyPrefix("tenant:").WithoutClientCache();

        var value = await fresh.GetOrSetAsync<string>(
            "key", static _ => ValueTask.FromResult<string?>("created"), TimeSpan.FromMinutes(1));

        await Assert.That(value).IsEqualTo("created");
        await Assert.That(server.ReceivedCommands[2]).IsEqualTo("GET tenant:key");
        await Assert.That(server.ReceivedCommands[3]).StartsWith("SET tenant:key");
        await Assert.That(server.ReceivedCommands.Contains("CLIENT CACHING YES")).IsFalse();
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);
    }

    [Test]
    public async Task WithoutClientCacheReturnsSameClientWhenCachingIsDisabled()
    {
        await using var client = RespireClient.Create(new RespireOptions { Endpoints = [new("127.0.0.1", 1)] });
        await Assert.That(ReferenceEquals(client.WithoutClientCache(), client)).IsTrue();

        await using var cached = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", 1)], ClientSideCache = new(),
        });
        var fresh = cached.WithoutClientCache();
        await Assert.That(ReferenceEquals(fresh, cached)).IsFalse();
        await Assert.That(ReferenceEquals(fresh.WithoutClientCache(), fresh)).IsTrue();
    }

    private static RespireOptions Options(FakeRespServer server, IReadOnlyList<RespireKey> prefixes)
        => new()
        {
            Endpoints = [new("127.0.0.1", server.Port)],
            Connections = 1,
            ClientSideCache = new() { KeyPrefixes = prefixes },
        };
}
