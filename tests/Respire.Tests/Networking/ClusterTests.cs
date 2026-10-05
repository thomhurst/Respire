using System.Buffers;
using System.Runtime.CompilerServices;
using Respire.Internal;
using Respire.Commands;
using Respire.Infrastructure;
using Respire.Networking;
using Respire.Protocol;
using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClusterTests
{
    [Test]
    public async Task ReadFrom_ProbeCompletionWaitsForSuccessOrAllFailures()
    {
        var failed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var winner = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = ClusterRouter.FirstSuccessfulReplicaProbeAsync([failed.Task, winner.Task, pending.Task]);
        failed.SetResult(false);
        await Assert.That(result.IsCompleted).IsFalse();
        winner.SetResult(true);
        await Assert.That(await result.WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();
        await Assert.That(pending.Task.IsCompleted).IsFalse();
        pending.SetResult(false);
        await Assert.That(await ClusterRouter.FirstSuccessfulReplicaProbeAsync([Task.FromResult(false), Task.FromResult(false)])).IsFalse();
        await Assert.That(await ClusterRouter.FirstSuccessfulReplicaProbeAsync([])).IsFalse();
    }

    [Test]
    public async Task ReadFrom_ProbeCompletionSurfacesFault()
    {
        var error = new InvalidOperationException("probe failed");
        var result = ClusterRouter.FirstSuccessfulReplicaProbeAsync([Task.FromException<bool>(error)]);
        await Assert.That(async () => await result).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task ReadFrom_ReplicaTransportDoesNotCarryPrimaryCacheHooks()
    {
        await using var client = RespireClient.Create(new RespireOptions
        {
            UseCluster = true, Protocol = RespProtocol.Resp3, ClientSideCache = new(),
            Username = "reader", Password = "password",
            Endpoints = [new("localhost", 16379)],
        });
        var router = client.Core.Cluster!;
        router.ApplyTopology([new ClusterTopologyRange(0, 16383, new("localhost", 16379), "primary", [])
        {
            Replicas = [new(new("localhost", 16380), "replica", [])],
        }], 0, 1);
        var primary = router.GetKnownSlotOwner(0)!.Options;
        var replica = ReplicaRoutes(client)[0]!.Nodes[0].Options;
        await Assert.That(primary.EnableClientTracking).IsTrue();
        await Assert.That(primary.PushHandler).IsNotNull();
        await Assert.That(primary.CredentialCacheInvalidation is not null).IsTrue();
        await Assert.That(replica.EnableClientTracking).IsFalse();
        await Assert.That(replica.PushHandler).IsNull();
        await Assert.That(replica.CredentialCacheInvalidation is null).IsTrue();
        await Assert.That(replica.CredentialCacheRetirementFence is null).IsTrue();
        await Assert.That(replica.ReadOnly).IsTrue();
        await Assert.That(replica.Username).IsEqualTo("reader");
        await Assert.That(replica.Password).IsEqualTo("password");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReadFrom_EmptyRefreshPreservesUnpromotedSiblingReplicas(bool promote)
    {
        byte[]? topology = null;
        await using var replica = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? topology
                : command.StartsWith("GET ") ? "$5\r\nvalue\r\n"u8.ToArray() : null,
        };
        await using var primary = new FakeRespServer(8, FakeRespServer.OkReply);
        await using var retainedReplica = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            ReplyOverride = replica.ReplyOverride,
        };
        var initial = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(ClusterTopology(primary.Port, replica.Port))
            .Replace("*1\r\n*4", "*1\r\n*5")
            + $"*3\r\n$9\r\n127.0.0.1\r\n:{retainedReplica.Port}\r\n$8\r\nretained\r\n");
        primary.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? initial : null;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            ConnectTimeout = TimeSpan.FromMilliseconds(250), CommandTimeout = TimeSpan.FromMilliseconds(250),
            Endpoints = [new("127.0.0.1", primary.Port)],
        });
        var reads = client.WithReadFrom(RespireReadFrom.Replica);
        await reads.GetStringAsync("key");
        var sibling = ReplicaRoutes(client)[0]!;
        topology = ClusterTopologyWithoutReplicas(promote ? replica.Port : primary.Port);
        primary.SuppressReply = command => command == "CLUSTER SLOTS";
        ReplicaRoutes(client)[ClusterHash.GetSlot("key")]!.MarkValidated(TimeSpan.Zero);
        try { await reads.GetStringAsync("key"); } catch (RespireConnectionException) { }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (ReplicaRoutes(client)[ClusterHash.GetSlot("key")]!.Nodes.Length != 0) await Task.Delay(5, timeout.Token);
        if (promote)
        {
            await Assert.That(ReplicaRoutes(client)[0]!.Nodes.Length).IsEqualTo(1);
            await Assert.That(ReplicaRoutes(client)[0]!.Nodes[0]).IsSameReferenceAs(sibling.Nodes[1]);
        }
        else
        {
            await Assert.That(ReplicaRoutes(client)[0]).IsSameReferenceAs(sibling);
        }
        await Assert.That(sibling.Nodes[1].IsRetired).IsFalse();
    }

    [Test]
    [Arguments(RespireReadFrom.Replica, false)]
    [Arguments(RespireReadFrom.ReplicaPreferred, false)]
    [Arguments(RespireReadFrom.Replica, true)]
    [Arguments(RespireReadFrom.Nearest, false)]
    [Arguments(RespireReadFrom.Nearest, true)]
    public async Task ReadFrom_BatchPreservesPrimaryCacheUnlessItContainsWrite(RespireReadFrom policy, bool includeWrite)
    {
        await using var replica = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "HELLO 3" ? "%1\r\n+proto\r\n:3\r\n"u8.ToArray()
                : command.StartsWith("GET ") ? "$5\r\nvalue\r\n"u8.ToArray() : null,
        };
        await using var primary = new FakeRespServer(8, FakeRespServer.OkReply);
        primary.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? ClusterTopology(primary.Port, replica.Port)
            : command == "HELLO 3" ? "%1\r\n+proto\r\n:3\r\n"u8.ToArray()
            : command.StartsWith("GET ") ? "$5\r\nvalue\r\n"u8.ToArray() : null;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            UseCluster = true, Connections = 1, ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", primary.Port)], ClientSideCache = new(),
        });
        await client.GetStringAsync("cached");
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(1);
        if (policy == RespireReadFrom.Nearest)
            client.Core.Cluster!.NearestLatency = new ReadLatencySampler<RespireConnection>((connection, _) =>
                ValueTask.FromResult(connection.Port == replica.Port ? 10L : 100L));
        using var batch = client.WithReadFrom(policy).CreateBatch();
        var read = batch.Strings.GetString("key");
        if (includeWrite) _ = batch.Strings.Set("key", "value");
        await batch.ExecuteAsync();
        await Assert.That(await read).IsEqualTo("value");
        await Assert.That(client.ClientSideCache.Count).IsEqualTo(includeWrite ? 0 : 1);
        await Assert.That(replica.ReceivedCommands.Contains("GET key")).IsEqualTo(!includeWrite);
    }

    [Test]
    [NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReadFrom_ReplicaRetirementPreservesPrimaryCache(bool stream)
    {
        await using var replica = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "HELLO 3" ? "%1\r\n+proto\r\n:3\r\n"u8.ToArray() : null,
        };
        await using var replacement = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "HELLO 3" ? "%1\r\n+proto\r\n:3\r\n"u8.ToArray()
                : command.StartsWith("GET ") ? "$5\r\nvalue\r\n"u8.ToArray() : null,
        };
        await using var primary = new FakeRespServer(8, FakeRespServer.OkReply);
        primary.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? ClusterTopology(primary.Port, replica.Port)
            : command == "HELLO 3" ? "%1\r\n+proto\r\n:3\r\n"u8.ToArray()
            : command.StartsWith("GET ") ? "$5\r\nvalue\r\n"u8.ToArray() : null;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            UseCluster = true, Connections = 1, ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", primary.Port)], ClientSideCache = new(),
        });
        await client.GetStringAsync("cached");
        var retired = 0;
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == "Respire",
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) =>
                System.Diagnostics.ActivitySamplingResult.AllData,
            ActivityStarted = activity =>
            {
                if (activity.OperationName != "GET" || activity.GetTagItem("server.port") is not int port
                    || port != replica.Port || Interlocked.CompareExchange(ref retired, 1, 0) != 0) return;
                var router = client.Core.Cluster!;
                router.ApplyTopology([new ClusterTopologyRange(0, 16383, new("127.0.0.1", primary.Port), "primary", [])
                {
                    Replicas = [new(new("127.0.0.1", replacement.Port), "replacement", [])],
                }], router.TopologyVersion, long.MaxValue);
            },
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);
        var reads = client.WithReadFrom(RespireReadFrom.Replica);
        if (stream)
        {
            await using var result = await reads.Strings.GetStreamAsync("key");
            using var reader = new StreamReader(result!);
            await Assert.That(await reader.ReadToEndAsync()).IsEqualTo("value");
        }
        else await Assert.That(await reads.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(retired).IsEqualTo(1);
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(1);
        await Assert.That(replica.ReceivedCommands).DoesNotContain("GET key");
    }

    [Test]
    [Arguments("node-id")]
    [Arguments("primary-alias")]
    [Arguments("replica-alias")]
    [Arguments("shared-alias")]
    public async Task ReadFrom_ParsedReplicaCannotMatchAnyPrimaryIdentity(string identity)
    {
        await using var primary = new FakeRespServer(8, FakeRespServer.OkReply);
        var replicaHost = identity == "primary-alias" ? "LOCALHOST" : "other.invalid";
        var replicaId = identity == "node-id" ? "primary" : "replica";
        var replicaMetadata = identity switch
        {
            "replica-alias" => "*2\r\n+ip\r\n+127.0.0.1\r\n",
            "shared-alias" => "*2\r\n+hostname\r\n+localhost\r\n",
            _ => "*0\r\n",
        };
        var topology = Encoding.ASCII.GetBytes($"*1\r\n*4\r\n:0\r\n:16383\r\n" +
            $"*4\r\n+127.0.0.1\r\n:{primary.Port}\r\n+primary\r\n*2\r\n+hostname\r\n+localhost\r\n" +
            $"*4\r\n+{replicaHost}\r\n:{primary.Port}\r\n+{replicaId}\r\n{replicaMetadata}");
        primary.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? topology : null;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", primary.Port)],
        });
        await Assert.That(ReplicaRoutes(client)[0]!.Nodes).IsEmpty();
    }

    [Test]
    [Arguments("HSCAN", false)]
    [Arguments("SSCAN", false)]
    [Arguments("ZSCAN", false)]
    [Arguments("HSCAN", true)]
    public async Task ReadFrom_FreshRawCursorRevalidatesButContinuationKeepsAffinity(string operation, bool typedBatch)
    {
        byte[]? topology = null;
        await using var replica = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? Volatile.Read(ref topology)
                : command.StartsWith(operation) ? "*2\r\n$1\r\n7\r\n*0\r\n"u8.ToArray() : null,
        };
        await using var primary = new FakeRespServer(8, FakeRespServer.OkReply);
        topology = ClusterTopology(primary.Port, replica.Port);
        primary.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? Volatile.Read(ref topology) : null;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            ReplicaRouteRevalidationInterval = TimeSpan.FromMinutes(1),
            Endpoints = [new("127.0.0.1", primary.Port)],
        });
        await using var reads = client.WithReadFrom(RespireReadFrom.Replica);
        var descriptor = operation switch
        {
            "HSCAN" => RespireCommands.Hash.HSCAN,
            "SSCAN" => RespireCommands.Set.SSCAN,
            _ => RespireCommands.SortedSet.ZSCAN,
        };
        async Task ReadPageAsync(ulong cursor)
        {
            if (typedBatch)
            {
                using var batch = reads.CreateBatch();
                var page = batch.Hashes.ScanFieldsPage("key", cursor);
                await batch.ExecuteAsync();
                _ = page.Result;
            }
            else
            {
                using var result = await reads.ExecuteAsync(descriptor, "key", cursor);
            }
        }
        await ReadPageAsync(0);
        ReplicaRoutes(client)[ClusterHash.GetSlot("key")]!.MarkValidated(TimeSpan.Zero);
        Volatile.Write(ref topology, ClusterTopologyWithoutReplicas(replica.Port));
        await ReadPageAsync(7);
        await Assert.That(replica.ReceivedCommands.Contains("CLUSTER SLOTS")).IsFalse();
        await Assert.That(async () => await ReadPageAsync(0))
            .Throws<RespireConnectionException>();
        await Assert.That(replica.ReceivedCommands).Contains("CLUSTER SLOTS");
        await Assert.That(replica.ReceivedCommands.Count(command => command.StartsWith(operation))).IsEqualTo(2);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task ReadFrom_MixedFreshAndContinuationPagesRevalidateBeforeSending(bool freshFirst, bool removeReplica)
    {
        byte[]? topology = null;
        var reply = "*2\r\n$1\r\n7\r\n*0\r\n"u8.ToArray();
        await using var replica = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? Volatile.Read(ref topology)
                : command.StartsWith("HSCAN ", StringComparison.Ordinal) ? reply : null,
        };
        await using var primary = new FakeRespServer(8, FakeRespServer.OkReply);
        topology = ClusterTopology(primary.Port, replica.Port);
        primary.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? Volatile.Read(ref topology) : null;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            ReplicaRouteRevalidationInterval = TimeSpan.FromMinutes(1),
            Endpoints = [new("127.0.0.1", primary.Port)],
        });
        await using var reads = client.WithReadFrom(RespireReadFrom.Replica);
        await reads.Hashes.ScanFieldsPageAsync("key");
        ReplicaRoutes(client)[ClusterHash.GetSlot("key")]!.MarkValidated(TimeSpan.Zero);
        if (removeReplica) Volatile.Write(ref topology, ClusterTopologyWithoutReplicas(replica.Port));
        using var batch = reads.CreateBatch();
        var first = batch.Hashes.ScanFieldsPage("key", freshFirst ? 0UL : 7UL);
        var second = batch.Hashes.ScanFieldsPage("key", freshFirst ? 7UL : 0UL);
        if (removeReplica)
        {
            await Assert.That(async () => await batch.ExecuteAsync()).Throws<RespireConnectionException>();
            await Assert.That(() => first.Result).Throws<RespireConnectionException>();
            await Assert.That(() => second.Result).Throws<RespireConnectionException>();
            await Assert.That(replica.ReceivedCommands.Count(command => command.StartsWith("HSCAN ", StringComparison.Ordinal))).IsEqualTo(1);
        }
        else
        {
            await batch.ExecuteAsync();
            await Assert.That(first.Result.Cursor).IsEqualTo(7UL);
            await Assert.That(second.Result.Cursor).IsEqualTo(7UL);
            await Assert.That(replica.ReceivedCommands.Where(command => command.StartsWith("HSCAN ", StringComparison.Ordinal)).ToArray())
                .IsEquivalentTo(new[] { "HSCAN key 0 NOVALUES", $"HSCAN key {(freshFirst ? 0 : 7)} NOVALUES", $"HSCAN key {(freshFirst ? 7 : 0)} NOVALUES" });
        }
        await Assert.That(replica.ReceivedCommands).Contains("CLUSTER SLOTS");
    }

    [Test]
    public async Task ReadFrom_TouchKeepsTypedAndRawCallsOnPrimary()
    {
        await using var replica = new FakeRespServer(8, FakeRespServer.OkReply);
        await using var primary = new FakeRespServer(8, ":1\r\n"u8.ToArray());
        primary.ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
            ? ClusterTopology(primary.Port, replica.Port) : null;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", primary.Port)],
        });
        await using var reads = client.WithReadFrom(RespireReadFrom.Replica);
        await Assert.That(await reads.Keys.TouchAsync("key")).IsEqualTo(1);
        using (var result = await reads.ExecuteAsync(RespireCommands.Key.TOUCH, "key"))
            await Assert.That(result.AsInteger()).IsEqualTo(1);
        using (var result = await reads.ExecuteAsync("TOUCH", "key"))
            await Assert.That(result.AsInteger()).IsEqualTo(1);
        using (var batch = reads.CreateBatch())
        {
            var touched = batch.Keys.Touch("key");
            await batch.ExecuteAsync();
            await Assert.That(await touched).IsEqualTo(1);
        }
        await Assert.That(primary.ReceivedCommands.Count(command => command == "TOUCH key")).IsEqualTo(4);
        await Assert.That(replica.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task ReadFrom_ParsedReplicaCannotBeItsRangePrimary()
    {
        await using var primary = new FakeRespServer(8, FakeRespServer.OkReply);
        primary.ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
            ? ClusterTopology(primary.Port, primary.Port) : FakeRespServer.OkReply;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", primary.Port)],
        });
        await Assert.That(ReplicaRoutes(client)[0]!.Nodes).IsEmpty();
        await Assert.That(async () => await client.WithReadFrom(RespireReadFrom.Replica).Strings.GetStringAsync("key"))
            .Throws<RespireConnectionException>();
        await Assert.That(primary.ReceivedCommands.Any(command => command == "READONLY" || command.StartsWith("GET "))).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReadFrom_HealthyRefreshTimeoutDoesNotShrinkWithMasterCount(bool stallFirstTwo)
    {
        await using var replica = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "READONLY" ? FakeRespServer.OkReply : "$5\r\nvalue\r\n"u8.ToArray(),
        };
        await using var first = new FakeRespServer(8, FakeRespServer.OkReply);
        await using var second = new FakeRespServer(8, FakeRespServer.OkReply);
        await using var third = new FakeRespServer(8, FakeRespServer.OkReply);
        var topology = Encoding.ASCII.GetBytes("*3\r\n" +
            $"*3\r\n:0\r\n:5000\r\n*2\r\n$9\r\n127.0.0.1\r\n:{first.Port}\r\n" +
            $"*3\r\n:5001\r\n:10000\r\n*2\r\n$9\r\n127.0.0.1\r\n:{second.Port}\r\n" +
            $"*3\r\n:10001\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{third.Port}\r\n");
        first.ReplyOverride = (_, _) => Volatile.Read(ref topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            ConnectTimeout = TimeSpan.FromSeconds(1), CommandTimeout = TimeSpan.FromSeconds(1),
            Endpoints = [new("127.0.0.1", first.Port)],
        });
        Volatile.Write(ref topology, ClusterTopology(first.Port, replica.Port));
        foreach (var server in new[] { first, second, third })
        {
            server.DelayCommand("CLUSTER SLOTS", 750);
            server.SuppressReply = command => stallFirstTwo && !ReferenceEquals(server, third) && command == "CLUSTER SLOTS";
            server.ReplyOverride = (_, _) => Volatile.Read(ref topology);
        }
        await Assert.That(await client.WithReadFrom(RespireReadFrom.Replica).Strings.GetStringAsync("key")
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5))).IsEqualTo("value");
    }

    [Test]
    [Arguments("HSCAN")]
    [Arguments("SSCAN")]
    [Arguments("ZSCAN")]
    public async Task ReadFrom_BatchedCursorPagesAreRejectedBeforeSending(string operation)
    {
        await using var replica = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "READONLY" ? FakeRespServer.OkReply : "*2\r\n$1\r\n0\r\n*0\r\n"u8.ToArray(),
        };
        await using var primary = new FakeRespServer(8, FakeRespServer.OkReply);
        primary.ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
            ? ClusterTopology(primary.Port, replica.Port) : "*2\r\n$1\r\n7\r\n*0\r\n"u8.ToArray();
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", primary.Port)],
        });
        await using var reads = client.WithReadFrom(RespireReadFrom.Replica);
        var descriptor = operation switch
        {
            "HSCAN" => RespireCommands.Hash.HSCAN,
            "SSCAN" => RespireCommands.Set.SSCAN,
            _ => RespireCommands.SortedSet.ZSCAN,
        };
        foreach (var cursor in new[] { "0", "7" })
        {
            using var batch = reads.CreateBatch();
            await Assert.That(() => { _ = batch.Execute(descriptor, "key", cursor); }).ThrowsExactly<NotSupportedException>();
            await Assert.That(batch.Count).IsEqualTo(0);
        }
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(["CLUSTER SLOTS"]);
        await Assert.That(replica.ReceivedCommands.Any(command => command.StartsWith(operation))).IsFalse();
    }

    private static RespireClient CreateLazyClusterClient() => RespireClient.Create(new RespireOptions
    {
        Protocol = RespProtocol.Resp2,
        UseCluster = true,
        Endpoints = { new RespireEndpoint("localhost") },
    });

    private static readonly TimeSpan TestConnectTimeout = TimeSpan.FromSeconds(1);

    [Test]
    public async Task ReadFrom_ReplicaSetUsesClockForRevalidationAndRefreshThrottle()
    {
        long now = 100;
        var routes = new ClusterReplicaSet([], TimeSpan.FromMilliseconds(50), () => now);
        await Assert.That(routes.IsDueForRevalidation).IsFalse();
        now = 150;
        await Assert.That(routes.IsDueForRevalidation).IsTrue();
        var first = routes.JoinOrStartRefresh(() => Task.CompletedTask);
        await first!;
        await Assert.That(routes.JoinOrStartRefresh(() => Task.CompletedTask)).IsNull();
        now += ClusterReplicaSet.RefreshIntervalMilliseconds;
        var second = routes.JoinOrStartRefresh(() => Task.CompletedTask);
        await Assert.That(second).IsNotNull();
        await second!;
        routes.MarkValidated(TimeSpan.FromMilliseconds(50));
        await Assert.That(routes.IsDueForRevalidation).IsFalse();
    }

    [Test]
    public async Task ReadFrom_FailedRefreshesRemainThrottled()
    {
        long now = 100;
        var attempts = 0;
        var routes = new ClusterReplicaSet([], TimeSpan.Zero, () => now);
        Task Fail()
        {
            attempts++;
            return Task.FromException(new RespireConnectionException("discovery unavailable"));
        }
        await routes.JoinOrStartRefresh(Fail)!;
        for (var index = 0; index < 100; index++)
            await Assert.That(routes.JoinOrStartRefresh(Fail)).IsNull();
        await Assert.That(attempts).IsEqualTo(1);
        now += ClusterReplicaSet.RefreshIntervalMilliseconds;
        await routes.JoinOrStartRefresh(Fail)!;
        await Assert.That(attempts).IsEqualTo(2);
    }

    [Test]
    public async Task ReadFrom_StrictReplicaReportsPersistentRefreshFailure()
    {
        await using var primary = new FakeRespServer(8, FakeRespServer.OkReply);
        primary.ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
            ? ClusterTopologyWithoutReplicas(primary.Port) : null;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", primary.Port)],
        });
        primary.ReplyOverride = (_, _) => "-ERR topology unavailable\r\n"u8.ToArray();
        await using var strict = client.WithReadFrom(RespireReadFrom.Replica);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var error = await Assert.That(async () => await strict.Strings.GetStringAsync("key"))
                .ThrowsExactly<RespireConnectionException>();
            await Assert.That(error!.Message).Contains("no replica available");
            await Assert.That(error.Message).Contains(ClusterHash.GetSlot("key").ToString());
        }
        await Assert.That(primary.ReceivedCommands.Count(command => command == "CLUSTER SLOTS")).IsEqualTo(2);
        await Assert.That(primary.ReceivedCommands.Any(command => command.StartsWith("GET "))).IsFalse();
    }

    [Test]
    public async Task ReadFrom_PrimaryPinnedCursorDoesNotDiscoverUnknownReplicas()
    {
        await using var primary = new FakeRespServer(8, FakeRespServer.OkReply);
        await using var client = CreateLazyClusterClient();
        var router = client.Core.Cluster!;
        var node = router.GetOrCreateNode(new("127.0.0.1", primary.Port));
        router.SetSlotOwner(1, node);
        var probes = 0;
        var coordinator = new ClusterReplicaDiscovery(_ => { probes++; return Task.CompletedTask; }, _ => false);
        typeof(ClusterRouter).GetField("_unknownReplicaDiscovery",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(router, coordinator);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var connection = await router.GetPinnedReadConnectionAsync(1, node, timeout.Token, revalidate: true);
        await Assert.That(connection.Port).IsEqualTo(primary.Port);
        await Assert.That(probes).IsEqualTo(0);
    }

    [Test]
    public async Task ReadFrom_InvalidatedDiscoveryReturnProbesReplacementCoverage()
    {
        await using var replica = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "READONLY" ? FakeRespServer.OkReply : "$5\r\nvalue\r\n"u8.ToArray(),
        };
        await using var client = CreateLazyClusterClient();
        var router = client.Core.Cluster!;
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var unknown = (ClusterReplicaSet)typeof(ClusterRouter).GetField("_unknownReplicaRoutes", flags)!.GetValue(router)!;
        var refreshes = 0;
        var invalidate = true;
        var refreshing = false;
        ClusterReplicaDiscovery coordinator = null!;
        coordinator = new ClusterReplicaDiscovery(_ =>
        {
            var version = (long)typeof(ClusterRouter).GetField("_topologyVersion", flags)!.GetValue(router)!;
            refreshing = true;
            router.ApplyTopology([new ClusterTopologyRange(0, 16383, new("127.0.0.1", 16379), "primary", [])
            {
                Replicas = [new(new("127.0.0.1", replica.Port), "replica", [])],
            }], version, ++refreshes);
            refreshing = false;
            return Task.CompletedTask;
        }, slot =>
        {
            var covered = ReplicaRoutes(client)[slot] is { Nodes.Length: > 0 };
            if (covered && invalidate && !refreshing)
            {
                invalidate = false;
                // Publish exactly the invalidated marker after discovery observes coverage,
                // before its caller reloads routes. No timing or background thread is needed.
                SetReplicaRoutes(client, slot, unknown);
                coordinator.Invalidate(slot);
            }
            return covered;
        });
        typeof(ClusterRouter).GetField("_unknownReplicaDiscovery", flags)!.SetValue(router, coordinator);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.That(await client.WithReadFrom(RespireReadFrom.Replica).Strings.GetStringAsync("key", timeout.Token))
            .IsEqualTo("value");
        await Assert.That(refreshes).IsEqualTo(2);
    }

    [Test]
    public async Task ReadFrom_LazyReplicaReadDiscoversConfiguredSeed()
    {
        await using var replica = new FakeRespServer(FakeRespServer.OkReply, "$5\r\nvalue\r\n"u8.ToArray());
        var replies = new byte[][] { [] };
        await using var primary = new FakeRespServer(replies);
        replies[0] = ClusterTopology(primary.Port, replica.Port);
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", primary.Port) },
        });
        await Assert.That(await client.WithReadFrom(RespireReadFrom.Replica).Strings.GetStringAsync("key"))
            .IsEqualTo("value");
    }

    [Test]
    public async Task ReadFrom_ConnectedSeedRecoversFailedInitialTopology()
    {
        await using var replica = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "READONLY" ? FakeRespServer.OkReply : "$5\r\nvalue\r\n"u8.ToArray(),
        };
        await using var primary = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, _) => "-ERR topology unavailable\r\n"u8.ToArray(),
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", primary.Port)],
        });
        primary.ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
            ? ClusterTopology(primary.Port, replica.Port) : null;
        await using var reads = client.WithReadFrom(RespireReadFrom.Replica);
        await Assert.That(await reads.Strings.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(primary.ReceivedCommands.Count(command => command == "CLUSTER SLOTS")).IsEqualTo(2);
    }

    [Test]
    public async Task ReadFrom_RedundantMovedPreservesRefreshCoordination()
    {
        await using var replica = new FakeRespServer(16, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "READONLY" ? FakeRespServer.OkReply : "$5\r\nvalue\r\n"u8.ToArray(),
        };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var primary = new FakeRespServer(16, FakeRespServer.OkReply)
        {
            SuppressReply = command =>
            {
                if (command != "CLUSTER SLOTS") return false;
                started.TrySetResult();
                return true;
            },
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", primary.Port)],
        });
        var router = client.Core.Cluster!;
        var owner = router.GetOrCreateNode(new("127.0.0.1", primary.Port));
        var slot = ClusterHash.GetSlot("key");
        router.SetSlotOwner(slot, owner);
        await using var reads = client.WithReadFrom(RespireReadFrom.Replica);
        var first = reads.Strings.GetStringAsync("key").AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        router.SetSlotOwner(slot, owner);
        var second = reads.Strings.GetStringAsync("key").AsTask();
        // A redundant MOVED fences the old reply, but must not start a concurrent probe.
        primary.SuppressReply = null;
        primary.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? ClusterTopology(primary.Port, replica.Port) : null;
        await primary.SendRawAsync(ClusterTopology(primary.Port, replica.Port), primary.ReceivedConnectionIds[^1]);
        // Both calls share the fenced result; a later request retries after the throttle.
        await Assert.That(async () => await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5)))
            .ThrowsExactly<RespireConnectionException>();
        await Assert.That(first.IsFaulted && second.IsFaulted).IsTrue();
        await Assert.That(primary.ReceivedCommands.Count(command => command == "CLUSTER SLOTS")).IsEqualTo(1);
    }

    [Test]
    public async Task ReadFrom_ManyMovedSlotsShareDiscoveryAndCallerCancellation()
    {
        await using var replica = new FakeRespServer(128, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "READONLY" ? FakeRespServer.OkReply : "$5\r\nvalue\r\n"u8.ToArray(),
        };
        await using var primary = new FakeRespServer(128, FakeRespServer.OkReply);
        primary.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? ClusterTopology(primary.Port, replica.Port) : null;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", primary.Port)],
        });
        var router = client.Core.Cluster!;
        var keys = Enumerable.Range(0, 64).Select(index => $"unknown:{index}").ToArray();
        foreach (var key in keys)
        {
            var slot = ClusterHash.GetSlot(key);
            router.SetSlotOwner(slot, router.GetKnownSlotOwner(slot)!);
        }
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.SuppressReply = command =>
        {
            if (command != "CLUSTER SLOTS") return false;
            started.TrySetResult();
            return true;
        };
        await using var reads = client.WithReadFrom(RespireReadFrom.Replica);
        using var cancellation = new CancellationTokenSource();
        var canceled = reads.Strings.GetStringAsync(keys[0], cancellation.Token).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var pending = keys.Select(key => reads.Strings.GetStringAsync(key).AsTask()).ToArray();
        cancellation.Cancel();
        await Assert.That(async () => await canceled).Throws<OperationCanceledException>();
        primary.SuppressReply = null;
        await primary.SendRawAsync(ClusterTopology(primary.Port, replica.Port), primary.ReceivedConnectionIds[^1]);
        var values = await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(values.All(value => value == "value")).IsTrue();
        await Assert.That(primary.ReceivedCommands.Count(command => command == "CLUSTER SLOTS")).IsEqualTo(2);
    }

    [Test]
    public async Task ReadFrom_DisposalCancelsUnknownSlotDiscovery()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var primary = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            SuppressReply = command =>
            {
                if (command != "CLUSTER SLOTS") return false;
                started.TrySetResult();
                return true;
            },
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            ConnectTimeout = TimeSpan.FromMinutes(1), CommandTimeout = TimeSpan.FromMinutes(1),
            Endpoints = [new("127.0.0.1", primary.Port)],
        });
        await using var reads = client.WithReadFrom(RespireReadFrom.Replica);
        var pending = reads.Strings.GetStringAsync("unknown").AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await reads.DisposeAsync();
        await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5))).Throws<RespireConnectionException>();
    }

    [Test]
    public async Task ReadFrom_ConcurrentLazyReadsShareInitialDiscovery()
    {
        await using var replica = new FakeRespServer(64, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "READONLY" ? FakeRespServer.OkReply : "$5\r\nvalue\r\n"u8.ToArray(),
        };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var primary = new FakeRespServer(64, FakeRespServer.OkReply)
        {
            SuppressReply = command =>
            {
                if (command != "CLUSTER SLOTS") return false;
                started.TrySetResult();
                return true;
            },
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", primary.Port)],
        });
        await using var reads = client.WithReadFrom(RespireReadFrom.Replica);
        var pending = Enumerable.Range(0, 16).Select(i => reads.Strings.GetStringAsync($"key:{i}").AsTask()).ToArray();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        primary.SuppressReply = null;
        primary.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? ClusterTopology(primary.Port, replica.Port) : null;
        await primary.SendRawAsync(ClusterTopology(primary.Port, replica.Port), primary.ReceivedConnectionIds[^1]);
        var values = await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(values.All(value => value == "value")).IsTrue();
        await Assert.That(primary.ReceivedCommands.Count(command => command == "CLUSTER SLOTS")).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReadFrom_PartialDiscoveryIsScopedToRequestedSlot(bool moved)
    {
        await using var replica = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "READONLY" ? FakeRespServer.OkReply : "$5\r\nvalue\r\n"u8.ToArray(),
        };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        await using var primary = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            SuppressReply = command =>
            {
                if (command != "CLUSTER SLOTS") return false;
                if (Interlocked.Increment(ref requests) == 1) started.TrySetResult();
                else secondRequest.TrySetResult();
                return true;
            },
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", primary.Port)],
        });
        var keys = Enumerable.Range(0, 100).Select(i => $"key:{i}").ToArray();
        var lowKey = keys.First(key => ClusterHash.GetSlot(key) < 8192);
        var highKey = keys.First(key => ClusterHash.GetSlot(key) >= 8192);
        var full = ClusterTopology(primary.Port, replica.Port);
        primary.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? full : null;
        if (moved)
        {
            var router = client.Core.Cluster!;
            var owner = router.GetOrCreateNode(new("127.0.0.1", primary.Port));
            router.SetSlotOwner(ClusterHash.GetSlot(lowKey), owner);
            router.SetSlotOwner(ClusterHash.GetSlot(highKey), owner);
        }
        await using var reads = client.WithReadFrom(RespireReadFrom.Replica);
        var lowRead = reads.Strings.GetStringAsync(lowKey).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var highRead = reads.Strings.GetStringAsync(highKey).AsTask();
        var partial = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(full).Replace(":0\r\n:16383", ":0\r\n:8191"));
        await primary.SendRawAsync(partial, primary.ReceivedConnectionIds[^1]);
        await secondRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await primary.SendRawAsync(full, primary.ReceivedConnectionIds[^1]);
        await Assert.That(await lowRead.WaitAsync(TimeSpan.FromSeconds(5))).IsEqualTo("value");
        await Assert.That(await highRead.WaitAsync(TimeSpan.FromSeconds(5))).IsEqualTo("value");
        await Assert.That(primary.ReceivedCommands.Count(command => command == "CLUSTER SLOTS")).IsEqualTo(2);
        await Assert.That(ReplicaRoutes(client)[ClusterHash.GetSlot(lowKey)]!.Nodes).IsNotEmpty();
        await Assert.That(ReplicaRoutes(client)[ClusterHash.GetSlot(highKey)]!.Nodes).IsNotEmpty();
    }

    [Test]
    public async Task ReadFrom_CompletedRefreshReplacesRetiredSelection()
    {
        var handshake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var oldReplica = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            SuppressReply = command =>
            {
                if (command != "READONLY") return false;
                handshake.TrySetResult();
                return true;
            },
        };
        await using var newReplica = new FakeRespServer(8, FakeRespServer.OkReply);
        await using var primary = new FakeRespServer(8, FakeRespServer.OkReply);
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", primary.Port)],
        });
        var router = client.Core.Cluster!;
        List<ClusterTopologyRange> Topology(int port) => [new(0, 16383, new("127.0.0.1", primary.Port), "primary", [])
        {
            Replicas = [new(new("127.0.0.1", port), port.ToString(), [])],
        }];
        router.ApplyTopology(Topology(oldReplica.Port), 0, 1);
        var previous = new ClusterReplicaSet(ReplicaRoutes(client)[0]!.Nodes, TimeSpan.FromMinutes(1), static () => 0);
        SetReplicaRoutes(client, 0, previous);
        // Keep the old range throttled after its refresh completes. Selection already holds
        // this range when publication retires its candidate during the READONLY handshake.
        await previous.JoinOrStartRefresh(() => Task.CompletedTask)!;
        var selection = router.GetReadConnectionAsync(0, RespireReadFrom.Replica, CancellationToken.None).AsTask();
        await handshake.Task.WaitAsync(TimeSpan.FromSeconds(5));
        router.ApplyTopology(Topology(newReplica.Port), 0, 2);
        var connection = await selection.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(connection.Port).IsEqualTo(newReplica.Port);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    public async Task ReadFrom_PartialReplicaCoverageContinuesToPrimary(bool stalledReplica, bool emptyReplicaReply)
    {
        byte[]? partial = null;
        await using var oldReplica = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            SuppressReply = command => stalledReplica && command == "CLUSTER SLOTS",
            ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? partial
                : command == "READONLY" ? FakeRespServer.OkReply : "$3\r\nold\r\n"u8.ToArray(),
        };
        await using var newReplica = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "READONLY" ? FakeRespServer.OkReply : "$3\r\nnew\r\n"u8.ToArray(),
        };
        byte[]? topology = null;
        await using var primary = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? Volatile.Read(ref topology) : null,
        };
        topology = ClusterTopology(primary.Port, oldReplica.Port);
        partial = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(topology).Replace(":0\r\n:16383", ":0\r\n:8191"));
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            ReplicaRouteRevalidationInterval = TimeSpan.FromMinutes(1),
            ConnectTimeout = TimeSpan.FromSeconds(1), CommandTimeout = TimeSpan.FromSeconds(1),
            Endpoints = [new("127.0.0.1", primary.Port)],
        });
        var key = Enumerable.Range(0, 100).Select(i => $"key:{i}").First(value => ClusterHash.GetSlot(value) >= 8192);
        await using var reads = client.WithReadFrom(RespireReadFrom.Replica);
        await Assert.That(await reads.Strings.GetStringAsync(key)).IsEqualTo("old");
        var routes = ReplicaRoutes(client)[ClusterHash.GetSlot(key)]!;
        Volatile.Write(ref topology, ClusterTopology(primary.Port, newReplica.Port));
        if (emptyReplicaReply)
        {
            partial = ClusterTopologyWithoutReplicas(primary.Port);
            primary.DelayCommand("CLUSTER SLOTS", 250);
        }
        routes.MarkValidated(TimeSpan.Zero);
        // Selection schedules revalidation without sending a GET behind the deliberately
        // suppressed topology response on the fake server's FIFO connection.
        await client.Core.Cluster!.GetReadConnectionAsync(ClusterHash.GetSlot(key), RespireReadFrom.Replica, CancellationToken.None);
        var refresh = routes.JoinOrStartRefresh(() => throw new InvalidOperationException("Read did not start refresh"));
        if (refresh is not null) await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(primary.ReceivedCommands.Count(command => command == "CLUSTER SLOTS")).IsEqualTo(2);
        await Assert.That(await reads.Strings.GetStringAsync(key)).IsEqualTo("new");
    }

    [Test]
    public async Task ReadFrom_ReplicaSelectionRetriesPublishedRoutesWhileOldRefreshIsThrottled()
    {
        await using var oldReplica = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            SuppressReply = command => command == "READONLY",
        };
        await using var newReplica = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("GET ") ? "$3\r\nnew\r\n"u8.ToArray() : null,
        };
        await using var primary = new FakeRespServer(8, FakeRespServer.OkReply);
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, Connections = 1,
            ClusterTopologyRefreshInterval = null, ConnectTimeout = TimeSpan.FromSeconds(5),
            Endpoints = [new("127.0.0.1", primary.Port)],
        });
        var router = client.Core.Cluster!;
        void Publish(int port, long generation) => router.ApplyTopology(
            [new ClusterTopologyRange(0, 16383, new("127.0.0.1", primary.Port), "primary", [])
            {
                Replicas = [new(new("127.0.0.1", port), "replica", [])],
            }], router.TopologyVersion, generation);
        Publish(oldReplica.Port, 1);
        var slot = ClusterHash.GetSlot("key");
        // Freeze the old set's throttle so scheduler delays cannot reopen its refresh budget.
        var oldRoutes = new ClusterReplicaSet(ReplicaRoutes(client)[slot]!.Nodes, TimeSpan.FromMinutes(1), () => 0);
        SetReplicaRoutes(client, slot, oldRoutes);
        await oldRoutes.JoinOrStartRefresh(() => Task.CompletedTask)!;
        await using var reads = client.WithReadFrom(RespireReadFrom.Replica);
        var read = reads.Strings.GetStringAsync("key").AsTask();
        await WaitForCommandsAsync(oldReplica, 1);
        Publish(newReplica.Port, 2);
        await Assert.That(await read.WaitAsync(TimeSpan.FromSeconds(5))).IsEqualTo("new");
        await Assert.That(oldReplica.ReceivedCommands).DoesNotContain("GET key");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReadFrom_MigrationReplicaCoverageDropsSourceRoutes(bool entireRange)
    {
        await using var client = RespireClient.Create(new RespireOptions
        {
            UseCluster = true, ClusterTopologyRefreshInterval = null,
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Enabled,
            Endpoints = [new("127.0.0.1", 6379)],
        });
        var router = client.Core.Cluster!;
        var sourceEndpoint = new RespireEndpoint("source", 7000);
        var targetEndpoint = new RespireEndpoint("target", 7001);
        router.ApplyTopology([new ClusterTopologyRange(0, 16383, sourceEndpoint, "source", [])
        {
            Replicas = [new(new("replica", 7002), "replica", [])],
        }], 0, 1);
        var source = router.GetKnownSlotOwner(0)!;
        var oldRoutes = ReplicaRoutes(client)[0]!;
        var replica = oldRoutes.Nodes[0];
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        router.TopologyChanged += (_, _, _) => changed.TrySetResult();
        source.PublishMaintenanceNotification(new object(), new("SMIGRATED", 1, Migrations:
            [new(sourceEndpoint, targetEndpoint, entireRange ? "0-16383" : "0")]));
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(router.GetSlotOwnerEndpoint(0)).IsEqualTo(targetEndpoint);
        await Assert.That(ReplicaRoutes(client)[0]).IsNull();
        if (entireRange)
        {
            await router.WaitForRetirementAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(router.IsReplicaNode(replica)).IsFalse();
            await Assert.That(replica.IsRetired).IsTrue();
        }
        else await Assert.That(ReplicaRoutes(client)[1]).IsSameReferenceAs(oldRoutes);
        await Assert.That(async () => await router.GetPinnedReadConnectionAsync(0, replica, CancellationToken.None))
            .ThrowsExactly<RespireConnectionException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReadFrom_ReplicaRefreshPreservesUncoveredShard(bool staleFullSnapshot)
    {
        byte[]? reply = null;
        await using var otherReplica = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("GET ") ? "$5\r\nvalue\r\n"u8.ToArray() : null,
        };
        await using var otherPrimary = new FakeRespServer(8, FakeRespServer.OkReply);
        await using var replica = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? reply
                : command == "READONLY" ? FakeRespServer.OkReply : "$5\r\nvalue\r\n"u8.ToArray(),
        };
        await using var primary = new FakeRespServer(8, FakeRespServer.OkReply);
        static string Range(int start, int end, int owner, int replicaPort)
            => $"*4\r\n:{start}\r\n:{end}\r\n*2\r\n+127.0.0.1\r\n:{owner}\r\n*2\r\n+127.0.0.1\r\n:{replicaPort}\r\n";
        var topology = Encoding.ASCII.GetBytes($"*2\r\n{Range(0, 8191, otherPrimary.Port, otherReplica.Port)}{Range(8192, 16383, primary.Port, replica.Port)}");
        primary.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? topology : null;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            ConnectTimeout = TimeSpan.FromMilliseconds(250), CommandTimeout = TimeSpan.FromMilliseconds(250),
            Endpoints = [new("127.0.0.1", primary.Port)],
        });
        var key = Enumerable.Range(0, 100).Select(i => $"key:{i}").First(value => ClusterHash.GetSlot(value) >= 8192);
        var slot = ClusterHash.GetSlot(key);
        await using var reads = client.WithReadFrom(RespireReadFrom.Replica);
        await reads.Strings.GetStringAsync(key);
        var lowOwner = client.Core.Cluster!.GetKnownSlotOwner(0);
        var lowReplicas = ReplicaRoutes(client)[0];
        reply = staleFullSnapshot
            ? ClusterTopologyWithoutReplicas(replica.Port)
            : Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(ClusterTopologyWithoutReplicas(primary.Port))
                .Replace(":0\r\n:16383", ":8192\r\n:16383"));
        // Keep this regression focused on the replica's partial reply; concurrent primary
        // discovery must not win with its original full snapshot before that reply arrives.
        primary.SuppressReply = command => command == "CLUSTER SLOTS";
        otherPrimary.SuppressReply = command => command == "CLUSTER SLOTS";
        ReplicaRoutes(client)[slot]!.MarkValidated(TimeSpan.Zero);
        try { await reads.Strings.GetStringAsync(key); }
        catch (RespireConnectionException) { }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (ReplicaRoutes(client)[slot]?.Nodes.Length != 0) await Task.Delay(5, timeout.Token);
        await Assert.That(client.Core.Cluster.GetKnownSlotOwner(0)).IsSameReferenceAs(lowOwner);
        await Assert.That(ReplicaRoutes(client)[0]).IsSameReferenceAs(lowReplicas);
        await Assert.That(lowOwner!.IsRetired).IsFalse();
        var lowKey = Enumerable.Range(0, 100).Select(i => $"key:{i}").First(value => ClusterHash.GetSlot(value) < 8192);
        await Assert.That(await reads.Strings.GetStringAsync(lowKey)).IsEqualTo("value");
    }

    [Test]
    [Arguments("MOVED", 0)]
    [Arguments("MOVED", 1)]
    [Arguments("MOVED", 2)]
    [Arguments("MOVED", 3)]
    [Arguments("ASK", 0)]
    [Arguments("ASK", 1)]
    [Arguments("ASK", 2)]
    [Arguments("ASK", 3)]
    [Arguments("MOVED", 0, RespireReadFrom.AzAffinity)]
    [Arguments("MOVED", 1, RespireReadFrom.AzAffinity)]
    [Arguments("MOVED", 2, RespireReadFrom.AzAffinity)]
    [Arguments("MOVED", 3, RespireReadFrom.AzAffinity)]
    [Arguments("ASK", 0, RespireReadFrom.AzAffinity)]
    [Arguments("ASK", 1, RespireReadFrom.AzAffinity)]
    [Arguments("ASK", 2, RespireReadFrom.AzAffinity)]
    [Arguments("ASK", 3, RespireReadFrom.AzAffinity)]
    [Arguments("MOVED", 0, RespireReadFrom.AzAffinityReplicasAndPrimary)]
    [Arguments("MOVED", 1, RespireReadFrom.AzAffinityReplicasAndPrimary)]
    [Arguments("MOVED", 2, RespireReadFrom.AzAffinityReplicasAndPrimary)]
    [Arguments("MOVED", 3, RespireReadFrom.AzAffinityReplicasAndPrimary)]
    [Arguments("ASK", 0, RespireReadFrom.AzAffinityReplicasAndPrimary)]
    [Arguments("ASK", 1, RespireReadFrom.AzAffinityReplicasAndPrimary)]
    [Arguments("ASK", 2, RespireReadFrom.AzAffinityReplicasAndPrimary)]
    [Arguments("ASK", 3, RespireReadFrom.AzAffinityReplicasAndPrimary)]
    public async Task ReadFrom_RoleFallbackFollowsRedirectWithoutReturningToFailedReplica(string redirect, int mode, RespireReadFrom policy = RespireReadFrom.ReplicaPreferred)
    {
        const string key = "{batch-fallback}:key";
        var slot = ClusterHash.GetSlot(key);
        byte[]? targetTopology = null;
        await using var target = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? targetTopology
                : command.StartsWith("GET ") ? "$5\r\nvalue\r\n"u8.ToArray()
                : command.StartsWith("XREAD ") ? "*0\r\n"u8.ToArray() : null,
        };
        await using var replica = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("GET ") || command.StartsWith("XREAD ")
                ? "-LOADING unavailable\r\n"u8.ToArray() : null,
        };
        // A successful redirect refresh still advertises the replica that rejected this read.
        targetTopology = ClusterTopology(target.Port, replica.Port);
        await using var primary = new FakeRespServer(8, FakeRespServer.OkReply);
        primary.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? ClusterTopology(primary.Port, replica.Port)
            : Encoding.ASCII.GetBytes($"-{redirect} {slot} 127.0.0.1:{target.Port}\r\n");
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            ClientAvailabilityZone = "local",
            Endpoints = [new("127.0.0.1", primary.Port)],
        });
        await using var reads = client.WithReadFrom(policy);
        if (mode == 1)
        {
            using var batch = reads.CreateBatch();
            var value = batch.Strings.GetString(key);
            await batch.ExecuteAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(await value).IsEqualTo("value");
        }
        else if (mode == 2)
        {
            await using var stream = await reads.Strings.GetStreamAsync(key);
            using var reader = new StreamReader(stream!);
            await Assert.That(await reader.ReadToEndAsync()).IsEqualTo("value");
        }
        else if (mode == 3)
            await Assert.That(await reads.Streams.ReadAsync(key, waitFor: TimeSpan.FromMilliseconds(1))).IsEmpty();
        else
            await Assert.That(await reads.Strings.GetStringAsync(key)).IsEqualTo("value");
        var prefix = mode == 3 ? "XREAD " : "GET ";
        await Assert.That(replica.ReceivedCommands.Count(command => command.StartsWith(prefix))).IsEqualTo(1);
        await Assert.That(primary.ReceivedCommands.Count(command => command.StartsWith(prefix))).IsEqualTo(1);
        if (redirect == "ASK") await Assert.That(target.ReceivedCommands).Contains("ASKING");
    }

    [Test]
    public async Task ReadFrom_PrimaryPreferredKeepsReplicaRoleAfterMoved()
    {
        const string key = "{primary-fallback}:key";
        var slot = ClusterHash.GetSlot(key);
        await using var replacementReplica = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("GET ") ? "$5\r\nvalue\r\n"u8.ToArray() : null,
        };
        await using var target = new FakeRespServer(8, FakeRespServer.OkReply);
        target.ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
            ? ClusterTopology(target.Port, replacementReplica.Port) : "-LOADING unavailable\r\n"u8.ToArray();
        await using var replica = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("GET ")
                ? Encoding.ASCII.GetBytes($"-MOVED {slot} 127.0.0.1:{target.Port}\r\n") : null,
        };
        await using var primary = new FakeRespServer(8, FakeRespServer.OkReply);
        primary.ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
            ? ClusterTopology(primary.Port, replica.Port) : "-LOADING unavailable\r\n"u8.ToArray();
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", primary.Port)],
        });
        await Assert.That(await client.WithReadFrom(RespireReadFrom.PrimaryPreferred).Strings.GetStringAsync(key))
            .IsEqualTo("value");
        await Assert.That(target.ReceivedCommands.Any(command => command.StartsWith("GET "))).IsFalse();
    }

    [Test]
    public async Task ReadFrom_DisposalCancelsSharedReplicaRefresh()
    {
        var refreshing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var primary = new FakeRespServer(2)
        {
            SuppressReply = command =>
            {
                if (command != "CLUSTER SLOTS") return false;
                refreshing.TrySetResult();
                return true;
            },
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            CommandTimeout = TimeSpan.FromMinutes(1),
            Endpoints = { new RespireEndpoint("127.0.0.1", primary.Port) },
        });
        var read = client.WithReadFrom(RespireReadFrom.Replica).Strings.GetStringAsync("key").AsTask();
        await refreshing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(async () => await read.WaitAsync(TimeSpan.FromSeconds(5)))
            .ThrowsExactly<RespireConnectionException>();
    }

    [Test]
    [Arguments(RespireReadFrom.PrimaryPreferred, RespireErrorCodes.Loading)]
    [Arguments(RespireReadFrom.PrimaryPreferred, RespireErrorCodes.MasterDown)]
    [Arguments(RespireReadFrom.PrimaryPreferred, RespireErrorCodes.ClusterDown)]
    [Arguments(RespireReadFrom.ReplicaPreferred, RespireErrorCodes.Loading)]
    [Arguments(RespireReadFrom.ReplicaPreferred, RespireErrorCodes.MasterDown)]
    [Arguments(RespireReadFrom.ReplicaPreferred, RespireErrorCodes.ClusterDown)]
    public async Task ReadFrom_BatchRetriesUnavailableRoleInOrder(RespireReadFrom policy, string code)
    {
        var unavailable = Encoding.ASCII.GetBytes($"-{code} unavailable\r\n");
        var value = "$5\r\nvalue\r\n"u8.ToArray();
        await using var replica = new FakeRespServer(2)
        {
            ReplyOverride = (_, command) => command == "READONLY" ? FakeRespServer.OkReply
                : policy == RespireReadFrom.ReplicaPreferred ? unavailable : value,
        };
        byte[]? topology = null;
        await using var primary = new FakeRespServer(2)
        {
            ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? topology
                : policy == RespireReadFrom.PrimaryPreferred ? unavailable : value,
        };
        topology = ClusterTopology(primary.Port, replica.Port);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", primary.Port) },
        });
        using var batch = client.WithReadFrom(policy).CreateBatch();
        var first = batch.Strings.GetString("{batch}:first");
        var second = batch.Strings.GetString("{batch}:second");
        await batch.ExecuteAsync();
        await Assert.That(await first).IsEqualTo("value");
        await Assert.That(await second).IsEqualTo("value");
        foreach (var server in new[] { primary, replica })
            await Assert.That(server.ReceivedCommands.Where(command => command.StartsWith("GET ")).ToArray())
                .IsEquivalentTo(["GET {batch}:first", "GET {batch}:second"]);
    }

    [Test]
    public async Task ReadFrom_PerSlotRoutesExcludeAdvertisedPrimary()
    {
        await using var client = CreateLazyClusterClient();
        var endpoint = new RespireEndpoint("127.0.0.1", 16379);
        client.Core.Cluster!.ApplyTopology([
            new ClusterTopologyRange(0, 16383, endpoint, "primary", [])
            {
                Replicas = [new ClusterTopologyReplica(endpoint, "primary", [])],
            },
        ], 0, 1);
        await Assert.That(ReplicaRoutes(client)[0]!.Nodes).IsEmpty();
    }

    [Test]
    public async Task ReadFrom_RevalidationDiscoversPromotionThroughConnectedReplica()
    {
        byte[]? promotedTopology = null;
        var primaryStopped = false;
        await using var replica = new FakeRespServer(4)
        {
            ReplyOverride = (_, command) => command switch
            {
                "READONLY" => FakeRespServer.OkReply,
                "CLUSTER SLOTS" => Volatile.Read(ref promotedTopology),
                _ => "$5\r\nvalue\r\n"u8.ToArray(),
            },
        };
        var primaryReplies = new byte[][] { [] };
        await using var primary = new FakeRespServer(primaryReplies);
        primaryReplies[0] = ClusterTopology(primary.Port, replica.Port);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            // Healthy startup uses the ordinary connection deadline. After shutdown, fail
            // primary discovery explicitly instead of imposing a 200 ms deadline on all peers.
            TestingStreamFactory = OpenStreamAsync,
            Endpoints = { new RespireEndpoint("127.0.0.1", primary.Port) },
        });
        var strict = client.WithReadFrom(RespireReadFrom.Replica);
        await Assert.That(await strict.Strings.GetStringAsync("key")).IsEqualTo("value");
        Volatile.Write(ref promotedTopology, ClusterTopologyWithoutReplicas(replica.Port));
        await primary.DisposeAsync();
        Volatile.Write(ref primaryStopped, true);
        ReplicaRoutes(client)[ClusterHash.GetSlot("key")]!.MarkValidated(TimeSpan.Zero);
        try { await strict.Strings.GetStringAsync("key"); }
        catch (RespireConnectionException) { }
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (ReplicaRoutes(client)[ClusterHash.GetSlot("key")]!.Nodes.Length != 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        await Assert.That(ReplicaRoutes(client)[ClusterHash.GetSlot("key")]!.Nodes).IsEmpty();
        await Assert.That(ReplicaRoutes(client)[0]!.Nodes).IsEmpty();
        await Assert.That(replica.ReceivedCommands).Contains("CLUSTER SLOTS");
        await Assert.That(async () => await strict.Strings.GetStringAsync("key"))
            .ThrowsExactly<RespireConnectionException>();
        await Assert.That(async () => await strict.Strings.GetStringAsync("sibling"))
            .ThrowsExactly<RespireConnectionException>();
        await Assert.That(replica.ReceivedCommands).DoesNotContain("GET sibling");

        async ValueTask<Stream> OpenStreamAsync(string host, int port, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (port == primary.Port && Volatile.Read(ref primaryStopped))
                throw new RespireConnectionException("The test primary has stopped.");
            var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(host, port, token);
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        }
    }

    [Test]
    public async Task ReadFrom_UsesReplicaHandshakeAndKeepsWritesAndUnknownCommandsOnPrimary()
    {
        await using var replica = new FakeRespServer(
            FakeRespServer.OkReply,
            "$7\r\nreplica\r\n"u8.ToArray());
        await using var primary = new FakeRespServer(2, FakeRespServer.OkReply);
        primary.ReplyOverride = (_, command) => command switch
        {
            "CLUSTER SLOTS" => ClusterTopology(primary.Port, replica.Port),
            "SET write value" => FakeRespServer.OkReply,
            "GET caller-descriptor" or "CUSTOM unknown" or "GET default" => "$7\r\nprimary\r\n"u8.ToArray(),
            _ => null,
        };

        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", primary.Port) },
        });
        var replicaView = client.WithReadFrom(RespireReadFrom.Replica);
        var prefixedFirst = replicaView.WithKeyPrefix("tenant:");
        var prefixedSecond = client.WithKeyPrefix("tenant:").WithReadFrom(RespireReadFrom.Replica);

        await Assert.That(await prefixedFirst.Strings.GetStringAsync("first")).IsEqualTo("replica");
        var cluster = client.Core.Cluster!;
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var routes = (ClusterReplicaSet?[])typeof(ClusterRouter)
            .GetField("_replicasBySlot", flags)!.GetValue(cluster)!;
        var replicaNode = routes[ClusterHash.GetSlot("tenant:first")]!.Nodes[0];
        var identities = (ClusterNodeIdentityIndex)typeof(ClusterRouter)
            .GetField("_identities", flags)!.GetValue(cluster)!;
        await Assert.That(replicaNode.Options.ReadOnly).IsTrue();
        await Assert.That(identities.Replicas.TryGetId(replicaNode, out var replicaId)).IsTrue();
        await Assert.That(replicaId).IsEqualTo("replica-id");
        await Assert.That(identities.Replicas.TryGetById("replica-id", out var identifiedReplica)).IsTrue();
        await Assert.That(ReferenceEquals(identifiedReplica, replicaNode)).IsTrue();
        await Assert.That(await prefixedSecond.Strings.GetStringAsync("second")).IsEqualTo("replica");
        using (var raw = await replicaView.ExecuteAsync(RespireCommands.String.GET, ["raw"]))
        {
            await Assert.That(raw.AsString()).IsEqualTo("replica");
        }
        using (var batch = replicaView.CreateBatch())
        {
            var batchedRead = batch.Strings.GetString("batch");
            await batch.ExecuteAsync();
            await Assert.That(await batchedRead).IsEqualTo("replica");
        }

        await replicaView.Strings.SetAsync("write", "value");
        using (var descriptorRead = await replicaView.ExecuteAsync(RespireCommands.String.GET, ["descriptor"]))
        {
            await Assert.That(descriptorRead.AsString()).IsEqualTo("replica");
        }
        using (var callerDescriptorRead = await replicaView.ExecuteAsync(
            RespireCommand.Create("GET"), ["caller-descriptor"]))
        {
            await Assert.That(primary.ReceivedCommands).Contains("GET caller-descriptor");
            await Assert.That(callerDescriptorRead.AsString()).IsEqualTo("primary");
        }
        using (var unknownRead = await replicaView.ExecuteAsync(RespireCommand.Create("CUSTOM"), ["unknown"]))
        {
            await Assert.That(unknownRead.AsString()).IsEqualTo("primary");
        }
        await Assert.That(await client.Strings.GetStringAsync("default")).IsEqualTo("primary");

        await Assert.That(replica.ReceivedCommands).IsEquivalentTo(
            ["READONLY", "GET tenant:first", "GET tenant:second", "GET raw", "GET batch", "GET descriptor"]);
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(
            ["CLUSTER SLOTS", "SET write value", "GET caller-descriptor", "CUSTOM unknown", "GET default"]);
    }

    [Test]
    [Arguments("?", "hostname", "127.0.0.1")]
    [Arguments("?", "ip", "127.0.0.1")]
    [Arguments("", "hostname", "127.0.0.1")]
    // A known preferred endpoint wins over metadata, as it does for primaries.
    [Arguments("127.0.0.1", "hostname", "replica.invalid")]
    public async Task ReadFrom_UsesReplicaMetadataOnlyWhenPreferredEndpointIsUnknown(
        string preferred, string metadataName, string metadataValue)
    {
        await using var replica = new FakeRespServer(FakeRespServer.OkReply, "$7\r\nreplica\r\n"u8.ToArray());
        var primaryReplies = new byte[][] { [] };
        await using var primary = new FakeRespServer(primaryReplies);
        primaryReplies[0] = Encoding.ASCII.GetBytes(
            $"*1\r\n*4\r\n:0\r\n:16383\r\n" +
            $"*3\r\n$9\r\n127.0.0.1\r\n:{primary.Port}\r\n$7\r\nprimary\r\n" +
            $"*4\r\n${preferred.Length}\r\n{preferred}\r\n:{replica.Port}\r\n$10\r\nreplica-id\r\n" +
            $"%1\r\n+{metadataName}\r\n${metadataValue.Length}\r\n{metadataValue}\r\n");

        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", primary.Port) },
        });

        var value = await client.WithReadFrom(RespireReadFrom.Replica).Strings.GetStringAsync("key");

        await Assert.That(value).IsEqualTo("replica");
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(["CLUSTER SLOTS"]);
        await Assert.That(replica.ReceivedCommands).IsEquivalentTo(["READONLY", "GET key"]);
    }

    [Test]
    public async Task ReadFrom_ViewDisposalDoesNotOwnClientCoreInEitherOrder()
    {
        var client = CreateLazyClusterClient();
        var view = client.WithReadFrom(RespireReadFrom.Replica);
        await view.DisposeAsync();
        await client.DisposeAsync();
        await view.DisposeAsync();
    }

    [Test]
    public async Task ReadFrom_StrictReplicaFailsAndReplicaPreferredFallsBackWhenNoReplicaExists()
    {
        var primaryReplies = new byte[][]
        {
            [],
            "$7\r\nprimary\r\n"u8.ToArray(),
        };
        await using var primary = new FakeRespServer(primaryReplies);
        primaryReplies[0] = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{primary.Port}\r\n");
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", primary.Port) },
        });

        var strict = client.WithReadFrom(RespireReadFrom.Replica);
        var error = await Assert.That(async () => await strict.Strings.GetStringAsync("strict"))
            .ThrowsExactly<RespireConnectionException>();
        await Assert.That(error!.Message).Contains("no replica");
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(["CLUSTER SLOTS", "CLUSTER SLOTS"]);

        var preferred = client.WithReadFrom(RespireReadFrom.ReplicaPreferred);
        await Assert.That(await preferred.Strings.GetStringAsync("fallback")).IsEqualTo("primary");
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(
            ["CLUSTER SLOTS", "CLUSTER SLOTS", "GET fallback"]);
    }

    [Test]
    public async Task ReadFrom_RefreshesEmptyReplicaSnapshotBeforeFailing()
    {
        var key = "{replica-added}:key";
        await using var replica = new FakeRespServer(FakeRespServer.OkReply, "$5\r\nfresh\r\n"u8.ToArray());
        var primaryReplies = new byte[][] { [], [] };
        await using var primary = new FakeRespServer(2, primaryReplies);
        primaryReplies[0] = ClusterTopologyWithoutReplicas(primary.Port);
        primaryReplies[1] = ClusterTopology(primary.Port, replica.Port);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", primary.Port) },
        });

        await Assert.That(await client.WithReadFrom(RespireReadFrom.Replica)
            .Strings.GetStringAsync(key)).IsEqualTo("fresh");
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(["CLUSTER SLOTS", "CLUSTER SLOTS"]);
        await Assert.That(replica.ReceivedCommands).IsEquivalentTo(["READONLY", $"GET {key}"]);
    }

    [Test]
    public async Task ReadFrom_ThrottlesEmptyReplicaSnapshotRefreshAndRetriesAfterInterval()
    {
        var key = "{replica-added-later}:key";
        await using var replica = new FakeRespServer(FakeRespServer.OkReply, "$5\r\nfresh\r\n"u8.ToArray());
        var primaryReplies = new byte[][] { [], [], ClusterTopology(1, replica.Port) };
        await using var primary = new FakeRespServer(3, primaryReplies);
        primaryReplies[0] = ClusterTopologyWithoutReplicas(primary.Port);
        primaryReplies[1] = ClusterTopologyWithoutReplicas(primary.Port);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", primary.Port) },
        });
        var strict = client.WithReadFrom(RespireReadFrom.Replica);

        await Assert.That(async () => await strict.Strings.GetStringAsync(key)).ThrowsExactly<RespireConnectionException>();
        await Assert.That(async () => await strict.Strings.GetStringAsync(key)).ThrowsExactly<RespireConnectionException>();
        await Assert.That(primary.ReceivedCommands.Count(command => command == "CLUSTER SLOTS")).IsEqualTo(2);

        await Task.Delay(TimeSpan.FromMilliseconds(1100));
        await Assert.That(await strict.Strings.GetStringAsync(key)).IsEqualTo("fresh");
        await Assert.That(primary.ReceivedCommands.Count(command => command == "CLUSTER SLOTS")).IsEqualTo(3);
    }

    [Test]
    public async Task IsConnected_RemainsTrueWhenOnlyReplicaConnectionIsConnected()
    {
        await using var replica = new FakeRespServer(FakeRespServer.OkReply, "$8\r\nreplica!\r\n"u8.ToArray());
        var primaryReplies = new byte[][] { [] };
        await using var primary = new FakeRespServer(primaryReplies);
        primaryReplies[0] = ClusterTopology(primary.Port, replica.Port);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", primary.Port) },
        });

        await Assert.That(await client.WithReadFrom(RespireReadFrom.Replica)
            .Strings.GetStringAsync("{replica-only}:key")).IsEqualTo("replica!");
        await primary.DisposeAsync();
        await Task.Delay(100);

        await Assert.That(client.IsConnected).IsTrue();
    }

    [Test]
    public async Task ReadFrom_RoutesBlockingCatalogReadToReplicaDedicatedPool()
    {
        var key = "{blocking-read}:stream";
        await using var replica = new FakeRespServer(3)
        {
            ReplyOverride = static (_, command) => command.StartsWith("XREAD ", StringComparison.Ordinal)
                ? "*0\r\n"u8.ToArray()
                : FakeRespServer.OkReply,
        };
        var primaryReplies = new byte[][] { [] };
        await using var primary = new FakeRespServer(primaryReplies);
        primaryReplies[0] = ClusterTopology(primary.Port, replica.Port);
        await using var routedClient = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", primary.Port) },
        });

        using var response = await routedClient.WithReadFrom(RespireReadFrom.Replica)
            .ExecuteAsync(RespireCommands.Stream.XREAD, ["BLOCK", 1, "STREAMS", key, "0"]);
        var entries = await routedClient.WithReadFrom(RespireReadFrom.Replica).Streams.ReadAsync(
            key, waitFor: TimeSpan.FromMilliseconds(1));

        await Assert.That(entries).IsEmpty();
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(["CLUSTER SLOTS"]);
        await Assert.That(replica.ReceivedCommands.Count(command => command.StartsWith("XREAD ", StringComparison.Ordinal)))
            .IsEqualTo(2);
        await Assert.That(replica.ReceivedCommands).Contains("READONLY");
    }

    [Test]
    public async Task ReadFrom_PrimaryPreferredUsesHealthyPrimaryBeforeReplica()
    {
        var primaryReplies = new byte[][]
        {
            [],
            "$7\r\nprimary\r\n"u8.ToArray(),
        };
        await using var primary = new FakeRespServer(primaryReplies);
        await using var replica = new FakeRespServer(FakeRespServer.OkReply);
        primaryReplies[0] = Encoding.ASCII.GetBytes(
            $"*1\r\n*4\r\n:0\r\n:16383\r\n" +
            $"*3\r\n$9\r\n127.0.0.1\r\n:{primary.Port}\r\n$7\r\nprimary\r\n" +
            $"*3\r\n$9\r\n127.0.0.1\r\n:{replica.Port}\r\n$10\r\nreplica-id\r\n");
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", primary.Port) },
        });

        await Assert.That(await client.WithReadFrom(RespireReadFrom.PrimaryPreferred)
            .Strings.GetStringAsync("preferred")).IsEqualTo("primary");
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(["CLUSTER SLOTS", "GET preferred"]);
        await Assert.That(replica.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task ReadFrom_CancellationDuringReplicaHandshakeDoesNotFallBackToPrimary()
    {
        var readOnlyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var replica = new FakeRespServer
        {
            SuppressReply = command =>
            {
                if (command == "READONLY") readOnlyStarted.TrySetResult();
                return command == "READONLY";
            },
        };
        var primaryReplies = new byte[][] { [] };
        await using var primary = new FakeRespServer(primaryReplies);
        primaryReplies[0] = Encoding.ASCII.GetBytes(
            $"*1\r\n*4\r\n:0\r\n:16383\r\n" +
            $"*3\r\n$9\r\n127.0.0.1\r\n:{primary.Port}\r\n$7\r\nprimary\r\n" +
            $"*3\r\n$9\r\n127.0.0.1\r\n:{replica.Port}\r\n$10\r\nreplica-id\r\n");
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", primary.Port) },
        });
        using var cancellation = new CancellationTokenSource();
        var read = client.WithReadFrom(RespireReadFrom.ReplicaPreferred)
            .Strings.GetStringAsync("cancelled", cancellation.Token).AsTask();
        await readOnlyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.That(async () => await read).Throws<OperationCanceledException>();
        await Assert.That(replica.ReceivedCommands).IsEquivalentTo(["READONLY"]);
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(["CLUSTER SLOTS"]);
    }

    [Test]
    [Arguments(false, RespireReadFrom.Replica)]
    [Arguments(true, RespireReadFrom.Replica)]
    [Arguments(false, RespireReadFrom.Nearest)]
    [Arguments(true, RespireReadFrom.Nearest)]
    public async Task ReadFrom_MovedRefreshesReplicaRoutesBeforeRetry(bool partial, RespireReadFrom policy)
    {
        var key = "{moved}:key";
        var slot = ClusterHash.GetSlot(key);
        await using var newReplica = new FakeRespServer(
            FakeRespServer.OkReply,
            "$10\r\nreplicated\r\n"u8.ToArray());
        var targetReplies = new byte[][] { [] };
        await using var target = new FakeRespServer(targetReplies);
        targetReplies[0] = ClusterTopology(target.Port, newReplica.Port);
        if (partial)
            targetReplies[0] = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(targetReplies[0])
                .Replace(":0\r\n:16383", $":{slot}\r\n:{slot}"));
        var moved = Encoding.ASCII.GetBytes($"-MOVED {slot} 127.0.0.1:{target.Port}\r\n");
        await using var oldReplica = new FakeRespServer(FakeRespServer.OkReply, moved);
        var primaryReplies = new byte[][] { [] };
        await using var primary = new FakeRespServer(primaryReplies);
        primaryReplies[0] = ClusterTopology(primary.Port, oldReplica.Port);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", primary.Port) },
        });

        var otherSlot = (slot + 1) % ClusterHash.SlotCount;
        var otherRoutes = ReplicaRoutes(client)[otherSlot];
        if (policy == RespireReadFrom.Nearest)
            client.Core.Cluster!.NearestLatency = new ReadLatencySampler<Respire.Networking.RespireConnection>((connection, _) =>
                ValueTask.FromResult(connection.Port == oldReplica.Port || connection.Port == newReplica.Port ? 10L : 100L));
        await Assert.That(await client.WithReadFrom(policy)
            .Strings.GetStringAsync(key)).IsEqualTo("replicated");
        if (partial) await Assert.That(ReplicaRoutes(client)[otherSlot]).IsSameReferenceAs(otherRoutes);
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(["CLUSTER SLOTS"]);
        await Assert.That(oldReplica.ReceivedCommands).IsEquivalentTo(["READONLY", $"GET {key}"]);
        await Assert.That(target.ReceivedCommands).IsEquivalentTo(["CLUSTER SLOTS"]);
        await Assert.That(newReplica.ReceivedCommands).IsEquivalentTo(["READONLY", $"GET {key}"]);
    }

    [Test]
    public async Task ReadFrom_RefreshStartedForOneMovedSlotRepublishesOtherMovedSlots()
    {
        const string firstKey = "{moved-shared-a}:key";
        const string secondKey = "{moved-shared-b}:key";
        var firstSlot = ClusterHash.GetSlot(firstKey);
        var secondSlot = ClusterHash.GetSlot(secondKey);
        await using var replica = new FakeRespServer(2)
        {
            ReplyOverride = static (_, command) => command == "READONLY"
                ? FakeRespServer.OkReply
                : "$7\r\nreplica\r\n"u8.ToArray(),
        };
        byte[]? topology = null;
        await using var primary = new FakeRespServer(2)
        {
            ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
                ? Volatile.Read(ref topology)
                : "$7\r\nprimary\r\n"u8.ToArray(),
        };
        Volatile.Write(ref topology, ClusterTopology(primary.Port, replica.Port));
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", primary.Port) },
        });
        var router = client.Core.Cluster!;
        var owner = router.GetKnownSlotOwner(firstSlot)!;

        // MOVED corrections share unknown coverage until discovery publishes actual routes.
        router.SetSlotOwner(firstSlot, owner);
        router.SetSlotOwner(secondSlot, owner);
        var routes = ReplicaRoutes(client);
        await Assert.That(ReferenceEquals(routes[firstSlot], routes[secondSlot])).IsTrue();
        await Assert.That(routes[firstSlot]!.Nodes).IsEmpty();

        var strict = client.WithReadFrom(RespireReadFrom.Replica);
        await Assert.That(await strict.Strings.GetStringAsync(firstKey)).IsEqualTo("replica");
        // The refresh started for the first slot sent CLUSTER SLOTS, which republished the
        // second slot's replicas as well, so its read needs no refresh of its own.
        await Assert.That(await strict.Strings.GetStringAsync(secondKey)).IsEqualTo("replica");

        await Assert.That(primary.ReceivedCommands.Count(command => command == "CLUSTER SLOTS")).IsEqualTo(2);
        await Assert.That(primary.ReceivedCommands).DoesNotContain($"GET {firstKey}");
        await Assert.That(primary.ReceivedCommands).DoesNotContain($"GET {secondKey}");
    }

    [Test]
    public async Task ReadFrom_ConcurrentStrictReadsShareInFlightReplicaRefresh()
    {
        var key = "{replica-concurrent}:key";
        await using var replica = new FakeRespServer(8)
        {
            ReplyOverride = static (_, command) => command == "READONLY"
                ? FakeRespServer.OkReply
                : "$5\r\nfresh\r\n"u8.ToArray(),
        };
        var primaryReplies = new byte[][] { [], [] };
        await using var primary = new FakeRespServer(2, primaryReplies);
        primaryReplies[0] = ClusterTopologyWithoutReplicas(primary.Port);
        primaryReplies[1] = ClusterTopology(primary.Port, replica.Port);
        // Hold the refresh reply so the other reads arrive while it is still in flight.
        primary.DelayReply(1, 300);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", primary.Port) },
        });
        var strict = client.WithReadFrom(RespireReadFrom.Replica);

        var reads = Enumerable.Range(0, 8)
            .Select(_ => strict.Strings.GetStringAsync(key).AsTask())
            .ToArray();
        var values = await Task.WhenAll(reads);

        await Assert.That(values.All(value => value == "fresh")).IsTrue();
        await Assert.That(primary.ReceivedCommands.Count(command => command == "CLUSTER SLOTS")).IsEqualTo(2);
        await Assert.That(replica.ReceivedCommands.Count(command => command == $"GET {key}")).IsEqualTo(8);
    }

    [Test]
    public async Task ReadFrom_ThrottlesRefreshWhenEveryAdvertisedReplicaIsUnreachable()
    {
        var key = "{replica-down}:key";
        // Rejecting READONLY fails every replica handshake immediately, well inside the
        // refresh interval, so the second read must not trigger another CLUSTER SLOTS.
        await using var replica = new FakeRespServer(4)
        {
            ReplyOverride = static (_, _) => "-ERR replica unavailable\r\n"u8.ToArray(),
        };
        var unreachablePort = replica.Port;
        var primaryReplies = new byte[][] { [], [], "$7\r\nprimary\r\n"u8.ToArray() };
        await using var primary = new FakeRespServer(2, primaryReplies);
        primaryReplies[0] = ClusterTopology(primary.Port, unreachablePort);
        primaryReplies[1] = ClusterTopology(primary.Port, unreachablePort);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", primary.Port) },
        });
        var preferred = client.WithReadFrom(RespireReadFrom.ReplicaPreferred);

        await Assert.That(await preferred.Strings.GetStringAsync(key)).IsEqualTo("primary");
        await Assert.That(await preferred.Strings.GetStringAsync(key)).IsEqualTo("primary");

        // One refresh after the first exhaustion; the second read is inside the interval.
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(
            ["CLUSTER SLOTS", "CLUSTER SLOTS", $"GET {key}", $"GET {key}"]);
    }

    [Test]
    public async Task ReadFrom_EmptyReplicaRefreshThrottleIsPerSlotRange()
    {
        var lowKey = Enumerable.Range(0, 1000).Select(index => $"{{low{index}}}:key")
            .First(candidate => ClusterHash.GetSlot(candidate) <= 8191);
        var highKey = Enumerable.Range(0, 1000).Select(index => $"{{high{index}}}:key")
            .First(candidate => ClusterHash.GetSlot(candidate) > 8191);
        await using var replica = new FakeRespServer(FakeRespServer.OkReply, "$4\r\nhigh\r\n"u8.ToArray());
        var primaryReplies = new byte[][] { [], [], [] };
        await using var primary = new FakeRespServer(2, primaryReplies);
        primaryReplies[0] = SplitClusterTopology(primary.Port, highReplicaPort: null);
        primaryReplies[1] = SplitClusterTopology(primary.Port, highReplicaPort: null);
        primaryReplies[2] = SplitClusterTopology(primary.Port, replica.Port);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", primary.Port) },
        });
        var strict = client.WithReadFrom(RespireReadFrom.Replica);

        await Assert.That(async () => await strict.Strings.GetStringAsync(lowKey))
            .ThrowsExactly<RespireConnectionException>();
        // The low range's refresh must not throttle the high range's first refresh.
        await Assert.That(await strict.Strings.GetStringAsync(highKey)).IsEqualTo("high");
        await Assert.That(primary.ReceivedCommands.Count(command => command == "CLUSTER SLOTS")).IsEqualTo(3);
        await Assert.That(replica.ReceivedCommands).IsEquivalentTo(["READONLY", $"GET {highKey}"]);
    }

    [Test]
    [Arguments(RespireReadFrom.ReplicaPreferred)]
    [Arguments(RespireReadFrom.Nearest)]
    public async Task ReadFrom_StrictReplicaFailsOnAskWhilePreferredFollowsIt(RespireReadFrom policy)
    {
        var key = "{asked}:key";
        var slot = ClusterHash.GetSlot(key);
        await using var importing = new FakeRespServer(FakeRespServer.OkReply, "$9\r\nimporting\r\n"u8.ToArray());
        var ask = Encoding.ASCII.GetBytes($"-ASK {slot} 127.0.0.1:{importing.Port}\r\n");
        await using var replica = new FakeRespServer(2)
        {
            ReplyOverride = (_, command) => command == "READONLY" ? FakeRespServer.OkReply : ask,
        };
        var primaryReplies = new byte[][] { [] };
        await using var primary = new FakeRespServer(primaryReplies);
        primaryReplies[0] = ClusterTopology(primary.Port, replica.Port);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", primary.Port) },
        });

        var error = await Assert.That(async () => await client.WithReadFrom(RespireReadFrom.Replica)
            .Strings.GetStringAsync(key)).ThrowsExactly<RespireConnectionException>();
        await Assert.That(error!.Message).Contains("ASK");
        await Assert.That(importing.ReceivedCommands).IsEmpty();

        if (policy == RespireReadFrom.Nearest)
            client.Core.Cluster!.NearestLatency = new ReadLatencySampler<Respire.Networking.RespireConnection>((connection, _) =>
                ValueTask.FromResult(connection.Port == replica.Port ? 10L : 100L));
        await Assert.That(await client.WithReadFrom(policy)
            .Strings.GetStringAsync(key)).IsEqualTo("importing");
        await Assert.That(importing.ReceivedCommands).IsEquivalentTo(["ASKING", $"GET {key}"]);
    }

    [Test]
    public async Task ReadFrom_StreamingGetUsesReplica()
    {
        var key = "{streamed}:key";
        await using var replica = new FakeRespServer(FakeRespServer.OkReply, "$7\r\nreplica\r\n"u8.ToArray());
        var primaryReplies = new byte[][] { [] };
        await using var primary = new FakeRespServer(primaryReplies);
        primaryReplies[0] = ClusterTopology(primary.Port, replica.Port);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", primary.Port) },
        });

        string text;
        await using (var stream = await client.WithReadFrom(RespireReadFrom.Replica).Strings.GetStreamAsync(key))
        {
            using var reader = new StreamReader(stream!);
            text = await reader.ReadToEndAsync();
        }

        await Assert.That(text).IsEqualTo("replica");
        await Assert.That(replica.ReceivedCommands).IsEquivalentTo(["READONLY", $"GET {key}"]);
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(["CLUSTER SLOTS"]);
    }

    [Test]
    public async Task ReadFrom_UnknownCallerSuppliedNamesStayOnPrimary()
    {
        await using var replica = new FakeRespServer(2)
        {
            ReplyOverride = static (_, command) => command == "READONLY"
                ? FakeRespServer.OkReply
                : "$7\r\nreplica\r\n"u8.ToArray(),
        };
        var primaryReplies = new byte[][] { [], "$7\r\nprimary\r\n"u8.ToArray() };
        await using var primary = new FakeRespServer(2, primaryReplies);
        primaryReplies[0] = ClusterTopology(primary.Port, replica.Port);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", primary.Port) },
        });
        var view = client.WithReadFrom(RespireReadFrom.Replica);

        using (var implicitName = await view.ExecuteAsync("UNKNOWN_READ", "{implicit}:key"))
        {
            await Assert.That(implicitName.AsString()).IsEqualTo("primary");
        }
        using (var callerSupplied = await view.ExecuteAsync(RespireCommand.Create("UNKNOWN_READ"), "{caller}:key"))
        {
            await Assert.That(callerSupplied.AsString()).IsEqualTo("primary");
        }
        using (var catalog = await view.ExecuteAsync(RespireCommands.String.GET, ["{catalog}:key"]))
            await Assert.That(catalog.AsString()).IsEqualTo("replica");

        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(
            ["CLUSTER SLOTS", "UNKNOWN_READ {implicit}:key", "UNKNOWN_READ {caller}:key"]);
        await Assert.That(replica.ReceivedCommands).IsEquivalentTo(["READONLY", "GET {catalog}:key"]);
    }

    [Test]
    public async Task ReadFrom_UnknownInterpolatedCommandsStayOnPrimary()
    {
        await using var replica = new FakeRespServer(2)
        {
            ReplyOverride = static (_, command) => command == "READONLY"
                ? FakeRespServer.OkReply
                : "$7\r\nreplica\r\n"u8.ToArray(),
        };
        byte[]? topology = null;
        await using var primary = new FakeRespServer(2)
        {
            ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
                ? Volatile.Read(ref topology)
                : "$7\r\nprimary\r\n"u8.ToArray(),
        };
        Volatile.Write(ref topology, ClusterTopology(primary.Port, replica.Port));
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", primary.Port) },
        });
        var view = client.WithReadFrom(RespireReadFrom.Replica);
        var interpolatedKey = "{interpolated}:key";

        await view.ExecuteFireAndForgetAsync(RespireCommand.Create("UNKNOWN_READ"), ["{forget}:key"]);
        await view.ExecuteFireAndForgetAsync($"UNKNOWN_READ {"{forget-interpolated}:key"}");
        using (var interpolated = await view.ExecuteAsync($"UNKNOWN_READ {interpolatedKey}"))
        {
            await Assert.That(interpolated.AsString()).IsEqualTo("primary");
        }
        await view.ExecuteFireAndForgetAsync(RespireCommands.String.GET, ["{catalog-forget}:key"]);

        // The interpolated read follows the fire-and-forget sends on the same primary pipeline.
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(
            ["CLUSTER SLOTS", "UNKNOWN_READ {forget}:key", "UNKNOWN_READ {forget-interpolated}:key", "UNKNOWN_READ {interpolated}:key"]);
        await Assert.That(replica.ReceivedCommands).IsEquivalentTo(["READONLY", "GET {catalog-forget}:key"]);
    }

    [Test]
    public async Task ReadFrom_RevalidatesAgedReplicaRoutesInBackground()
    {
        var key = "{revalidated}:key";
        await using var oldReplica = new FakeRespServer(2)
        {
            ReplyOverride = static (_, command) => command == "READONLY"
                ? FakeRespServer.OkReply
                : "$3\r\nold\r\n"u8.ToArray(),
        };
        await using var newReplica = new FakeRespServer(2)
        {
            ReplyOverride = static (_, command) => command == "READONLY"
                ? FakeRespServer.OkReply
                : "$3\r\nnew\r\n"u8.ToArray(),
        };
        byte[]? topology = null;
        await using var primary = new FakeRespServer(2)
        {
            ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
                ? Volatile.Read(ref topology)
                : "$7\r\nprimary\r\n"u8.ToArray(),
        };
        Volatile.Write(ref topology, ClusterTopology(primary.Port, oldReplica.Port));
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", primary.Port) },
            ReplicaRouteRevalidationInterval = TimeSpan.FromMinutes(1),
        });
        var strict = client.WithReadFrom(RespireReadFrom.Replica);
        await Assert.That(await strict.Strings.GetStringAsync(key)).IsEqualTo("old");
        var oldNode = ReplicaRoutes(client)[ClusterHash.GetSlot(key)]!.Nodes[0];

        // A failover changes the replica role without any redirect reaching this client.
        Volatile.Write(ref topology, ClusterTopology(primary.Port, newReplica.Port));
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.SuppressReply = command =>
        {
            if (command != "CLUSTER SLOTS") return false;
            refreshStarted.TrySetResult();
            return true;
        };
        ReplicaRoutes(client)[ClusterHash.GetSlot(key)]!.MarkValidated(TimeSpan.Zero);
        // Hold publication until the read completes, so retirement cannot reroute it first.
        await Assert.That(await strict.Strings.GetStringAsync(key)).IsEqualTo("old");
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        primary.SuppressReply = null;
        await primary.SendRawAsync(topology, primary.ReceivedConnectionIds[^1]);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        string? value;
        do
        {
            value = await strict.Strings.GetStringAsync(key);
            if (value == "new") break;
            await Task.Delay(20);
        }
        while (DateTime.UtcNow < deadline);

        await Assert.That(value).IsEqualTo("new");
        await Assert.That(primary.ReceivedCommands.Count(command => command == "CLUSTER SLOTS")).IsGreaterThanOrEqualTo(2);
        await Assert.That(primary.ReceivedCommands).DoesNotContain($"GET {key}");
        var retirementDeadline = DateTime.UtcNow.AddSeconds(10);
        while (!oldNode.IsRetired && DateTime.UtcNow < retirementDeadline) await Task.Delay(10);
        await Assert.That(oldNode.IsRetired).IsTrue();
    }

    [Test]
    public async Task ReadFrom_RecentlyValidatedReplicaRoutesDoNotRefresh()
    {
        var key = "{validated}:key";
        await using var replica = new FakeRespServer(2)
        {
            ReplyOverride = static (_, command) => command == "READONLY"
                ? FakeRespServer.OkReply
                : "$7\r\nreplica\r\n"u8.ToArray(),
        };
        var primaryReplies = new byte[][] { [] };
        await using var primary = new FakeRespServer(primaryReplies);
        primaryReplies[0] = ClusterTopology(primary.Port, replica.Port);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", primary.Port) },
        });
        var strict = client.WithReadFrom(RespireReadFrom.Replica);

        for (var read = 0; read < 3; read++)
        {
            await Assert.That(await strict.Strings.GetStringAsync(key)).IsEqualTo("replica");
        }

        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(["CLUSTER SLOTS"]);
    }

    [Test]
    [Arguments(RespireErrorCodes.Loading)]
    [Arguments(RespireErrorCodes.MasterDown)]
    [Arguments(RespireErrorCodes.ClusterDown)]
    public async Task ReadFrom_PreferredPoliciesRetryOnOtherRoleAfterUnavailableReply(string code)
    {
        var unavailable = Encoding.ASCII.GetBytes($"-{code} unavailable for this test\r\n");
        var key = "{unavailable}:key";
        await using var replica = new FakeRespServer(2)
        {
            ReplyOverride = (_, command) => command == "READONLY" ? FakeRespServer.OkReply : unavailable,
        };
        byte[]? topology = null;
        await using var primary = new FakeRespServer(2)
        {
            ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
                ? Volatile.Read(ref topology)
                : "$7\r\nprimary\r\n"u8.ToArray(),
        };
        Volatile.Write(ref topology, ClusterTopology(primary.Port, replica.Port));
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", primary.Port) },
        });

        await Assert.That(await client.WithReadFrom(RespireReadFrom.ReplicaPreferred)
            .Strings.GetStringAsync(key)).IsEqualTo("primary");
        await using (var stream = await client.WithReadFrom(RespireReadFrom.ReplicaPreferred).Strings.GetStreamAsync(key))
        {
            using var reader = new StreamReader(stream!);
            await Assert.That(await reader.ReadToEndAsync()).IsEqualTo("primary");
        }

        // Strict Replica never switches roles; the server error reaches the caller.
        var error = await Assert.That(async () => await client.WithReadFrom(RespireReadFrom.Replica)
            .Strings.GetStringAsync(key)).ThrowsExactly<RespireServerException>();
        await Assert.That(error!.Code).IsEqualTo(code);

        await Assert.That(replica.ReceivedCommands.Count(command => command == $"GET {key}")).IsEqualTo(3);
        await Assert.That(primary.ReceivedCommands.Count(command => command == $"GET {key}")).IsEqualTo(2);
    }

    [Test]
    public async Task ReadFrom_PrimaryPreferredRetriesReplicaWhenPrimaryIsLoading()
    {
        var key = "{primary-loading}:key";
        await using var replica = new FakeRespServer(2)
        {
            ReplyOverride = static (_, command) => command == "READONLY"
                ? FakeRespServer.OkReply
                : "$7\r\nreplica\r\n"u8.ToArray(),
        };
        byte[]? topology = null;
        await using var primary = new FakeRespServer(2)
        {
            ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
                ? Volatile.Read(ref topology)
                : "-LOADING Redis is loading the dataset in memory\r\n"u8.ToArray(),
        };
        Volatile.Write(ref topology, ClusterTopology(primary.Port, replica.Port));
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", primary.Port) },
        });

        await Assert.That(await client.WithReadFrom(RespireReadFrom.PrimaryPreferred)
            .Strings.GetStringAsync(key)).IsEqualTo("replica");
        // Primary is the default and never switches roles.
        var error = await Assert.That(async () => await client.Strings.GetStringAsync(key))
            .ThrowsExactly<RespireServerException>();
        await Assert.That(error!.Code).IsEqualTo(RespireErrorCodes.Loading);
        await Assert.That(replica.ReceivedCommands).IsEquivalentTo(["READONLY", $"GET {key}"]);
    }

    [Test]
    public async Task ReadFrom_BlockingReplicaPreferredRetriesPrimaryWhenReplicaIsLoading()
    {
        var key = "{blocking-loading}:stream";
        await using var replica = new FakeRespServer(3)
        {
            ReplyOverride = static (_, command) => command.StartsWith("XREAD ", StringComparison.Ordinal)
                ? "-LOADING Redis is loading the dataset in memory\r\n"u8.ToArray()
                : FakeRespServer.OkReply,
        };
        byte[]? topology = null;
        await using var primary = new FakeRespServer(3)
        {
            ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
                ? Volatile.Read(ref topology)
                : "*0\r\n"u8.ToArray(),
        };
        Volatile.Write(ref topology, ClusterTopology(primary.Port, replica.Port));
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", primary.Port) },
        });

        var entries = await client.WithReadFrom(RespireReadFrom.ReplicaPreferred).Streams.ReadAsync(
            key, waitFor: TimeSpan.FromMilliseconds(1));

        await Assert.That(entries).IsEmpty();
        await Assert.That(replica.ReceivedCommands.Count(command => command.StartsWith("XREAD ", StringComparison.Ordinal)))
            .IsEqualTo(1);
        await Assert.That(primary.ReceivedCommands.Count(command => command.StartsWith("XREAD ", StringComparison.Ordinal)))
            .IsEqualTo(1);
    }

    [Test]
    public async Task ReadFrom_PreferredFallbackStillAppliesWithNoRedirect()
    {
        var key = "{no-redirect-loading}:key";
        await using var replica = new FakeRespServer(3)
        {
            ReplyOverride = static (_, command) => command == "READONLY"
                ? FakeRespServer.OkReply
                : "-LOADING Redis is loading the dataset in memory\r\n"u8.ToArray(),
        };
        byte[]? topology = null;
        await using var primary = new FakeRespServer(3)
        {
            ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
                ? Volatile.Read(ref topology)
                : command.StartsWith("XREAD ", StringComparison.Ordinal)
                    ? "*0\r\n"u8.ToArray()
                    : "$7\r\nprimary\r\n"u8.ToArray(),
        };
        Volatile.Write(ref topology, ClusterTopology(primary.Port, replica.Port));
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", primary.Port) },
        });
        var preferred = client.WithReadFrom(RespireReadFrom.ReplicaPreferred);

        // NoRedirect only surfaces MOVED and ASK; it does not disable the read policy's role fallback.
        using (var multiplexed = await preferred.ExecuteAsync(
                   RespireCommands.String.GET, [key], RespireCommandFlags.NoRedirect))
        {
            await Assert.That(multiplexed.AsString()).IsEqualTo("primary");
        }

        using (var blocking = await preferred.ExecuteAsync(
                   RespireCommands.Stream.XREAD, ["BLOCK", 1, "STREAMS", key, "0"], RespireCommandFlags.NoRedirect))
        {
            await Assert.That(blocking.IsError).IsFalse();
        }

        await Assert.That(replica.ReceivedCommands).Contains($"GET {key}");
        await Assert.That(replica.ReceivedCommands.Count(command => command.StartsWith("XREAD ", StringComparison.Ordinal)))
            .IsEqualTo(1);
        await Assert.That(primary.ReceivedCommands).Contains($"GET {key}");
        await Assert.That(primary.ReceivedCommands.Count(command => command.StartsWith("XREAD ", StringComparison.Ordinal)))
            .IsEqualTo(1);
    }

    [Test]
    public async Task ReadFrom_BlockingPreferredReadDoesNotSwitchRolesAfterFollowingAsk()
    {
        var key = "{blocking-asked-loading}:stream";
        var slot = ClusterHash.GetSlot(key);
        await using var importing = new FakeRespServer(3)
        {
            ReplyOverride = static (_, command) => command.StartsWith("XREAD ", StringComparison.Ordinal)
                ? "-LOADING Redis is loading the dataset in memory\r\n"u8.ToArray()
                : FakeRespServer.OkReply,
        };
        var ask = Encoding.ASCII.GetBytes($"-ASK {slot} 127.0.0.1:{importing.Port}\r\n");
        await using var replica = new FakeRespServer(3)
        {
            ReplyOverride = static (_, command) => command.StartsWith("XREAD ", StringComparison.Ordinal)
                ? "*0\r\n"u8.ToArray()
                : FakeRespServer.OkReply,
        };
        byte[]? topology = null;
        await using var primary = new FakeRespServer(3)
        {
            ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
                ? Volatile.Read(ref topology)
                : ask,
        };
        Volatile.Write(ref topology, ClusterTopology(primary.Port, replica.Port));
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", primary.Port) },
        });

        // During a migration only the importing node is authoritative, so its LOADING reply
        // reaches the caller instead of rerouting the read to a replica of the old owner.
        var error = await Assert.That(async () => await client.WithReadFrom(RespireReadFrom.PrimaryPreferred)
                .Streams.ReadAsync(key, waitFor: TimeSpan.FromMilliseconds(1)))
            .ThrowsExactly<RespireServerException>();
        await Assert.That(error!.Code).IsEqualTo(RespireErrorCodes.Loading);
        await Assert.That(importing.ReceivedCommands).Contains("ASKING");
        await Assert.That(importing.ReceivedCommands.Count(command => command.StartsWith("XREAD ", StringComparison.Ordinal)))
            .IsEqualTo(1);
        await Assert.That(replica.ReceivedCommands.Any(command => command.StartsWith("XREAD ", StringComparison.Ordinal)))
            .IsFalse();
    }

    [Test]
    public async Task ReadFrom_StrictReplicaBlockingNoRedirectSurfacesAskReply()
    {
        var key = "{blocking-ask}:stream";
        var slot = ClusterHash.GetSlot(key);
        var ask = Encoding.ASCII.GetBytes($"-ASK {slot} 127.0.0.1:1\r\n");
        await using var replica = new FakeRespServer(3)
        {
            ReplyOverride = (_, command) => command.StartsWith("XREAD ", StringComparison.Ordinal)
                ? ask
                : FakeRespServer.OkReply,
        };
        var primaryReplies = new byte[][] { [] };
        await using var primary = new FakeRespServer(primaryReplies);
        primaryReplies[0] = ClusterTopology(primary.Port, replica.Port);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", primary.Port) },
        });
        var strict = client.WithReadFrom(RespireReadFrom.Replica);

        // NoRedirect callers handle redirects themselves, so they receive the ASK reply itself.
        var raw = await Assert.That(async () => await strict.ExecuteAsync(
                RespireCommands.Stream.XREAD, ["BLOCK", 1, "STREAMS", key, "0"], RespireCommandFlags.NoRedirect))
            .ThrowsExactly<RespireServerException>();
        await Assert.That(raw!.Code).IsEqualTo(RespireErrorCodes.Ask);

        // Without NoRedirect a strict read still refuses to follow ASK to a primary.
        var routed = await Assert.That(async () => await strict.ExecuteAsync(
                RespireCommands.Stream.XREAD, ["BLOCK", 1, "STREAMS", key, "0"]))
            .ThrowsExactly<RespireConnectionException>();
        await Assert.That(routed!.Message).Contains("ASK");
    }

    [Test]
    [Arguments(RespireReadFrom.Replica)]
    [Arguments(RespireReadFrom.ReplicaPreferred)]
    public async Task ReadFrom_MovedWithFailedReplicaRefresh(RespireReadFrom readFrom)
    {
        var key = "{moved-refresh-fails}:key";
        var slot = ClusterHash.GetSlot(key);
        await using var target = new FakeRespServer(2)
        {
            ReplyOverride = static (_, command) => command == "CLUSTER SLOTS"
                ? "-ERR topology unavailable\r\n"u8.ToArray()
                : "$6\r\ntarget\r\n"u8.ToArray(),
        };
        var moved = Encoding.ASCII.GetBytes($"-MOVED {slot} 127.0.0.1:{target.Port}\r\n");
        await using var oldReplica = new FakeRespServer(2)
        {
            ReplyOverride = (_, command) => command == "READONLY" ? FakeRespServer.OkReply : moved,
        };
        var primaryReplies = new byte[][] { [] };
        await using var primary = new FakeRespServer(primaryReplies);
        primaryReplies[0] = ClusterTopology(primary.Port, oldReplica.Port);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", primary.Port) },
        });
        var view = client.WithReadFrom(readFrom);

        if (readFrom == RespireReadFrom.Replica)
        {
            // Without fresh routes a strict read has no replica to use, so it fails closed.
            var error = await Assert.That(async () => await view.Strings.GetStringAsync(key))
                .ThrowsExactly<RespireConnectionException>();
            await Assert.That(error!.Message).Contains("after a redirect");
            await Assert.That(target.ReceivedCommands).DoesNotContain($"GET {key}");
        }
        else
        {
            // A preferred read uses the redirected primary instead.
            await Assert.That(await view.Strings.GetStringAsync(key)).IsEqualTo("target");
            await Assert.That(target.ReceivedCommands).Contains($"GET {key}");
        }

        await Assert.That(oldReplica.ReceivedCommands).Contains($"GET {key}");
        await Assert.That(target.ReceivedCommands).Contains("CLUSTER SLOTS");
    }

    private readonly record struct ReplicaRouteView(ClusterRoutingSnapshot Snapshot)
    {
        public ClusterReplicaSet? this[int slot] => Snapshot[slot].Replicas;
    }

    private static ReplicaRouteView ReplicaRoutes(RespireClient client)
        => new(client.Core.Cluster!.RoutingSnapshot);

    private static void SetReplicaRoutes(RespireClient client, int slot, ClusterReplicaSet routes)
    {
        var router = client.Core.Cluster!;
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        lock (router.NodeStateGate)
        {
            typeof(ClusterRouter).GetMethod("SetReplicaRoutesLocked", flags)!.Invoke(router, [slot, routes]);
            typeof(ClusterRouter).GetMethod("PublishTopologyLocked", flags)!.Invoke(router, null);
        }
    }

    [Test]
    [Arguments("GET", true)]
    [Arguments("get", true)]
    [Arguments("OBJECT ENCODING", true)]
    [Arguments("XINFO STREAM", true)]
    [Arguments("MEMORY USAGE", true)]
    [Arguments("SET", false)]
    [Arguments("NOT-A-COMMAND", false)]
    public async Task ReadOnlyMetadata_MatchesCanonicalMultiWordOperationNames(string operation, bool expected)
        => await Assert.That(RespireCommands.All.ToArray().Any(command => command.Name.Equals(operation, StringComparison.OrdinalIgnoreCase)
                && command.ReadKind != ReadCommandKind.None)).IsEqualTo(expected);

    private static byte[] SplitClusterTopology(int primaryPort, int? highReplicaPort)
    {
        var primaryNode = $"*3\r\n$9\r\n127.0.0.1\r\n:{primaryPort}\r\n$7\r\nprimary\r\n";
        var highRange = highReplicaPort is { } replicaPort
            ? $"*4\r\n:8192\r\n:16383\r\n{primaryNode}*3\r\n$9\r\n127.0.0.1\r\n:{replicaPort}\r\n$10\r\nreplica-id\r\n"
            : $"*3\r\n:8192\r\n:16383\r\n{primaryNode}";
        return Encoding.ASCII.GetBytes($"*2\r\n*3\r\n:0\r\n:8191\r\n{primaryNode}{highRange}");
    }

    private static byte[] ClusterTopology(int primaryPort, int replicaPort)
        => Encoding.ASCII.GetBytes(
            $"*1\r\n*4\r\n:0\r\n:16383\r\n" +
            $"*3\r\n$9\r\n127.0.0.1\r\n:{primaryPort}\r\n$7\r\nprimary\r\n" +
            $"*3\r\n$9\r\n127.0.0.1\r\n:{replicaPort}\r\n$10\r\nreplica-id\r\n");

    private static byte[] ClusterTopologyWithoutReplicas(int primaryPort)
        => Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:0\r\n:16383\r\n" +
            $"*2\r\n$9\r\n127.0.0.1\r\n:{primaryPort}\r\n");

    [Test]
    public async Task ReadFrom_RoutesReadOnlyScriptCommandsToReplica()
    {
        var primaryReplies = new byte[][]
        {
            [],
            "$1\r\n1\r\n"u8.ToArray(),
            "$1\r\n2\r\n"u8.ToArray(),
            "$1\r\n3\r\n"u8.ToArray(),
        };
        await using var primary = new FakeRespServer(primaryReplies);
        await using var replica = new FakeRespServer(
            FakeRespServer.OkReply,
            "$1\r\n1\r\n"u8.ToArray(),
            "$1\r\n2\r\n"u8.ToArray(),
            "$1\r\n3\r\n"u8.ToArray());
        primaryReplies[0] = Encoding.ASCII.GetBytes(
            $"*1\r\n*4\r\n:0\r\n:16383\r\n" +
            $"*3\r\n$9\r\n127.0.0.1\r\n:{primary.Port}\r\n$7\r\nprimary\r\n" +
            $"*3\r\n$9\r\n127.0.0.1\r\n:{replica.Port}\r\n$10\r\nreplica-id\r\n");

        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", primary.Port) },
        });
        var readView = client.WithReadFrom(RespireReadFrom.Replica);
        using var eval = await readView.ExecuteAsync(RespireCommands.Scripting.EVAL_RO, ["return 1", "1", "eval-key"]);
        using var evalSha = await readView.ExecuteAsync(RespireCommands.Scripting.EVALSHA_RO, ["digest", "1", "sha-key"]);
        using var fcall = await readView.ExecuteAsync(RespireCommands.Scripting.FCALL_RO, ["read", "1", "function-key"]);

        await Assert.That(eval.AsString()).IsEqualTo("1");
        await Assert.That(evalSha.AsString()).IsEqualTo("2");
        await Assert.That(fcall.AsString()).IsEqualTo("3");
        await Assert.That(replica.ReceivedCommands).IsEquivalentTo(
            ["READONLY", "EVAL_RO return 1 1 eval-key", "EVALSHA_RO digest 1 sha-key", "FCALL_RO read 1 function-key"]);
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(["CLUSTER SLOTS"]);
    }

    [Test]
    public async Task ReadFrom_KeepsTransactionsOnPrimary()
    {
        var primaryReplies = new byte[][]
        {
            [],
            FakeRespServer.OkReply,
            "+QUEUED\r\n"u8.ToArray(),
            "+QUEUED\r\n"u8.ToArray(),
            "*2\r\n+OK\r\n*2\r\n$1\r\n0\r\n*0\r\n"u8.ToArray(),
        };
        await using var primary = new FakeRespServer(primaryReplies);
        await using var replica = new FakeRespServer(FakeRespServer.OkReply);
        primaryReplies[0] = Encoding.ASCII.GetBytes(
            $"*1\r\n*4\r\n:0\r\n:16383\r\n" +
            $"*3\r\n$9\r\n127.0.0.1\r\n:{primary.Port}\r\n$7\r\nprimary\r\n" +
            $"*3\r\n$9\r\n127.0.0.1\r\n:{replica.Port}\r\n$10\r\nreplica-id\r\n");
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", primary.Port) },
        });

        await using var transaction = client.WithReadFrom(RespireReadFrom.Replica).CreateTransaction();
        var pending = transaction.Strings.Set("{tenant}key", "value");
        var page = transaction.Hashes.ScanFieldsPage("{tenant}key");
        await transaction.CommitAsync();

        await Assert.That(pending.Result).IsTrue();
        await Assert.That(page.Result.IsComplete).IsTrue();
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(
            ["CLUSTER SLOTS", "MULTI", "SET {tenant}key value", "HSCAN {tenant}key 0 NOVALUES", "EXEC"]);
        await Assert.That(replica.ReceivedCommands).IsEmpty();
    }

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
    public async Task MGet_RejectsCrossSlotKeysLocallyWithoutClientCache()
    {
        await using var server = new FakeRespServer(
            "-CROSSSLOT Keys in request don't hash to the same slot\r\n"u8.ToArray());
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
        });

        var error = await Assert.That(async () =>
                await client.Strings.GetManyAsync("{first}key", "{second}key"))
            .ThrowsExactly<RespireServerException>();

        await Assert.That(error!.Code).IsEqualTo("CROSSSLOT");
        await Assert.That(error.CommandName).IsEqualTo("MGET");
        await Assert.That(server.ReceivedCommands).IsEmpty();
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
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
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
    public async Task MovedRedirect_ReplaysSeekableStreamFromOriginalPosition()
    {
        const string key = "streamed-key";
        var slot = ClusterHash.GetSlot(key);
        var payload = new byte[] { 10, 11, 12, 13, 14, 15 };
        await using var target = new FakeRespServer(2, FakeRespServer.OkReply);
        await using var seed = CreateRedirectingSeed(slot, target, RespireErrorCodes.Moved);
        await using var client = await CreateClusterClientAsync(seed);
        await using var stream = new MemoryStream(payload) { Position = 1 };

        await Assert.That(await client.Strings.SetAsync(key, stream, 4)).IsTrue();

        await Assert.That(target.ReceivedCommands).Count().IsEqualTo(1);
        await Assert.That(target.ReceivedArguments[^1][2]).IsEquivalentTo(new byte[] { 11, 12, 13, 14 });
        await Assert.That(stream.Position).IsEqualTo(5);
    }

    [Test]
    public async Task MovedRedirect_ResetFailurePreservesServerRedirect()
    {
        const string key = "reset-failure-key";
        var slot = ClusterHash.GetSlot(key);
        await using var target = new FakeRespServer(2, FakeRespServer.OkReply);
        await using var seed = CreateRedirectingSeed(slot, target, RespireErrorCodes.Moved);
        await using var client = await CreateClusterClientAsync(seed);
        await using var stream = new ResetFailingMemoryStream([1, 2, 3]);

        var error = await Assert.That(async () => await client.Strings.SetAsync(key, stream, 3))
            .Throws<RespireServerException>();

        await Assert.That(error!.Code).IsEqualTo(RespireErrorCodes.Moved);
        await Assert.That(target.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task MovedRedirect_ReplaysStreamAcrossMultipleHops()
    {
        const string key = "multi-hop-stream-key";
        var slot = ClusterHash.GetSlot(key);
        var payload = new byte[] { 4, 8, 12, 16 };
        await using var target = new FakeRespServer(2, FakeRespServer.OkReply);
        await using var middle = new FakeRespServer(2, FakeRespServer.OkReply);
        middle.ReplyOverride = (_, command) => command.StartsWith("SET ", StringComparison.Ordinal)
            ? Encoding.ASCII.GetBytes($"-MOVED {slot} 127.0.0.1:{target.Port}\r\n")
            : null;
        await using var seed = CreateRedirectingSeed(slot, middle, RespireErrorCodes.Moved);
        await using var client = await CreateClusterClientAsync(seed);
        await using var stream = new MemoryStream(payload);

        await Assert.That(await client.Strings.SetAsync(key, stream, payload.Length)).IsTrue();

        await Assert.That(target.ReceivedCommands).Count().IsEqualTo(1);
        await Assert.That(target.ReceivedArguments[^1][2]).IsEquivalentTo(payload);
    }

    [Test]
    public async Task ReplayResetDiscardsRetirementPrefixWrapper()
    {
        await using var source = new MemoryStream([10, 20, 30, 40]) { Position = 1 };
        var command = new StreamedSetCommand("prefix-reset-key", source, 3, default, SetWhen.Always);
        command.RestoreSourcePrefixForRetry([99]);

        command.ResetSourceForReplay();
        var buffer = new byte[3];
        var read = await command.SourceStream!.ReadAsync(buffer);

        await Assert.That(read).IsEqualTo(3);
        await Assert.That(buffer).IsEquivalentTo(new byte[] { 20, 30, 40 });
    }

    [Test]
    public async Task AskRedirect_ReadFailureDiscardsLeaseWithoutConsumingAskingState()
    {
        const string key = "asking-read-failure-key";
        var slot = ClusterHash.GetSlot(key);
        await using var target = new FakeRespServer(2, FakeRespServer.OkReply, FakeRespServer.OkReply);
        await using var seed = CreateRedirectingSeed(slot, target, RespireErrorCodes.Ask, maxConnections: 3);
        await using var client = await CreateClusterClientAsync(seed);
        await using var stream = new ThrowOnceStream([1, 2, 3]);

        await Assert.That(async () => await client.Strings.SetAsync(key, stream, 3))
            .Throws<IOException>();
        await seed.PeerClosed.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(target.ReceivedCommands).IsEmpty();
        await Assert.That(await client.Strings.SetAsync(key, stream, 3)).IsTrue();

        await Assert.That(target.ReceivedCommands).Count().IsEqualTo(2);
        await Assert.That(target.ReceivedCommands[0]).IsEqualTo("ASKING");
        await Assert.That(target.ReceivedCommands[1]).StartsWith($"SET {key}");
    }

    [Test]
    public async Task MovedRedirect_ReplaysReadOnlySequencePayload()
    {
        const string key = "sequence-key";
        var slot = ClusterHash.GetSlot(key);
        var payload = new byte[] { 2, 4, 6, 8 };
        await using var target = new FakeRespServer(2, FakeRespServer.OkReply);
        await using var seed = CreateRedirectingSeed(slot, target, RespireErrorCodes.Moved);
        await using var client = await CreateClusterClientAsync(seed);

        await Assert.That(await client.Strings.SetAsync(key, new ReadOnlySequence<byte>(payload))).IsTrue();

        await Assert.That(target.ReceivedCommands).Count().IsEqualTo(1);
        await Assert.That(target.ReceivedArguments[^1][2]).IsEquivalentTo(payload);
    }

    [Test]
    public async Task AskRedirect_SendsAskingBeforeReplayableStream()
    {
        const string key = "asking-stream-key";
        var slot = ClusterHash.GetSlot(key);
        var payload = new byte[] { 3, 5, 7, 9 };
        await using var target = new FakeRespServer(2, FakeRespServer.OkReply, FakeRespServer.OkReply);
        await using var seed = CreateRedirectingSeed(slot, target, RespireErrorCodes.Ask);
        await using var client = await CreateClusterClientAsync(seed);
        await using var stream = new MemoryStream(payload);

        await Assert.That(await client.Strings.SetAsync(key, stream, payload.Length)).IsTrue();

        await Assert.That(target.ReceivedCommands[0]).IsEqualTo("ASKING");
        await Assert.That(target.ReceivedCommands[1]).StartsWith($"SET {key}");
        await Assert.That(target.ReceivedArguments[^1][2]).IsEquivalentTo(payload);
        await Assert.That(stream.Position).IsEqualTo(payload.Length);
    }

    [Test]
    public async Task AskRedirect_SendsAskingBeforeReadOnlySequencePayload()
    {
        const string key = "asking-sequence-key";
        var slot = ClusterHash.GetSlot(key);
        var payload = new byte[] { 2, 3, 5, 7 };
        await using var target = new FakeRespServer(2, FakeRespServer.OkReply, FakeRespServer.OkReply);
        await using var seed = CreateRedirectingSeed(slot, target, RespireErrorCodes.Ask);
        await using var client = await CreateClusterClientAsync(seed);

        await Assert.That(await client.Strings.SetAsync(key, new ReadOnlySequence<byte>(payload))).IsTrue();

        await Assert.That(target.ReceivedCommands[0]).IsEqualTo("ASKING");
        await Assert.That(target.ReceivedCommands[1]).StartsWith($"SET {key}");
        await Assert.That(target.ReceivedArguments[^1][2]).IsEquivalentTo(payload);
    }

    [Test]
    public async Task MovedRedirect_DoesNotReplayNonSeekableStream()
    {
        const string key = "nonseekable-key";
        var slot = ClusterHash.GetSlot(key);
        var payload = new byte[] { 1, 3, 5, 7 };
        await using var target = new FakeRespServer(2, FakeRespServer.OkReply);
        await using var seed = CreateRedirectingSeed(slot, target, RespireErrorCodes.Moved);
        await using var client = await CreateClusterClientAsync(seed);
        await using var stream = new NonSeekableMemoryStream(payload);

        var error = await Assert.That(async () => await client.Strings.SetAsync(key, stream, payload.Length))
            .Throws<RespireServerException>();

        await Assert.That(error!.Code).IsEqualTo(RespireErrorCodes.Moved);
        await Assert.That(target.ReceivedCommands).IsEmpty();
        await Assert.That(stream.BytesRead).IsEqualTo(payload.Length);
    }

    [Test]
    public async Task FireAndForget_MovedRedirect_IsFollowedAndSlotIsCached()
    {
        await using var target = new FakeRespServer(
            FakeRespServer.OkReply,
            FakeRespServer.OkReply);
        var slot = ClusterHash.GetSlot("key");
        await using var seed = new FakeRespServer(
            "*0\r\n"u8.ToArray(),
            Encoding.ASCII.GetBytes($"-MOVED {slot} 127.0.0.1:{target.Port}\r\n"));
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });

        await client.ExecuteFireAndForgetAsync(RespireCommands.String.SET, "key", "first");
        await client.ExecuteFireAndForgetAsync(RespireCommands.String.SET, "key", "second");
        await WaitForCommandsAsync(target, 2);

        await Assert.That(seed.ReceivedCommands)
            .IsEquivalentTo(["CLUSTER SLOTS", "SET key first"]);
        await Assert.That(target.ReceivedCommands)
            .IsEquivalentTo(["SET key first", "SET key second"]);
    }

    [Test]
    public async Task FireAndForget_ShutdownCompletesWhenClusterNodeClosesWithoutReply()
    {
        await using var server = new FakeRespServer("*0\r\n"u8.ToArray())
        {
            CloseConnectionAfterCommand = 2,
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
        });

        await client.ExecuteFireAndForgetAsync(RespireCommands.Server.SHUTDOWN)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        await WaitForCommandsAsync(server, 2);

        await Assert.That(server.ReceivedCommands)
            .IsEquivalentTo(["CLUSTER SLOTS", "SHUTDOWN"]);
    }

    [Test]
    public async Task CatalogNoRedirect_SurfacesMovedRedirect()
    {
        await using var target = new FakeRespServer("$5\r\nvalue\r\n"u8.ToArray());
        var slot = ClusterHash.GetSlot("key");
        await using var seed = new FakeRespServer(
            "*0\r\n"u8.ToArray(),
            Encoding.ASCII.GetBytes($"-MOVED {slot} 127.0.0.1:{target.Port}\r\n"));
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });

        var error = await Assert.That(async () =>
                await client.ExecuteAsync(
                    RespireCommands.String.GET,
                    ["key"],
                    RespireCommandFlags.NoRedirect))
            .Throws<RespireServerException>();

        await Assert.That(error!.Code).IsEqualTo("MOVED");
        await Assert.That(seed.ReceivedCommands).IsEquivalentTo(["CLUSTER SLOTS", "GET key"]);
        await Assert.That(target.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task RawNoRedirect_SurfacesMovedRedirect()
    {
        await using var target = new FakeRespServer("$5\r\nvalue\r\n"u8.ToArray());
        var slot = ClusterHash.GetSlot("key");
        await using var seed = new FakeRespServer(
            "*0\r\n"u8.ToArray(),
            Encoding.ASCII.GetBytes($"-MOVED {slot} 127.0.0.1:{target.Port}\r\n"));
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });

        var error = await Assert.That(async () =>
                await client.ExecuteAsync("GET", ["key"], RespireCommandFlags.NoRedirect))
            .Throws<RespireServerException>();

        await Assert.That(error!.Code).IsEqualTo("MOVED");
        await Assert.That(seed.ReceivedCommands).IsEquivalentTo(["CLUSTER SLOTS", "GET key"]);
        await Assert.That(target.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task InterpolatedNoRedirect_SurfacesMovedRedirect()
    {
        await using var target = new FakeRespServer("$5\r\nvalue\r\n"u8.ToArray());
        var slot = ClusterHash.GetSlot("key");
        await using var seed = new FakeRespServer(
            "*0\r\n"u8.ToArray(),
            Encoding.ASCII.GetBytes($"-MOVED {slot} 127.0.0.1:{target.Port}\r\n"));
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });
        RespireKey key = "key";

        var error = await Assert.That(async () =>
                await client.ExecuteAsync($"GET {key}", RespireCommandFlags.NoRedirect))
            .Throws<RespireServerException>();

        await Assert.That(error!.Code).IsEqualTo("MOVED");
        await Assert.That(seed.ReceivedCommands).IsEquivalentTo(["CLUSTER SLOTS", "GET key"]);
        await Assert.That(target.ReceivedCommands).IsEmpty();
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
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });
        var reconnecting = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += change =>
        {
            if (change.State == RespireConnectionState.Reconnecting)
            {
                reconnecting.TrySetResult();
            }
        };

        var value = await client.GetStringAsync("key");
        var second = await client.GetStringAsync("key");

        await Assert.That(value).IsEqualTo("value");
        await Assert.That(second).IsEqualTo("value");
        await Assert.That(seed.ReceivedCommands).Count().IsEqualTo(3);
        await Assert.That(target.ReceivedCommands[0]).IsEqualTo("ASKING");
        await Assert.That(target.ReceivedCommands[1]).IsEqualTo("GET key");
        await Assert.That(target.ReceivedCommands[2]).IsEqualTo("ASKING");
        await Assert.That(target.ReceivedCommands[3]).IsEqualTo("GET key");

        await target.DisposeAsync();
        var completed = await Task.WhenAny(reconnecting.Task, Task.Delay(500));
        await Assert.That(completed).IsNotEqualTo(reconnecting.Task);
    }

    [Test]
    public async Task ClusterSlots_RoutesFirstKeyedCommandDirectly()
    {
        await using var target = new FakeRespServer("$5\r\nvalue\r\n"u8.ToArray());
        var slot = ClusterHash.GetSlot("key");
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:{slot}\r\n:{slot}\r\n" +
            $"*4\r\n$9\r\n127.0.0.1\r\n:{target.Port}\r\n$2\r\nid\r\n" +
            "%1\r\n+hostname\r\n$16\r\nunusable.invalid\r\n");
        await using var seed = new FakeRespServer(topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });

        var value = await client.GetStringAsync("key");

        await Assert.That(value).IsEqualTo("value");
        await Assert.That(seed.ReceivedCommands).Count().IsEqualTo(1);
        await Assert.That(seed.ReceivedCommands[0]).IsEqualTo("CLUSTER SLOTS");
        await Assert.That(target.ReceivedCommands[0]).IsEqualTo("GET key");
    }

    [Test]
    public async Task RawCommandRouting_UsesCommandSpecificKeyPositions()
    {
        var objectTokens = new RespireValue[] { "OBJECT", "ENCODING", "object-key" };
        var evalTokens = new RespireValue[] { "EVAL", "return redis.call('GET', KEYS[1])", 1, "eval-key" };
        var evalReadOnlyTokens = new RespireValue[] { "EVAL_RO", "return redis.call('GET', KEYS[1])", 1, "eval-ro-key" };
        var xreadTokens = new RespireValue[] { "XREAD", "COUNT", 1, "STREAMS"u8.ToArray(), "stream-key", "0" };
        var infoTokens = new RespireValue[] { "INFO", "memory" };
        var msetexTokens = new RespireValue[] { "MSETEX", 1, "msetex-key", "value" };
        var integerKeyTokens = new RespireValue[] { "GET", 123 };
        var booleanKeyTokens = new RespireValue[] { "GET", true };

        await Assert.That(RawSlot("OBJECT", objectTokens, 1)).IsEqualTo(ClusterHash.GetSlot("object-key"));
        await Assert.That(RawSlot("EVAL", evalTokens, 1)).IsEqualTo(ClusterHash.GetSlot("eval-key"));
        await Assert.That(RawSlot("EVAL_RO", evalReadOnlyTokens, 1)).IsEqualTo(ClusterHash.GetSlot("eval-ro-key"));
        await Assert.That(RawSlot("XREAD", xreadTokens, 1)).IsEqualTo(ClusterHash.GetSlot("stream-key"));
        await Assert.That(RawSlot("GET", integerKeyTokens, 1)).IsEqualTo(ClusterHash.GetSlot("123"));
        await Assert.That(RawSlot("GET", booleanKeyTokens, 1)).IsEqualTo(ClusterHash.GetSlot("1"));
        await Assert.That(RawSlot("INFO", infoTokens, 1)).IsNull();
        await Assert.That(RawSlot("MSETEX", msetexTokens, 1)).IsEqualTo(ClusterHash.GetSlot("msetex-key"));

        foreach (var (parent, subcommand, key) in new[]
        {
            ("OBJECT", "FREQ", "object-key"),
            ("OBJECT", "IDLETIME", "object-key"),
            ("OBJECT", "REFCOUNT", "object-key"),
            ("XGROUP", "DESTROY", "stream-key"),
            ("XGROUP", "SETID", "stream-key"),
            ("XGROUP", "CREATECONSUMER", "stream-key"),
            ("XGROUP", "DELCONSUMER", "stream-key"),
            ("XINFO", "CONSUMERS", "stream-key"),
        })
        {
            var operation = RespireClient.KnownRawOperation(parent, subcommand);
            await Assert.That(operation).IsNotNull();
            await Assert.That(RawSlot(operation!, [parent, subcommand, key], 2))
                .IsEqualTo(ClusterHash.GetSlot(key));
        }
    }

    [Test]
    public async Task PreencodedXGroupSubcommand_RoutesByStreamKey()
    {
        const string subcommand = "DESTROY";
        var key = "stream-key";
        var keySlot = ClusterHash.GetSlot(key);
        var subcommandSlot = ClusterHash.GetSlot(subcommand);
        while (keySlot == 0 || keySlot == subcommandSlot)
        {
            key += "x";
            keySlot = ClusterHash.GetSlot(key);
        }
        await using var target = new FakeRespServer(FakeRespServer.OkReply);
        await using var seed = new FakeRespServer(FakeRespServer.OkReply);
        seed.ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
            ? Encoding.ASCII.GetBytes(
                $"*2\r\n*3\r\n:0\r\n:{keySlot - 1}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{seed.Port}\r\n" +
                $"*3\r\n:{keySlot}\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{target.Port}\r\n")
            : null;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });

        using var result = await client.ExecuteAsync(
            RespireCommand.Create("XGROUP"), [subcommand, key, "group"], flags: RespireCommandFlags.NoRedirect);

        await Assert.That(result.AsString()).IsEqualTo("OK");
        await Assert.That(seed.ReceivedCommands).DoesNotContain($"XGROUP {subcommand} {key} group");
        await Assert.That(target.ReceivedCommands).Contains($"XGROUP {subcommand} {key} group");
    }

    [Test]
    public async Task CatalogCommandRouting_UsesDescriptorNameAndArguments()
    {
        await Assert.That(CatalogSlot(RespireCommands.String.GET, ["catalog-key"]))
            .IsEqualTo(ClusterHash.GetSlot("catalog-key"));
        await Assert.That(CatalogSlot(
                RespireCommands.Scripting.EVAL, ["return redis.call('GET', KEYS[1])", 1, "eval-key"]))
            .IsEqualTo(ClusterHash.GetSlot("eval-key"));
        await Assert.That(CatalogSlot(RespireCommands.Server.INFO, ["memory"]))
            .IsNull();
        await Assert.That(CatalogSlot(RespireCommands.Server.ACL_GETUSER, ["default"]))
            .IsNull();
        await Assert.That(CatalogSlot(RespireCommands.String.MSETEX, [1, "msetex-key", "value"]))
            .IsEqualTo(ClusterHash.GetSlot("msetex-key"));
    }

    [Test]
    public async Task CatalogCommand_UsesCachedOwnerWhenSeedIsUnavailable()
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
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            ConnectTimeout = TestConnectTimeout,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });

        using var first = await client.ExecuteAsync(RespireCommands.String.GET, "key");
        await seed.DisposeAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (client.Core.Multiplexer.IsConnected)
        {
            await Task.Delay(10, timeout.Token);
        }

        using var second = await client.ExecuteAsync(
            RespireCommands.String.GET, ["key"], cancellationToken: timeout.Token);

        await Assert.That(first.AsString()).IsEqualTo("value");
        await Assert.That(second.AsString()).IsEqualTo("second");
        await Assert.That(seed.ReceivedCommands).Count().IsEqualTo(2);
        await Assert.That(target.ReceivedCommands).IsEquivalentTo(["GET key", "GET key"]);
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
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });

        var batch = client.CreateBatch();
        var first = batch.GetString("foo");
        var second = batch.GetString("bar");
        await batch.ExecuteAsync();

        await Assert.That(first.Result).IsEqualTo("one");
        await Assert.That(second.Result).IsEqualTo("two");
        await Assert.That(firstNode.ReceivedCommands[0]).IsEqualTo("GET foo");
        await Assert.That(secondNode.ReceivedCommands[0]).IsEqualTo("GET bar");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Batch_FirstErrorFollowsOriginalQueueOrderAcrossNodes(bool inspect)
    {
        const string firstKey = "{first}a";
        const string secondKey = "{second}b";
        const string thirdKey = "{first}c";
        await using var firstNode = new FakeRespServer(
            "$3\r\none\r\n"u8.ToArray(),
            "-ERR third failed\r\n"u8.ToArray());
        await using var secondNode = new FakeRespServer("-ERR second failed\r\n"u8.ToArray());
        var firstSlot = ClusterHash.GetSlot(firstKey);
        var secondSlot = ClusterHash.GetSlot(secondKey);
        var topology = Encoding.ASCII.GetBytes(
            $"*2\r\n" +
            $"*3\r\n:{firstSlot}\r\n:{firstSlot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{firstNode.Port}\r\n" +
            $"*3\r\n:{secondSlot}\r\n:{secondSlot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{secondNode.Port}\r\n");
        await using var seed = new FakeRespServer(topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });

        var batch = client.CreateBatch();
        var first = batch.GetString(firstKey);
        var second = batch.GetString(secondKey);
        var third = batch.GetString(thirdKey);

        if (!inspect)
        {
            var error = await Assert.That(async () => await batch.ExecuteAsync())
                .ThrowsExactly<RespireServerException>();
            await Assert.That(error).IsSameReferenceAs(second.Error);
            await Assert.That(first.Result).IsEqualTo("one");
            await Assert.That(third.Error).IsNotNull();
            return;
        }
        var result = await batch.TryExecuteAsync();

        await Assert.That(first.Result).IsEqualTo("one");
        await Assert.That(result.FailureCount).IsEqualTo(2);
        await Assert.That(result.FirstError).IsSameReferenceAs(second.Error);
        await Assert.That(result.FirstError).IsNotSameReferenceAs(third.Error);
        await Assert.That(result.Failures.Count).IsEqualTo(2);
        await Assert.That(result.Failures[0].Index).IsEqualTo(1);
        await Assert.That(result.Failures[0].Operation).IsEqualTo("GET");
        await Assert.That(result.Failures[1].Index).IsEqualTo(2);
        await Assert.That(result.Failures[1].Operation).IsEqualTo("GET");
        await Assert.That(result.Failures[0].Error).IsSameReferenceAs(second.Error);
        await Assert.That(result.Failures[1].Error).IsSameReferenceAs(third.Error);
    }

    [Test]
    public async Task Batch_PreservesSameSlotOrderAcrossConnections()
    {
        var slot = ClusterHash.GetSlot("key");
        await using var target = new FakeRespServer(
            2, FakeRespServer.OkReply, "$5\r\nvalue\r\n"u8.ToArray());
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:{slot}\r\n:{slot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{target.Port}\r\n");
        await using var seed = new FakeRespServer(2, topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Connections = 2,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });
        var batch = client.CreateBatch();
        var set = batch.Set("key", "value");
        var get = batch.GetString("key");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await batch.ExecuteAsync(timeout.Token);

        await Assert.That(target.ReceivedCommands).Count().IsEqualTo(2);
        await Assert.That(target.ReceivedCommands[0]).IsEqualTo("SET key value");
        await Assert.That(target.ReceivedCommands[1]).IsEqualTo("GET key");
        await Assert.That(target.ReceivedConnectionIds[0]).IsEqualTo(target.ReceivedConnectionIds[1]);
        await Assert.That(set.Result).IsTrue();
        await Assert.That(get.Result).IsEqualTo("value");
    }

    [Test]
    public async Task Batch_FacetMultiKeyCommand_RoutesByItsSharedSlot()
    {
        // Hash-tagged keys share a slot, so a multi-key facet command queued on a batch must
        // route as one unit to that slot's node.
        var slot = ClusterHash.GetSlot("{acct}dest");
        await using var target = new FakeRespServer(":2\r\n"u8.ToArray());
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:{slot}\r\n:{slot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{target.Port}\r\n");
        await using var seed = new FakeRespServer(topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });

        var batch = client.CreateBatch();
        var stored = batch.Sets.UnionStore("{acct}dest", "{acct}a", "{acct}b");
        var deleted = batch.Keys.Delete("{acct}a", "{acct}b");
        await batch.ExecuteAsync();

        await Assert.That(stored.Result).IsEqualTo(2);
        await Assert.That(deleted.Result).IsEqualTo(2);
        await Assert.That(target.ReceivedCommands[0]).IsEqualTo("SUNIONSTORE {acct}dest {acct}a {acct}b");
        await Assert.That(target.ReceivedCommands[1]).IsEqualTo("DEL {acct}a {acct}b");
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
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });

        var transaction = client.CreateTransaction();
        var pending = transaction.Set("{account}name", "Ada");
        await transaction.CommitAsync();

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
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });

        var transaction = client.CreateTransaction();
        var pending = transaction.Set("{account}name", "Ada");
        await transaction.CommitAsync();

        await Assert.That(pending.Result).IsTrue();
        await Assert.That(seed.ReceivedCommands[^3]).IsEqualTo("MULTI");
        await Assert.That(target.ReceivedCommands[0]).IsEqualTo("MULTI");
        await Assert.That(target.ReceivedCommands[1]).IsEqualTo("SET {account}name Ada");
        await Assert.That(target.ReceivedCommands[2]).IsEqualTo("EXEC");
    }

    [Test]
    public async Task Transaction_RejectsAskRedirectDuringSlotMigration()
    {
        await using var target = new FakeRespServer();
        var slot = ClusterHash.GetSlot("{account}name");
        await using var seed = new FakeRespServer(
            "*0\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            Encoding.ASCII.GetBytes($"-ASK {slot} 127.0.0.1:{target.Port}\r\n"),
            "-EXECABORT Transaction discarded because of previous errors.\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });

        var transaction = client.CreateTransaction();
        _ = transaction.Set("{account}name", "Ada");

        var error = await Assert.That(async () => await transaction.CommitAsync())
            .Throws<RespireConnectionException>();

        await Assert.That(error!.Message).Contains("cannot follow ASK redirects");
        await Assert.That(target.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task Transaction_RejectsKeysFromDifferentHashSlots()
    {
        await using var client = CreateLazyClusterClient();
        await using var transaction = client.CreateTransaction();
        _ = transaction.Set("foo", "one");

        var error = Assert.Throws<InvalidOperationException>(() => transaction.Set("bar", "two"));

        await Assert.That(error.Message).Contains("same hash slot");
    }

    [Test]
    public async Task Transaction_ZeroKeyScriptDoesNotRouteByArgument()
    {
        await using var client = CreateLazyClusterClient();
        await using var transaction = client.CreateTransaction();
        var script = RespireScript.Create("return ARGV[1]");

        _ = transaction.Set("{account}name", "Ada");
        _ = transaction.Scripts.Evaluate(script, args: ["{other}value"]);

        await Assert.That(transaction.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Transaction_RejectsCrossSlotKeysWithinFacetCommands()
    {
        await using var client = CreateLazyClusterClient();
        Action<RespireTransaction>[] queueCommands =
        [
            transaction => transaction.Keys.Delete("foo", "bar"),
            transaction => transaction.Keys.Rename("foo", "bar"),
            transaction => transaction.Strings.GetMany("foo", "bar"),
            transaction => transaction.Strings.SetMany(("foo", "one"), ("bar", "two")),
            transaction => transaction.Strings.SetManyExpire(
                RespireExpiry.In(TimeSpan.FromMinutes(1)), SetWhen.Always,
                ("foo", "one"), ("bar", "two")),
            transaction => transaction.Strings.Lcs("foo", "bar"),
            transaction => transaction.Lists.Move("foo", "bar"),
            transaction => transaction.Sets.Union("foo", "bar"),
            transaction => transaction.Sets.UnionStore("foo", "bar"),
            transaction => transaction.Bitmaps.Operate(BitOperation.Or, "foo", "bar"),
            transaction => transaction.HyperLogLog.Count("foo", "bar"),
            transaction => transaction.HyperLogLog.Merge("foo", "bar"),
            transaction => transaction.Geo.SearchStore(
                "foo", "bar", GeoSearchOrigin.FromMember("member"), GeoSearchShape.Circle(1)),
        ];

        foreach (var queueCommand in queueCommands)
        {
            await using var transaction = client.CreateTransaction();

            var error = Assert.Throws<InvalidOperationException>(() => queueCommand(transaction));

            await Assert.That(error.Message).Contains("same hash slot");
            await Assert.That(transaction.Count).IsEqualTo(0);
        }
    }

    [Test]
    public async Task Transaction_CrossSlotRejectionDoesNotPinSlot()
    {
        await using var client = CreateLazyClusterClient();
        await using var transaction = client.CreateTransaction();

        _ = Assert.Throws<InvalidOperationException>(
            () => transaction.Keys.Delete("foo", "bar"));
        _ = transaction.Set("bar", "two");

        await Assert.That(transaction.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Transaction_RejectedMultiKeySerializationDoesNotPinSlot()
    {
        await using var client = CreateLazyClusterClient();
        await using var transaction = client.CreateTransaction();

        _ = Assert.Throws<ArgumentException>(() => transaction.Strings.SetMany(
            ("{rejected}one", RespireValue.Null),
            ("{rejected}two", "value")));
        _ = transaction.Set("{accepted}key", "value");

        await Assert.That(transaction.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Transaction_SuccessfulMultiKeyCommandPinsSlot()
    {
        await using var client = CreateLazyClusterClient();
        await using var transaction = client.CreateTransaction();

        _ = transaction.Strings.GetMany("{account}one", "{account}two");

        _ = Assert.Throws<InvalidOperationException>(
            () => transaction.Set("{other}key", "value"));
        await Assert.That(transaction.Count).IsEqualTo(1);
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
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
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
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
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
    public async Task TrackedCorrectionBroadcast_PreservesAskingAfterAskRedirect()
    {
        var slot = ClusterHash.GetSlot("cache-key");
        await using var target = new FakeRespServer(
            ":42\r\n"u8.ToArray(),
            ":0\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            "$5\r\nvalue\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            ":1\r\n"u8.ToArray());
        await using var initial = new FakeRespServer(
            ":41\r\n"u8.ToArray(),
            ":0\r\n"u8.ToArray(),
            Encoding.ASCII.GetBytes($"-ASK {slot} 127.0.0.1:{target.Port}\r\n"));
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:{slot}\r\n:{slot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{initial.Port}\r\n");
        await using var seed = new FakeRespServer(topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });
        var script = RespireScript.Create("return redis.call('GET', KEYS[1])");
        var execution = await client.StartTrackedScriptExecutionAsync(
            script, ["cache-key"], [], CancellationToken.None);
        using var result = await execution.Response;

        await client.ExecuteOnAllConnectionsAsync(
            script, ["cache-key"], [], execution.ConnectionIdentity);

        await Assert.That(execution.ConnectionIdentity.RequiresAsking).IsTrue();
        await Assert.That(target.ReceivedCommands[^2]).IsEqualTo("ASKING");
        await Assert.That(target.ReceivedCommands[^1]).StartsWith("EVAL ");
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
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });
        var client = (RespireClient)owner.WithKeyPrefix(prefix);

        await client.UnlinkGuardedAsync(key, CancellationToken.None);

        await Assert.That(target.ReceivedCommands).Contains(command => command.StartsWith("SET "));
        await Assert.That(target.ReceivedCommands).Contains(command => command.StartsWith("EVAL "));
        await Assert.That(seed.ReceivedCommands).Count().IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Connect_TriesLaterSeedWhenFirstIsUnavailable(bool useConnectionString)
    {
        await using var target = new FakeRespServer("$5\r\nvalue\r\n"u8.ToArray());
        var slot = ClusterHash.GetSlot("key");
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:{slot}\r\n:{slot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{target.Port}\r\n");
        await using var seed = new FakeRespServer(topology);
        var options = new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            ConnectTimeout = TestConnectTimeout,
            Endpoints =
            {
                new RespireEndpoint("127.0.0.1", 1),
                new RespireEndpoint("127.0.0.1", seed.Port),
            },
        };
        if (useConnectionString)
        {
            options = RespireOptions.Parse(
                $"127.0.0.1:1,127.0.0.1:{seed.Port},cluster=true,connectTimeout=1000,protocol=2");
        }
        await using var client = await RespireClient.ConnectAsync(options);

        var value = await client.GetStringAsync("key");

        await Assert.That(client.IsConnected).IsTrue();
        await Assert.That(value).IsEqualTo("value");
        await Assert.That(seed.ReceivedCommands[0]).IsEqualTo("CLUSTER SLOTS");
        await Assert.That(target.ReceivedCommands[0]).IsEqualTo("GET key");
    }

    [Test]
    public async Task UnkeyedCommand_UsesDiscoveredMasterWhenSeedIsUnavailable()
    {
        await using var master = new FakeRespServer(
            "$5\r\nvalue\r\n"u8.ToArray(),
            FakeRespServer.PongReply);
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{master.Port}\r\n");
        var seed = new FakeRespServer(topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            ConnectTimeout = TestConnectTimeout,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });

        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await seed.DisposeAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (client.Core.Multiplexer.IsConnected)
        {
            await Task.Delay(10, timeout.Token);
        }

        _ = await client.PingAsync(timeout.Token);
        await Assert.That(master.ReceivedCommands).IsEquivalentTo(["GET key", "PING"]);
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
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });

        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await seed.DisposeAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (client.Core.Multiplexer.IsConnected)
        {
            await Task.Delay(10, timeout.Token);
        }

        await Assert.That(client.IsConnected).IsTrue();
        await Assert.That(await client.GetStringAsync("key", timeout.Token)).IsEqualTo("second");
        await Assert.That(target.ReceivedCommands).Count().IsEqualTo(2);
    }

    [Test]
    public async Task FailedCachedSlotOwner_RefreshesThroughDiscoveredMasterWhenSeedUnavailable()
    {
        var slot = ClusterHash.GetSlot("key");
        await using var target = new FakeRespServer("$5\r\nvalue\r\n"u8.ToArray());
        var refreshedTopology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{target.Port}\r\n");
        await using var master = new FakeRespServer(refreshedTopology);
        var failedOwner = new FakeRespServer();

        var initialTopology = Encoding.ASCII.GetBytes(
            $"*3\r\n" +
            $"*3\r\n:0\r\n:{slot - 1}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{master.Port}\r\n" +
            $"*3\r\n:{slot}\r\n:{slot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{failedOwner.Port}\r\n" +
            $"*3\r\n:{slot + 1}\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{master.Port}\r\n");
        var seed = new FakeRespServer(initialTopology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            ConnectTimeout = TestConnectTimeout,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });

        await seed.DisposeAsync();
        await failedOwner.DisposeAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (client.Core.Cluster!.IsSlotConnected(slot))
        {
            await Task.Delay(10, timeout.Token);
        }

        var value = await client.GetStringAsync("key", timeout.Token);

        await Assert.That(value).IsEqualTo("value");
        await Assert.That(master.ReceivedCommands[0]).IsEqualTo("CLUSTER SLOTS");
        await Assert.That(target.ReceivedCommands[0]).IsEqualTo("GET key");
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
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            ConnectTimeout = TestConnectTimeout,
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
        // Disconnect-triggered topology refresh can append CLUSTER SLOTS after the fallback GET.
        await Assert.That(seed.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(1);
    }

    [Test]
    public async Task Scan_TraversesEveryKnownMaster()
    {
        await using var firstNode = new FakeRespServer(
            "*2\r\n$1\r\n0\r\n*1\r\n$3\r\ntwo\r\n"u8.ToArray());
        await using var secondNode = new FakeRespServer(
            "*2\r\n$1\r\n0\r\n*1\r\n$3\r\none\r\n"u8.ToArray());
        var topology = Encoding.ASCII.GetBytes(
            $"*2\r\n" +
            $"*3\r\n:0\r\n:8191\r\n*2\r\n$9\r\n127.0.0.1\r\n:{firstNode.Port}\r\n" +
            $"*3\r\n:8192\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{secondNode.Port}\r\n");
        ConfigureClusterScanMetadata(firstNode, "first", "0-8191", topology);
        ConfigureClusterScanMetadata(secondNode, "second", "8192-16383", topology);
        await using var seed = new FakeRespServer(topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });
        var keys = new List<string>();

        await foreach (var key in client.Keys.ScanAsync())
        {
            keys.Add(key);
        }

        await Assert.That(keys).IsEquivalentTo(["one", "two"]);
        await Assert.That(firstNode.ReceivedCommands.First(command => command.StartsWith("SCAN "))).IsEqualTo("SCAN 0 COUNT 250");
        await Assert.That(secondNode.ReceivedCommands.First(command => command.StartsWith("SCAN "))).IsEqualTo("SCAN 0 COUNT 250");
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
        ConfigureClusterScanMetadata(currentNode, "current", "0-16383", currentTopology);
        await using var seed = new FakeRespServer(staleTopology, currentTopology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            ConnectTimeout = TestConnectTimeout,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });
        var keys = new List<string>();

        await foreach (var key in client.Keys.ScanAsync())
        {
            keys.Add(key);
        }

        await Assert.That(keys).IsEquivalentTo(["current"]);
        await Assert.That(currentNode.ReceivedCommands.First(command => command.StartsWith("SCAN "))).IsEqualTo("SCAN 0 COUNT 250");
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
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });
        var core = client.Core;
        var retiredMultiplexer = core.Cluster!.GetMultiplexer(
            new RespireEndpoint("127.0.0.1", retiredNode.Port));
        var states = new List<RespireConnectionState>();
        core.ConnectionStateChanged += change => states.Add(change.State);
        core.NotifyCommandStateChanged(retiredMultiplexer, 0, RespireConnectionState.Reconnecting);

        _ = await core.Cluster.GetMasterConnectionsAsync(CancellationToken.None, discovery: null);

        await Assert.That(states).IsEquivalentTo(
            [RespireConnectionState.Reconnecting, RespireConnectionState.Connected]);
    }

    [Test]
    public async Task MovedFinalSlot_ClearsReconnectStateForRetiredMaster()
    {
        await using var currentNode = new FakeRespServer(FakeRespServer.PongReply);
        await using var retiredNode = new FakeRespServer(FakeRespServer.PongReply);
        var slot = ClusterHash.GetSlot("retired-key");
        var topology = Encoding.ASCII.GetBytes(
            $"*2\r\n" +
            $"*3\r\n:0\r\n:{slot - 1}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{currentNode.Port}\r\n" +
            $"*3\r\n:{slot}\r\n:{slot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{retiredNode.Port}\r\n");
        await using var seed = new FakeRespServer(topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });
        var core = client.Core;
        var cluster = core.Cluster!;
        var retiredMultiplexer = cluster.GetMultiplexer(new RespireEndpoint("127.0.0.1", retiredNode.Port));
        var currentMultiplexer = cluster.GetMultiplexer(new RespireEndpoint("127.0.0.1", currentNode.Port));
        var states = new List<RespireConnectionState>();
        core.ConnectionStateChanged += change => states.Add(change.State);
        core.NotifyCommandStateChanged(retiredMultiplexer, 0, RespireConnectionState.Reconnecting);

        cluster.SetSlotOwner(slot, currentMultiplexer);

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
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            ConnectTimeout = TestConnectTimeout,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });

        var failed = false;
        try
        {
            await foreach (var _ in client.Keys.ScanAsync())
            {
            }
        }
        catch (Exception exception) when (
            exception is RespireConnectionException or OperationCanceledException or System.Net.Sockets.SocketException)
        {
            failed = true;
        }

        await Assert.That(failed).IsTrue();
    }

    [Test]
    public async Task Scan_RefreshesThroughCachedMasterWhenSeedIsUnavailable()
    {
        await using var refreshedFirstNode = new FakeRespServer(
            "*2\r\n$1\r\n0\r\n*1\r\n$3\r\ntwo\r\n"u8.ToArray());
        await using var secondNode = new FakeRespServer(
            "*2\r\n$1\r\n0\r\n*1\r\n$3\r\none\r\n"u8.ToArray());
        var refreshedTopology = Encoding.ASCII.GetBytes(
            $"*2\r\n" +
            $"*3\r\n:0\r\n:8191\r\n*2\r\n$9\r\n127.0.0.1\r\n:{refreshedFirstNode.Port}\r\n" +
            $"*3\r\n:8192\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{secondNode.Port}\r\n");
        ConfigureClusterScanMetadata(refreshedFirstNode, "first", "0-8191", refreshedTopology);
        ConfigureClusterScanMetadata(secondNode, "second", "8192-16383", refreshedTopology);
        await using var cachedFirstNode = new FakeRespServer(refreshedTopology);
        var initialTopology = Encoding.ASCII.GetBytes(
            $"*2\r\n" +
            $"*3\r\n:0\r\n:8191\r\n*2\r\n$9\r\n127.0.0.1\r\n:{cachedFirstNode.Port}\r\n" +
            $"*3\r\n:8192\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{secondNode.Port}\r\n");
        var seed = new FakeRespServer(initialTopology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            ConnectTimeout = TestConnectTimeout,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });
        await seed.DisposeAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (client.Core.Multiplexer.IsConnected)
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
        await Assert.That(refreshedFirstNode.ReceivedCommands.First(command => command.StartsWith("SCAN "))).IsEqualTo("SCAN 0 COUNT 250");
    }

    [Test]
    public async Task PubSubEndpoint_FallsBackToDiscoveredMaster()
    {
        await using var master = new FakeRespServer(FakeRespServer.PongReply);
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{master.Port}\r\n");
        var seed = new FakeRespServer(topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            ConnectTimeout = TestConnectTimeout,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });
        await seed.DisposeAsync();

        var endpoint = await client.Core.Cluster!.GetPubSubEndpointAsync(CancellationToken.None);

        await Assert.That(endpoint).IsEqualTo(new RespireEndpoint("127.0.0.1", master.Port));
    }

    [Test]
    public async Task ReadOnlyScriptRoutesToKeyOwnerAndFallsBackOnNoScript()
    {
        var script = RespireScript.Create("return KEYS[1]", readOnly: true);
        await using var target = new FakeRespServer("-NOSCRIPT missing\r\n"u8.ToArray(), ":7\r\n"u8.ToArray());
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{target.Port}\r\n");
        await using var seed = new FakeRespServer(topology);
        await using var owner = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });
        var client = owner.WithKeyPrefix("tenant:");
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(script, ["{key}"])).IsEqualTo(7);
        await Assert.That(target.ReceivedCommands).IsEquivalentTo([
            $"EVALSHA_RO {script.Sha1} 1 tenant:{{key}}", "EVAL_RO return KEYS[1] 1 tenant:{key}"]);
        await using var tx = client.CreateTransaction();
        await Assert.That(() => tx.Scripts.Evaluate(script, ["{first}", "{second}"]))
            .ThrowsExactly<InvalidOperationException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ScriptCacheFanOutObservesEveryPrimaryBeforeCompleting(bool firstFails)
    {
        var flags = "*1\r\n:1\r\n"u8.ToArray();
        await using var firstNode = new FakeRespServer(flags) { SuppressReply = _ => true };
        await using var secondNode = new FakeRespServer(flags) { SuppressReply = _ => true };
        var topology = Encoding.ASCII.GetBytes(
            $"*2\r\n" +
            $"*3\r\n:0\r\n:8191\r\n*2\r\n$9\r\n127.0.0.1\r\n:{firstNode.Port}\r\n" +
            $"*3\r\n:8192\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{secondNode.Port}\r\n");
        await using var seed = new FakeRespServer(topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });
        var pending = client.Scripts.ExistsAsync("digest").AsTask();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (firstNode.CommandsSeen == 0 || secondNode.CommandsSeen == 0)
        {
            await Task.Delay(10, deadline.Token);
        }
        await firstNode.SendRawAsync(firstFails ? "-ERR rejected\r\n"u8.ToArray() : flags);
        await Assert.That(pending.IsCompleted).IsFalse();
        await secondNode.SendRawAsync(flags);
        if (firstFails)
        {
            await Assert.That(async () => await pending).ThrowsExactly<RespireServerException>();
        }
        else
        {
            await Assert.That(await pending).IsEquivalentTo([true]);
        }
    }

    [Test]
    public async Task ScriptCacheQueriesRequireEveryPrimaryAndFlushFansOut()
    {
        await using var firstNode = new FakeRespServer("*2\r\n:1\r\n:1\r\n"u8.ToArray(), FakeRespServer.OkReply);
        await using var secondNode = new FakeRespServer("*2\r\n:0\r\n:1\r\n"u8.ToArray(), FakeRespServer.OkReply);
        var topology = Encoding.ASCII.GetBytes(
            $"*2\r\n" +
            $"*3\r\n:0\r\n:8191\r\n*2\r\n$9\r\n127.0.0.1\r\n:{firstNode.Port}\r\n" +
            $"*3\r\n:8192\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{secondNode.Port}\r\n");
        await using var seed = new FakeRespServer(topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });
        var exists = await client.Scripts.ExistsAsync("partial", "everywhere");
        await client.Scripts.FlushAsync(ScriptFlushMode.Async);
        await Assert.That(exists).IsEquivalentTo([false, true]);
        await Assert.That(firstNode.ReceivedCommands).IsEquivalentTo([
            "SCRIPT EXISTS partial everywhere", "SCRIPT FLUSH ASYNC"]);
        await Assert.That(secondNode.ReceivedCommands).IsEquivalentTo(firstNode.ReceivedCommands);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ScriptLoad_VisitsEveryMaster(bool mismatchedDigest)
    {
        var script = RespireScript.Create("return 1");
        var response = Encoding.ASCII.GetBytes($"$40\r\n{script.Sha1}\r\n");
        await using var firstNode = new FakeRespServer(response);
        await using var secondNode = new FakeRespServer(mismatchedDigest
            ? Encoding.ASCII.GetBytes($"$40\r\n{new string('0', 40)}\r\n") : response);
        var topology = Encoding.ASCII.GetBytes(
            $"*2\r\n" +
            $"*3\r\n:0\r\n:8191\r\n*2\r\n$9\r\n127.0.0.1\r\n:{firstNode.Port}\r\n" +
            $"*3\r\n:8192\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{secondNode.Port}\r\n");
        await using var seed = new FakeRespServer(topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });

        if (mismatchedDigest)
        {
            await Assert.That(async () => await client.Scripts.LoadAsync(script))
                .Throws<RespireProtocolException>();
        }
        else
        {
            await Assert.That(await client.Scripts.LoadAsync(script)).IsEqualTo(script.Sha1);
        }
        await Assert.That(firstNode.ReceivedCommands).IsEquivalentTo(["SCRIPT LOAD return 1"]);
        await Assert.That(secondNode.ReceivedCommands).IsEquivalentTo(["SCRIPT LOAD return 1"]);
    }

    [Test]
    public async Task ScriptLoad_RefreshesTopologyBeforeVisitingMasters()
    {
        var script = RespireScript.Create("return 1");
        var response = Encoding.ASCII.GetBytes($"$40\r\n{script.Sha1}\r\n");
        await using var firstNode = new FakeRespServer(response);
        await using var addedNode = new FakeRespServer(response);
        var initialTopology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{firstNode.Port}\r\n");
        var refreshedTopology = Encoding.ASCII.GetBytes(
            $"*2\r\n" +
            $"*3\r\n:0\r\n:8191\r\n*2\r\n$9\r\n127.0.0.1\r\n:{firstNode.Port}\r\n" +
            $"*3\r\n:8192\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{addedNode.Port}\r\n");
        await using var seed = new FakeRespServer(initialTopology, refreshedTopology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });

        var sha1 = await client.Scripts.LoadAsync(script);

        await Assert.That(sha1).IsEqualTo(script.Sha1);
        await Assert.That(seed.ReceivedCommands).IsEquivalentTo(["CLUSTER SLOTS", "CLUSTER SLOTS"]);
        await Assert.That(firstNode.ReceivedCommands).IsEquivalentTo(["SCRIPT LOAD return 1"]);
        await Assert.That(addedNode.ReceivedCommands).IsEquivalentTo(["SCRIPT LOAD return 1"]);
    }

    [Test]
    [Arguments(ServerFlushMode.Default, "")]
    [Arguments(ServerFlushMode.Sync, " SYNC")]
    [Arguments(ServerFlushMode.Async, " ASYNC")]
    public async Task ClusterWideServerCommands_VisitEveryMaster(ServerFlushMode mode, string suffix)
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
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
            AllowAdmin = true,
        });

        await Assert.That(await client.Server.DatabaseSizeAsync()).IsEqualTo(5);
        await client.Server.FlushDatabaseAsync(mode, default);
        await client.Server.FlushAllAsync(mode, default);

        var expected = new[] { "DBSIZE", "FLUSHDB" + suffix, "FLUSHALL" + suffix };
        await Assert.That(firstNode.ReceivedCommands).IsEquivalentTo(expected);
        await Assert.That(secondNode.ReceivedCommands).IsEquivalentTo(expected);
    }

    [Test]
    public async Task FunctionLibraryMutations_VisitEveryMaster()
    {
        await using var firstNode = new FakeRespServer(
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply);
        await using var secondNode = new FakeRespServer(
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply);
        var topology = Encoding.ASCII.GetBytes(
            $"*2\r\n" +
            $"*3\r\n:0\r\n:8191\r\n*2\r\n$9\r\n127.0.0.1\r\n:{firstNode.Port}\r\n" +
            $"*3\r\n:8192\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{secondNode.Port}\r\n");
        await using var seed = new FakeRespServer(topology, topology, topology, topology, topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });

        using var load = await client.ExecuteAsync(
            RespireCommands.Scripting.FUNCTION_LOAD, "#!lua name=library");
        using var delete = await client.ExecuteAsync("FUNCTION DELETE", "library");
        RespireValue flushSubcommand = "FLUSH";
        using var flush = await client.ExecuteAsync($"FUNCTION {flushSubcommand}");
        using var restore = await client.ExecuteAsync(
            RespireCommands.Scripting.FUNCTION,
            ["RESTORE", "payload"],
            cancellationToken: CancellationToken.None);

        var expected = new[]
        {
            "FUNCTION LOAD #!lua name=library",
            "FUNCTION DELETE library",
            "FUNCTION FLUSH",
            "FUNCTION RESTORE payload",
        };
        await Assert.That(firstNode.ReceivedCommands).IsEquivalentTo(expected);
        await Assert.That(secondNode.ReceivedCommands).IsEquivalentTo(expected);
    }

    [Test]
    public async Task ClusterWideMutations_RejectCommandFlags()
    {
        var topology = "*0\r\n"u8.ToArray();
        await using var seed = new FakeRespServer(topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });

        await Assert.That(async () => await client.ExecuteAsync(
                RespireCommands.Scripting.FUNCTION_LOAD,
                ["#!lua name=library"],
                RespireCommandFlags.NoRedirect))
            .Throws<NotSupportedException>();
        await Assert.That(async () => await client.ExecuteAsync(
                "SCRIPT FLUSH", [], RespireCommandFlags.NoRedirect))
            .Throws<NotSupportedException>();
        RespireValue subcommand = "FLUSH";
        await Assert.That(async () => await client.ExecuteAsync(
                $"FUNCTION {subcommand}", RespireCommandFlags.NoRedirect))
            .Throws<NotSupportedException>();

        await Assert.That(seed.ReceivedCommands).IsEquivalentTo(["CLUSTER SLOTS"]);
    }

    [Test]
    public async Task ScriptCacheMutations_VisitEveryMaster()
    {
        await using var firstNode = new FakeRespServer(
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply);
        await using var secondNode = new FakeRespServer(
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply);
        var topology = Encoding.ASCII.GetBytes(
            $"*2\r\n" +
            $"*3\r\n:0\r\n:8191\r\n*2\r\n$9\r\n127.0.0.1\r\n:{firstNode.Port}\r\n" +
            $"*3\r\n:8192\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{secondNode.Port}\r\n");
        await using var seed = new FakeRespServer(topology, topology, topology, topology, topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });

        using var catalogLoad = await client.ExecuteAsync(
            RespireCommands.Scripting.SCRIPT_LOAD, "return 1");
        using var rawFlush = await client.ExecuteAsync("SCRIPT FLUSH");
        RespireValue loadSubcommand = "LOAD";
        RespireValue secondScript = "return 2";
        using var interpolatedLoad = await client.ExecuteAsync($"SCRIPT {loadSubcommand} {secondScript}");
        using var catalogFlush = await client.ExecuteAsync(RespireCommands.Scripting.SCRIPT_FLUSH);

        var expected = new[]
        {
            "SCRIPT LOAD return 1",
            "SCRIPT FLUSH",
            "SCRIPT LOAD return 2",
            "SCRIPT FLUSH",
        };
        await Assert.That(firstNode.ReceivedCommands).IsEquivalentTo(expected);
        await Assert.That(secondNode.ReceivedCommands).IsEquivalentTo(expected);
    }

    [Test]
    public async Task SplitRawScriptFlushFireAndForget_VisitsEveryMaster()
    {
        await using var firstNode = new FakeRespServer(FakeRespServer.OkReply);
        await using var secondNode = new FakeRespServer(FakeRespServer.OkReply);
        var topology = Encoding.ASCII.GetBytes(
            $"*2\r\n" +
            $"*3\r\n:0\r\n:8191\r\n*2\r\n$9\r\n127.0.0.1\r\n:{firstNode.Port}\r\n" +
            $"*3\r\n:8192\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{secondNode.Port}\r\n");
        await using var seed = new FakeRespServer(topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });

        await client.ExecuteFireAndForgetAsync("SCRIPT", "FLUSH");
        await WaitForCommandsAsync(firstNode, 1);
        await WaitForCommandsAsync(secondNode, 1);

        await Assert.That(firstNode.ReceivedCommands).IsEquivalentTo(["SCRIPT FLUSH"]);
        await Assert.That(secondNode.ReceivedCommands).IsEquivalentTo(["SCRIPT FLUSH"]);
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
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
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
    [NotInParallel] // A 50 ms watchdog must not compete with the full coverage suite's socket workload.
    public async Task ClusterBlockingCommand_SuppressesResponseWatchdog()
    {
        var slot = ClusterHash.GetSlot("key");
        await using var target = new FakeRespServer(2, FakeRespServer.PongReply);
        target.DelayReply(0, 250);
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:{slot}\r\n:{slot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{target.Port}\r\n");
        await using var seed = new FakeRespServer(topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            ConnectionIdleReadTimeout = TimeSpan.FromMilliseconds(50),
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var response = await client.SendBlockingAsync(
            "BLPOP", new Cmd1(Verbs.BLPop, "key"), timeout.Token);

        await Assert.That(response.AsString()).IsEqualTo("PONG");
        response.Dispose();
    }

    [Test]
    [NotInParallel] // Preserve the real watchdog/deadline test without scheduler pressure from unrelated tests.
    public async Task ClusterBlockingAskRetry_SuppressesResponseWatchdog()
    {
        var slot = ClusterHash.GetSlot("key");
        await using var target = new FakeRespServer(
            2, FakeRespServer.OkReply, FakeRespServer.PongReply);
        target.DelayReply(1, 250);
        await using var initial = new FakeRespServer(
            2, Encoding.ASCII.GetBytes($"-ASK {slot} 127.0.0.1:{target.Port}\r\n"));
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:{slot}\r\n:{slot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{initial.Port}\r\n");
        await using var seed = new FakeRespServer(topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            ConnectionIdleReadTimeout = TimeSpan.FromMilliseconds(50),
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var response = await client.SendBlockingAsync(
            "BLPOP", new Cmd1(Verbs.BLPop, "key"), timeout.Token);

        await Assert.That(response.AsString()).IsEqualTo("PONG");
        response.Dispose();
        await Assert.That(target.ReceivedCommands).Count().IsEqualTo(2);
        await Assert.That(target.ReceivedCommands[0]).IsEqualTo("ASKING");
        await Assert.That(target.ReceivedCommands[1]).IsEqualTo("BLPOP key");
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
    [NotInParallel] // The shared no-GC measurement boundary is process-wide.
    public async Task UnkeyedBuiltInRouting_DoesNotAllocate()
    {
        var command = new Cmd(Verbs.ClusterSlots);
        _ = MeasureUnkeyedRouting(command, allocate: false);
        _ = MeasureUnkeyedRouting(command, allocate: true);

        // Concurrent GC can perturb the thread allocation counter. See docs/ALLOCATION_MEASUREMENT.md.
        var (allocated, control) = AllocationMeasurement.WithoutConcurrentGc(() =>
            (MeasureUnkeyedRouting(command, allocate: false), MeasureUnkeyedRouting(command, allocate: true)));
        await Assert.That(allocated).IsEqualTo(0);
        await Assert.That(control).IsGreaterThanOrEqualTo(1_000 * 37);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureUnkeyedRouting(Cmd command, bool allocate)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
        {
            _ = TryGetSlot(command, out _);
            if (allocate) GC.KeepAlive(AllocateControl());
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object AllocateControl() => new byte[37];

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
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });

        await Assert.That(async () => await client.Server.DatabaseSizeAsync())
            .Throws<RespireConnectionException>();
        await Assert.That(seed.ReceivedCommands).DoesNotContain("DBSIZE");
    }

    [Test]
    public async Task ClusterMode_NonZeroDatabaseDefersCapabilityValidationUntilConnection()
    {
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true, Database = 1, Endpoints = { new RespireEndpoint("localhost") },
        });
        await Assert.That(client.Core.Cluster!.IsConnected).IsFalse();
        await Assert.That(client.Core.Options.Database).IsEqualTo(1);
    }

    [Test]
    public async Task ConnectionString_ParsesClusterMode()
    {
        var options = RespireOptions.Parse("redis://localhost?cluster=true");

        await Assert.That(options.UseCluster).IsTrue();
    }

    private static void ConfigureClusterScanMetadata(FakeRespServer server, string id, string slots, byte[] topology)
    {
        server.ReplyOverride = (_, command) => command switch
        {
            "CLUSTER SLOTS" => topology,
            "CLUSTER NODES" => ScanMetadataReply($"{id} 127.0.0.1:{server.Port}@17000 myself,master - 0 0 1 connected {slots}\n"),
            "INFO server" => ScanMetadataReply($"run_id:{id}-run\r\n"),
            _ => null,
        };
    }

    private static byte[] ScanMetadataReply(string value)
        => Encoding.UTF8.GetBytes($"${Encoding.UTF8.GetByteCount(value)}\r\n{value}\r\n");

    private static FakeRespServer CreateRedirectingSeed(int slot, FakeRespServer target, string code, int maxConnections = 2)
    {
        var seed = new FakeRespServer(maxConnections, FakeRespServer.OkReply);
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:{slot}\r\n:{slot}\r\n*1\r\n*2\r\n$9\r\n127.0.0.1\r\n:{seed.Port}\r\n");
        var redirect = Encoding.ASCII.GetBytes($"-{code} {slot} 127.0.0.1:{target.Port}\r\n");
        seed.ReplyOverride = (_, command) => command switch
        {
            "CLUSTER SLOTS" => topology,
            _ when command.StartsWith("SET ", StringComparison.Ordinal) => redirect,
            _ => null,
        };
        return seed;
    }

    private static ValueTask<RespireClient> CreateClusterClientAsync(FakeRespServer seed)
        => RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });

    private sealed class NonSeekableMemoryStream(byte[] value) : Stream
    {
        private int _offset;
        internal int BytesRead => _offset;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(buffer.Length, value.Length - _offset);
            value.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return ValueTask.FromResult(count);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var copied = Math.Min(count, value.Length - _offset);
            value.AsSpan(_offset, copied).CopyTo(buffer.AsSpan(offset));
            _offset += copied;
            return copied;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ResetFailingMemoryStream(byte[] value) : MemoryStream(value)
    {
        public override long Position
        {
            get => base.Position;
            set => throw new IOException("Stream position reset failed.");
        }
    }

    private sealed class ThrowOnceStream(byte[] value) : MemoryStream(value)
    {
        private bool _throw = true;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_throw)
            {
                _throw = false;
                throw new IOException("Source read failed.");
            }

            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    private static bool TryGetSlot<TCommand>(TCommand command, out int slot)
        where TCommand : struct, IRespCommand
        => command.TryGetClusterSlot(out slot);

    private static async Task WaitForCommandsAsync(FakeRespServer server, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (server.CommandsSeen < count)
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static int? RawSlot(string operation, RespireValue[] tokens, int firstArgumentIndex)
    {
        var routingKeyIndex = DynamicCommandRouting.GetRoutingKeyIndex(
            operation, tokens, firstArgumentIndex);
        var command = new DynamicCommand(tokens, routingKeyIndex);
        return command.TryGetClusterSlot(out var slot) ? slot : null;
    }

    private static int? CatalogSlot(RespireCommand descriptor, RespireValue[] args)
    {
        var command = new CatalogCommand(descriptor, args);
        return command.TryGetClusterSlot(out var slot) ? slot : null;
    }
}
