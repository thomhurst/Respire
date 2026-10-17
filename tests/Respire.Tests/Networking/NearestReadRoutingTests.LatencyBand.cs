using System.Text;
using Respire.Internal;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public partial class NearestReadRoutingTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LocalBandReplicaDoesNotAcquireUnavailablePrimary(bool cluster)
    {
        await using var seed = BandServer("primary", "remote");
        await using var primary = BandServer("primary", "local");
        await using var replica = BandServer("replica", "local");
        var original = seed.ReplyOverride!;
        seed.ReplyOverride = (id, command) => command == "CLUSTER SLOTS"
            ? SlotReply(primary.Port, 0, 16383, replica.Port) : original(id, command);
        var attempts = 0;
        await using var client = RespireClient.Create(Options(cluster ? seed : primary, replica) with
        {
            UseCluster = cluster, ReplicaEndpoints = cluster ? [] : [new("127.0.0.1", replica.Port)],
            ClusterTopologyRefreshInterval = null, ReadFrom = RespireReadFrom.AzAffinityReplicasAndPrimary,
            ClientAvailabilityZone = "local", ReplicaLatencyTolerance = TimeSpan.Zero,
            TestingStreamFactory = async (host, port, token) =>
            {
                if (port == primary.Port)
                {
                    Interlocked.Increment(ref attempts);
                    throw new RespireConnectionException("The primary is unavailable.");
                }
                var tcp = new System.Net.Sockets.TcpClient();
                await tcp.ConnectAsync(host, port, token);
                return tcp.GetStream();
            },
        });
        if (cluster)
            client.Core.Cluster!.NearestLatency = new((_, _) => ValueTask.FromResult(10L), () => 0);
        else
            client.Core.ReadRouter.NearestLatency = new((_, _) => ValueTask.FromResult(10L), () => 0);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.That(await client.GetStringAsync("key", deadline.Token)).IsEqualTo("replica");
        await Assert.That(attempts).IsEqualTo(0);
        await Assert.That(client.GetReadRoutingSnapshot()!.EligibleEndpoints.Single().Port).IsEqualTo(replica.Port);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LocalBandWaitsForUnknownReplicasBeforePrimary(bool cluster)
    {
        await using var primary = BandServer("primary", "local");
        await using var replica = BandServer("replica", "local");
        var original = primary.ReplyOverride!;
        primary.ReplyOverride = (id, command) => command == "CLUSTER SLOTS"
            ? SlotReply(primary.Port, 0, 16383, replica.Port) : original(id, command);
        await using var sentinel = new FakeRespServer(16)
        {
            ReplyOverride = (_, command) => command.StartsWith("SENTINEL GET-MASTER-ADDR-BY-NAME ")
                ? Encoding.ASCII.GetBytes($"*2\r\n+127.0.0.1\r\n+{primary.Port}\r\n")
                : command.StartsWith("SENTINEL REPLICAS ") ? SentinelReplicaReply(replica.Port) : "*0\r\n"u8.ToArray(),
        };
        await using var client = await RespireClient.ConnectAsync(Options(cluster ? primary : sentinel) with
        {
            UseCluster = cluster, SentinelPrimaryName = cluster ? null : "mymaster",
            ClusterTopologyRefreshInterval = null, ReadFrom = RespireReadFrom.AzAffinityReplicasAndPrimary,
            ClientAvailabilityZone = "local", ReplicaLatencyTolerance = TimeSpan.Zero,
        });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (cluster)
        {
            var router = client.Core.Cluster!;
            var slot = new RespireKey("key").ClusterSlot;
            router.SetSlotOwner(slot, router.GetKnownSlotOwner(slot)!);
            router.NearestLatency = new((_, _) => ValueTask.FromResult(10L), () => 0);
            primary.GateCommand("CLUSTER SLOTS", release.Task, () => entered.TrySetResult());
        }
        else
        {
            client.Core.ReadRouter.NearestLatency = new((_, _) => ValueTask.FromResult(10L), () => 0);
            sentinel.GateCommand("SENTINEL REPLICAS ", release.Task, () => entered.TrySetResult());
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var read = client.GetStringAsync("key", deadline.Token).AsTask();
        try
        {
            await entered.Task.WaitAsync(deadline.Token);
            await Assert.That(read.IsCompleted).IsFalse();
        }
        finally { release.TrySetResult(); }
        await Assert.That(await read).IsEqualTo("replica");
        await Assert.That(primary.ReceivedCommands.Any(command => command.StartsWith("GET "))).IsFalse();
    }

    [Test]
    [Arguments(0, RespireReadFrom.Replica)]
    [Arguments(1, RespireReadFrom.Replica)]
    [Arguments(2, RespireReadFrom.Replica)]
    [Arguments(0, RespireReadFrom.ReplicaPreferred)]
    [Arguments(1, RespireReadFrom.ReplicaPreferred)]
    [Arguments(0, RespireReadFrom.Nearest)]
    [Arguments(1, RespireReadFrom.Nearest)]
    [Arguments(2, RespireReadFrom.Nearest)]
    [Arguments(0, RespireReadFrom.AzAffinity)]
    [Arguments(1, RespireReadFrom.AzAffinity)]
    [Arguments(2, RespireReadFrom.AzAffinity)]
    [Arguments(0, RespireReadFrom.AzAffinityReplicasAndPrimary)]
    [Arguments(1, RespireReadFrom.AzAffinityReplicasAndPrimary)]
    public async Task LatencyBandDistributesReadsAndReportsEligibleEndpoints(int topology, RespireReadFrom policy)
    {
        await using var primary = BandServer("primary", "remote");
        await using var fast = BandServer("fast", "local");
        await using var near = BandServer("near", "local");
        await using var slow = BandServer("slow", "local");
        await using var remote = BandServer("remote", "remote");
        var replicas = new[] { fast, near, slow, remote };
        var original = primary.ReplyOverride!;
        primary.ReplyOverride = (id, command) => command == "CLUSTER SLOTS"
            ? SlotReply(primary.Port, 0, 16383, replicas.Select(node => node.Port).ToArray()) : original(id, command);
        await using var sentinel = new FakeRespServer(16)
        {
            ReplyOverride = (_, command) => command.StartsWith("SENTINEL GET-MASTER-ADDR-BY-NAME ")
                ? Encoding.ASCII.GetBytes($"*2\r\n+127.0.0.1\r\n+{primary.Port}\r\n")
                : command.StartsWith("SENTINEL REPLICAS ")
                    ? Encoding.ASCII.GetBytes($"*{replicas.Length}\r\n" + string.Concat(replicas.Select(node =>
                        $"*6\r\n+ip\r\n+127.0.0.1\r\n+port\r\n+{node.Port}\r\n+flags\r\n+slave\r\n")))
                    : "*0\r\n"u8.ToArray(),
        };
        await using var client = await RespireClient.ConnectAsync(Options(topology == 2 ? sentinel : primary, replicas) with
        {
            UseCluster = topology == 1, SentinelPrimaryName = topology == 2 ? "mymaster" : null,
            ReplicaEndpoints = topology == 0 ? replicas.Select(node => new RespireEndpoint("127.0.0.1", node.Port)).ToArray() : [],
            ClusterTopologyRefreshInterval = null, ReplicaRefreshInterval = TimeSpan.FromMinutes(1),
            ReadFrom = policy, ClientAvailabilityZone = "local", ReplicaLatencyTolerance = TimeSpan.FromTicks(5),
        });
        var zoneAware = ReadFallbackPolicy.UsesAvailabilityZone(policy);
        var sampler = new ReadLatencySampler<RespireConnection>((connection, _) => ValueTask.FromResult(
            connection.Port == primary.Port ? 100L : connection.Port == fast.Port ? 10L
                : connection.Port == near.Port ? 15L : connection.Port == remote.Port ? zoneAware ? 1L : 100L : 30L), () => 0);
        if (topology == 1) client.Core.Cluster!.NearestLatency = sampler;
        else client.Core.ReadRouter.NearestLatency = sampler;
        if (topology == 2) await client.Core.ReadRouter.RefreshNowAsync(CancellationToken.None);
        await Assert.That(client.GetReadRoutingSnapshot()).IsNull();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var results = new HashSet<string?>();
        for (var index = 0; index < 20; index++)
            results.Add(await client.GetStringAsync("band-key", deadline.Token));
        await Assert.That(results.SetEquals(["fast", "near"])).IsTrue();
        var snapshot = client.GetReadRoutingSnapshot()!;
        await Assert.That(snapshot.ReadFrom).IsEqualTo(policy);
        await Assert.That(snapshot.ClusterSlot).IsEqualTo(topology == 1 ? (int?)new RespireKey("band-key").ClusterSlot : null);
        await Assert.That(snapshot.EligibleEndpoints.Select(endpoint => endpoint.Port).ToHashSet()
            .SetEquals([fast.Port, near.Port])).IsTrue();
        await client.SetAsync("band-key", "write", cancellationToken: deadline.Token);
        await Assert.That(primary.ReceivedCommands.Any(command => command.StartsWith("SET "))).IsTrue();
        foreach (var replica in replicas)
            await Assert.That(replica.ReceivedCommands.Any(command => command.StartsWith("SET "))).IsFalse();
        if (policy == RespireReadFrom.Replica)
        {
            var slot = new RespireKey("band-key").ClusterSlot;
            var selected = topology == 1
                ? await client.Core.Cluster!.GetReadConnectionAsync(slot, policy, deadline.Token, default)
                : (await client.Core.ReadRouter.SelectAsync(policy, deadline.Token, default)).Connection;
            var hedge = topology == 1
                ? await client.Core.Cluster!.GetHedgeConnectionAsync(slot, policy, selected, deadline.Token)
                : await client.Core.ReadRouter.GetHedgeConnectionAsync(policy, selected, deadline.Token);
            await Assert.That(hedge).IsNotNull();
            await Assert.That(hedge!.Port).IsEqualTo(selected.Port == fast.Port ? near.Port : fast.Port);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LatencyBandDoesNotOverrideLocalPrimaryPriority(bool cluster)
    {
        await using var primary = BandServer("primary", "local");
        await using var replica = BandServer("replica", "remote");
        var original = primary.ReplyOverride!;
        primary.ReplyOverride = (id, command) => command == "CLUSTER SLOTS"
            ? SlotReply(primary.Port, 0, 16383, replica.Port) : original(id, command);
        await using var client = await RespireClient.ConnectAsync(Options(primary, replica) with
        {
            UseCluster = cluster, ClusterTopologyRefreshInterval = null,
            ReplicaEndpoints = cluster ? [] : [new("127.0.0.1", replica.Port)],
            ReadFrom = RespireReadFrom.AzAffinityReplicasAndPrimary, ClientAvailabilityZone = "local",
            ReplicaLatencyTolerance = TimeSpan.MaxValue,
        });
        var sampler = new ReadLatencySampler<RespireConnection>((connection, _) =>
            ValueTask.FromResult(connection.Port == primary.Port ? 1_000L : 1L));
        if (cluster) client.Core.Cluster!.NearestLatency = sampler;
        else client.Core.ReadRouter.NearestLatency = sampler;
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("primary");
        await Assert.That(client.GetReadRoutingSnapshot()!.EligibleEndpoints.Single().Port).IsEqualTo(primary.Port);
    }

    private static FakeRespServer BandServer(string value, string zone)
    {
        var server = Server(value);
        var original = server.ReplyOverride!;
        server.ReplyOverride = (id, command) => command == "INFO SERVER"
            ? Encoding.ASCII.GetBytes($"${"availability_zone:".Length + zone.Length + 2}\r\navailability_zone:{zone}\r\n\r\n")
            : original(id, command);
        return server;
    }

    [Test]
    public async Task StrictBandReadRejectsReplicaPromotedDuringSampling()
    {
        await using var primary = Server("primary");
        await using var promoted = Server("promoted");
        await using var remaining = Server("remaining");
        var changed = false;
        foreach (var server in new[] { primary, promoted, remaining })
        {
            var original = server.ReplyOverride!;
            server.ReplyOverride = (id, command) => command == "CLUSTER SLOTS"
                ? changed ? SlotReply(promoted.Port, 0, 16383, remaining.Port)
                    : SlotReply(primary.Port, 0, 16383, promoted.Port, remaining.Port)
                : original(id, command);
        }
        await using var client = await RespireClient.ConnectAsync(Options(primary) with
        {
            UseCluster = true, ClusterTopologyRefreshInterval = null, ReplicaLatencyTolerance = TimeSpan.FromTicks(5),
        });
        var router = client.Core.Cluster!;
        var promotedNode = KnownReplicaRoutes(router, 1)!.Nodes.Single(node => node.ActiveConnectionEndpoint.Port == promoted.Port);
        router.NearestLatency = new ReadLatencySampler<RespireConnection>((connection, _) =>
        {
            if (connection.Port == promoted.Port && !changed)
            {
                changed = true;
                router.SetSlotOwner(1, promotedNode);
            }
            return ValueTask.FromResult(connection.Port == promoted.Port ? 10L : 100L);
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var selected = await router.GetReadConnectionAsync(1, RespireReadFrom.Replica, deadline.Token, default);
        await Assert.That(selected.Port).IsEqualTo(remaining.Port);
    }
}
