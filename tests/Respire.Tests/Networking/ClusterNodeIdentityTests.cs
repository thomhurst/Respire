using System.Text;
using Respire.Infrastructure;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClusterNodeIdentityTests
{
    [Test]
    [Arguments(RespireConnectionState.Reconnecting)]
    [Arguments(RespireConnectionState.Disconnected)]
    public async Task StaleHealthCallbacksPreserveRepopulatedCache(RespireConnectionState state)
    {
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("localhost") }, UseCluster = true, ClientSideCache = new(),
        });
        var core = client.Core;
        var router = core.Cluster!;
        var original = core.Multiplexer;
        var replacement = router.GetMultiplexer(new RespireEndpoint("replacement"));
        router.SetSlotOwner(0, original);
        router.SetSlotOwner(0, replacement);
        var cache = core.ClientCache!;
        RespireKey key = "cached";
        var token = cache.BeginRead(in key);
        var value = RespValue.BulkString("value"u8.ToArray());
        cache.CompleteRead(in token, in value, allowInsert: true);
        var flushes = cache.GetStatistics().ContinuityFlushes;

        core.NotifyCommandStateChanged(original, 0, state);
        core.NotifyCommandNodeRetired(replacement);

        await Assert.That(cache.Count).IsEqualTo(1);
        await Assert.That(cache.GetStatistics().ContinuityFlushes).IsEqualTo(flushes);
        // A genuine active-transport failure must still invalidate tracked data.
        core.NotifyCommandStateChanged(replacement, 0, state);
        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(cache.GetStatistics().ContinuityFlushes).IsEqualTo(flushes + 1);
    }

    [Test]
    public async Task CorrectionPoolsShareTransportAndPeerAcrossConnections()
    {
        await using var server = new FakeRespServer(3, FakeRespServer.OkReply);
        var options = Options(server.Port);
        await using var primary = await RespireConnectionMultiplexer.CreateAsync(
            "127.0.0.1", server.Port, connectionCount: 2, options: options.ToConnectionOptions());
        await using var replacement = await RespireConnectionMultiplexer.CreateAsync(
            "127.0.0.1", server.Port, options: options.ToConnectionOptions());
        await using var router = new ClusterRouter(options, primary);
        var first = primary.GetConnection(0);
        var second = primary.GetConnection(1);
        await Assert.That(ReferenceEquals(first, second)).IsFalse();

        var pool = router.GetCorrectionPool(first);
        await Assert.That(ReferenceEquals(pool, router.GetCorrectionPool(second))).IsTrue();
        await Assert.That(ReferenceEquals(pool, router.GetCorrectionPool(replacement.GetConnection()))).IsFalse();
    }

    [Test]
    public async Task CorrectionPoolsKeepDifferentNetworkPeersSeparate()
    {
        await using var oldPeer = new FakeRespServer();
        await using var newPeer = new FakeRespServer();
        var options = Options(oldPeer.Port);
        await using var primary = RespireConnectionMultiplexer.Create("redis.example", options: options.ToConnectionOptions());
        await using var router = new ClusterRouter(options, primary);
        await using var first = await RespireConnection.ConnectAsync("127.0.0.1", oldPeer.Port);
        await using var second = await RespireConnection.ConnectAsync("127.0.0.1", newPeer.Port);
        // Model successive physical connections resolving the same transport to different peers.
        first.Multiplexer = primary;
        second.Multiplexer = primary;

        await Assert.That(ReferenceEquals(router.GetCorrectionPool(first), router.GetCorrectionPool(second))).IsFalse();
    }

    [Test]
    public async Task TrackedCorrectionsRetainTransportAfterEndpointReassignment()
    {
        await using var target = new FakeRespServer(4,
            ":42\r\n"u8.ToArray(), ":0\r\n"u8.ToArray(),
            "$5\r\nvalue\r\n"u8.ToArray(), ":1\r\n"u8.ToArray());
        await using var seed = new FakeRespServer(
            Topology("localhost", target.Port, "old-id"), Topology("localhost", target.Port, "new-id"));
        await using var client = await RespireClient.ConnectAsync(Options(seed.Port));
        var script = RespireScript.Create("return redis.call('GET', KEYS[1])");
        var execution = await client.StartTrackedScriptExecutionAsync(script, ["key"], [], CancellationToken.None);
        using var result = await execution.Response;
        var original = execution.Connection;
        var router = client.Core.Cluster!;
        await router.GetMasterConnectionsAsync(CancellationToken.None);
        var replacement = await router.GetConnectionAsync(ClusterHash.GetSlot("key"), CancellationToken.None);
        await Assert.That(ReferenceEquals(original, replacement)).IsFalse();

        await client.ExecuteOnAllConnectionsAsync(script, ["key"], [], execution.ConnectionIdentity);

        var correctionIndex = target.ReceivedCommands.ToList().FindLastIndex(command => command.StartsWith("EVAL "));
        await Assert.That(target.ReceivedConnectionIds[correctionIndex]).IsEqualTo(0);
        await client.FenceCorrectionConnectionAsync(execution.ConnectionIdentity);
        await Assert.That(original.IsConnected).IsFalse();
        await Assert.That(replacement.IsConnected).IsTrue();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task AnnouncedAliasesShareMultiplexerAndDedicatedPool(bool includeNodeId)
    {
        await using var target = new FakeRespServer(FakeRespServer.PongReply);
        await using var seed = new FakeRespServer(Topology("localhost", target.Port, includeNodeId ? "node-1" : null));
        var options = Options(seed.Port);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", seed.Port, options: options.ToConnectionOptions());
        await using var router = new ClusterRouter(options, primary);
        await router.EnsureConnectedAsync(default);

        var hostname = new RespireEndpoint("localhost", target.Port);
        var address = new RespireEndpoint("127.0.0.1", target.Port);
        await Assert.That(ReferenceEquals(router.GetMultiplexer(hostname), router.GetMultiplexer(address))).IsTrue();
        await Assert.That(ReferenceEquals(router.GetMultiplexer(hostname),
            router.GetMultiplexer(new RespireEndpoint("LOCALHOST", target.Port)))).IsTrue();
        await Assert.That(ReferenceEquals(router.GetDedicatedPool(hostname), router.GetDedicatedPool(address))).IsTrue();
        await Assert.That(router.GetMultiplexer(address).Host).IsEqualTo("localhost");
    }

    [Test]
    public async Task ConfiguredSeedAliasRetainsExistingMultiplexer()
    {
        var commandReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var seed = new FakeRespServer
        {
            SuppressReply = _ => { commandReceived.TrySetResult(); return true; },
        };
        var options = Options(seed.Port);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", seed.Port, options: options.ToConnectionOptions());
        await using var router = new ClusterRouter(options, primary);
        var connect = router.EnsureConnectedAsync(default).AsTask();
        await commandReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await seed.SendRawAsync(Topology("localhost", seed.Port, "seed-node"));
        await connect;

        await Assert.That(ReferenceEquals(primary,
            router.GetMultiplexer(new RespireEndpoint("localhost", seed.Port)))).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SupersededSeedUsesReplacementForSlotlessCommands(bool movedHost)
    {
        var firstDiscovery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var discoveries = 0;
        // The same listener represents either a new node at the old address or a node whose
        // newly advertised hostname replaces its old address without an alias relationship.
        await using var seed = new FakeRespServer(2, FakeRespServer.PongReply)
        {
            SuppressReply = _ =>
            {
                if (Interlocked.Increment(ref discoveries) == 1)
                {
                    firstDiscovery.TrySetResult();
                }
                return true;
            },
        };
        var options = Options(seed.Port);
        await using var primary = RespireConnectionMultiplexer.Create(
            "127.0.0.1", seed.Port, options: options.ToConnectionOptions());
        await using var router = new ClusterRouter(options, primary);
        var connect = router.EnsureConnectedAsync(default).AsTask();
        await firstDiscovery.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var initial = Encoding.UTF8.GetBytes("*1\r\n" + Range(
            0, 16383, "127.0.0.1", seed.Port, "original", "", includeAliases: false));
        await seed.SendRawAsync(initial);
        await connect;

        var preferred = movedHost ? "localhost" : "127.0.0.1";
        var replacement = Encoding.UTF8.GetBytes("*1\r\n" + Range(
            0, 16383, preferred, seed.Port, movedHost ? "original" : "replacement", "", includeAliases: false));
        var refreshReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        seed.SuppressReply = _ => { refreshReceived.TrySetResult(); return true; };
        var refresh = router.GetMasterConnectionsAsync(default).AsTask();
        await refreshReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await seed.SendRawAsync(replacement);
        await refresh;

        var current = router.GetMultiplexer(new RespireEndpoint(preferred, seed.Port));
        await Assert.That(ReferenceEquals(current, primary)).IsFalse();
        await Assert.That(primary.IsConnected).IsTrue();
        var slotless = await router.GetConnectionAsync(null, default);
        await Assert.That(ReferenceEquals(slotless, current.GetConnection())).IsTrue();
        await Assert.That(router.SeedEndpoint.Host).IsEqualTo(preferred);
    }

    [Test]
    public async Task MissingNodeIdPrefersExistingPreferredTransportOverAlias()
    {
        await using var target = new FakeRespServer(FakeRespServer.PongReply);
        await using var seed = new FakeRespServer(Topology("127.0.0.1", target.Port, null));
        var options = Options(seed.Port);
        await using var primary = RespireConnectionMultiplexer.Create(
            "127.0.0.1", seed.Port, options: options.ToConnectionOptions());
        await using var router = new ClusterRouter(options, primary);
        var preferred = router.GetMultiplexer(new RespireEndpoint("127.0.0.1", target.Port));
        var alias = router.GetMultiplexer(new RespireEndpoint("localhost", target.Port));
        await router.EnsureConnectedAsync(default);

        await Assert.That(ReferenceEquals(preferred, alias)).IsFalse();
        await Assert.That(ReferenceEquals(preferred,
            router.GetMultiplexer(new RespireEndpoint("localhost", target.Port)))).IsTrue();
        await Assert.That(ReferenceEquals(preferred,
            router.GetMultiplexer(new RespireEndpoint("127.0.0.1", target.Port)))).IsTrue();
    }

    [Test]
    [Arguments(RespireConnectionState.Reconnecting)]
    [Arguments(RespireConnectionState.Disconnected)]
    public async Task DelayedRetirementDoesNotClearReactivatedNodeHealth(RespireConnectionState state)
    {
        await using var oldServer = new FakeRespServer();
        await using var newServer = new FakeRespServer();
        var slot = ClusterHash.GetSlot("key");
        await using var seed = new FakeRespServer(Encoding.UTF8.GetBytes("*1\r\n" + Range(
            slot, slot, "127.0.0.1", oldServer.Port, "original", "", includeAliases: false)));
        await using var client = await RespireClient.ConnectAsync(Options(seed.Port));
        var core = client.Core;
        var router = core.Cluster!;
        var original = router.GetMultiplexer(new RespireEndpoint("127.0.0.1", oldServer.Port));
        var replacement = router.GetMultiplexer(new RespireEndpoint("127.0.0.1", newServer.Port));
        router.SetSlotOwner(slot, replacement);
        router.SetSlotOwner(slot, original);
        var states = new List<RespireConnectionState>();
        core.ConnectionStateChanged += change => states.Add(change.State);
        core.NotifyCommandStateChanged(original, 0, state);

        // Model an earlier retirement callback delivered after the node was reactivated.
        core.NotifyCommandNodeRetired(original);

        await Assert.That(states).IsEquivalentTo([state]);
    }

    [Test]
    [Arguments(RespireConnectionState.Reconnecting)]
    [Arguments(RespireConnectionState.Disconnected)]
    public async Task RetiredNodeLateHealthNotificationIsIgnored(RespireConnectionState state)
    {
        await using var oldServer = new FakeRespServer();
        await using var newServer = new FakeRespServer();
        var slot = ClusterHash.GetSlot("key");
        await using var seed = new FakeRespServer(Encoding.UTF8.GetBytes("*1\r\n" + Range(
            slot, slot, "127.0.0.1", oldServer.Port, "original", "", includeAliases: false)));
        await using var client = await RespireClient.ConnectAsync(Options(seed.Port));
        var core = client.Core;
        var router = core.Cluster!;
        var original = router.GetMultiplexer(new RespireEndpoint("127.0.0.1", oldServer.Port));
        var replacement = router.GetMultiplexer(new RespireEndpoint("127.0.0.1", newServer.Port));
        router.SetSlotOwner(slot, replacement);
        var states = new List<RespireConnectionState>();
        core.ConnectionStateChanged += change => states.Add(change.State);

        // A transport can already have captured its event delegate before unsubscription.
        core.NotifyCommandStateChanged(original, 0, state);

        await Assert.That(states).IsEmpty();
    }

    [Test]
    public async Task DifferentNodeIdsDoNotMergeThroughConflictingMetadata()
    {
        await using var first = new FakeRespServer();
        var firstRange = Range(0, 8191, "127.0.0.1", first.Port, "first", "shared.invalid");
        var secondRange = Range(8192, 16383, "localhost", first.Port, "second", "shared.invalid");
        await using var seed = new FakeRespServer(Encoding.UTF8.GetBytes("*2\r\n" + firstRange + secondRange));
        var options = Options(seed.Port);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", seed.Port, options: options.ToConnectionOptions());
        await using var router = new ClusterRouter(options, primary);
        await router.EnsureConnectedAsync(default);

        await Assert.That(ReferenceEquals(
            router.GetMultiplexer(new RespireEndpoint("127.0.0.1", first.Port)),
            router.GetMultiplexer(new RespireEndpoint("localhost", first.Port)))).IsFalse();
    }

    [Test]
    public async Task NodeIdLinksPreferredFormsWithoutMetadataAliases()
    {
        await using var target = new FakeRespServer();
        var ranges = Range(0, 8191, "localhost", target.Port, "same-node", "", includeAliases: false)
            + Range(8192, 16383, "127.0.0.1", target.Port, "same-node", "", includeAliases: false);
        await using var seed = new FakeRespServer(Encoding.UTF8.GetBytes("*2\r\n" + ranges));
        var options = Options(seed.Port);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", seed.Port, options: options.ToConnectionOptions());
        await using var router = new ClusterRouter(options, primary);
        await router.EnsureConnectedAsync(default);

        await Assert.That(ReferenceEquals(
            router.GetMultiplexer(new RespireEndpoint("localhost", target.Port)),
            router.GetMultiplexer(new RespireEndpoint("127.0.0.1", target.Port)))).IsTrue();
    }

    [Test]
    public async Task EndpointReassignedToDifferentNodeIdGetsNewTransport()
    {
        await using var target = new FakeRespServer(FakeRespServer.PongReply);
        await using var seed = new FakeRespServer(
            Topology("localhost", target.Port, "old-node"),
            Topology("localhost", target.Port, "new-node"));
        var options = Options(seed.Port);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", seed.Port, options: options.ToConnectionOptions());
        await using var router = new ClusterRouter(options, primary);
        await router.EnsureConnectedAsync(default);
        var endpoint = new RespireEndpoint("localhost", target.Port);
        var old = router.GetMultiplexer(endpoint);
        await router.GetMasterConnectionsAsync(default);

        await Assert.That(ReferenceEquals(old, router.GetMultiplexer(endpoint))).IsFalse();
        await Assert.That(ReferenceEquals(router.GetMultiplexer(endpoint),
            router.GetMultiplexer(new RespireEndpoint("127.0.0.1", target.Port)))).IsTrue();
    }

    [Test]
    public async Task ChangedPortForKnownNodeIdCreatesTransportAtNewPort()
    {
        await using var original = new FakeRespServer(FakeRespServer.PongReply);
        await using var replacement = new FakeRespServer(FakeRespServer.PongReply);
        await using var seed = new FakeRespServer(
            Topology("localhost", original.Port, "same-node"),
            Topology("localhost", replacement.Port, "same-node"));
        var options = Options(seed.Port);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", seed.Port, options: options.ToConnectionOptions());
        await using var router = new ClusterRouter(options, primary);
        await router.EnsureConnectedAsync(default);
        var connections = await router.GetMasterConnectionsAsync(default);

        await Assert.That(connections).Count().IsEqualTo(1);
        await Assert.That(connections[0].Port).IsEqualTo(replacement.Port);
    }

    [Test]
    public async Task StableNodeIdMovingHostUsesNewTransport()
    {
        await using var target = new FakeRespServer(FakeRespServer.PongReply);
        var original = Encoding.UTF8.GetBytes("*1\r\n" + Range(0, 16383, "127.0.0.2", target.Port, "same-node", "", includeAliases: false));
        var replacement = Encoding.UTF8.GetBytes("*1\r\n" + Range(0, 16383, "127.0.0.1", target.Port, "same-node", "", includeAliases: false));
        await using var seed = new FakeRespServer(original, replacement);
        var options = Options(seed.Port);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", seed.Port, options: options.ToConnectionOptions());
        await using var router = new ClusterRouter(options, primary);
        await router.EnsureConnectedAsync(default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var connections = await router.GetMasterConnectionsAsync(timeout.Token);

        await Assert.That(connections[0].Host).IsEqualTo("127.0.0.1");
    }

    [Test]
    public async Task ReassignedAliasResolvesToItsNewNodeId()
    {
        await using var target = new FakeRespServer(FakeRespServer.PongReply);
        var original = Encoding.UTF8.GetBytes("*1\r\n" + Range(0, 16383, "old.invalid", target.Port, "old-node", "old.invalid"));
        await using var seed = new FakeRespServer(original, Topology("localhost", target.Port, "new-node"));
        var options = Options(seed.Port);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", seed.Port, options: options.ToConnectionOptions());
        await using var router = new ClusterRouter(options, primary);
        await router.EnsureConnectedAsync(default);
        var address = new RespireEndpoint("127.0.0.1", target.Port);
        var old = router.GetMultiplexer(address);
        await router.GetMasterConnectionsAsync(default);

        await Assert.That(ReferenceEquals(old, router.GetMultiplexer(address))).IsFalse();
        await Assert.That(ReferenceEquals(router.GetMultiplexer(address),
            router.GetMultiplexer(new RespireEndpoint("localhost", target.Port)))).IsTrue();
        await Assert.That(ReferenceEquals(router.GetDedicatedPool(address),
            router.GetDedicatedPool(new RespireEndpoint("localhost", target.Port)))).IsTrue();
    }

    internal static byte[] Topology(string preferred, int port, string? nodeId)
        => Encoding.UTF8.GetBytes("*1\r\n" + Range(0, 16383, preferred, port, nodeId, "localhost"));

    private static string Range(
        int start, int end, string preferred, int port, string? nodeId, string hostname, bool includeAliases = true)
        => $"*3\r\n:{start}\r\n:{end}\r\n*{(includeAliases ? 4 : 3)}\r\n{Bulk(preferred)}:{port}\r\n{(nodeId is null ? "$-1\r\n" : Bulk(nodeId))}"
            + (includeAliases ? $"%2\r\n+hostname\r\n{Bulk(hostname)}+ip\r\n{Bulk("127.0.0.1")}" : "");

    private static string Bulk(string value) => $"${Encoding.UTF8.GetByteCount(value)}\r\n{value}\r\n";

    private static RespireOptions Options(int seedPort) => new()
    {
        UseCluster = true,
        Endpoints = { new RespireEndpoint("127.0.0.1", seedPort) },
        Connections = 1,
    };
}
