using Microsoft.Extensions.Logging;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class NearestReadRoutingTests
{
    private static readonly byte[] ReplicaRole = "*5\r\n$5\r\nslave\r\n$9\r\n127.0.0.1\r\n:6379\r\n$9\r\nconnected\r\n:0\r\n"u8.ToArray();

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task ColdCandidatesShareOneSamplingWaitBudget(bool cluster, bool slowHandshake)
    {
        await using var primary = Server("primary");
        await using var first = Server("first");
        await using var second = Server("second");
        await using var third = Server("third");
        foreach (var replica in new[] { first, second, third }) replica.SuppressReply = command => command == "PING";
        primary.ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
            ? System.Text.Encoding.ASCII.GetBytes($"*1\r\n*6\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{primary.Port}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{first.Port}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{second.Port}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{third.Port}\r\n")
            : Reply(command, "primary");
        var options = Options(primary, first, second, third);
        if (slowHandshake)
        {
            options = options with { Password = "test-password" };
            primary.DelayCommand("AUTH ", 3_000);
        }
        if (cluster) options = options with { UseCluster = true, ReplicaEndpoints = [], ClusterTopologyRefreshInterval = null };
        await using var client = await RespireClient.ConnectAsync(options);
        // Connection handshakes and topology discovery are setup, not sampling wait time.
        // Warm every transport through ordinary replica reads, leaving latency samples cold.
        await using var replicas = client.WithReadFrom(RespireReadFrom.Replica);
        var warmed = new HashSet<string?>();
        for (var attempt = 0; attempt < 30 && warmed.Count < 3; attempt++)
            warmed.Add(await replicas.GetStringAsync("warmup"));
        await Assert.That(warmed).IsEquivalentTo(new string?[] { "first", "second", "third" });
        await Assert.That(cluster ? client.Core.Cluster!.NearestLatency : client.Core.ReadRouter.NearestLatency).IsNull();
        await using var nearest = client.WithReadFrom(RespireReadFrom.Nearest);
        // Three stalled samples previously consumed three independent one-second waits.
        await Assert.That(await nearest.GetStringAsync("key").AsTask().WaitAsync(TimeSpan.FromSeconds(2.5)))
            .IsEqualTo("primary");
        var sampler = cluster ? client.Core.Cluster!.NearestLatency! : client.Core.ReadRouter.NearestLatency!;
        await Assert.That(sampler.SamplesStarted).IsEqualTo(4);
        foreach (var replica in new[] { first, second, third })
            await Assert.That(replica.ReceivedCommands.Contains("GET key")).IsFalse();
    }

    [Test]
    public async Task UnknownClusterReplicasRefreshWithoutDelayingHealthyPrimary()
    {
        await using var primary = Server("primary");
        primary.ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
            ? System.Text.Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{primary.Port}\r\n")
            : Reply(command, "primary");
        await using var client = await RespireClient.ConnectAsync(Options(primary) with
        {
            UseCluster = true, ClusterTopologyRefreshInterval = null, CommandTimeout = TimeSpan.FromSeconds(10),
        });
        var slot = ClusterHash.GetSlot("key");
        var router = client.Core.Cluster!;
        router.SetSlotOwner(slot, router.GetKnownSlotOwner(slot)!);
        primary.SuppressReply = command => command == "CLUSTER SLOTS";
        // A separate topology connection can stall without holding up this primary's data socket.
        router.NearestLatency = new ReadLatencySampler<RespireConnection>((_, _) => ValueTask.FromResult(10L));
        await using var nearest = client.WithReadFrom(RespireReadFrom.Nearest);
        await Assert.That(await nearest.GetStringAsync("key").AsTask().WaitAsync(TimeSpan.FromSeconds(2)))
            .IsEqualTo("primary");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (primary.ReceivedCommands.Count(command => command == "CLUSTER SLOTS") < 2)
            await Task.Delay(1, timeout.Token);
        await Assert.That(await nearest.GetStringAsync("again").AsTask().WaitAsync(TimeSpan.FromSeconds(2)))
            .IsEqualTo("primary");
        var commands = primary.ReceivedCommands.ToList();
        await Assert.That(primary.ReceivedConnectionIds[commands.FindLastIndex(command => command == "CLUSTER SLOTS")])
            .IsNotEqualTo(primary.ReceivedConnectionIds[commands.FindIndex(command => command == "GET key")]);
    }

    [Test]
    public async Task UnknownSentinelReplicasDoNotDelayHealthyPrimary()
    {
        await using var primary = Server("primary");
        await using var sentinel = new FakeRespServer(16, "*0\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command.StartsWith("SENTINEL GET-MASTER-ADDR-BY-NAME ")
                ? System.Text.Encoding.ASCII.GetBytes($"*2\r\n$9\r\n127.0.0.1\r\n${primary.Port.ToString().Length}\r\n{primary.Port}\r\n")
                : "*0\r\n"u8.ToArray(),
            SuppressReply = command => command.StartsWith("SENTINEL REPLICAS "),
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, SentinelPrimaryName = "mymaster",
            Endpoints = [new("127.0.0.1", sentinel.Port)], CommandTimeout = TimeSpan.FromSeconds(10),
        });
        await using var nearest = client.WithReadFrom(RespireReadFrom.Nearest);
        await Assert.That(await nearest.GetStringAsync("key").AsTask().WaitAsync(TimeSpan.FromSeconds(2)))
            .IsEqualTo("primary");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!sentinel.ReceivedCommands.Any(command => command.StartsWith("SENTINEL REPLICAS ")))
            await Task.Delay(1, timeout.Token);
        await Assert.That(await nearest.GetStringAsync("again").AsTask().WaitAsync(TimeSpan.FromSeconds(2)))
            .IsEqualTo("primary");
    }

    [Test]
    public async Task CanceledColdSamplingWaitPreservesConnectionReplyOrder()
    {
        await using var primary = Server("primary");
        primary.DelayCommand("PING", 250);
        await using var replica = Server("replica");
        replica.ReplyOverride = (_, command) => command == "PING"
            ? "-NOPERM ping denied\r\n"u8.ToArray() : Reply(command, "replica");
        await using var client = RespireClient.Create(Options(primary, replica));
        await using var nearest = client.WithReadFrom(RespireReadFrom.Nearest);
        using var cancellation = new CancellationTokenSource();
        var first = nearest.GetStringAsync("first", cancellation.Token).AsTask();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!primary.ReceivedCommands.Contains("PING")) await Task.Delay(1, deadline.Token);
        cancellation.Cancel();
        await Assert.That(async () => await first).Throws<OperationCanceledException>();
        await Assert.That(await nearest.GetStringAsync("second", deadline.Token)).IsEqualTo("primary");
        await Assert.That(primary.ReceivedCommands.Count(command => command == "PING")).IsEqualTo(1);
        await Assert.That(primary.ReceivedCommands).DoesNotContain("GET first");
    }

    [Test]
    public async Task NearestRejectsFastCandidateAfterItsRoleChangesAndRecovers()
    {
        await using var primary = Server("primary");
        await using var replica = Server("replica");
        var wrongRole = false;
        replica.ReplyOverride = (_, command) => command == "ROLE" && Volatile.Read(ref wrongRole)
            ? "*3\r\n$6\r\nmaster\r\n:0\r\n*0\r\n"u8.ToArray() : Reply(command, "replica");
        await using var client = RespireClient.Create(Options(primary, replica) with { ReplicaRefreshInterval = TimeSpan.Zero });
        client.Core.ReadRouter.NearestLatency = new ReadLatencySampler<RespireConnection>((connection, _) =>
            ValueTask.FromResult(connection.Port == replica.Port ? 10L : 100L));
        await using var nearest = client.WithReadFrom(RespireReadFrom.Nearest);
        await Assert.That(await nearest.GetStringAsync("key")).IsEqualTo("replica");
        Volatile.Write(ref wrongRole, true);
        await Assert.That(await nearest.GetStringAsync("key")).IsEqualTo("primary");
        Volatile.Write(ref wrongRole, false);
        await Assert.That(await nearest.GetStringAsync("key")).IsEqualTo("replica");
    }

    [Test]
    public async Task NearestUsesHealthyReplicaWhenPrimaryCannotConnect()
    {
        using var unavailable = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        unavailable.Start();
        var port = ((System.Net.IPEndPoint)unavailable.LocalEndpoint).Port;
        unavailable.Stop();
        await using var replica = Server("replica");
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, ConnectTimeout = TimeSpan.FromMilliseconds(200),
            Endpoints = [new("127.0.0.1", port)],
            ReplicaEndpoints = [new("127.0.0.1", replica.Port)],
        });
        await using var nearest = client.WithReadFrom(RespireReadFrom.Nearest);
        await Assert.That(await nearest.GetStringAsync("key")).IsEqualTo("replica");
        await Assert.That(await nearest.GetStringAsync("key")).IsEqualTo("replica");
    }

    [Test]
    public async Task RoleInvalidatedDuringSamplingCannotWinSelection()
    {
        await using var primary = Server("primary");
        await using var replica = Server("replica");
        var wrongRole = false;
        replica.ReplyOverride = (_, command) => command == "ROLE" && Volatile.Read(ref wrongRole)
            ? "*3\r\n$6\r\nmaster\r\n:0\r\n*0\r\n"u8.ToArray() : Reply(command, "replica");
        await using var client = RespireClient.Create(Options(primary, replica) with { ReplicaRefreshInterval = TimeSpan.Zero });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Core.ReadRouter.NearestLatency = new ReadLatencySampler<RespireConnection>(async (connection, token) =>
        {
            if (connection.Port != replica.Port) return 100L;
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return 10L;
        });
        await using var nearest = client.WithReadFrom(RespireReadFrom.Nearest);
        var pending = nearest.GetStringAsync("key").AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Volatile.Write(ref wrongRole, true);
            await Assert.That(async () => await client.WithReadFrom(RespireReadFrom.Replica).GetStringAsync("key"))
                .Throws<RespireConnectionException>();
        }
        finally { release.TrySetResult(); }
        await Assert.That(await pending).IsEqualTo("primary");
        await Assert.That(replica.ReceivedCommands.Any(command => command.StartsWith("GET "))).IsFalse();
    }

    [Test]
    public async Task NearestPrefersConnectedReplicationLinkOverLowerLatencyDisconnectedReplica()
    {
        await using var primary = Server("primary");
        await using var replica = Server("replica");
        replica.ReplyOverride = (_, command) => command == "ROLE"
            ? "*5\r\n$5\r\nslave\r\n$9\r\n127.0.0.1\r\n:6379\r\n$7\r\nconnect\r\n:0\r\n"u8.ToArray() : Reply(command, "replica");
        await using var client = RespireClient.Create(Options(primary, replica));
        client.Core.ReadRouter.NearestLatency = new ReadLatencySampler<RespireConnection>((connection, _) =>
            ValueTask.FromResult(connection.Port == replica.Port ? 10L : 100L));
        await using var nearest = client.WithReadFrom(RespireReadFrom.Nearest);
        await Assert.That(await nearest.GetStringAsync("key")).IsEqualTo("primary");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ClusterNearestChoosesLowestLatencyEligibleRole(bool movedCoverage)
    {
        await using var primary = Server("primary");
        await using var replica = Server("replica");
        primary.ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
            ? System.Text.Encoding.ASCII.GetBytes($"*1\r\n*4\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{primary.Port}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{replica.Port}\r\n")
            : Reply(command, "primary");
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, UseCluster = true, ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", primary.Port)],
        });
        client.Core.Cluster!.NearestLatency = new ReadLatencySampler<RespireConnection>((connection, _) =>
            ValueTask.FromResult(connection.Port == replica.Port ? 10L : 100L));
        if (movedCoverage)
        {
            var slot = ClusterHash.GetSlot("key");
            await client.Core.Cluster.GetReadConnectionAsync(slot, RespireReadFrom.Replica, default);
            client.Core.Cluster.SetSlotOwner(slot, client.Core.Cluster.GetKnownSlotOwner(slot)!);
        }
        await using var nearest = client.WithReadFrom(RespireReadFrom.Nearest);
        if (movedCoverage)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (await nearest.GetStringAsync("key", timeout.Token) != "replica") await Task.Delay(1, timeout.Token);
        }
        await Assert.That(await nearest.GetStringAsync("key")).IsEqualTo("replica");
        await Assert.That(await nearest.SetAsync("key", "write")).IsTrue();
        await Assert.That(primary.ReceivedCommands).Contains("SET key write");
        await Assert.That(replica.ReceivedCommands).Contains("READONLY");
        if (movedCoverage)
            await Assert.That(primary.ReceivedCommands.Count(command => command == "CLUSTER SLOTS")).IsEqualTo(2);
    }

    [Test]
    public async Task SentinelNearestDropsRemovedCandidates()
    {
        await using var primary = Server("primary");
        await using var replica = Server("replica");
        var removed = false;
        await using var sentinel = new FakeRespServer(64, "*0\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command.StartsWith("SENTINEL GET-MASTER-ADDR-BY-NAME ")
                ? System.Text.Encoding.ASCII.GetBytes($"*2\r\n$9\r\n127.0.0.1\r\n${primary.Port.ToString().Length}\r\n{primary.Port}\r\n")
                : command.StartsWith("SENTINEL REPLICAS ") && !Volatile.Read(ref removed)
                    ? System.Text.Encoding.ASCII.GetBytes($"*1\r\n*6\r\n$2\r\nip\r\n$9\r\n127.0.0.1\r\n$4\r\nport\r\n${replica.Port.ToString().Length}\r\n{replica.Port}\r\n$5\r\nflags\r\n$5\r\nslave\r\n")
                    : "*0\r\n"u8.ToArray(),
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, SentinelPrimaryName = "mymaster",
            Endpoints = [new("127.0.0.1", sentinel.Port)],
        });
        client.Core.ReadRouter.NearestLatency = new ReadLatencySampler<RespireConnection>((connection, _) =>
            ValueTask.FromResult(connection.Port == replica.Port ? 10L : 100L));
        await client.PingAsync();
        await client.Core.ReadRouter.RefreshNowAsync(default);
        await using var nearest = client.WithReadFrom(RespireReadFrom.Nearest);
        await Assert.That(await nearest.GetStringAsync("key")).IsEqualTo("replica");
        Volatile.Write(ref removed, true);
        await client.Core.ReadRouter.RefreshNowAsync(default);
        await Assert.That(await nearest.GetStringAsync("key")).IsEqualTo("primary");
    }

    [Test]
    public async Task NearestViewsDoNotReadOrPopulateThePrimaryCache()
    {
        await using var primary = Server("primary");
        await using var replica = Server("replica");
        await using var client = RespireClient.Create(Options(primary, replica) with
        {
            Protocol = RespProtocol.Resp3, ClientSideCache = new(),
        });
        client.Core.ReadRouter.NearestLatency = new ReadLatencySampler<RespireConnection>((connection, _) =>
            ValueTask.FromResult(connection.Port == replica.Port ? 10L : 100L));
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("primary");
        await using var nearest = client.WithReadFrom(RespireReadFrom.Nearest);
        await Assert.That(await nearest.GetStringAsync("key")).IsEqualTo("replica");
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(1);
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("primary");
        await Assert.That(primary.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(1);
    }

    [Test]
    public async Task NearestCursorRemainsOnItsOriginalEndpoint()
    {
        await using var primary = Server("primary");
        await using var replica = Server("replica");
        replica.ReplyOverride = (_, command) => command.StartsWith("SCAN 0 ")
            ? "*2\r\n$1\r\n7\r\n*1\r\n$1\r\na\r\n"u8.ToArray()
            : command.StartsWith("SCAN 7 ") ? "*2\r\n$1\r\n0\r\n*1\r\n$1\r\nb\r\n"u8.ToArray() : Reply(command, "replica");
        await using var client = RespireClient.Create(Options(primary, replica));
        long now = 100;
        var changed = false;
        client.Core.ReadRouter.NearestLatency = new ReadLatencySampler<RespireConnection>((connection, _) =>
            ValueTask.FromResult(connection.Port == primary.Port ? 100L : changed ? 1_000L : 10L), () => now);
        await using var nearest = client.WithReadFrom(RespireReadFrom.Nearest);
        await using var scan = nearest.Keys.ScanAsync().GetAsyncEnumerator();
        await Assert.That(await scan.MoveNextAsync()).IsTrue();
        changed = true;
        now += ReadLatencySampler<RespireConnection>.IntervalMilliseconds;
        await Assert.That(await scan.MoveNextAsync()).IsTrue();
        await Assert.That(await scan.MoveNextAsync()).IsFalse();
        await Assert.That(primary.ReceivedCommands.Any(command => command.StartsWith("SCAN "))).IsFalse();
        await Assert.That(replica.ReceivedCommands.Count(command => command.StartsWith("SCAN "))).IsEqualTo(2);
    }

    [Test]
    public async Task ConfiguredNearestSelectsLowestLatencyAndKeepsWritesAndUnknownCommandsPrimary()
    {
        await using var primary = Server("primary");
        await using var slower = Server("slower");
        await using var faster = Server("faster");
        await using var client = RespireClient.Create(Options(primary, slower, faster));
        client.Core.ReadRouter.NearestLatency = new ReadLatencySampler<RespireConnection>((connection, _) =>
            ValueTask.FromResult(connection.Port == faster.Port ? 10L : connection.Port == slower.Port ? 50L : 100L));
        await using var nearest = client.WithReadFrom(RespireReadFrom.Nearest);
        await Assert.That(await nearest.GetStringAsync("key")).IsEqualTo("faster");
        await Assert.That(await nearest.SetAsync("key", "write")).IsTrue();
        using var raw = await nearest.ExecuteAsync("GET key");
        await Assert.That(raw.AsString()).IsEqualTo("primary");
        await Assert.That(primary.ReceivedCommands).Contains("SET key write");
        await Assert.That(slower.ReceivedCommands.Any(command => command.StartsWith("GET "))).IsFalse();
    }

    [Test]
    public async Task NearestAdaptsWhenMeasuredLatencyChanges()
    {
        await using var primary = Server("primary");
        await using var replica = Server("replica");
        await using var client = RespireClient.Create(Options(primary, replica));
        long now = 100;
        var changed = false;
        client.Core.ReadRouter.NearestLatency = new ReadLatencySampler<RespireConnection>((connection, _) =>
            ValueTask.FromResult(connection.Port == primary.Port ? 100L : changed ? 1_000L : 10L), () => now);
        await using var nearest = client.WithReadFrom(RespireReadFrom.Nearest);
        await Assert.That(await nearest.GetStringAsync("key")).IsEqualTo("replica");
        changed = true;
        now += ReadLatencySampler<RespireConnection>.IntervalMilliseconds;
        await Assert.That(await nearest.GetStringAsync("key")).IsEqualTo("primary");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EqualLatenciesRotateBetweenEligibleRoles(bool pendingReplica)
    {
        await using var primary = Server("primary");
        await using var replica = Server("replica");
        await using var client = RespireClient.Create(Options(primary, replica));
        var reply = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Core.ReadRouter.NearestLatency = new ReadLatencySampler<RespireConnection>((connection, token) =>
        {
            if (pendingReplica && connection.Port == replica.Port) return new(reply.Task.WaitAsync(token));
            reply.TrySetResult(10L);
            return ValueTask.FromResult(10L);
        });
        await using var nearest = client.WithReadFrom(RespireReadFrom.Nearest);
        var first = await nearest.GetStringAsync("key");
        var second = await nearest.GetStringAsync("key");
        await Assert.That(new[] { first, second }).IsEquivalentTo(new[] { "primary", "replica" });
    }

    [Test]
    public async Task PrimaryDoesNotCreateSamplerOrSendHealthChecks()
    {
        await using var primary = Server("primary");
        await using var replica = Server("replica");
        await using var client = RespireClient.Create(Options(primary, replica));
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("primary");
        await Assert.That(client.Core.ReadRouter.NearestLatency).IsNull();
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(["GET key"]);
        await Assert.That(replica.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task PingsAreAdvisoryAndCannotMakeHealthyReadsFail()
    {
        await using var primary = Server("primary");
        await using var replica = Server("replica");
        primary.ReplyOverride = (_, command) => command == "PING" ? "-NOPERM ping denied\r\n"u8.ToArray() : Reply(command, "primary");
        replica.ReplyOverride = (_, command) => command == "PING" ? "-NOPERM ping denied\r\n"u8.ToArray() : Reply(command, "replica");
        await using var client = RespireClient.Create(Options(primary, replica));
        await using var nearest = client.WithReadFrom(RespireReadFrom.Nearest);
        await Assert.That(await nearest.GetStringAsync("key")).IsNotNull();
        await Assert.That(primary.ReceivedCommands).Contains("PING");
        await Assert.That(replica.ReceivedCommands).Contains("PING");
    }

    [Test]
    public async Task ConcurrentUnknownSlotsEachFinishTheirBackgroundDiscovery()
    {
        await using var primary = Server("primary");
        await using var replica = Server("replica");
        primary.ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
            ? SlotReply(primary.Port, 0, 16383) : Reply(command, "primary");
        await using var client = await RespireClient.ConnectAsync(Options(primary) with
        {
            UseCluster = true, ClusterTopologyRefreshInterval = null, CommandTimeout = TimeSpan.FromSeconds(10),
        });
        var router = client.Core.Cluster!;
        var owner = router.GetKnownSlotOwner(0)!;
        router.SetSlotOwner(0, owner);
        router.SetSlotOwner(1, owner);
        router.NearestLatency = new ReadLatencySampler<RespireConnection>((connection, _) =>
            ValueTask.FromResult(connection.Port == replica.Port ? 10L : 100L));
        primary.SuppressReply = command => command == "CLUSTER SLOTS";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.That((await router.GetReadConnectionAsync(0, RespireReadFrom.Nearest, timeout.Token)).Port).IsEqualTo(primary.Port);
        while (primary.ReceivedCommands.Count(command => command == "CLUSTER SLOTS") < 2)
            await Task.Delay(1, timeout.Token);
        await Assert.That((await router.GetReadConnectionAsync(1, RespireReadFrom.Nearest, timeout.Token)).Port).IsEqualTo(primary.Port);
        var firstQuery = primary.ReceivedCommands.ToList().FindLastIndex(command => command == "CLUSTER SLOTS");
        await primary.SendRawAsync(SlotReply(primary.Port, 0, 0), primary.ReceivedConnectionIds[firstQuery]);
        // No second read for slot 1: its existing background waiter must request independent coverage.
        while (primary.ReceivedCommands.Count(command => command == "CLUSTER SLOTS") < 3)
            await Task.Delay(1, timeout.Token);
        var secondQuery = primary.ReceivedCommands.ToList().FindLastIndex(command => command == "CLUSTER SLOTS");
        await primary.SendRawAsync(SlotReply(primary.Port, 1, 1, replica.Port), primary.ReceivedConnectionIds[secondQuery]);
        // The discovery inventory is published before the per-slot routes. Wait for the
        // membership used by selection, not the earlier inventory notification.
        while (KnownReplicaRoutes(router, 1)?.Nodes.Any(node => node.Port == replica.Port) != true)
            await Task.Delay(1, timeout.Token);
        await Assert.That((await router.GetReadConnectionAsync(1, RespireReadFrom.Nearest, timeout.Token)).Port).IsEqualTo(replica.Port);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task SentinelRefreshRescuesExhaustedCandidatesWithoutDelayingHealthyReplica(bool oldUnavailable, bool sameEndpoints)
    {
        await using var primary = Server("primary");
        await using var old = Server("old");
        await using var replacement = Server("replacement");
        var replicaPort = old.Port;
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var sentinel = new FakeRespServer(32, "*0\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command.StartsWith("SENTINEL GET-MASTER-ADDR-BY-NAME ")
                ? System.Text.Encoding.ASCII.GetBytes($"*2\r\n$9\r\n127.0.0.1\r\n${primary.Port.ToString().Length}\r\n{primary.Port}\r\n")
                : command.StartsWith("SENTINEL REPLICAS ") ? SentinelReplicaReply(replicaPort) : "*0\r\n"u8.ToArray(),
        };
        await using var client = await RespireClient.ConnectAsync(Options(sentinel) with { SentinelPrimaryName = "mymaster" });
        var router = client.Core.ReadRouter;
        await router.RefreshNowAsync(CancellationToken.None);
        router.SentinelRefreshInterval = TimeSpan.Zero;
        long now = 0;
        router.NearestLatency = new ReadLatencySampler<RespireConnection>((_, _) => ValueTask.FromResult(10L), () => Volatile.Read(ref now));
        router.NearestLatency.ConnectionFailed(client.Core.Multiplexer);
        old.ReplyOverride = (_, command) => oldUnavailable && command == "ROLE"
            ? "-LOADING stale replica\r\n"u8.ToArray() : Reply(command, "old");
        replicaPort = sameEndpoints ? old.Port : replacement.Port;
        sentinel.SuppressReply = command =>
        {
            if (!command.StartsWith("SENTINEL REPLICAS ")) return false;
            refreshStarted.TrySetResult();
            return true;
        };
        var read = client.WithReadFrom(RespireReadFrom.Nearest).GetStringAsync("key").AsTask();
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (!oldUnavailable)
            await Assert.That(await read.WaitAsync(TimeSpan.FromSeconds(2))).IsEqualTo("old");
        var query = sentinel.ReceivedCommands.ToList().FindLastIndex(command => command.StartsWith("SENTINEL REPLICAS "));
        // The same primary becomes eligible again during discovery, without an address change.
        if (sameEndpoints) Volatile.Write(ref now, 2_000);
        sentinel.SuppressReply = null;
        await sentinel.SendRawAsync(SentinelReplicaReply(replicaPort), sentinel.ReceivedConnectionIds[query]);
        var expected = oldUnavailable ? "replacement" : "old";
        if (sameEndpoints) expected = "primary";
        await Assert.That(await read.WaitAsync(TimeSpan.FromSeconds(5))).IsEqualTo(expected);
    }

    [Test]
    public async Task ExhaustedClusterCandidatesJoinRefreshThroughAnotherMaster()
    {
        await using var primary = Server("primary");
        await using var old = Server("old");
        await using var replacement = Server("replacement");
        await using var otherMaster = Server("primary");
        primary.ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
            ? SlotReply(primary.Port, 0, 16383, old.Port) : Reply(command, "primary");
        otherMaster.ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
            ? SlotReply(primary.Port, 0, 8191, replacement.Port) : Reply(command, "primary");
        await using var client = await RespireClient.ConnectAsync(Options(primary) with
        {
            UseCluster = true, ClusterTopologyRefreshInterval = null, ReplicaRouteRevalidationInterval = TimeSpan.Zero,
        });
        var router = client.Core.Cluster!;
        var owner = router.GetKnownSlotOwner(1)!;
        router.SetSlotOwner(8192, router.GetOrCreateNode(new("127.0.0.1", otherMaster.Port)));
        await KnownReplicaRoutes(router, 1)!.Nodes[0].RetireAsync();
        router.NearestLatency = new ReadLatencySampler<RespireConnection>((_, _) => ValueTask.FromResult(10L), () => 0);
        router.NearestLatency.ConnectionFailed(owner);
        primary.ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
            ? "-ERR old owner unavailable\r\n"u8.ToArray() : Reply(command, "primary");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var selected = await router.GetReadConnectionAsync(1, RespireReadFrom.Nearest, timeout.Token);
        await Assert.That(selected.Port).IsEqualTo(replacement.Port);
        await Assert.That(otherMaster.ReceivedCommands).Contains("CLUSTER SLOTS");
    }

    [Test]
    public async Task ClusterPublicationBeforeQueueingAnyCandidateRetriesNewOwner()
    {
        await using var primary = Server("primary");
        await using var replacement = Server("primary");
        primary.ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
            ? SlotReply(primary.Port, 0, 16383) : Reply(command, "primary");
        await using var client = await RespireClient.ConnectAsync(Options(primary) with
        {
            UseCluster = true, ClusterTopologyRefreshInterval = null,
        });
        var router = client.Core.Cluster!;
        var replacementNode = router.GetOrCreateNode(new("127.0.0.1", replacement.Port));
        // Any advisory discovery after the redirect must describe the replacement too;
        // this test isolates publication during selection, not conflicting server views.
        foreach (var server in new[] { primary, replacement })
            server.ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
                ? SlotReply(replacement.Port, 0, 16383) : Reply(command, "primary");
        Task? retirement = null;
        router.NearestLatency = new ReadLatencySampler<RespireConnection>((connection, _) =>
        {
            if (connection.Port == primary.Port)
            {
                router.SetSlotOwner(1, replacementNode);
                retirement = connection.RetireAsync();
            }
            return ValueTask.FromResult(10L);
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var selected = await router.GetReadConnectionAsync(1, RespireReadFrom.Nearest, timeout.Token);
            await Assert.That(selected.Port).IsEqualTo(replacement.Port);
        }
        finally { if (retirement is not null) await retirement.WaitAsync(timeout.Token); }
    }

    private static ClusterReplicaSet? KnownReplicaRoutes(ClusterRouter router, int slot)
        => (ClusterReplicaSet?)typeof(ClusterRouter).GetMethod("GetKnownReplicas",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(router, [slot]);

    [Test]
    public async Task ClusterOwnerChangedDuringConnectionRetriesBeforeStalledReplicaDiscovery()
    {
        await using var seed = Server("primary");
        await using var old = Server("primary");
        await using var replacement = Server("primary");
        seed.ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
            ? SlotReply(seed.Port, 0, 16383) : Reply(command, "primary");
        Action? connectingOld = null;
        await using var client = await RespireClient.ConnectAsync(Options(seed) with
        {
            UseCluster = true,
            ClusterTopologyRefreshInterval = null,
            TestingStreamFactory = async (host, port, token) =>
            {
                if (port == old.Port) connectingOld?.Invoke();
                var tcp = new System.Net.Sockets.TcpClient();
                await tcp.ConnectAsync(host, port, token);
                return tcp.GetStream();
            },
        });
        var router = client.Core.Cluster!;
        var oldNode = router.GetOrCreateNode(new("127.0.0.1", old.Port));
        var replacementNode = router.GetOrCreateNode(new("127.0.0.1", replacement.Port));
        await replacementNode.EnsureConnectedAsync(CancellationToken.None);
        router.SetSlotOwner(1, oldNode);
        connectingOld = () => router.SetSlotOwner(1, replacementNode);
        foreach (var server in new[] { seed, old, replacement })
            server.SuppressReply = command => command == "CLUSTER SLOTS";
        router.NearestLatency = new ReadLatencySampler<RespireConnection>((_, _) => ValueTask.FromResult(10L));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var selected = await router.GetReadConnectionAsync(1, RespireReadFrom.Nearest, timeout.Token);
        await Assert.That(selected.Port).IsEqualTo(replacement.Port);
    }

    [Test]
    public async Task CooldownRetryRetainsTheOriginalConnectionFailure()
    {
        var refused = new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused);
        var attempts = 0;
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1,
            Endpoints = [new("unavailable.test", 6379)],
            ReplicaEndpoints = [new("unavailable.test", 6379)],
            // A released ephemeral port can be reused by another parallel test. Inject the
            // transport failure directly so this test never connects to an unrelated server.
            TestingStreamFactory = (_, _, token) =>
            {
                token.ThrowIfCancellationRequested();
                Interlocked.Increment(ref attempts);
                return ValueTask.FromException<Stream>(refused);
            },
        });
        client.Core.ReadRouter.NearestLatency = new ReadLatencySampler<RespireConnection>(
            (_, _) => ValueTask.FromResult(1L), () => 0L);
        client.Core.ReadRouter.FailedReplicaCooldown = TimeSpan.FromMinutes(1);
        var failure = await Assert.That(async () =>
            await client.WithReadFrom(RespireReadFrom.Nearest).GetStringAsync("key"))
            .Throws<RespireConnectionException>();
        await Assert.That(failure!.InnerException).IsSameReferenceAs(refused);
        await Assert.That(attempts).IsEqualTo(2);
    }

    [Test]
    public async Task TemporaryTopologyHandshakeFailureReachesRefreshWarning()
    {
        await using var primary = Server("primary");
        var rejectAuthentication = false;
        primary.ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
            ? SlotReply(primary.Port, 0, 16383)
            : rejectAuthentication && command.StartsWith("AUTH ")
                ? "-ERR injected topology handshake failure\r\n"u8.ToArray() : Reply(command, "primary");
        var logger = new RefreshWarningLogger();
        await using var client = await RespireClient.ConnectAsync(Options(primary) with
        {
            UseCluster = true, ClusterTopologyRefreshInterval = null, Password = "test-password", LoggerFactory = logger,
        });
        var router = client.Core.Cluster!;
        router.SetSlotOwner(0, router.GetKnownSlotOwner(0)!);
        router.NearestLatency = new ReadLatencySampler<RespireConnection>((_, _) => ValueTask.FromResult(10L));
        rejectAuthentication = true;
        await router.GetReadConnectionAsync(0, RespireReadFrom.Nearest, CancellationToken.None);
        var failure = await logger.Failure.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.Message).Contains("injected topology handshake failure");
    }

    private sealed class RefreshWarningLogger : ILoggerFactory, ILogger
    {
        internal readonly TaskCompletionSource<Exception?> Failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ILogger CreateLogger(string categoryName) => this;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            if (level == LogLevel.Warning && formatter(state, error).StartsWith("Replica route refresh for Redis Cluster slot "))
                Failure.TrySetResult(error);
        }
    }

    private static byte[] SlotReply(int primaryPort, int start, int end, params int[] replicas)
    {
        var text = new System.Text.StringBuilder($"*1\r\n*{3 + replicas.Length}\r\n:{start}\r\n:{end}\r\n");
        foreach (var port in new[] { primaryPort }.Concat(replicas))
            text.Append($"*2\r\n$9\r\n127.0.0.1\r\n:{port}\r\n");
        return System.Text.Encoding.ASCII.GetBytes(text.ToString());
    }

    private static byte[] SentinelReplicaReply(int port)
        => System.Text.Encoding.ASCII.GetBytes($"*1\r\n*6\r\n$2\r\nip\r\n$9\r\n127.0.0.1\r\n$4\r\nport\r\n${port.ToString().Length}\r\n{port}\r\n$5\r\nflags\r\n$5\r\nslave\r\n");

    private static RespireOptions Options(FakeRespServer primary, params FakeRespServer[] replicas) => new()
    {
        Protocol = RespProtocol.Resp2, Connections = 1,
        Endpoints = [new("127.0.0.1", primary.Port)],
        ReplicaEndpoints = replicas.Select(replica => new RespireEndpoint("127.0.0.1", replica.Port)).ToArray(),
    };

    private static FakeRespServer Server(string value) => new(32, FakeRespServer.OkReply)
    {
        ReplyOverride = (_, command) => Reply(command, value),
    };

    private static byte[] Reply(string command, string value) => command switch
    {
        "HELLO 3" => "%1\r\n+proto\r\n:3\r\n"u8.ToArray(),
        "ROLE" => value == "primary" ? "*3\r\n$6\r\nmaster\r\n:0\r\n*0\r\n"u8.ToArray() : ReplicaRole,
        "PING" => "+PONG\r\n"u8.ToArray(),
        _ when command.StartsWith("GET ") => System.Text.Encoding.ASCII.GetBytes($"${value.Length}\r\n{value}\r\n"),
        _ => FakeRespServer.OkReply,
    };
}
