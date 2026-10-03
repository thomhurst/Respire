using System.Net;
using System.Net.Sockets;
using System.Text;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClusterReadOnlyTests
{
    private static readonly byte[] ReadOnlyReply = "-READONLY You can't write against a read only replica.\r\n"u8.ToArray();

    [Test]
    [NotInParallel] // Preserve the final-seed scheduling budget while other wire tests run.
    [Arguments(false)]
    [Arguments(true)]
    public async Task UnavailableLastSeedLeavesReservedTimeForLastUsableSeed(bool configuredPolicy)
    {
        await using var replacement = new FakeRespServer(FakeRespServer.OkReply);
        await using var replica = new FakeRespServer(ReadOnlyReply);
        await using var initialSeed = new FakeRespServer(Topology(replica.Port));
        await using var healthySeed = new FakeRespServer(Topology(replacement.Port));
        // Cached-owner connection failure can consume the primary phase first. This reply
        // fits the final quarter-round but cannot run on the already-expired early-seed token.
        healthySeed.DelayReply(0, 100);
        using var unavailable = new ReservedUnavailablePort();
        var unavailablePort = unavailable.Port;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true, Connections = 1, ConnectTimeout = TimeSpan.FromSeconds(2), CommandTimeout = null,
            ReconnectPolicy = configuredPolicy ? new() { InitialDelay = TimeSpan.Zero, JitterRatio = 0 } : null,
            Endpoints = [new("127.0.0.1", initialSeed.Port), new("127.0.0.1", healthySeed.Port),
                new("127.0.0.1", unavailablePort)],
        });
        initialSeed.SuppressReply = _ => true;
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        replica.SuppressReply = _ => { received.TrySetResult(); return true; };
        var write = client.SetAsync("key", "value").AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var router = client.Core.Cluster!;
        router.SetSlotOwner(ClusterHash.GetSlot("key"),
            router.GetMultiplexer(new RespireEndpoint("127.0.0.1", unavailablePort)));
        await replica.SendRawAsync(ReadOnlyReply);

        await Assert.That(await write.WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();
        await Assert.That(healthySeed.ReceivedCommands).IsEquivalentTo(["CLUSTER SLOTS"]);
        await Assert.That(replacement.ReceivedCommands).IsEquivalentTo(["SET key value"]);
    }

    [Test]
    [NotInParallel] // Other wire tests must not consume this test's final-seed scheduling budget.
    [Arguments(false)]
    [Arguments(true)]
    public async Task ManyStalledSeedsLeaveUsableTimeForFinalSeed(bool configuredPolicy)
    {
        await using var replacement = new FakeRespServer(FakeRespServer.OkReply);
        await using var replica = new FakeRespServer(ReadOnlyReply);
        await using var initialSeed = new FakeRespServer(Topology(replica.Port));
        await using var finalSeed = new FakeRespServer(Topology(replacement.Port));
        finalSeed.DelayReply(0, 600);
        var stalledSeeds = Enumerable.Range(0, 8)
            .Select(_ => new FakeRespServer { SuppressReply = _ => true }).ToArray();
        try
        {
            var options = new RespireOptions
            {
                Protocol = RespProtocol.Resp2,
                UseCluster = true,
                ReconnectPolicy = configuredPolicy ? new() { InitialDelay = TimeSpan.Zero, JitterRatio = 0 } : null,
                Connections = 1,
                ConnectTimeout = TimeSpan.FromSeconds(2),
                CommandTimeout = null,
                Endpoints = [new("127.0.0.1", initialSeed.Port),
                    .. stalledSeeds.Select(seed => new RespireEndpoint("127.0.0.1", seed.Port)),
                    new("127.0.0.1", finalSeed.Port)],
            };
            await using var client = await RespireClient.ConnectAsync(options);
            initialSeed.SuppressReply = _ => true;

            await Assert.That(await client.SetAsync("key", "value")).IsTrue();
            await Assert.That(finalSeed.ReceivedCommands).IsEquivalentTo(["CLUSTER SLOTS"]);
            await Assert.That(replacement.ReceivedCommands).IsEquivalentTo(["SET key value"]);
        }
        finally
        {
            foreach (var seed in stalledSeeds)
            {
                await seed.DisposeAsync();
            }
        }
    }

    [Test]
    public async Task OlderRefresh_PreservesOwnerChangedAwayAndBack()
    {
        await using var original = new FakeRespServer();
        await using var intermediate = new FakeRespServer();
        await using var stale = new FakeRespServer();
        await using var seed = new FakeRespServer(FullTopology(original.Port));
        await using var client = await ConnectAsync(seed.Port);
        var router = client.Core.Cluster!;
        var refreshing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        seed.SuppressReply = _ => { refreshing.TrySetResult(); return true; };
        var refresh = router.GetMasterConnectionsAsync(CancellationToken.None, discovery: null).AsTask();
        await refreshing.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var slot = ClusterHash.GetSlot("key");
        var originalOwner = router.GetMultiplexer(new RespireEndpoint("127.0.0.1", original.Port));
        router.SetSlotOwner(slot, router.GetMultiplexer(new RespireEndpoint("127.0.0.1", intermediate.Port)));
        router.SetSlotOwner(slot, originalOwner);
        await seed.SendRawAsync(FullTopology(stale.Port));
        _ = await refresh.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That((await router.GetConnectionAsync(slot, CancellationToken.None, discovery: null)).Port).IsEqualTo(original.Port);
        await Assert.That((await router.GetConnectionAsync((slot + 1) % 16384, CancellationToken.None, discovery: null)).Port)
            .IsEqualTo(stale.Port);
    }

    [Test]
    public async Task DisposedDiscoveryCandidate_PropagatesProgrammingFailure()
    {
        await using var replica = new FakeRespServer(ReadOnlyReply);
        await using var other = new FakeRespServer();
        await using var seed = new FakeRespServer(SplitTopology(other.Port, replica.Port));
        await using var client = await ConnectAsync(seed.Port);
        await client.Core.Cluster!.GetMultiplexer(new RespireEndpoint("127.0.0.1", other.Port)).DisposeAsync();

        await Assert.That(async () => await client.SetAsync("key", "value")).Throws<ObjectDisposedException>();
        await Assert.That(seed.CommandsSeen).IsEqualTo(1);
    }

    [Test]
    [NotInParallel] // Other wire tests must not consume this test's final-seed scheduling budget.
    public async Task TwoStalledPrimariesStillLeaveTimeForSeedDiscovery()
    {
        await using var replacement = new FakeRespServer(FakeRespServer.OkReply);
        await using var replica = new FakeRespServer(ReadOnlyReply);
        await using var first = new FakeRespServer { SuppressReply = _ => true };
        await using var second = new FakeRespServer { SuppressReply = _ => true };
        await using var seed = new FakeRespServer(SplitTopology(first.Port, replica.Port), Topology(replacement.Port));
        seed.DelayReply(1, 600);
        await using var client = await ConnectAsync(seed.Port, TimeSpan.FromSeconds(2));
        var router = client.Core.Cluster!;
        router.SetSlotOwner(1, router.GetMultiplexer(new RespireEndpoint("127.0.0.1", second.Port)));

        await Assert.That(await client.SetAsync("key", "value")).IsTrue();
        await Assert.That(seed.CommandsSeen).IsEqualTo(2);
        await Assert.That(replacement.ReceivedCommands).Contains("SET key value");
    }

    [Test]
    public async Task FireAndForgetSurfacesReadOnlyWhenRecoveryFails()
    {
        await using var replica = new FakeRespServer(ReadOnlyReply, ReadOnlyReply);
        await using var seed = new FakeRespServer(Topology(replica.Port), Topology(replica.Port));
        await using var client = await ConnectAsync(seed.Port);

        var error = await Assert.That(async () =>
            await client.ExecuteFireAndForgetAsync(RespireCommands.String.SET, "key", "value"))
            .Throws<RespireServerException>();
        await Assert.That(error!.Code).IsEqualTo(RespireErrorCodes.ReadOnly);
    }

    [Test]
    [Arguments("facet")]
    [Arguments("raw")]
    [Arguments("catalog")]
    [Arguments("fire-and-forget")]
    [Arguments("batch")]
    public async Task Write_RefreshesOwnerAndCachesReplacement(string path)
    {
        await using var replacement = new FakeRespServer(FakeRespServer.OkReply);
        await using var replica = new FakeRespServer(ReadOnlyReply);
        await using var seed = new FakeRespServer(Topology(replica.Port), Topology(replacement.Port));
        await using var client = await ConnectAsync(seed.Port);

        switch (path)
        {
            case "facet":
                await client.SetAsync("key", "first");
                break;
            case "raw":
                using (await client.ExecuteAsync("SET", "key", "first")) { }
                break;
            case "catalog":
                using (await client.ExecuteAsync(RespireCommands.String.SET, "key", "first")) { }
                break;
            case "fire-and-forget":
                await client.ExecuteFireAndForgetAsync(RespireCommands.String.SET, "key", "first");
                break;
            case "batch":
                using (var batch = client.CreateBatch())
                {
                    var pending = batch.Set("key", "first");
                    await batch.ExecuteAsync();
                    await Assert.That(pending.Result).IsTrue();
                }
                break;
        }

        await client.SetAsync("key", "second");

        await Assert.That(replica.ReceivedCommands).IsEquivalentTo(["SET key first"]);
        await Assert.That(replacement.ReceivedCommands)
            .IsEquivalentTo(["SET key first", "SET key second"], CollectionOrdering.Matching);
        await Assert.That(seed.ReceivedCommands).IsEquivalentTo(["CLUSTER SLOTS", "CLUSTER SLOTS"]);
    }

    [Test]
    public async Task UnavailableCachedReplacement_ContinuesThroughSeeds()
    {
        await using var replacement = new FakeRespServer(FakeRespServer.OkReply);
        await using var replica = new FakeRespServer(ReadOnlyReply);
        await using var seed = new FakeRespServer(Topology(replica.Port), Topology(replacement.Port));
        using var unavailable = new ReservedUnavailablePort();
        var unavailablePort = unavailable.Port;
        await using var client = await ConnectAsync(seed.Port, TimeSpan.FromSeconds(5));
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        replica.SuppressReply = _ => { received.TrySetResult(); return true; };
        var write = client.SetAsync("key", "value").AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var router = client.Core.Cluster!;
        router.SetSlotOwner(ClusterHash.GetSlot("key"),
            router.GetMultiplexer(new RespireEndpoint("127.0.0.1", unavailablePort)));
        await replica.SendRawAsync(ReadOnlyReply);

        await Assert.That(await write.WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();
        await Assert.That(replacement.ReceivedCommands).IsEquivalentTo(["SET key value"]);
        await Assert.That(seed.CommandsSeen).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OlderRefresh_DoesNotReplaceNewerOwner(bool topologyUpdate)
    {
        await using var replacement = new FakeRespServer(FakeRespServer.OkReply);
        await using var healthy = new FakeRespServer(FakeRespServer.OkReply);
        await using var replica = new FakeRespServer(ReadOnlyReply);
        var initial = Encoding.ASCII.GetBytes(
            $"*2\r\n*3\r\n:0\r\n:0\r\n*2\r\n$9\r\n127.0.0.1\r\n:{healthy.Port}\r\n" +
            $"*3\r\n:1\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{replica.Port}\r\n");
        var refreshed = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{replacement.Port}\r\n");
        await using var seed = new FakeRespServer(initial, refreshed);
        await using var client = await ConnectAsync(seed.Port);
        var refreshing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        healthy.SuppressReply = _ => { refreshing.TrySetResult(); return true; };
        var write = client.SetAsync("key", "first").AsTask();
        await refreshing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var router = client.Core.Cluster!;
        if (topologyUpdate)
        {
            _ = await router.GetMasterConnectionsAsync(CancellationToken.None, discovery: null);
        }
        else
        {
            var slot = ClusterHash.GetSlot("key");
            var source = router.GetMultiplexer(new RespireEndpoint("127.0.0.1", replica.Port)).GetConnection();
            _ = await router.GetRedirectConnectionAsync(
                new RespireServerException($"MOVED {slot} 127.0.0.1:{replacement.Port}", "SET"),
                source, CancellationToken.None, commandSlot: null, discovery: null);
        }
        await healthy.SendRawAsync(initial);

        await Assert.That(await write.WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();
        await client.SetAsync("key", "second");
        await Assert.That(replacement.ReceivedCommands)
            .IsEquivalentTo(["SET key first", "SET key second"], CollectionOrdering.Matching);
        await Assert.That(replica.ReceivedCommands).IsEquivalentTo(["SET key first"]);
        await Assert.That(seed.CommandsSeen).IsEqualTo(topologyUpdate ? 2 : 1);
    }

    [Test]
    [Arguments(false, false, false)]
    [Arguments(true, false, false)]
    [Arguments(true, true, false)]
    [Arguments(false, false, true)]
    [Arguments(true, false, true)]
    [Arguments(true, true, true)]
    public async Task StalledCandidate_LeavesTimeForHealthySeed(bool duringConnect, bool cachedOwner, bool expireRound)
    {
        var clock = new RecoveryTestClock();
        var stalledRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var replacement = new FakeRespServer(FakeRespServer.OkReply);
        await using var replica = new FakeRespServer(FakeRespServer.OkReply, ReadOnlyReply);
        await using var stalled = new FakeRespServer(FakeRespServer.OkReply);
        stalled.SuppressReply = command =>
        {
            var suppress = duringConnect ? command.StartsWith("CLIENT SETNAME ") : command == "CLUSTER SLOTS";
            if (suppress) stalledRequest.TrySetResult();
            return suppress;
        };
        var initial = SplitTopology(stalled.Port, replica.Port);
        await using var seed = new FakeRespServer(FakeRespServer.OkReply, initial, Topology(replacement.Port));
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Connections = 1,
            ClientName = "recovery",
            ClusterRecoveryClock = clock,
            ConnectTimeout = TimeSpan.FromSeconds(2),
            CommandTimeout = null,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        replica.SuppressReply = command =>
        {
            if (!command.StartsWith("SET ")) return false;
            received.TrySetResult();
            return true;
        };
        var write = client.SetAsync("key", "value").AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cachedOwner)
        {
            var router = client.Core.Cluster!;
            router.SetSlotOwner(ClusterHash.GetSlot("key"),
                router.GetMultiplexer(new RespireEndpoint("127.0.0.1", stalled.Port)));
        }
        await replica.SendRawAsync(ReadOnlyReply);

        await stalledRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // No real-time scheduling margin is required. Expiring the whole round models a
        // primary continuation that cannot resume until the seed reservation is also gone.
        clock.Advance(TimeSpan.FromSeconds(expireRound ? 2 : 1));

        if (expireRound)
        {
            var error = await Assert.That(async () => await write.WaitAsync(TimeSpan.FromSeconds(5)))
                .Throws<RespireServerException>();
            await Assert.That(error!.Code).IsEqualTo(RespireErrorCodes.ReadOnly);
            await Assert.That(replacement.ReceivedCommands).IsEmpty();
            await Assert.That(seed.ReceivedCommands.Count(command => command == "CLUSTER SLOTS")).IsEqualTo(1);
            return;
        }

        await Assert.That(await write.WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();
        await Assert.That(stalled.ReceivedCommands).Contains(duringConnect ? "CLIENT SETNAME recovery" : "CLUSTER SLOTS");
        await Assert.That(replacement.ReceivedCommands).Contains("SET key value");
        await Assert.That(seed.ReceivedCommands.Count(command => command == "CLUSTER SLOTS")).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task HealthyDiscoveryBudgetDoesNotShrinkWithCandidateCount(bool manyPrimaries)
    {
        await using var replacement = new FakeRespServer(FakeRespServer.OkReply);
        await using var replica = new FakeRespServer(ReadOnlyReply);
        await using var other = new FakeRespServer(manyPrimaries
            ? Topology(replacement.Port) : "-NOPERM discovery denied\r\n"u8.ToArray());
        await using var seed = new FakeRespServer(SplitTopology(other.Port, replica.Port), Topology(replacement.Port));
        if (manyPrimaries) other.DelayReply(0, 600);
        else seed.DelayReply(1, 600);
        // A 600 ms reply still exceeds a budget divided among 32 candidates, but the
        // correct five-second primary phase leaves ample scheduling margin on busy CI.
        await using var client = await ConnectAsync(seed.Port, TimeSpan.FromSeconds(10));
        if (manyPrimaries)
        {
            var router = client.Core.Cluster!;
            // No sockets are opened for these unused candidates. Their count must not
            // shorten the healthy first primary's topology request allowance.
            for (var slot = 1; slot < 32; slot++)
            {
                router.SetSlotOwner(slot, router.GetMultiplexer(new RespireEndpoint($"unused-{slot}.invalid")));
            }
        }

        await Assert.That(await client.SetAsync("key", "value")).IsTrue();
        await Assert.That(replacement.ReceivedCommands).Contains("SET key value");
        await Assert.That(seed.CommandsSeen).IsEqualTo(manyPrimaries ? 1 : 2);
    }

    [Test]
    public async Task UnrelatedMoved_PreservesUsefulRefreshAndNewerRoute()
    {
        await using var replacement = new FakeRespServer(FakeRespServer.OkReply);
        await using var moved = new FakeRespServer(FakeRespServer.OkReply);
        await using var healthy = new FakeRespServer(FakeRespServer.OkReply);
        await using var replica = new FakeRespServer(ReadOnlyReply);
        await using var seed = new FakeRespServer(SplitTopology(healthy.Port, replica.Port),
            "-NOPERM discovery denied\r\n"u8.ToArray());
        await using var client = await ConnectAsync(seed.Port, TimeSpan.FromSeconds(5));
        var refreshing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        healthy.SuppressReply = _ => { refreshing.TrySetResult(); return true; };
        var write = client.SetAsync("key", "value").AsTask();
        await refreshing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var router = client.Core.Cluster!;
        var source = router.GetMultiplexer(new RespireEndpoint("127.0.0.1", replica.Port)).GetConnection();
        _ = await router.GetRedirectConnectionAsync(
            new RespireServerException($"MOVED 0 127.0.0.1:{moved.Port}", "SET"), source, CancellationToken.None, commandSlot: null, discovery: null);
        await healthy.SendRawAsync(SplitTopology(healthy.Port, replacement.Port));

        await Assert.That(await write.WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();
        await Assert.That(replacement.ReceivedCommands).IsEquivalentTo(["SET key value"]);
        await Assert.That((await router.GetConnectionAsync(0, CancellationToken.None, discovery: null)).Port).IsEqualTo(moved.Port);
        await Assert.That(seed.CommandsSeen).IsEqualTo(1);
    }

    private static byte[] SplitTopology(int firstPort, int remainingPort)
        => Encoding.ASCII.GetBytes(
            $"*2\r\n*3\r\n:0\r\n:0\r\n*2\r\n$9\r\n127.0.0.1\r\n:{firstPort}\r\n" +
            $"*3\r\n:1\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{remainingPort}\r\n");

    [Test]
    public async Task StaleTopology_PreservesReadOnlyWithoutResendingWrite()
    {
        await using var replica = new FakeRespServer(ReadOnlyReply);
        await using var seed = new FakeRespServer(Topology(replica.Port));
        await using var client = await ConnectAsync(seed.Port);

        var error = await Assert.That(async () => await client.SetAsync("key", "value").AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5))).Throws<RespireServerException>();

        await Assert.That(error!.Code).IsEqualTo(RespireErrorCodes.ReadOnly);
        await Assert.That(error.CommandName).IsEqualTo("SET");
        await Assert.That(replica.ReceivedCommands.Count(command => command.StartsWith("SET "))).IsEqualTo(1);
        await Assert.That(seed.CommandsSeen).IsEqualTo(2);
    }

    [Test]
    public async Task RefreshTimeout_PreservesReadOnly()
    {
        await using var replica = new FakeRespServer(ReadOnlyReply);
        await using var seed = new FakeRespServer(Topology(replica.Port));
        // This timeout also covers healthy initial socket creation on loaded CI workers.
        await using var client = await ConnectAsync(seed.Port, TimeSpan.FromSeconds(2));
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        seed.SuppressReply = command =>
        {
            if (command == "CLUSTER SLOTS") refreshStarted.TrySetResult();
            return true;
        };

        var write = client.SetAsync("key", "value").AsTask();
        // Observe the stalled recovery itself before applying its hang guard. Scheduling the
        // initial connection and READONLY response is separate from the recovery wait below.
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var error = await Assert.That(async () => await write.WaitAsync(TimeSpan.FromSeconds(5)))
            .Throws<RespireServerException>();

        await Assert.That(error!.Code).IsEqualTo(RespireErrorCodes.ReadOnly);
        await Assert.That(seed.CommandsSeen).IsEqualTo(2);
    }

    [Test]
    public async Task BlockingCommand_RefreshesDedicatedPool()
    {
        await using var replacement = new FakeRespServer(2,
            "*2\r\n$3\r\nkey\r\n$5\r\nvalue\r\n"u8.ToArray());
        await using var replica = new FakeRespServer(2, ReadOnlyReply);
        await using var seed = new FakeRespServer(Topology(replica.Port), Topology(replacement.Port));
        await using var client = await ConnectAsync(seed.Port);

        await Assert.That(await client.Lists.LeftPopAsync("key", waitFor: TimeSpan.FromSeconds(1)))
            .IsEqualTo("value");
        await Assert.That(replacement.ReceivedCommands).IsEquivalentTo(["BLPOP key 1"]);
    }

    [Test]
    public async Task Transaction_RefreshesAfterQueueErrorAbortsExec()
    {
        await using var replacement = new FakeRespServer(
            FakeRespServer.OkReply, "+QUEUED\r\n"u8.ToArray(), "*1\r\n+OK\r\n"u8.ToArray());
        await using var replica = new FakeRespServer(
            FakeRespServer.OkReply, ReadOnlyReply, "-EXECABORT discarded\r\n"u8.ToArray());
        await using var seed = new FakeRespServer(Topology(replica.Port), Topology(replacement.Port));
        await using var client = await ConnectAsync(seed.Port);
        await using var transaction = client.CreateTransaction();
        var pending = transaction.Set("key", "value");

        await transaction.CommitAsync();

        await Assert.That(pending.Result).IsTrue();
        await Assert.That(replacement.ReceivedCommands).IsEquivalentTo(["MULTI", "SET key value", "EXEC"]);
    }

    [Test]
    public async Task Transaction_DoesNotReplayErrorsInsideExecutedResults()
    {
        await using var replica = new FakeRespServer(
            FakeRespServer.OkReply, "+QUEUED\r\n"u8.ToArray(), "+QUEUED\r\n"u8.ToArray(),
            [.. "*2\r\n+OK\r\n"u8, .. ReadOnlyReply]);
        await using var seed = new FakeRespServer(Topology(replica.Port));
        await using var client = await ConnectAsync(seed.Port);
        await using var transaction = client.CreateTransaction();
        var succeeded = transaction.Set("key", "first");
        var pending = transaction.Set("key", "second");

        await transaction.CommitAsync();

        await Assert.That(pending.Error).IsTypeOf<RespireServerException>();
        await Assert.That(succeeded.Result).IsTrue();
        await Assert.That(seed.CommandsSeen).IsEqualTo(1);
        await Assert.That(replica.CommandsSeen).IsEqualTo(4);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ChangingReadOnlyOwners_RespectsRetryLimit(bool batched)
    {
        await using var first = new FakeRespServer(ClusterRouter.RedirectLimit + 1, ReadOnlyReply);
        await using var second = new FakeRespServer(ClusterRouter.RedirectLimit + 1, ReadOnlyReply);
        var topologies = Enumerable.Range(0, ClusterRouter.RedirectLimit + 1)
            .Select(index => Topology(index % 2 == 0 ? first.Port : second.Port)).ToArray();
        await using var seed = new FakeRespServer(topologies);
        await using var client = await ConnectAsync(seed.Port);

        if (batched)
        {
            using var batch = client.CreateBatch();
            var pending = batch.Set("key", "value");
            await batch.TryExecuteAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(pending.Error).IsTypeOf<RespireServerException>();
        }
        else
        {
            await Assert.That(async () => await client.SetAsync("key", "value").AsTask().WaitAsync(TimeSpan.FromSeconds(10))).Throws<RespireServerException>();
        }

        await Assert.That(first.CommandsSeen + second.CommandsSeen).IsEqualTo(ClusterRouter.RedirectLimit + 1);
        await Assert.That(seed.CommandsSeen).IsEqualTo(ClusterRouter.RedirectLimit + 1);
    }

    [Test]
    public async Task Batch_UsesAlreadyRefreshedOwnerForLaterQueuedErrors()
    {
        await using var replacement = new FakeRespServer(FakeRespServer.OkReply);
        await using var replica = new FakeRespServer(ReadOnlyReply);
        await using var seed = new FakeRespServer(Topology(replica.Port), Topology(replacement.Port));
        await using var client = await ConnectAsync(seed.Port);
        using var batch = client.CreateBatch();
        var first = batch.Set("key", "first");
        var second = batch.Set("key", "second");

        await batch.ExecuteAsync();

        await Assert.That(first.Result).IsTrue();
        await Assert.That(second.Result).IsTrue();
        await Assert.That(seed.CommandsSeen).IsEqualTo(2);
        await Assert.That(replacement.ReceivedCommands)
            .IsEquivalentTo(["SET key first", "SET key second"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Refresh_UsesOtherDiscoveredPrimaryBeforeSeed()
    {
        await using var replacement = new FakeRespServer(FakeRespServer.OkReply);
        await using var healthy = new FakeRespServer(Topology(replacement.Port));
        await using var replica = new FakeRespServer(ReadOnlyReply);
        var initial = Encoding.ASCII.GetBytes(
            $"*2\r\n*3\r\n:0\r\n:0\r\n*2\r\n$9\r\n127.0.0.1\r\n:{healthy.Port}\r\n" +
            $"*3\r\n:1\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{replica.Port}\r\n");
        await using var seed = new FakeRespServer(initial, "-NOPERM discovery denied\r\n"u8.ToArray());
        await using var client = await ConnectAsync(seed.Port);

        await Assert.That(await client.SetAsync("key", "value")).IsTrue();
        await Assert.That(healthy.ReceivedCommands).IsEquivalentTo(["CLUSTER SLOTS"]);
        await Assert.That(seed.CommandsSeen).IsEqualTo(1);
    }

    [Test]
    public async Task RefreshCanDiscoverPromotedOwnerFromReachableReadOnlySource()
    {
        await using var replacement = new FakeRespServer(FakeRespServer.OkReply);
        var topologyRequests = 0;
        await using var sourceServer = new FakeRespServer(FakeRespServer.OkReply);
        sourceServer.ReplyOverride = (_, command) =>
        {
            if (command != "CLUSTER SLOTS") return null;
            return Interlocked.Increment(ref topologyRequests) == 1
                ? Topology(sourceServer.Port)
                : Topology(replacement.Port);
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            ClusterTopologyRefreshInterval = null,
            ReconnectPolicy = new() { InitialDelay = TimeSpan.Zero, JitterRatio = 0 },
            Endpoints = [new RespireEndpoint("127.0.0.1", sourceServer.Port)],
        });
        await client.Core.Cluster!.EnsureConnectedAsync(CancellationToken.None, discovery: null);
        var slot = ClusterHash.GetSlot("key");
        var source = await client.Core.Cluster.GetConnectionAsync(slot, CancellationToken.None, discovery: null);

        var recovered = await client.Core.Cluster.GetRedirectConnectionAsync(
            new RespireServerException("READONLY demoted"), source, CancellationToken.None, slot, discovery: null)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(recovered.Port).IsEqualTo(replacement.Port);
        await Assert.That(topologyRequests).IsGreaterThanOrEqualTo(2);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task RecoveryDeadlinePreservesOriginalErrorOrCallerCancellation(bool cancelCaller, bool configuredPolicy)
    {
        var clock = new RecoveryTestClock();
        await using var replica = new FakeRespServer(ReadOnlyReply);
        await using var seed = new FakeRespServer(Topology(replica.Port));
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true, Connections = 1, ConnectTimeout = TimeSpan.FromSeconds(2), CommandTimeout = null,
            ClusterRecoveryClock = clock,
            ReconnectPolicy = configuredPolicy ? new() { InitialDelay = TimeSpan.Zero, JitterRatio = 0 } : null,
            Endpoints = [new("127.0.0.1", seed.Port)],
        });
        var router = client.Core.Cluster!;
        var slot = ClusterHash.GetSlot("key");
        var source = await router.GetConnectionAsync(slot, CancellationToken.None, discovery: null);
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        seed.SuppressReply = _ => { requested.TrySetResult(); return true; };
        using var caller = new CancellationTokenSource();
        var original = new RespireServerException("READONLY original rejection");
        var recovery = router.GetRedirectConnectionAsync(original, source, caller.Token, slot, discovery: null).AsTask();
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancelCaller)
        {
            caller.Cancel();
            var error = await Assert.That(async () => await recovery.WaitAsync(TimeSpan.FromSeconds(5)))
                .Throws<OperationCanceledException>();
            await Assert.That(error!.CancellationToken).IsEqualTo(caller.Token);
        }
        else
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            var error = await Assert.That(async () => await recovery.WaitAsync(TimeSpan.FromSeconds(5)))
                .Throws<RespireServerException>();
            await Assert.That(ReferenceEquals(error, original)).IsTrue();
        }
    }

    [Test]
    public async Task Refresh_PropagatesCallerCancellation()
    {
        await using var replica = new FakeRespServer(ReadOnlyReply);
        await using var seed = new FakeRespServer(Topology(replica.Port));
        await using var client = await ConnectAsync(seed.Port);
        var refreshing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        seed.SuppressReply = _ => { refreshing.TrySetResult(); return true; };
        using var cancellation = new CancellationTokenSource();

        var pending = client.SetAsync("key", "value", cancellationToken: cancellation.Token).AsTask();
        await refreshing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.That(async () => await pending).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task ConcurrentReadOnlyRecoveriesShareDiscoveryAndWaiterCancellation()
    {
        await using var sourceServer = new FakeRespServer(FakeRespServer.OkReply);
        await using var replacement = new FakeRespServer(FakeRespServer.OkReply);
        var slotsReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var seed = new FakeRespServer
        {
            SuppressReply = command =>
            {
                if (command == "CLUSTER SLOTS") slotsReceived.TrySetResult();
                return command == "CLUSTER SLOTS";
            },
            ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? Topology(replacement.Port) : null,
        };
        var slot = ClusterHash.GetSlot("key");
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            ClusterTopologyRefreshInterval = null,
            ReconnectPolicy = new() { InitialDelay = TimeSpan.Zero, JitterRatio = 0 },
            Endpoints = [new RespireEndpoint("127.0.0.1", seed.Port)],
        });
        await using var source = await Respire.Networking.RespireConnection.ConnectAsync("127.0.0.1", sourceServer.Port);
        using var cancelledWaiter = new CancellationTokenSource();
        var router = client.Core.Cluster!;
        var rejection = new RespireServerException("READONLY demoted");
        var cancelled = router.GetRedirectConnectionAsync(rejection, source, cancelledWaiter.Token, slot, discovery: null).AsTask();
        await slotsReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var waiters = Enumerable.Range(0, 7)
            .Select(_ => router.GetRedirectConnectionAsync(rejection, source, CancellationToken.None, slot, discovery: null).AsTask())
            .ToArray();

        cancelledWaiter.Cancel();
        await Assert.That(async () => await cancelled).Throws<OperationCanceledException>();
        await seed.SendRawAsync(Topology(replacement.Port));
        var recovered = await Task.WhenAll(waiters).WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(recovered.Select(static connection => connection.Port)).IsEquivalentTo(
            Enumerable.Repeat(replacement.Port, waiters.Length));
        await Assert.That(seed.ReceivedCommands).IsEquivalentTo(["CLUSTER SLOTS"]);
    }

    [Test]
    public async Task NewReadOnlyCallerDoesNotJoinCanceledLastWaiterFlight()
    {
        await using var replica = new FakeRespServer(ReadOnlyReply);
        await using var replacement = new FakeRespServer(FakeRespServer.OkReply);
        var firstRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var topologyRequests = 0;
        await using var seed = new FakeRespServer(4, Topology(replica.Port))
        {
            ReplyOverride = (_, command) =>
            {
                if (command == "CLUSTER SLOTS" && Volatile.Read(ref topologyRequests) >= 2)
                    nextRefresh.TrySetResult();
                return command == "CLUSTER SLOTS" ? Topology(replacement.Port) : null;
            },
            SuppressReply = command =>
            {
                if (command != "CLUSTER SLOTS" || Interlocked.Increment(ref topologyRequests) != 1)
                    return false;
                firstRefresh.TrySetResult();
                return true;
            },
        };
        await using var client = await ConnectAsync(seed.Port);
        await using var source = await Respire.Networking.RespireConnection.ConnectAsync("127.0.0.1", replica.Port);
        using var cancellation = new CancellationTokenSource();
        var router = client.Core.Cluster!;
        var rejection = new RespireServerException("READONLY demoted");
        var canceled = router.GetRedirectConnectionAsync(
            rejection, source, cancellation.Token, ClusterHash.GetSlot("key"), discovery: null).AsTask();
        await firstRefresh.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.That(async () => await canceled).Throws<OperationCanceledException>();
        await seed.SendRawAsync(Topology(replica.Port));

        var lateCaller = router.GetRedirectConnectionAsync(
            rejection, source, CancellationToken.None, ClusterHash.GetSlot("key"), discovery: null).AsTask();
        await nextRefresh.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var recovered = await lateCaller.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(recovered.Port).IsEqualTo(replacement.Port);
        await Assert.That(topologyRequests).IsGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task ConcurrentReadOnlyRecoveriesDiscoverEachCallersSlot()
    {
        await using var sourceServer = new FakeRespServer(ReadOnlyReply);
        await using var firstReplacement = new FakeRespServer(FakeRespServer.OkReply);
        await using var secondReplacement = new FakeRespServer(FakeRespServer.OkReply);
        await using var seed = new FakeRespServer(TopologyRanges(sourceServer.Port, sourceServer.Port));
        await using var client = await ConnectAsync(seed.Port);
        await using var source = await Respire.Networking.RespireConnection.ConnectAsync("127.0.0.1", sourceServer.Port);
        var firstRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        seed.SuppressReply = command =>
        {
            if (command == "CLUSTER SLOTS")
            {
                if (seed.CommandsSeen == 2) firstRefresh.TrySetResult();
                if (seed.CommandsSeen == 3) secondRefresh.TrySetResult();
                return true;
            }
            return false;
        };

        var rejection = new RespireServerException("READONLY demoted");
        var router = client.Core.Cluster!;
        var first = router.GetRedirectConnectionAsync(rejection, source, CancellationToken.None, 100, discovery: null).AsTask();
        await firstRefresh.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = router.GetRedirectConnectionAsync(rejection, source, CancellationToken.None, 200, discovery: null).AsTask();
        await seed.SendRawAsync(TopologyRanges(firstReplacement.Port, sourceServer.Port));
        await secondRefresh.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await seed.SendRawAsync(TopologyRanges(firstReplacement.Port, secondReplacement.Port));

        var recovered = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(recovered.Select(static connection => connection.Port)).IsEquivalentTo(
            [firstReplacement.Port, secondReplacement.Port]);
        await Assert.That(seed.CommandsSeen).IsEqualTo(3);
    }

    [Test]
    public async Task NoRedirect_PreservesReadOnlyWithoutRefresh()
    {
        await using var replica = new FakeRespServer(ReadOnlyReply);
        await using var seed = new FakeRespServer(Topology(replica.Port));
        await using var client = await ConnectAsync(seed.Port);

        await Assert.That(async () =>
        {
            using var result = await client.ExecuteAsync(
                RespireCommands.String.SET, ["key", "value"], RespireCommandFlags.NoRedirect);
        }).Throws<RespireServerException>();
        await Assert.That(seed.CommandsSeen).IsEqualTo(1);
    }

    [Test]
    public async Task TrackedScript_RefreshesAndRetainsReplacementIdentity()
    {
        await using var replacement = new FakeRespServer(":42\r\n"u8.ToArray(), ":0\r\n"u8.ToArray(), FakeRespServer.OkReply);
        await using var replica = new FakeRespServer(":41\r\n"u8.ToArray(), ":0\r\n"u8.ToArray(), ReadOnlyReply);
        await using var seed = new FakeRespServer(Topology(replica.Port), Topology(replacement.Port));
        await using var client = await ConnectAsync(seed.Port);
        var script = RespireScript.Create("return redis.call('SET', KEYS[1], ARGV[1])");

        var execution = await client.StartTrackedScriptExecutionAsync(
            script, ["key"], ["value"], CancellationToken.None, requireReliableCorrectionOrdering: true);
        using var result = await execution.Response;

        await Assert.That(result.AsString()).IsEqualTo("OK");
        await Assert.That(execution.ConnectionIdentity.Endpoint.Port).IsEqualTo(replacement.Port);
        await Assert.That(execution.ConnectionIdentity.ServerClientId).IsEqualTo(42);
    }

    [Test]
    [Arguments("READONLY", false)]
    [Arguments("ERR", true)]
    public async Task UnrecoverableErrors_DoNotRefresh(string code, bool keyed)
    {
        var reply = Encoding.ASCII.GetBytes($"-{code} command failed\r\n");
        await using var replica = new FakeRespServer(reply);
        await using var seed = new FakeRespServer(Topology(replica.Port), reply);
        await using var client = await ConnectAsync(seed.Port);

        await Assert.That(async () =>
        {
            if (keyed)
            {
                await client.SetAsync("key", "value");
            }
            else
            {
                await client.PingAsync();
            }
        }).Throws<RespireServerException>();
        await Assert.That(seed.CommandsSeen).IsEqualTo(keyed ? 1 : 2);
    }

    [Test]
    public async Task Standalone_PreservesReadOnlyWithoutTopologyCommands()
    {
        await using var server = new FakeRespServer(ReadOnlyReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await Assert.That(async () => await client.SetAsync("key", "value")).Throws<RespireServerException>();
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(["SET key value"]);
    }

    private static ValueTask<RespireClient> ConnectAsync(int port, TimeSpan? connectTimeout = null)
        => RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Connections = 1,
            ConnectTimeout = connectTimeout ?? TimeSpan.FromSeconds(1),
            CommandTimeout = null,
            Endpoints = { new RespireEndpoint("127.0.0.1", port) },
        });

    private static byte[] Topology(int port)
    {
        var slot = ClusterHash.GetSlot("key");
        return Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:{slot}\r\n:{slot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{port}\r\n");
    }

    private static byte[] FullTopology(int port)
        => Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{port}\r\n");

    private static byte[] TopologyRanges(int lowerPort, int upperPort)
        => Encoding.ASCII.GetBytes(
            $"*2\r\n*3\r\n:0\r\n:199\r\n*2\r\n$9\r\n127.0.0.1\r\n:{lowerPort}\r\n" +
            $"*3\r\n:200\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{upperPort}\r\n");
    [Test]
    public async Task UnavailableEndpointRemainsReservedAndRefusesConnections()
    {
        using var unavailable = new ReservedUnavailablePort();
        var port = unavailable.Port;
        using var competing = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            ExclusiveAddressUse = true,
        };
        await Assert.That(() => competing.Bind(new IPEndPoint(IPAddress.Loopback, port))).Throws<SocketException>();
        using var connection = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var error = await Assert.That(async () => await connection.ConnectAsync(
            new IPEndPoint(IPAddress.Loopback, port), timeout.Token)).Throws<SocketException>();
        await Assert.That(error!.SocketErrorCode).IsEqualTo(SocketError.ConnectionRefused);
    }

}
