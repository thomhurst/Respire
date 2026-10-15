using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class OptOutClientCacheTests
{
    private static readonly byte[] Hello = "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray();
    private static readonly byte[] Value = "$5\r\nvalue\r\n"u8.ToArray();

    [Test]
    [Arguments(RespireClientTrackingMode.OptIn, false)]
    [Arguments(RespireClientTrackingMode.OptIn, true)]
    [Arguments(RespireClientTrackingMode.OptOut, false)]
    [Arguments(RespireClientTrackingMode.OptOut, true)]
    [Arguments(RespireClientTrackingMode.Broadcast, true)]
    public async Task HandshakeAndCachedReadsUseSelectedMode(RespireClientTrackingMode mode, bool noLoop)
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        {
            ClientSideCache = new() { TrackingMode = mode, NoLoop = noLoop, KeyPrefixes = ["hot:"] },
        });
        await Assert.That(await client.GetStringAsync("hot:key")).IsEqualTo("value");
        await Assert.That(await client.GetStringAsync("hot:key")).IsEqualTo("value");
        var registration = mode switch
        {
            RespireClientTrackingMode.Broadcast => "BCAST PREFIX hot:",
            RespireClientTrackingMode.OptOut => "OPTOUT",
            _ => "OPTIN",
        };
        await Assert.That(server.ReceivedCommands.Contains("CLIENT TRACKING ON " + registration + (noLoop ? " NOLOOP" : ""))).IsTrue();
        await Assert.That(server.ReceivedCommands.Count(command => command == "GET hot:key")).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands.Count(command => command == "CLIENT CACHING YES"))
            .IsEqualTo(mode == RespireClientTrackingMode.OptIn ? 1 : 0);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task BypassPairsNoWithEachReadAndDoesNotPopulateCache(bool raw, bool retry)
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        {
            RetryPolicy = retry ? new() { MaxAttempts = 2, InitialBackoff = TimeSpan.Zero } : null,
        });
        var bypass = client.WithKeyPrefix("hot:").WithoutClientCache();
        for (var i = 0; i < 2; i++)
        {
            if (raw)
            {
                using var reply = await client.WithoutClientCache().ExecuteAsync(RespireCommands.String.GET, "hot:key");
                await Assert.That(reply.AsString()).IsEqualTo("value");
            }
            else await Assert.That(await bypass.GetStringAsync("key")).IsEqualTo("value");
        }
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);
        await client.GetStringAsync("hot:key");
        await client.GetStringAsync("hot:key");
        await Assert.That(server.ReceivedCommands.ToArray()).IsEquivalentTo(new[]
        {
            "HELLO 3", "CLIENT TRACKING ON OPTOUT",
            "CLIENT CACHING NO", "GET hot:key", "CLIENT CACHING NO", "GET hot:key", "GET hot:key",
        });
    }

    [Test]
    public async Task BypassRetryRepeatsNoAndReadTogether()
    {
        var calls = 0;
        await using var server = Server();
        server.ReplyOverride = (_, command) => command.StartsWith("GET ", StringComparison.Ordinal)
            ? Interlocked.Increment(ref calls) == 1 ? "-TRYAGAIN retry\r\n"u8.ToArray() : Value
            : command.StartsWith("HELLO ", StringComparison.Ordinal) ? Hello : FakeRespServer.OkReply;
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        {
            RetryPolicy = new() { MaxAttempts = 2, InitialBackoff = TimeSpan.Zero },
        });
        await Assert.That(await client.WithoutClientCache().GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(server.ReceivedCommands.Count(command => command == "CLIENT CACHING NO")).IsEqualTo(2);
        await Assert.That(calls).IsEqualTo(2);
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BypassPropagatesPreludeAndReadErrorsAndDrainsReplies(bool preludeError)
    {
        await using var server = new FakeRespServer(Hello, FakeRespServer.OkReply,
            preludeError ? "-ERR caching denied\r\n"u8.ToArray() : FakeRespServer.OkReply,
            preludeError ? Value : "-WRONGTYPE invalid type\r\n"u8.ToArray(), FakeRespServer.PongReply);
        await using var client = await RespireClient.ConnectAsync(Options(server));
        await Assert.That(async () => await client.WithoutClientCache().GetStringAsync("key"))
            .ThrowsExactly<RespireServerException>();
        await client.PingAsync();
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ConcurrentBypassAndCachedReadsKeepPreludeAdjacentToBypass()
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var bypass = client.WithoutClientCache();
        await Task.WhenAll(Enumerable.Range(0, 40).Select(async i =>
        {
            var reader = i % 2 == 0 ? bypass : client;
            await reader.GetStringAsync((i % 2 == 0 ? "skip:" : "keep:") + i);
        }));
        var commands = server.ReceivedCommands.ToArray();
        for (var i = 0; i < commands.Length; i++)
        {
            if (commands[i].StartsWith("GET skip:", StringComparison.Ordinal))
                await Assert.That(commands[i - 1]).IsEqualTo("CLIENT CACHING NO");
            if (commands[i].StartsWith("GET keep:", StringComparison.Ordinal))
                await Assert.That(commands[i - 1] == "CLIENT CACHING NO").IsFalse();
        }
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(20);
    }

    [Test]
    [Arguments("ASK", false)]
    [Arguments("ASK", true)]
    [Arguments("MOVED", false)]
    [Arguments("MOVED", true)]
    public async Task RedirectsPreserveOptOutRegistrationAndPerCallBypass(string redirect, bool bypass)
    {
        var slot = ClusterHash.GetSlot("key");
        await using var target = Server();
        await using var initial = Server();
        initial.ReplyOverride = (_, command) => command.StartsWith("GET ", StringComparison.Ordinal)
            ? Encoding.ASCII.GetBytes($"-{redirect} {slot} 127.0.0.1:{target.Port}\r\n")
            : command.StartsWith("HELLO ", StringComparison.Ordinal) ? Hello : FakeRespServer.OkReply;
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{initial.Port}\r\n");
        await using var seed = new FakeRespServer(topology)
        {
            ReplyOverride = (_, command) => command.StartsWith("HELLO ", StringComparison.Ordinal) ? Hello
                : command.StartsWith("CLIENT ", StringComparison.Ordinal) ? FakeRespServer.OkReply : topology,
        };
        await using var client = await RespireClient.ConnectAsync(Options(seed) with
        {
            UseCluster = true, RetryPolicy = new() { MaxAttempts = 2 },
        });
        var reader = bypass ? client.WithoutClientCache() : client;
        await Assert.That(await reader.GetStringAsync("key")).IsEqualTo("value");
        var commands = target.ReceivedCommands.ToArray();
        await Assert.That(commands.Contains("CLIENT TRACKING ON OPTOUT")).IsTrue();
        await Assert.That(commands.Contains("CLIENT CACHING YES")).IsFalse();
        await Assert.That(commands.Contains("CLIENT CACHING NO")).IsEqualTo(bypass);
        var readIndex = Array.IndexOf(commands, "GET key");
        if (bypass) await Assert.That(commands[readIndex - 1]).IsEqualTo("CLIENT CACHING NO");
        if (redirect == "ASK") await Assert.That(commands[readIndex - (bypass ? 2 : 1)]).IsEqualTo("ASKING");
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(bypass ? 0 : 1);
    }

    [Test]
    public async Task BypassPreservesReplicaRoutingWithoutSendingCachingToUntrackedConnections()
    {
        await using var primary = Server();
        await using var replica = Server();
        replica.ReplyOverride = (_, command) => command == "ROLE"
            ? "*5\r\n$5\r\nslave\r\n$9\r\n127.0.0.1\r\n:6379\r\n$9\r\nconnected\r\n:0\r\n"u8.ToArray()
            : command.StartsWith("HELLO ", StringComparison.Ordinal) ? Hello
            : command.StartsWith("GET ", StringComparison.Ordinal) ? Value : FakeRespServer.OkReply;
        await using var client = await RespireClient.ConnectAsync(Options(primary) with
        {
            ReplicaEndpoints = [new("127.0.0.1", replica.Port)],
        });
        var bypass = client.WithReadFrom(RespireReadFrom.Replica).WithoutClientCache();
        await Assert.That(await bypass.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(replica.ReceivedCommands.Contains("GET key")).IsTrue();
        await Assert.That(replica.ReceivedCommands.Any(command => command.StartsWith("CLIENT CACHING", StringComparison.Ordinal))).IsFalse();
        await Assert.That(primary.ReceivedCommands.Contains("GET key")).IsFalse();
    }

    [Test]
    public async Task AskingAndOptOutPreludeAreOneValidatedSequence()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply, FakeRespServer.OkReply, Value);
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port);
        var get = new Cmd1(Verbs.Get, "key");
        using var reply = await ClusterRouter.SendTrackedAskingAsync(connection, in get, default, optOut: true);
        await Assert.That(reply.AsString()).IsEqualTo("value");
        await Assert.That(server.ReceivedCommands.ToArray()).IsEquivalentTo(new[] { "ASKING", "CLIENT CACHING NO", "GET key" });
    }

    [Test]
    [Arguments(false, 1)]
    [Arguments(true, 2)]
    public async Task OptOutReservesSpaceForBypassPreludes(bool cluster, int capacity)
    {
        await Assert.That(() => RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("localhost", 1)], UseCluster = cluster, MaxInflightCommands = capacity,
            ClientSideCache = new() { TrackingMode = RespireClientTrackingMode.OptOut },
        })).ThrowsExactly<RespireConfigurationException>();
    }

    private static FakeRespServer Server() => new(Hello)
    {
        ReplyOverride = (_, command) => command.StartsWith("HELLO ", StringComparison.Ordinal) ? Hello
            : command.StartsWith("GET ", StringComparison.Ordinal) ? Value : FakeRespServer.OkReply,
    };

    private static RespireOptions Options(FakeRespServer server) => new()
    {
        Endpoints = [new("127.0.0.1", server.Port)], Connections = 1,
        MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled,
        ClientSideCache = new() { TrackingMode = RespireClientTrackingMode.OptOut },
    };
}
