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
    public async Task ClusterNearestChoosesLowestLatencyEligibleRole()
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
        await using var nearest = client.WithReadFrom(RespireReadFrom.Nearest);
        await Assert.That(await nearest.GetStringAsync("key")).IsEqualTo("replica");
        await Assert.That(await nearest.SetAsync("key", "write")).IsTrue();
        await Assert.That(primary.ReceivedCommands).Contains("SET key write");
        await Assert.That(replica.ReceivedCommands).Contains("READONLY");
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
    public async Task EqualLatenciesRotateBetweenEligibleRoles()
    {
        await using var primary = Server("primary");
        await using var replica = Server("replica");
        await using var client = RespireClient.Create(Options(primary, replica));
        client.Core.ReadRouter.NearestLatency = new ReadLatencySampler<RespireConnection>((_, _) => ValueTask.FromResult(10L));
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
