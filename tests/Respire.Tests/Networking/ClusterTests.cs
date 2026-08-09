using Respire.Internal;
using Respire.Commands;
using Respire.Protocol;
using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClusterTests
{
    [Test]
    public async Task HashSlot_UsesRedisCrc16AndHashTags()
    {
        await Assert.That(ClusterHash.GetSlot("123456789")).IsEqualTo(12_739);
        await Assert.That(ClusterHash.GetSlot("foo")).IsEqualTo(12_182);
        await Assert.That(ClusterHash.GetSlot("{user1000}.following"))
            .IsEqualTo(ClusterHash.GetSlot("{user1000}.followers"));
        await Assert.That(ClusterHash.GetSlot("{a{b}"))
            .IsEqualTo(ClusterHash.GetSlot("a{b"));
        await Assert.That(ClusterHash.GetSlot("£ sterling"))
            .IsEqualTo(ClusterHash.GetSlot(Encoding.UTF8.GetBytes("£ sterling")));
    }

    [Test]
    public async Task RemovalLeaseKey_UsesRequestedHashSlot()
    {
        var keys = new RespireKey[]
        {
            "plain",
            "{account}cache",
            "{}odd}key",
            "£ sterling",
            new byte[] { 0, (byte)'}', 255 },
        };

        foreach (var key in keys)
        {
            var lease = RespireClient.CreateClusterRemovalLeaseKey(key.ClusterSlot);
            await Assert.That(lease.ClusterSlot).IsEqualTo(key.ClusterSlot);
        }
    }

    [Test]
    public async Task MovedRedirect_IsFollowedAndSlotIsCached()
    {
        await using var target = new FakeRespServer("$5\r\nvalue\r\n"u8.ToArray());
        var slot = ClusterHash.GetSlot("key");
        await using var seed = new FakeRespServer(
            "*0\r\n"u8.ToArray(),
            Encoding.ASCII.GetBytes($"-MOVED {slot} 127.0.0.1:{target.Port}\r\n"));
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Cluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });

        var first = await client.GetStringAsync("key");
        var second = await client.GetStringAsync("key");

        await Assert.That(first).IsEqualTo("value");
        await Assert.That(second).IsEqualTo("value");
        await Assert.That(seed.ReceivedCommands).Count().IsEqualTo(2);
        await Assert.That(target.ReceivedCommands).Count().IsEqualTo(2);
    }

    [Test]
    public async Task AskRedirect_SendsAskingOnTargetWithoutCachingSlot()
    {
        await using var target = new FakeRespServer(
            FakeRespServer.OkReply,
            "$5\r\nvalue\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            "$5\r\nvalue\r\n"u8.ToArray())
        {
            MinimumCommandsBeforeReply = 2,
        };
        var slot = ClusterHash.GetSlot("key");
        await using var seed = new FakeRespServer(
            "*0\r\n"u8.ToArray(),
            Encoding.ASCII.GetBytes($"-ASK {slot} 127.0.0.1:{target.Port}\r\n"));
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Cluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });

        var value = await client.GetStringAsync("key");
        var second = await client.GetStringAsync("key");

        await Assert.That(value).IsEqualTo("value");
        await Assert.That(second).IsEqualTo("value");
        await Assert.That(seed.ReceivedCommands).Count().IsEqualTo(3);
        await Assert.That(target.ReceivedCommands[0]).IsEqualTo("ASKING");
        await Assert.That(target.ReceivedCommands[1]).IsEqualTo("GET key");
        await Assert.That(target.ReceivedCommands[2]).IsEqualTo("ASKING");
        await Assert.That(target.ReceivedCommands[3]).IsEqualTo("GET key");
    }

    [Test]
    public async Task ClusterSlots_RoutesFirstKeyedCommandDirectly()
    {
        await using var target = new FakeRespServer("$5\r\nvalue\r\n"u8.ToArray());
        var slot = ClusterHash.GetSlot("key");
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:{slot}\r\n:{slot}\r\n" +
            $"*4\r\n$9\r\n192.0.2.1\r\n:{target.Port}\r\n$2\r\nid\r\n" +
            "%1\r\n+hostname\r\n$9\r\n127.0.0.1\r\n");
        await using var seed = new FakeRespServer(topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Cluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });

        var value = await client.GetStringAsync("key");

        await Assert.That(value).IsEqualTo("value");
        await Assert.That(seed.ReceivedCommands).Count().IsEqualTo(1);
        await Assert.That(seed.ReceivedCommands[0]).IsEqualTo("CLUSTER SLOTS");
        await Assert.That(target.ReceivedCommands[0]).IsEqualTo("GET key");
    }

    [Test]
    public async Task Batch_RoutesCommandsAcrossNodes()
    {
        await using var firstNode = new FakeRespServer("$3\r\none\r\n"u8.ToArray());
        await using var secondNode = new FakeRespServer("$3\r\ntwo\r\n"u8.ToArray());
        var firstSlot = ClusterHash.GetSlot("foo");
        var secondSlot = ClusterHash.GetSlot("bar");
        var topology = Encoding.ASCII.GetBytes(
            $"*2\r\n" +
            $"*3\r\n:{firstSlot}\r\n:{firstSlot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{firstNode.Port}\r\n" +
            $"*3\r\n:{secondSlot}\r\n:{secondSlot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{secondNode.Port}\r\n");
        await using var seed = new FakeRespServer(topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Cluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });

        var batch = client.CreateBatch();
        var first = batch.GetStringAsync("foo");
        var second = batch.GetStringAsync("bar");
        await batch.SendAsync();

        await Assert.That(first.Result).IsEqualTo("one");
        await Assert.That(second.Result).IsEqualTo("two");
        await Assert.That(firstNode.ReceivedCommands[0]).IsEqualTo("GET foo");
        await Assert.That(secondNode.ReceivedCommands[0]).IsEqualTo("GET bar");
    }

    [Test]
    public async Task Transaction_RoutesToItsSingleHashSlot()
    {
        await using var target = new FakeRespServer(
            FakeRespServer.OkReply,
            "+QUEUED\r\n"u8.ToArray(),
            "*1\r\n+OK\r\n"u8.ToArray());
        var slot = ClusterHash.GetSlot("{account}name");
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:{slot}\r\n:{slot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{target.Port}\r\n");
        await using var seed = new FakeRespServer(topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Cluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });

        var transaction = client.CreateTransaction();
        var pending = transaction.SetAsync("{account}name", "Ada");
        var committed = await transaction.CommitAsync();

        await Assert.That(committed).IsTrue();
        await Assert.That(pending.Result).IsTrue();
        await Assert.That(target.ReceivedCommands[0]).IsEqualTo("MULTI");
        await Assert.That(target.ReceivedCommands[1]).IsEqualTo("SET {account}name Ada");
        await Assert.That(target.ReceivedCommands[2]).IsEqualTo("EXEC");
    }

    [Test]
    public async Task Transaction_FollowsMovedRedirectAsOneUnit()
    {
        await using var target = new FakeRespServer(
            FakeRespServer.OkReply,
            "+QUEUED\r\n"u8.ToArray(),
            "*1\r\n+OK\r\n"u8.ToArray());
        var slot = ClusterHash.GetSlot("{account}name");
        await using var seed = new FakeRespServer(
            "*0\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            Encoding.ASCII.GetBytes($"-MOVED {slot} 127.0.0.1:{target.Port}\r\n"),
            "-EXECABORT Transaction discarded because of previous errors.\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Cluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });

        var transaction = client.CreateTransaction();
        var pending = transaction.SetAsync("{account}name", "Ada");
        var committed = await transaction.CommitAsync();

        await Assert.That(committed).IsTrue();
        await Assert.That(pending.Result).IsTrue();
        await Assert.That(seed.ReceivedCommands[^3]).IsEqualTo("MULTI");
        await Assert.That(target.ReceivedCommands[0]).IsEqualTo("MULTI");
        await Assert.That(target.ReceivedCommands[1]).IsEqualTo("SET {account}name Ada");
        await Assert.That(target.ReceivedCommands[2]).IsEqualTo("EXEC");
    }

    [Test]
    public async Task Transaction_RejectsKeysFromDifferentHashSlots()
    {
        await using var client = RespireClient.Create(new RespireOptions { Cluster = true });
        await using var transaction = client.CreateTransaction();
        _ = transaction.SetAsync("foo", "one");

        var error = Assert.Throws<InvalidOperationException>(() => transaction.SetAsync("bar", "two"));

        await Assert.That(error.Message).Contains("same hash slot");
    }

    [Test]
    public async Task TrackedScript_RoutesByKeyAndUpdatesIdentityAfterMoved()
    {
        var slot = ClusterHash.GetSlot("cache-key");
        await using var redirected = new FakeRespServer(
            ":42\r\n"u8.ToArray(),
            ":0\r\n"u8.ToArray(),
            "$5\r\nvalue\r\n"u8.ToArray());
        await using var initial = new FakeRespServer(
            ":41\r\n"u8.ToArray(),
            ":0\r\n"u8.ToArray(),
            Encoding.ASCII.GetBytes($"-MOVED {slot} 127.0.0.1:{redirected.Port}\r\n"));
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:{slot}\r\n:{slot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{initial.Port}\r\n");
        await using var seed = new FakeRespServer(topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Cluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });
        var script = RespireScript.Create("return redis.call('GET', KEYS[1])");

        var execution = await client.StartTrackedScriptExecutionAsync(
            script, ["cache-key"], [], CancellationToken.None);
        using var result = await execution.Response;

        await Assert.That(result.AsString()).IsEqualTo("value");
        await Assert.That(execution.ConnectionIdentity.ServerClientId).IsEqualTo(42);
        await Assert.That(execution.ConnectionIdentity.Endpoint.Port).IsEqualTo(redirected.Port);
        await Assert.That(seed.ReceivedCommands).Count().IsEqualTo(1);
        await Assert.That(initial.ReceivedCommands[0]).IsEqualTo("CLIENT ID");
        await Assert.That(initial.ReceivedCommands[1]).StartsWith("CLIENT KILL ID 41");
        await Assert.That(initial.ReceivedCommands[2]).StartsWith("EVALSHA ");
        await Assert.That(redirected.ReceivedCommands[0]).IsEqualTo("CLIENT ID");
        await Assert.That(redirected.ReceivedCommands[1]).StartsWith("CLIENT KILL ID 42");
        await Assert.That(redirected.ReceivedCommands[2]).StartsWith("EVALSHA ");
    }

    [Test]
    public async Task TrackedCorrectionBroadcast_UsesOwningNodeMultiplexer()
    {
        var slot = ClusterHash.GetSlot("cache-key");
        await using var target = new FakeRespServer(
            ":42\r\n"u8.ToArray(),
            ":0\r\n"u8.ToArray(),
            "$5\r\nvalue\r\n"u8.ToArray(),
            ":1\r\n"u8.ToArray());
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:{slot}\r\n:{slot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{target.Port}\r\n");
        await using var seed = new FakeRespServer(topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Cluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });
        var script = RespireScript.Create("return redis.call('GET', KEYS[1])");
        var execution = await client.StartTrackedScriptExecutionAsync(
            script, ["cache-key"], [], CancellationToken.None);
        using var result = await execution.Response;

        await client.ExecuteOnAllConnectionsAsync(
            script, ["cache-key"], [], execution.ConnectionIdentity);

        await Assert.That(target.ReceivedCommands[^1]).StartsWith("EVAL ");
        await Assert.That(seed.ReceivedCommands).Count().IsEqualTo(1);
    }

    [Test]
    public async Task GuardedUnlink_RoutesLeaseAndScriptToKeyOwner()
    {
        const string prefix = "{}broken{";
        const string key = "cache-key";
        var slot = ClusterHash.GetSlot(prefix + key);
        await using var target = new FakeRespServer(2, ":1\r\n"u8.ToArray());
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:{slot}\r\n:{slot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{target.Port}\r\n");
        await using var seed = new FakeRespServer(topology);
        await using var owner = await RespireClient.ConnectAsync(new RespireOptions
        {
            Cluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });
        var client = (RespireClient)owner.WithKeyPrefix(prefix);

        await client.UnlinkGuardedAsync(key, CancellationToken.None);

        await Assert.That(target.ReceivedCommands).Contains(command => command.StartsWith("SET "));
        await Assert.That(target.ReceivedCommands).Contains(command => command.StartsWith("EVAL "));
        await Assert.That(seed.ReceivedCommands).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Connect_TriesLaterSeedWhenFirstIsUnavailable()
    {
        await using var target = new FakeRespServer("$5\r\nvalue\r\n"u8.ToArray());
        var slot = ClusterHash.GetSlot("key");
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:{slot}\r\n:{slot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{target.Port}\r\n");
        await using var seed = new FakeRespServer(topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Cluster = true,
            ConnectTimeout = TimeSpan.FromMilliseconds(250),
            Endpoints =
            {
                new RespireEndpoint("127.0.0.1", 1),
                new RespireEndpoint("127.0.0.1", seed.Port),
            },
        });

        var value = await client.GetStringAsync("key");

        await Assert.That(client.IsConnected).IsTrue();
        await Assert.That(value).IsEqualTo("value");
        await Assert.That(seed.ReceivedCommands[0]).IsEqualTo("CLUSTER SLOTS");
        await Assert.That(target.ReceivedCommands[0]).IsEqualTo("GET key");
    }

    [Test]
    public async Task CachedSlotRoute_WorksWhileSeedIsUnavailable()
    {
        await using var target = new FakeRespServer(
            "$5\r\nvalue\r\n"u8.ToArray(),
            "$6\r\nsecond\r\n"u8.ToArray());
        var slot = ClusterHash.GetSlot("key");
        var seed = new FakeRespServer(
            "*0\r\n"u8.ToArray(),
            Encoding.ASCII.GetBytes($"-MOVED {slot} 127.0.0.1:{target.Port}\r\n"));
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Cluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });

        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await seed.DisposeAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (client.IsConnected)
        {
            await Task.Delay(10, timeout.Token);
        }

        await Assert.That(await client.GetStringAsync("key", timeout.Token)).IsEqualTo("second");
        await Assert.That(target.ReceivedCommands).Count().IsEqualTo(2);
    }

    [Test]
    public async Task FailedCachedSlotOwner_FallsBackToHealthySeed()
    {
        await using var target = new FakeRespServer("$5\r\nvalue\r\n"u8.ToArray());
        var slot = ClusterHash.GetSlot("key");
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:{slot}\r\n:{slot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{target.Port}\r\n");
        await using var seed = new FakeRespServer(
            topology,
            "$8\r\nfallback\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Cluster = true,
            ConnectTimeout = TimeSpan.FromMilliseconds(250),
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });

        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await target.DisposeAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (client.Core.Cluster!.IsSlotConnected(slot))
        {
            await Task.Delay(10, timeout.Token);
        }

        var value = await client.GetStringAsync("key", timeout.Token);

        await Assert.That(value).IsEqualTo("fallback");
        await Assert.That(seed.ReceivedCommands[^1]).IsEqualTo("GET key");
    }

    [Test]
    public async Task Scan_TraversesEveryKnownMaster()
    {
        await using var firstNode = new FakeRespServer(
            "*2\r\n$1\r\n0\r\n*1\r\n$3\r\none\r\n"u8.ToArray());
        await using var secondNode = new FakeRespServer(
            "*2\r\n$1\r\n0\r\n*1\r\n$3\r\ntwo\r\n"u8.ToArray());
        var topology = Encoding.ASCII.GetBytes(
            $"*2\r\n" +
            $"*3\r\n:0\r\n:8191\r\n*2\r\n$9\r\n127.0.0.1\r\n:{firstNode.Port}\r\n" +
            $"*3\r\n:8192\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{secondNode.Port}\r\n");
        await using var seed = new FakeRespServer(topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Cluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });
        var keys = new List<string>();

        await foreach (var key in client.Keys.ScanAsync())
        {
            keys.Add(key);
        }

        await Assert.That(keys).IsEquivalentTo(["one", "two"]);
        await Assert.That(firstNode.ReceivedCommands[0]).IsEqualTo("SCAN 0 COUNT 250");
        await Assert.That(secondNode.ReceivedCommands[0]).IsEqualTo("SCAN 0 COUNT 250");
    }

    [Test]
    public async Task Scan_RefreshesTopologyBeforeVisitingCachedMasters()
    {
        await using var currentNode = new FakeRespServer(
            "*2\r\n$1\r\n0\r\n*1\r\n$7\r\ncurrent\r\n"u8.ToArray());
        var staleTopology = Encoding.ASCII.GetBytes(
            $"*2\r\n" +
            "*3\r\n:0\r\n:8191\r\n*2\r\n$9\r\n127.0.0.1\r\n:1\r\n" +
            $"*3\r\n:8192\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{currentNode.Port}\r\n");
        var currentTopology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{currentNode.Port}\r\n");
        await using var seed = new FakeRespServer(staleTopology, currentTopology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Cluster = true,
            ConnectTimeout = TimeSpan.FromMilliseconds(100),
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });
        var keys = new List<string>();

        await foreach (var key in client.Keys.ScanAsync())
        {
            keys.Add(key);
        }

        await Assert.That(keys).IsEquivalentTo(["current"]);
        await Assert.That(currentNode.ReceivedCommands[0]).IsEqualTo("SCAN 0 COUNT 250");
    }

    [Test]
    public async Task TopologyRefresh_ClearsReconnectStateForRetiredMaster()
    {
        await using var currentNode = new FakeRespServer(FakeRespServer.PongReply);
        await using var retiredNode = new FakeRespServer(FakeRespServer.PongReply);
        var initialTopology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{retiredNode.Port}\r\n");
        var refreshedTopology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{currentNode.Port}\r\n");
        await using var seed = new FakeRespServer(initialTopology, refreshedTopology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Cluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });
        var core = client.Core;
        var retiredMultiplexer = core.Cluster!.GetMultiplexer(
            new RespireEndpoint("127.0.0.1", retiredNode.Port));
        var states = new List<RespireConnectionState>();
        core.ConnectionStateChanged += states.Add;
        core.NotifyCommandStateChanged(retiredMultiplexer, 0, RespireConnectionState.Reconnecting);

        _ = await core.Cluster.GetMasterConnectionsAsync(CancellationToken.None);

        await Assert.That(states).IsEquivalentTo(
            [RespireConnectionState.Reconnecting, RespireConnectionState.Connected]);
    }

    [Test]
    public async Task Scan_FailsWhenCurrentMasterIsUnavailable()
    {
        await using var currentNode = new FakeRespServer(
            "*2\r\n$1\r\n0\r\n*1\r\n$7\r\ncurrent\r\n"u8.ToArray());
        var topology = Encoding.ASCII.GetBytes(
            $"*2\r\n" +
            "*3\r\n:0\r\n:8191\r\n*2\r\n$9\r\n127.0.0.1\r\n:1\r\n" +
            $"*3\r\n:8192\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{currentNode.Port}\r\n");
        await using var seed = new FakeRespServer(topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Cluster = true,
            ConnectTimeout = TimeSpan.FromMilliseconds(100),
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });

        var failed = false;
        try
        {
            await foreach (var _ in client.Keys.ScanAsync())
            {
            }
        }
        catch
        {
            failed = true;
        }

        await Assert.That(failed).IsTrue();
    }

    [Test]
    public async Task Scan_RefreshesThroughCachedMasterWhenSeedIsUnavailable()
    {
        await using var refreshedFirstNode = new FakeRespServer(
            "*2\r\n$1\r\n0\r\n*1\r\n$3\r\none\r\n"u8.ToArray());
        await using var secondNode = new FakeRespServer(
            "*2\r\n$1\r\n0\r\n*1\r\n$3\r\ntwo\r\n"u8.ToArray());
        var refreshedTopology = Encoding.ASCII.GetBytes(
            $"*2\r\n" +
            $"*3\r\n:0\r\n:8191\r\n*2\r\n$9\r\n127.0.0.1\r\n:{refreshedFirstNode.Port}\r\n" +
            $"*3\r\n:8192\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{secondNode.Port}\r\n");
        await using var cachedFirstNode = new FakeRespServer(refreshedTopology);
        var initialTopology = Encoding.ASCII.GetBytes(
            $"*2\r\n" +
            $"*3\r\n:0\r\n:8191\r\n*2\r\n$9\r\n127.0.0.1\r\n:{cachedFirstNode.Port}\r\n" +
            $"*3\r\n:8192\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{secondNode.Port}\r\n");
        var seed = new FakeRespServer(initialTopology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Cluster = true,
            ConnectTimeout = TimeSpan.FromMilliseconds(100),
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });
        await seed.DisposeAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (client.IsConnected)
        {
            await Task.Delay(10, timeout.Token);
        }

        var keys = new List<string>();

        await foreach (var key in client.Keys.ScanAsync(cancellationToken: timeout.Token))
        {
            keys.Add(key);
        }

        await Assert.That(keys).IsEquivalentTo(["one", "two"]);
        await Assert.That(cachedFirstNode.ReceivedCommands[0]).IsEqualTo("CLUSTER SLOTS");
        await Assert.That(refreshedFirstNode.ReceivedCommands[0]).IsEqualTo("SCAN 0 COUNT 250");
    }

    [Test]
    public async Task ClusterWideServerCommands_VisitEveryMaster()
    {
        await using var firstNode = new FakeRespServer(
            ":2\r\n"u8.ToArray(), FakeRespServer.OkReply, FakeRespServer.OkReply);
        await using var secondNode = new FakeRespServer(
            ":3\r\n"u8.ToArray(), FakeRespServer.OkReply, FakeRespServer.OkReply);
        var topology = Encoding.ASCII.GetBytes(
            $"*2\r\n" +
            $"*3\r\n:0\r\n:8191\r\n*2\r\n$9\r\n127.0.0.1\r\n:{firstNode.Port}\r\n" +
            $"*3\r\n:8192\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{secondNode.Port}\r\n");
        await using var seed = new FakeRespServer(topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Cluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });

        await Assert.That(await client.Server.DatabaseSizeAsync()).IsEqualTo(5);
        await client.Server.FlushDatabaseAsync();
        await client.Server.FlushAllAsync();

        var expected = new[] { "DBSIZE", "FLUSHDB", "FLUSHALL" };
        await Assert.That(firstNode.ReceivedCommands).IsEquivalentTo(expected);
        await Assert.That(secondNode.ReceivedCommands).IsEquivalentTo(expected);
    }

    [Test]
    public async Task BlockingServerError_ReturnsHealthyConnectionToNodePool()
    {
        await using var target = new FakeRespServer(
            2,
            "-WRONGTYPE wrong kind\r\n"u8.ToArray(),
            FakeRespServer.PongReply);
        var slot = ClusterHash.GetSlot("key");
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:{slot}\r\n:{slot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{target.Port}\r\n");
        await using var seed = new FakeRespServer(topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Cluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await Assert.That(async () =>
                await client.SendBlockingAsync(
                    "BLPOP", new Cmd1(Verbs.BLPop, "key"), timeout.Token))
            .Throws<RespireServerException>();
        var response = await client.SendBlockingAsync(
            "BLPOP", new Cmd1(Verbs.BLPop, "key"), timeout.Token);

        await Assert.That(response.AsString()).IsEqualTo("PONG");
        response.Dispose();
        await Assert.That(target.ReceivedCommands).Count().IsEqualTo(2);
    }

    [Test]
    public async Task XReadGroup_RoutesByStreamKeyAfterStreamsMarker()
    {
        RespireValue[] args =
        [
            "GROUP", "group", "consumer", "COUNT", 1, "BLOCK", 5000,
            "STREAMS", "stream-key", ">",
        ];
        var command = new CmdN(Verbs.XReadGroup, args);

        await Assert.That(command.TryGetClusterSlot(out var slot)).IsTrue();
        await Assert.That(slot).IsEqualTo(ClusterHash.GetSlot("stream-key"));
    }

    [Test]
    public async Task UnkeyedBuiltInRouting_DoesNotAllocate()
    {
        var command = new Cmd(Verbs.ClusterSlots);
        _ = TryGetSlot(command, out _);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
        {
            _ = TryGetSlot(command, out _);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        await Assert.That(allocated).IsEqualTo(0);
    }

    [Test]
    public async Task ArgumentBearingServerCommands_RemainUnkeyed()
    {
        await Assert.That(TryGetSlot(new Cmd1(Verbs.Info, "memory"), out _)).IsFalse();
        await Assert.That(TryGetSlot(new Cmd2(Verbs.ConfigSet, "timeout", 1), out _)).IsFalse();
        await Assert.That(TryGetSlot(new Cmd1(Verbs.ScriptLoad, "return 1"), out _)).IsFalse();
    }

    [Test]
    public async Task ClusterWideCommands_RejectUnavailableTopology()
    {
        await using var seed = new FakeRespServer("-NOPERM cluster slots denied\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Cluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });

        await Assert.That(async () => await client.Server.DatabaseSizeAsync())
            .Throws<RespireConnectionException>();
        await Assert.That(seed.ReceivedCommands).DoesNotContain("DBSIZE");
    }

    [Test]
    public async Task ClusterMode_RejectsNonZeroDatabase()
    {
        var error = Assert.Throws<ArgumentException>(() => RespireClient.Create(new RespireOptions
        {
            Cluster = true,
            Database = 1,
        }));

        await Assert.That(error.Message).Contains("database 0");
    }

    [Test]
    public async Task ConnectionString_ParsesClusterMode()
    {
        var options = RespireOptions.Parse("redis://localhost?cluster=true");

        await Assert.That(options.Cluster).IsTrue();
    }

    private static bool TryGetSlot<TCommand>(TCommand command, out int slot)
        where TCommand : struct, IRespCommand
        => command.TryGetClusterSlot(out slot);
}
