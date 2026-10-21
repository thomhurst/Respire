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
    [Arguments(RespireReadFrom.PrimaryPreferred, "typed")]
    [Arguments(RespireReadFrom.PrimaryPreferred, "streaming")]
    [Arguments(RespireReadFrom.PrimaryPreferred, "blocking")]
    [Arguments(RespireReadFrom.AzAffinityReplicasAndPrimary, "typed")]
    [Arguments(RespireReadFrom.AzAffinityReplicasAndPrimary, "streaming")]
    [Arguments(RespireReadFrom.AzAffinityReplicasAndPrimary, "blocking")]
    public async Task ClusterBandRoleFallbackThenMovedPreservesPolicy(RespireReadFrom policy, string path)
    {
        await using var primary = BandServer("primary", "local");
        await using var replica = BandServer("replica", "remote");
        var primaryReply = primary.ReplyOverride!;
        primary.ReplyOverride = (id, command) => command == "CLUSTER SLOTS"
            ? SlotReply(primary.Port, 0, 16383, replica.Port)
            : command.StartsWith("GET ") || command.StartsWith("XREAD ")
                ? "-LOADING unavailable\r\n"u8.ToArray() : primaryReply(id, command);
        var replicaReply = replica.ReplyOverride!;
        var replicaReads = 0;
        var slot = ClusterHash.GetSlot("key");
        replica.ReplyOverride = (id, command) => command.StartsWith("GET ") || command.StartsWith("XREAD ")
            ? Interlocked.Increment(ref replicaReads) == 1
                ? Encoding.ASCII.GetBytes($"-MOVED {slot} 127.0.0.1:{primary.Port}\r\n")
                : "$7\r\nreplica\r\n"u8.ToArray()
            : replicaReply(id, command);
#pragma warning disable RESP010 // This latency-band scenario owns and disposes its client to isolate measured candidates and routing policy.
        await using var client = await RespireClient.ConnectAsync(Options(primary, replica) with
        {
            UseCluster = true, ReplicaEndpoints = [], ClusterTopologyRefreshInterval = null,
            ReadFrom = policy, ClientAvailabilityZone = "local", ReplicaLatencyTolerance = TimeSpan.Zero,
        });
#pragma warning restore RESP010
        client.Core.Cluster!.NearestLatency = new((_, _) => ValueTask.FromResult(10L), () => 0);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string? value;
        if (path == "blocking")
        {
            using var response = await client.ExecuteAsync(RespireCommands.Stream.XREAD,
                deadline.Token, ["BLOCK", 1, "STREAMS", "key", "0"]);
            value = response.AsString();
        }
        else if (path == "streaming")
        {
            await using var stream = await client.Strings.GetStreamAsync("key", deadline.Token);
            using var reader = new StreamReader(stream!);
            value = await reader.ReadToEndAsync(deadline.Token);
        }
        else value = await client.GetStringAsync("key", deadline.Token);
        await Assert.That(value).IsEqualTo("replica");
        await Assert.That(replicaReads).IsEqualTo(2);
        await Assert.That(client.GetReadRoutingSnapshot()!.ReadFrom).IsEqualTo(policy);
        await Assert.That(client.GetReadRoutingSnapshot()!.EligibleEndpoints.Single().Port).IsEqualTo(replica.Port);
    }

    [Test]
    [Arguments(RespireReadFrom.PrimaryPreferred)]
    [Arguments(RespireReadFrom.AzAffinityReplicasAndPrimary)]
    public async Task ClusterBandDedicatedRetirementPreservesOriginalPolicy(RespireReadFrom policy)
    {
        await using var primary = BandServer("primary", "local");
        await using var replica = BandServer("replica", "local");
        await using var replacement = BandServer("replica", "local");
        var original = primary.ReplyOverride!;
        primary.ReplyOverride = (id, command) => command == "CLUSTER SLOTS"
            ? SlotReply(primary.Port, 0, 16383, replica.Port, replacement.Port) : original(id, command);
#pragma warning disable RESP010 // This latency-band scenario owns and disposes its client to isolate measured candidates and routing policy.
        await using var client = await RespireClient.ConnectAsync(Options(primary, replica) with
        {
            UseCluster = true, ReplicaEndpoints = [], ClusterTopologyRefreshInterval = null,
            ReadFrom = policy, ClientAvailabilityZone = "local", ReplicaLatencyTolerance = TimeSpan.Zero,
        });
#pragma warning restore RESP010
        var router = client.Core.Cluster!;
        router.NearestLatency = new((connection, _) => ValueTask.FromResult(connection.Port == replica.Port ? 10L : 100L), () => 0);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var fallback = new ReadFallbackPolicy.RoleFallback(policy);
        await Assert.That(fallback.TrySwitch(new RespireServerException("LOADING unavailable"), onReplica: false)).IsTrue();
        var pool = await router.GetOtherRoleDedicatedPoolAsync(1, fallback, deadline.Token, null, default);
        await pool.RetireAsync();
        // Reselection now has a different eligible pool; returning the same stopped pool
        // correctly fails rather than looping, so make the replacement the fastest band.
        router.NearestLatency = new((connection, _) => ValueTask.FromResult(connection.Port == replacement.Port ? 10L : 100L), () => 0);
        var zone = ReadFallbackPolicy.UsesAvailabilityZone(policy) ? "local" : null;
        var lease = await router.RentDedicatedConnectionAsync(pool,
            new ClusterRouter.DedicatedRoute(1, fallback.RecoveryPolicy, SnapshotReadFrom: fallback.Policy),
            deadline.Token, null, default, preferredZone: zone);
        try
        {
            await Assert.That(lease.Connection.Port).IsEqualTo(replacement.Port);
            await Assert.That(lease.Pool.IsReadOnly).IsTrue();
            await Assert.That(client.GetReadRoutingSnapshot()!.ReadFrom).IsEqualTo(policy);
            await Assert.That(client.GetReadRoutingSnapshot()!.EligibleEndpoints.Single().Port).IsEqualTo(replacement.Port);
        }
        finally { lease.Pool.Return(lease.Connection); }
    }

    [Test]
    [Arguments(RespireReadFrom.PrimaryPreferred, false)]
    [Arguments(RespireReadFrom.PrimaryPreferred, true)]
    [Arguments(RespireReadFrom.AzAffinityReplicasAndPrimary, false)]
    [Arguments(RespireReadFrom.AzAffinityReplicasAndPrimary, true)]
    public async Task ClusterBandRetryPreservesOriginalPolicy(RespireReadFrom policy, bool redirect)
    {
        await using var primary = Server("primary");
        await using var replica = Server("replica");
        var original = primary.ReplyOverride!;
        primary.ReplyOverride = (id, command) => command == "CLUSTER SLOTS"
            ? SlotReply(primary.Port, 0, 16383, replica.Port) : original(id, command);
#pragma warning disable RESP010 // This latency-band scenario owns and disposes its client to isolate measured candidates and routing policy.
        await using var client = await RespireClient.ConnectAsync(Options(primary, replica) with
        {
            UseCluster = true, ReplicaEndpoints = [], ClusterTopologyRefreshInterval = null,
            ReadFrom = policy, ClientAvailabilityZone = "local", ReplicaLatencyTolerance = TimeSpan.Zero,
        });
#pragma warning restore RESP010
        var router = client.Core.Cluster!;
        router.NearestLatency = new((_, _) => ValueTask.FromResult(10L), () => 0);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var fallback = new ReadFallbackPolicy.RoleFallback(policy);
        await Assert.That(fallback.TrySwitch(new RespireServerException("LOADING unavailable"), onReplica: false)).IsTrue();
        await router.GetOtherRoleReadConnectionAsync(1, fallback, deadline.Token, null, default);
        var zone = ReadFallbackPolicy.UsesAvailabilityZone(policy) ? "local" : null;
        var selected = redirect
            ? await router.SelectReadConnectionAfterRedirectAsync(
                await router.GetConnectionAsync(1, deadline.Token, null, default), 1,
                fallback.RecoveryPolicy, deadline.Token, null, default, zone, fallback.Policy)
            : await router.GetReadReplacementConnectionAsync(1, fallback.RecoveryPolicy,
                deadline.Token, null, default, zone, fallback.Policy);
        await Assert.That(selected.Port).IsEqualTo(replica.Port);
        await Assert.That(client.GetReadRoutingSnapshot()!.ReadFrom).IsEqualTo(policy);
        await Assert.That(client.GetReadRoutingSnapshot()!.EligibleEndpoints.Single().Port).IsEqualTo(replica.Port);
    }

    [Test]
    [Arguments(RespireReadFrom.Replica, false)]
    [Arguments(RespireReadFrom.Replica, true)]
    [Arguments(RespireReadFrom.Nearest, false)]
    [Arguments(RespireReadFrom.Nearest, true)]
    [Arguments(RespireReadFrom.AzAffinity, false)]
    [Arguments(RespireReadFrom.AzAffinity, true)]
    [Arguments(RespireReadFrom.AzAffinityReplicasAndPrimary, false)]
    [Arguments(RespireReadFrom.AzAffinityReplicasAndPrimary, true)]
    public async Task BandRejectsReplicationLinkChangesAfterSampling(RespireReadFrom policy, bool changeSelected)
    {
        await using var primary = BandServer("primary", "remote");
        await using var first = BandServer("replica", "local");
        await using var second = BandServer("replica", "local");
        await using var third = BandServer("replica", "local");
        var replicas = new[] { first, second, third }.OrderBy(server => server.Port).ToArray();
        var changedReplica = replicas[changeSelected ? 1 : 0];
        var remaining = replicas[changeSelected ? 0 : 1];
        var trigger = replicas[2];
#pragma warning disable RESP010 // This latency-band scenario owns and disposes its client to isolate measured candidates and routing policy.
        await using var client = await RespireClient.ConnectAsync(Options(primary, replicas) with
        {
            ReplicaLatencyTolerance = TimeSpan.Zero, ClientAvailabilityZone = "local",
        });
#pragma warning restore RESP010
        var router = client.Core.ReadRouter;
        var changed = false;
        var sampled = new List<int>();
        router.NearestLatency = new ReadLatencySampler<RespireConnection>(async (connection, token) =>
        {
            sampled.Add(connection.Port);
            if (connection.Port != trigger.Port) return connection.Port == primary.Port ? 1_000L : 10L;
            // Both fast replicas already supplied samples. Revalidate one through ROLE while
            // this later probe is pending, as a concurrent reader can do on the same entry.
            await Task.Yield();
            var original = changedReplica.ReplyOverride!;
            changedReplica.ReplyOverride = (id, command) => command == "ROLE"
                ? "*5\r\n$5\r\nslave\r\n$9\r\n127.0.0.1\r\n:6379\r\n$7\r\nconnect\r\n:0\r\n"u8.ToArray()
                : original(id, command);
            var entries = (System.Collections.Concurrent.ConcurrentDictionary<RespireEndpoint, ReadEndpointRouter.Entry>)
                typeof(ReadEndpointRouter).GetField("_entries",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(router)!;
            var entry = entries[new("127.0.0.1", changedReplica.Port)];
            var interval = router.RoleRevalidationInterval;
            router.RoleRevalidationInterval = TimeSpan.Zero;
            try { await entry.GetConnectionAsync(token); }
            finally { router.RoleRevalidationInterval = interval; }
            changed = entry.IsReplicationLinkDown;
            return 100L;
        }, () => 0);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var selected = await router.SelectAsync(policy, deadline.Token, default);
        await Assert.That(changed).IsTrue();
        await Assert.That(sampled.Where(port => port != primary.Port).Take(3)
            .SequenceEqual(replicas.Select(server => server.Port))).IsTrue();
        await Assert.That(selected.Connection.Port).IsEqualTo(remaining.Port);
        var snapshot = client.GetReadRoutingSnapshot()!;
        await Assert.That(snapshot.ReadFrom).IsEqualTo(policy);
        await Assert.That(snapshot.EligibleEndpoints.Single().Port).IsEqualTo(remaining.Port);
    }

    [Test]
    [Arguments(false, RespireReadFrom.PrimaryPreferred)]
    [Arguments(true, RespireReadFrom.PrimaryPreferred)]
    [Arguments(false, RespireReadFrom.AzAffinityReplicasAndPrimary)]
    [Arguments(true, RespireReadFrom.AzAffinityReplicasAndPrimary)]
    public async Task ServerRoleFallbackBandPreservesPolicy(bool cluster, RespireReadFrom policy)
    {
        await using var primary = Server("primary");
        await using var replica = Server("replica");
        var original = primary.ReplyOverride!;
        primary.ReplyOverride = (id, command) => command == "CLUSTER SLOTS"
            ? SlotReply(primary.Port, 0, 16383, replica.Port) : original(id, command);
#pragma warning disable RESP010 // This latency-band scenario owns and disposes its client to isolate measured candidates and routing policy.
        await using var client = await RespireClient.ConnectAsync(Options(primary, replica) with
        {
            UseCluster = cluster, ReplicaEndpoints = cluster ? [] : [new("127.0.0.1", replica.Port)],
            ClusterTopologyRefreshInterval = null, ReadFrom = policy,
            ClientAvailabilityZone = "local", ReplicaLatencyTolerance = TimeSpan.Zero,
        });
#pragma warning restore RESP010
        var primarySamples = 0;
        var sampler = new ReadLatencySampler<RespireConnection>((connection, _) =>
        {
            if (connection.Port == primary.Port) Interlocked.Increment(ref primarySamples);
            return ValueTask.FromResult(10L);
        }, () => 0);
        if (cluster) client.Core.Cluster!.NearestLatency = sampler;
        else client.Core.ReadRouter.NearestLatency = sampler;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        foreach (var code in new[] { "LOADING", "MASTERDOWN", "CLUSTERDOWN" })
        {
            var fallback = new ReadFallbackPolicy.RoleFallback(policy);
            await Assert.That(fallback.TrySwitch(new RespireServerException(code + " unavailable"), onReplica: false)).IsTrue();
            if (cluster)
            {
                var selected = await client.Core.Cluster!.GetOtherRoleReadConnectionAsync(1, fallback,
                    deadline.Token, discovery: null, observation: default);
                await Assert.That(selected.Port).IsEqualTo(replica.Port);
            }
            else
            {
                var lease = await client.Core.ReadRouter.RentDedicatedConnectionAsync(policy,
                    deadline.Token, observation: default, preferredZone: "local", replicaOnly: fallback.ReplicaOnly);
                try { await Assert.That(lease.Connection.Port).IsEqualTo(replica.Port); }
                finally { lease.Pool.Return(lease.Connection); }
            }
            var snapshot = client.GetReadRoutingSnapshot()!;
            await Assert.That(snapshot.ReadFrom).IsEqualTo(policy);
            await Assert.That(snapshot.EligibleEndpoints.Single().Port).IsEqualTo(replica.Port);
            await Assert.That(snapshot.ClusterSlot).IsEqualTo(cluster ? (int?)1 : null);
        }
        await Assert.That(primarySamples).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PrimaryPreferredBandFallbackPreservesPolicy(bool cluster)
    {
        await using var seed = Server("primary");
        await using var primary = Server("primary");
        await using var replica = Server("replica");
        var original = seed.ReplyOverride!;
        seed.ReplyOverride = (id, command) => command == "CLUSTER SLOTS"
            ? SlotReply(primary.Port, 0, 16383, replica.Port) : original(id, command);
        var attempts = 0;
#pragma warning disable RESP010 // This latency-band scenario owns and disposes its client to isolate measured candidates and routing policy.
        await using var client = RespireClient.Create(Options(cluster ? seed : primary, replica) with
        {
            UseCluster = cluster, ReplicaEndpoints = cluster ? [] : [new("127.0.0.1", replica.Port)],
            ClusterTopologyRefreshInterval = null, ReadFrom = RespireReadFrom.PrimaryPreferred,
            ReplicaLatencyTolerance = TimeSpan.Zero,
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
#pragma warning restore RESP010
        if (cluster)
            client.Core.Cluster!.NearestLatency = new((_, _) => ValueTask.FromResult(10L), () => 0);
        else
            client.Core.ReadRouter.NearestLatency = new((_, _) => ValueTask.FromResult(10L), () => 0);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.That(await client.GetStringAsync("key", deadline.Token)).IsEqualTo("replica");
        await Assert.That(attempts).IsGreaterThan(0);
        var snapshot = client.GetReadRoutingSnapshot()!;
        await Assert.That(snapshot.ReadFrom).IsEqualTo(RespireReadFrom.PrimaryPreferred);
        await Assert.That(snapshot.EligibleEndpoints.Single().Port).IsEqualTo(replica.Port);
        await Assert.That(snapshot.ClusterSlot).IsEqualTo(cluster ? (int?)new RespireKey("key").ClusterSlot : null);

        if (!cluster)
        {
            // A ready-only fallback cannot acquire the lazy primary, but can reuse the ready replica.
            var selected = await client.Core.ReadRouter.SelectAsync(RespireReadFrom.PrimaryPreferred,
                deadline.Token, default, readyOnly: true);
            await Assert.That(selected.Connection.Port).IsEqualTo(replica.Port);
            await Assert.That(client.GetReadRoutingSnapshot()!.ReadFrom).IsEqualTo(RespireReadFrom.PrimaryPreferred);
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task BandSnapshotRejectsTopologyChangesAfterSampling(bool cluster, bool promote)
    {
        await using var primary = Server("primary");
        await using var first = Server("replica");
        await using var second = Server("replica");
        await using var third = Server("replica");
        var replicas = new[] { first, second, third }.OrderBy(server => server.Port).ToArray();
        var removed = replicas[0];
        var remaining = replicas[1];
        var trigger = replicas[2];
        var original = primary.ReplyOverride!;
        primary.ReplyOverride = (id, command) => command == "CLUSTER SLOTS"
            ? SlotReply(primary.Port, 0, 16383, removed.Port, remaining.Port, trigger.Port) : original(id, command);
#pragma warning disable RESP010 // This latency-band scenario owns and disposes its client to isolate measured candidates and routing policy.
        await using var client = await RespireClient.ConnectAsync(Options(primary, removed, remaining, trigger) with
        {
            UseCluster = cluster, ClusterTopologyRefreshInterval = null, ReplicaLatencyTolerance = TimeSpan.Zero,
            ReplicaEndpoints = cluster ? [] : replicas.Select(server => new RespireEndpoint("127.0.0.1", server.Port)).ToArray(),
        });
#pragma warning restore RESP010
        var changed = false;
        var sampled = new List<int>();
        var sampler = new ReadLatencySampler<RespireConnection>(async (connection, _) =>
        {
            sampled.Add(connection.Port);
            if (connection.Port != trigger.Port) return 10L;
            // The first two replicas have already supplied usable samples. Change topology
            // across an asynchronous probe without invalidating the remaining winner.
            await Task.Yield();
            changed = true;
            if (cluster)
            {
                var router = client.Core.Cluster!;
                router.ApplyTopology([new(0, 16383, new("127.0.0.1", promote ? removed.Port : primary.Port), null, [])
                {
                    Replicas = [new(new("127.0.0.1", remaining.Port), null, []), new(new("127.0.0.1", trigger.Port), null, [])],
                }], router.TopologyVersion, long.MaxValue);
            }
            else
            {
                typeof(ReadEndpointRouter).GetMethod("SetEndpoints",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(client.Core.ReadRouter, [new RespireEndpoint[] { new("127.0.0.1", remaining.Port), new("127.0.0.1", trigger.Port) }]);
            }
            return 100L;
        }, () => 0);
        if (cluster) client.Core.Cluster!.NearestLatency = sampler;
        else client.Core.ReadRouter.NearestLatency = sampler;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var selected = cluster
            ? await client.Core.Cluster!.GetReadConnectionAsync(1, RespireReadFrom.Replica, deadline.Token, default)
            : (await client.Core.ReadRouter.SelectAsync(RespireReadFrom.Replica, deadline.Token, default)).Connection;
        await Assert.That(changed).IsTrue();
        await Assert.That(sampled.Take(3).SequenceEqual([removed.Port, remaining.Port, trigger.Port])).IsTrue();
        await Assert.That(selected.Port).IsEqualTo(remaining.Port);
        await Assert.That(client.GetReadRoutingSnapshot()!.EligibleEndpoints.Single().Port).IsEqualTo(remaining.Port);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RemoteBandReplicaDoesNotSampleKnownRemotePrimary(bool cluster)
    {
        await using var primary = BandServer("primary", "remote");
        await using var replica = BandServer("replica", "remote");
        var original = primary.ReplyOverride!;
        primary.ReplyOverride = (id, command) => command == "CLUSTER SLOTS"
            ? SlotReply(primary.Port, 0, 16383, replica.Port) : original(id, command);
#pragma warning disable RESP010 // This latency-band scenario owns and disposes its client to isolate measured candidates and routing policy.
        await using var client = await RespireClient.ConnectAsync(Options(primary, replica) with
        {
            UseCluster = cluster, ClusterTopologyRefreshInterval = null,
            ReplicaEndpoints = cluster ? [] : [new("127.0.0.1", replica.Port)],
            ReadFrom = RespireReadFrom.AzAffinityReplicasAndPrimary, ClientAvailabilityZone = "local",
            ReplicaLatencyTolerance = TimeSpan.Zero,
        });
#pragma warning restore RESP010
        var primarySamples = 0;
        var sampler = new ReadLatencySampler<RespireConnection>((connection, _) =>
        {
            if (connection.Port == primary.Port) Interlocked.Increment(ref primarySamples);
            return ValueTask.FromResult(10L);
        }, () => 0);
        if (cluster) client.Core.Cluster!.NearestLatency = sampler;
        else client.Core.ReadRouter.NearestLatency = sampler;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.That(await client.GetStringAsync("key", deadline.Token)).IsEqualTo("replica");
        await Assert.That(primarySamples).IsEqualTo(0);
        await Assert.That(client.GetReadRoutingSnapshot()!.EligibleEndpoints.Single().Port).IsEqualTo(replica.Port);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RemoteBandPrimaryStillServesWhenReplicaUnavailable(bool cluster)
    {
        await using var primary = BandServer("primary", "remote");
        await using var replica = BandServer("replica", "remote");
        var original = primary.ReplyOverride!;
        primary.ReplyOverride = (id, command) => command == "CLUSTER SLOTS"
            ? SlotReply(primary.Port, 0, 16383, replica.Port) : original(id, command);
#pragma warning disable RESP010 // This latency-band scenario owns and disposes its client to isolate measured candidates and routing policy.
        await using var client = await RespireClient.ConnectAsync(Options(primary, replica) with
        {
            UseCluster = cluster, ClusterTopologyRefreshInterval = null,
            ReplicaEndpoints = cluster ? [] : [new("127.0.0.1", replica.Port)],
            ReadFrom = RespireReadFrom.AzAffinityReplicasAndPrimary, ClientAvailabilityZone = "local",
            ReplicaLatencyTolerance = TimeSpan.Zero,
            TestingStreamFactory = async (host, port, token) =>
            {
                if (port == replica.Port) throw new RespireConnectionException("The replica is unavailable.");
                var tcp = new System.Net.Sockets.TcpClient();
                await tcp.ConnectAsync(host, port, token);
                return tcp.GetStream();
            },
        });
#pragma warning restore RESP010
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.That(await client.GetStringAsync("key", deadline.Token)).IsEqualTo("primary");
    }

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
#pragma warning disable RESP010 // This latency-band scenario owns and disposes its client to isolate measured candidates and routing policy.
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
#pragma warning restore RESP010
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
#pragma warning disable RESP010 // This latency-band scenario owns and disposes its client to isolate measured candidates and routing policy.
        await using var client = await RespireClient.ConnectAsync(Options(cluster ? primary : sentinel) with
        {
            UseCluster = cluster, SentinelPrimaryName = cluster ? null : "mymaster",
            ClusterTopologyRefreshInterval = null, ReadFrom = RespireReadFrom.AzAffinityReplicasAndPrimary,
            ClientAvailabilityZone = "local", ReplicaLatencyTolerance = TimeSpan.Zero,
        });
#pragma warning restore RESP010
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
#pragma warning disable RESP010 // This latency-band scenario owns and disposes its client to isolate measured candidates and routing policy.
        await using var client = await RespireClient.ConnectAsync(Options(topology == 2 ? sentinel : primary, replicas) with
        {
            UseCluster = topology == 1, SentinelPrimaryName = topology == 2 ? "mymaster" : null,
            ReplicaEndpoints = topology == 0 ? replicas.Select(node => new RespireEndpoint("127.0.0.1", node.Port)).ToArray() : [],
            ClusterTopologyRefreshInterval = null, ReplicaRefreshInterval = TimeSpan.FromMinutes(1),
            ReadFrom = policy, ClientAvailabilityZone = "local", ReplicaLatencyTolerance = TimeSpan.FromTicks(5),
        });
#pragma warning restore RESP010
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
#pragma warning disable RESP010 // This latency-band scenario owns and disposes its client to isolate measured candidates and routing policy.
        await using var client = await RespireClient.ConnectAsync(Options(primary, replica) with
        {
            UseCluster = cluster, ClusterTopologyRefreshInterval = null,
            ReplicaEndpoints = cluster ? [] : [new("127.0.0.1", replica.Port)],
            ReadFrom = RespireReadFrom.AzAffinityReplicasAndPrimary, ClientAvailabilityZone = "local",
            ReplicaLatencyTolerance = TimeSpan.MaxValue,
        });
#pragma warning restore RESP010
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
#pragma warning disable RESP010 // This latency-band scenario owns and disposes its client to isolate measured candidates and routing policy.
        await using var client = await RespireClient.ConnectAsync(Options(primary) with
        {
            UseCluster = true, ClusterTopologyRefreshInterval = null, ReplicaLatencyTolerance = TimeSpan.FromTicks(5),
        });
#pragma warning restore RESP010
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

    [Test]
    public async Task AzBandRejectsReplicaPromotedByMovedDuringSampling()
    {
        await using var primary = BandServer("primary", "local");
        await using var promoted = BandServer("promoted", "local");
        await using var remaining = BandServer("remaining", "local");
        var changed = false;
        foreach (var server in new[] { primary, promoted, remaining })
        {
            var original = server.ReplyOverride!;
            server.ReplyOverride = (id, command) => command == "CLUSTER SLOTS"
                ? changed ? SlotReply(promoted.Port, 0, 16383, remaining.Port)
                    : SlotReply(primary.Port, 0, 16383, promoted.Port, remaining.Port)
                : original(id, command);
        }
#pragma warning disable RESP010 // This latency-band scenario owns and disposes its client to isolate measured candidates and routing policy.
        await using var client = await RespireClient.ConnectAsync(Options(primary) with
        {
            UseCluster = true, ClusterTopologyRefreshInterval = null, ClientAvailabilityZone = "local",
            ReplicaLatencyTolerance = TimeSpan.Zero,
        });
#pragma warning restore RESP010
        var router = client.Core.Cluster!;
        var promotedNode = KnownReplicaRoutes(router, 1)!.Nodes.Single(node => node.ActiveConnectionEndpoint.Port == promoted.Port);
        router.NearestLatency = new ReadLatencySampler<RespireConnection>(async (connection, _) =>
        {
            if (connection.Port == promoted.Port && !changed)
            {
                await Task.Yield();
                changed = true;
                // MOVED changes one slot while other slots retain this replica's transport.
                router.SetSlotOwner(1, promotedNode);
            }
            return connection.Port == promoted.Port ? 10L : 100L;
        }, () => 0);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var selected = await router.GetReadConnectionAsync(1, RespireReadFrom.AzAffinityReplicasAndPrimary, deadline.Token, default);
        await Assert.That(changed).IsTrue();
        await Assert.That(selected.Port).IsEqualTo(remaining.Port);
        await Assert.That(client.GetReadRoutingSnapshot()!.EligibleEndpoints.Single().Port).IsEqualTo(remaining.Port);
    }
}
