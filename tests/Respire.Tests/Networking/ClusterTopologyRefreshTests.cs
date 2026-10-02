using System.Text;
using System.Threading.Channels;
using Respire.Infrastructure;
using Respire.Internal;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClusterTopologyRefreshTests
{
    [Test]
    public async Task CreateDoesNotConnectUntilFirstClusterOperation()
    {
        await using var seed = new FakeRespServer(FakeRespServer.OkReply);
        var clock = new ManualTopologyRefreshClock();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            ClusterTopologyRefreshInterval = TimeSpan.FromMilliseconds(50),
            ClusterTopologyRefreshClock = clock,
            Endpoints = [new RespireEndpoint("127.0.0.1", seed.Port)],
        });
        var router = client.Core.Cluster!;

        await Assert.That(seed.ReceivedCommands).IsEmpty();
        await Assert.That(clock.Created.Task.IsCompleted).IsFalse();

        await router.EnsureConnectedAsync(CancellationToken.None, discovery: null);
        var timer = await clock.Created.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.That(timer.DueTime).IsLessThanOrEqualTo(TimeSpan.FromMilliseconds(50));
        clock.Advance(TimeSpan.FromMilliseconds(50));
        timer.Fire();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (seed.ReceivedCommands.Count(command => command == "CLUSTER SLOTS") < 2)
            await Task.Delay(10, timeout.Token);
    }

    [Test]
    public async Task PeriodicDeadlineInterruptsMovedDebounce()
    {
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var seed = new FakeRespServer(FakeRespServer.OkReply);
        seed.ReplyOverride = (_, command) =>
        {
            if (command != "CLUSTER SLOTS") return null;
            if (Interlocked.Increment(ref calls) >= 2) refreshed.TrySetResult();
            return Topology(seed.Port, seed.Port);
        };
        var clock = new ManualTopologyRefreshClock();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            ClusterTopologyRefreshInterval = TimeSpan.FromMilliseconds(500),
            ClusterTopologyRefreshClock = clock,
            Endpoints = [new RespireEndpoint("127.0.0.1", seed.Port)],
        });
        var router = client.Core.Cluster!;
        await router.EnsureConnectedAsync(CancellationToken.None, discovery: null);
        var periodic = await clock.Created.Task.WaitAsync(TimeSpan.FromSeconds(2));

        router.SignalMovedTopologyRefresh();
        // The MOVED wake disposes the periodic wait and re-arms it with the same remaining time
        // (the manual clock has not moved). Fire the re-armed timer, not the disposed original.
        var rearmed = await clock.NextTimerAsync(periodic.DueTime, superseded: periodic).WaitAsync(TimeSpan.FromSeconds(2));
        clock.Advance(TimeSpan.FromMilliseconds(500));
        rearmed.Fire();

        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task LongRefreshIntervalUsesTimerSafeSegments()
    {
        await using var seed = new FakeRespServer(FakeRespServer.OkReply);
        var clock = new ManualTopologyRefreshClock();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            ClusterTopologyRefreshInterval = TimeSpan.FromDays(90),
            ClusterTopologyRefreshClock = clock,
            Endpoints = [new RespireEndpoint("127.0.0.1", seed.Port)],
        });
        var router = client.Core.Cluster!;
        await router.EnsureConnectedAsync(CancellationToken.None, discovery: null);

        var firstSegment = await clock.Created.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.That(firstSegment.DueTime).IsEqualTo(TimeSpan.FromDays(24));
    }

    [Test]
    public async Task ForcedRefreshSignalBypassesRecentSuccessWindow()
    {
        var secondRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fourthRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var seed = new FakeRespServer(FakeRespServer.OkReply);
        seed.ReplyOverride = (_, command) =>
        {
            if (command != "CLUSTER SLOTS") return null;
            var call = Interlocked.Increment(ref calls);
            if (call == 2) secondRefresh.TrySetResult();
            if (call >= 3) thirdRefresh.TrySetResult();
            if (call >= 4) fourthRefresh.TrySetResult();
            return Topology(seed.Port, seed.Port);
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            ClusterTopologyRefreshInterval = null,
            ClusterTopologyRefreshClock = new ManualTopologyRefreshClock(),
            Endpoints = [new RespireEndpoint("127.0.0.1", seed.Port)],
        });
        var router = client.Core.Cluster!;
        await router.EnsureConnectedAsync(CancellationToken.None, discovery: null);

        router.SignalTopologyRefresh();
        await secondRefresh.Task.WaitAsync(TimeSpan.FromSeconds(2));
        router.SignalTopologyRefresh(force: true);
        await thirdRefresh.Task.WaitAsync(TimeSpan.FromSeconds(2));
        router.SignalMovedTopologyRefresh();
        await Task.Delay(50);
        router.SignalTopologyRefresh(force: true);
        await fourthRefresh.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task RepeatedPrimaryDisconnectsWithinSpacingShareOneForcedRefresh()
    {
        var refreshes = 0;
        var secondRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var seed = new FakeRespServer(FakeRespServer.OkReply);
        seed.ReplyOverride = (_, command) =>
        {
            if (command != "CLUSTER SLOTS") return null;
            var count = Interlocked.Increment(ref refreshes);
            if (count == 2) secondRefresh.TrySetResult();
            if (count >= 3) thirdRefresh.TrySetResult();
            return Topology(seed.Port, seed.Port);
        };
        var clock = new ManualTopologyRefreshClock();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            ClusterTopologyRefreshInterval = null,
            ClusterTopologyRefreshClock = clock,
            Endpoints = [new RespireEndpoint("127.0.0.1", seed.Port)],
        });
        var router = client.Core.Cluster!;
        await router.EnsureConnectedAsync(CancellationToken.None, discovery: null);

        router.SignalPrimaryDisconnectRefresh();
        await secondRefresh.Task.WaitAsync(TimeSpan.FromSeconds(2));
        router.SignalPrimaryDisconnectRefresh();
        router.SignalPrimaryDisconnectRefresh();
        var spacing = await clock.NextTimerAsync(TimeSpan.FromSeconds(1)).WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(100);
        await Assert.That(Volatile.Read(ref refreshes)).IsEqualTo(2);

        // The suppressed disconnects are not lost: one trailing refresh runs when the spacing ends,
        // even with the periodic timer disabled.
        clock.Advance(TimeSpan.FromSeconds(1));
        spacing.Fire();
        await thirdRefresh.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(100);
        await Assert.That(Volatile.Read(ref refreshes)).IsEqualTo(3);
    }

    [Test]
    public async Task SuppressedPrimaryDisconnectWakesWorkerDuringMovedDebounce()
    {
        var refreshes = 0;
        var secondRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var seed = new FakeRespServer(FakeRespServer.OkReply);
        seed.ReplyOverride = (_, command) =>
        {
            if (command != "CLUSTER SLOTS") return null;
            var count = Interlocked.Increment(ref refreshes);
            if (count == 2) secondRefresh.TrySetResult();
            if (count >= 3) thirdRefresh.TrySetResult();
            return Topology(seed.Port, seed.Port);
        };
        // The manual clock never ends the MOVED debounce on its own.
        var clock = new ManualTopologyRefreshClock();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            ClusterTopologyRefreshInterval = null,
            ClusterTopologyRefreshClock = clock,
            Endpoints = [new RespireEndpoint("127.0.0.1", seed.Port)],
        });
        var router = client.Core.Cluster!;
        await router.EnsureConnectedAsync(CancellationToken.None, discovery: null);

        router.SignalPrimaryDisconnectRefresh();
        await secondRefresh.Task.WaitAsync(TimeSpan.FromSeconds(2));
        router.SignalMovedTopologyRefresh();
        _ = await clock.NextTimerAsync(TimeSpan.FromSeconds(5)).WaitAsync(TimeSpan.FromSeconds(2));
        // Inside the spacing window: the worker re-arms for the end of the window, not the debounce.
        router.SignalPrimaryDisconnectRefresh();
        var spacing = await clock.NextTimerAsync(TimeSpan.FromSeconds(1)).WaitAsync(TimeSpan.FromSeconds(2));
        clock.Advance(TimeSpan.FromSeconds(1));
        spacing.Fire();

        await thirdRefresh.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task ForceSignalRacingWithDelayDoesNotLeakIntoNextSignal()
    {
        var refreshes = 0;
        var secondRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unexpectedRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var seed = new FakeRespServer(FakeRespServer.OkReply);
        seed.ReplyOverride = (_, command) =>
        {
            if (command == "CLUSTER SLOTS")
            {
                var count = Interlocked.Increment(ref refreshes);
                if (count == 2) secondRefresh.TrySetResult();
                if (count >= 3) unexpectedRefresh.TrySetResult();
                return Topology(seed.Port, seed.Port);
            }
            return null;
        };
        var clock = new ManualTopologyRefreshClock();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            ClusterTopologyRefreshInterval = null,
            ClusterTopologyRefreshClock = clock,
            Endpoints = [new RespireEndpoint("127.0.0.1", seed.Port)],
        });
        var router = client.Core.Cluster!;
        await router.EnsureConnectedAsync(CancellationToken.None, discovery: null);

        router.SignalMovedTopologyRefresh();
        _ = await clock.NextTimerAsync(TimeSpan.FromSeconds(5)).WaitAsync(TimeSpan.FromSeconds(2));
        router.SignalTopologyRefresh(force: true);
        await secondRefresh.Task.WaitAsync(TimeSpan.FromSeconds(2));

        router.SignalMovedTopologyRefresh();
        var secondDelayTask = clock.NextTimerAsync(TimeSpan.FromSeconds(5));
        var nextEvent = await Task.WhenAny(secondDelayTask, unexpectedRefresh.Task).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(ReferenceEquals(nextEvent, secondDelayTask)).IsTrue();
        var secondDelay = await secondDelayTask;
        await Task.Delay(100);
        await Assert.That(Volatile.Read(ref refreshes)).IsEqualTo(2);
        secondDelay.Fire();
        await Task.Delay(100);
        await Assert.That(Volatile.Read(ref refreshes)).IsEqualTo(2);
    }

    [Test]
    public async Task RepeatedMovedSignalsDoNotRestartRefreshDebounce()
    {
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var seed = new FakeRespServer(FakeRespServer.OkReply);
        seed.ReplyOverride = (_, command) =>
        {
            if (command != "CLUSTER SLOTS") return null;
            if (Interlocked.Increment(ref calls) >= 2) refreshed.TrySetResult();
            return Topology(seed.Port, seed.Port);
        };
        var clock = new ManualTopologyRefreshClock();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            ClusterTopologyRefreshInterval = null,
            ClusterTopologyRefreshClock = clock,
            Endpoints = [new RespireEndpoint("127.0.0.1", seed.Port)],
        });
        var router = client.Core.Cluster!;
        await router.EnsureConnectedAsync(CancellationToken.None, discovery: null);

        router.SignalMovedTopologyRefresh();
        _ = await clock.NextTimerAsync(TimeSpan.FromSeconds(5)).WaitAsync(TimeSpan.FromSeconds(2));
        clock.Advance(TimeSpan.FromSeconds(1));
        router.SignalMovedTopologyRefresh();
        _ = await clock.NextTimerAsync(TimeSpan.FromSeconds(4)).WaitAsync(TimeSpan.FromSeconds(2));
        clock.Advance(TimeSpan.FromSeconds(1));
        router.SignalMovedTopologyRefresh();
        var finalDelay = await clock.NextTimerAsync(TimeSpan.FromSeconds(3)).WaitAsync(TimeSpan.FromSeconds(2));
        clock.Advance(TimeSpan.FromSeconds(3));
        finalDelay.Fire();
        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task WatchedMovedRouteSchedulesTopologyRefresh()
    {
        await using var seed = new FakeRespServer(FakeRespServer.OkReply);
        var clock = new ManualTopologyRefreshClock();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            ClusterTopologyRefreshInterval = null,
            ClusterTopologyRefreshClock = clock,
            Endpoints = [new RespireEndpoint("127.0.0.1", seed.Port)],
        });
        var router = client.Core.Cluster!;
        await router.EnsureConnectedAsync(CancellationToken.None, discovery: null);
        var connection = await router.GetConnectionAsync(0, CancellationToken.None, discovery: null);

        router.LearnWatchedRoute(new RespireServerException(
            $"MOVED 0 127.0.0.1:{seed.Port}"), connection, watchedSlot: null);

        _ = await clock.NextTimerAsync(TimeSpan.FromSeconds(5)).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task DisposeCancelsInflightTopologyRefresh()
    {
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var seed = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            ClusterTopologyRefreshInterval = null,
            Endpoints = [new RespireEndpoint("127.0.0.1", seed.Port)],
        });
        var router = client.Core.Cluster!;
        await router.EnsureConnectedAsync(CancellationToken.None, discovery: null);
        seed.SuppressReply = command =>
        {
            if (command == "CLUSTER SLOTS") refreshStarted.TrySetResult();
            return command == "CLUSTER SLOTS";
        };

        router.SignalTopologyRefresh();
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task RefreshUsesDisconnectedKnownMasterWhenSeedStalls()
    {
        var alternativeRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var alternative = new FakeRespServer(FakeRespServer.OkReply);
        alternative.ReplyOverride = (_, command) =>
        {
            if (command == "CLUSTER SLOTS") alternativeRefresh.TrySetResult();
            return TwoMasterTopology(alternative.Port, 8191, 8192, alternative.Port);
        };
        await using var seed = new FakeRespServer(FakeRespServer.OkReply);
        var initialTopology = TwoMasterTopology(seed.Port, 8191, 8192, alternative.Port);
        seed.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? initialTopology : null;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            ClusterTopologyRefreshInterval = null,
            ConnectTimeout = TimeSpan.FromMilliseconds(250),
            CommandTimeout = null,
            Endpoints = [new RespireEndpoint("127.0.0.1", seed.Port)],
        });
        seed.SuppressReply = command => command == "CLUSTER SLOTS";

        client.Core.Cluster!.SignalTopologyRefresh(force: true);
        await alternativeRefresh.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task RefreshCandidateTimeoutStartsAfterReconnectBackoff()
    {
        var alternativeRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var alternative = new FakeRespServer(FakeRespServer.OkReply);
        alternative.ReplyOverride = (_, command) =>
        {
            if (command == "CLUSTER SLOTS") alternativeRefresh.TrySetResult();
            return TwoMasterTopology(alternative.Port, 8191, 8192, alternative.Port);
        };
        await using var seed = new FakeRespServer(FakeRespServer.OkReply);
        seed.ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
            ? TwoMasterTopology(seed.Port, 8191, 8192, alternative.Port)
            : null;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            ClusterTopologyRefreshInterval = null,
            CommandTimeout = TimeSpan.FromMilliseconds(50),
            Endpoints = [new RespireEndpoint("127.0.0.1", seed.Port)],
            ReconnectPolicy = new() { InitialDelay = TimeSpan.FromMilliseconds(250), MaxDelay = TimeSpan.FromMilliseconds(250),
                JitterRatio = 0, MaxAttempts = 2 },
        });
        seed.SuppressReply = command => command == "CLUSTER SLOTS";

        client.Core.Cluster!.SignalTopologyRefresh(force: true);

        await alternativeRefresh.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task RefreshUsesKnownReplicaWhenPrimariesAndSeedsStall()
    {
        var replicaRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var replica = new FakeRespServer(FakeRespServer.OkReply);
        replica.ReplyOverride = (_, command) =>
        {
            if (command == "CLUSTER SLOTS") replicaRefresh.TrySetResult();
            return Topology(replica.Port, replica.Port);
        };
        await using var seed = new FakeRespServer(FakeRespServer.OkReply);
        var replicaPort = replica.Port;
        seed.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? Topology(seed.Port, replicaPort) : null;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            ClusterTopologyRefreshInterval = null,
            CommandTimeout = TimeSpan.FromMilliseconds(50),
            Endpoints = [new RespireEndpoint("127.0.0.1", seed.Port)],
        });
        seed.SuppressReply = command => command == "CLUSTER SLOTS";

        client.Core.Cluster!.SignalTopologyRefresh(force: true);

        await replicaRefresh.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task RefreshCandidatesTryConfiguredSeedBeforeDisconnectedMasters()
    {
        await using var firstMaster = new FakeRespServer(FakeRespServer.OkReply);
        await using var secondMaster = new FakeRespServer(FakeRespServer.OkReply);
        await using var seed = new FakeRespServer(FakeRespServer.OkReply);
        seed.ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
            ? TwoMasterTopology(firstMaster.Port, 8191, 8192, secondMaster.Port)
            : null;
        var seedEndpoint = new RespireEndpoint("127.0.0.1", seed.Port);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            ClusterTopologyRefreshInterval = null,
            Endpoints = [seedEndpoint],
        });
        var router = client.Core.Cluster!;
        seed.CloseConnections();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (router.GetMultiplexer(seedEndpoint).IsConnected) await Task.Delay(10, timeout.Token);

        var firstNode = router.GetMultiplexer(new RespireEndpoint("127.0.0.1", firstMaster.Port));
        var secondNode = router.GetMultiplexer(new RespireEndpoint("127.0.0.1", secondMaster.Port));
        await firstNode.EnsureConnectedAsync(CancellationToken.None);
        await secondNode.EnsureConnectedAsync(CancellationToken.None);

        var buildCandidates = typeof(ClusterRouter).GetMethod("GetTopologyRefreshCandidates",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var candidates = (List<ClusterRouter.TopologyRefreshCandidate>)buildCandidates.Invoke(router, null)!;
        var ordered = ClusterRouter.OrderTopologyRefreshCandidates(candidates);

        // One connected node first, then the disconnected configured seed, so several stalled
        // connected nodes cannot use up the deadline before the seed is tried. The remaining
        // connected node follows, and lazy fallbacks go last.
        await Assert.That(ordered.Count).IsEqualTo(3);
        await Assert.That(ordered[0].IsConnected).IsTrue();
        await Assert.That(ordered[1].IsConfiguredSeed).IsTrue();
        await Assert.That(ordered[1].Endpoint).IsEqualTo(seedEndpoint);
        await Assert.That(ordered[1].IsConnected).IsFalse();
        await Assert.That(ordered[2].IsConnected).IsTrue();
    }

    [Test]
    public async Task RefreshCandidatesDeduplicateReplicaEndpointsIgnoringHostCase()
    {
        await using var seed = new FakeRespServer(FakeRespServer.OkReply);
        seed.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? Topology(seed.Port, seed.Port + 1) : null;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            ClusterTopologyRefreshInterval = null,
            Endpoints = [new RespireEndpoint("127.0.0.1", seed.Port)],
        });
        var router = client.Core.Cluster!;
        typeof(ClusterRouter).GetField("_replicas", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(router, new[]
            {
                new ClusterTopologyReplica(new RespireEndpoint("redis-1", 6379), "replica-a", []),
                new ClusterTopologyReplica(new RespireEndpoint("REDIS-1", 6379), "replica-b", []),
            });

        var buildCandidates = typeof(ClusterRouter).GetMethod("GetTopologyRefreshCandidates",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var candidates = (List<ClusterRouter.TopologyRefreshCandidate>)buildCandidates.Invoke(router, null)!;
        var aliases = candidates.Where(candidate => candidate.Endpoint.Port == 6379
            && StringComparer.OrdinalIgnoreCase.Equals(candidate.Endpoint.Host, "redis-1")).ToArray();

        await Assert.That(aliases.Length).IsEqualTo(1);
        await Assert.That(aliases[0].Node).IsNull();
    }

    [Test]
    public async Task HealthyRefreshDoesNotCreateReplicaTransports()
    {
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var seed = new FakeRespServer(FakeRespServer.OkReply);
        var replicaEndpoint = new RespireEndpoint("127.0.0.1", 1);
        seed.ReplyOverride = (_, command) =>
        {
            if (command != "CLUSTER SLOTS") return null;
            if (Interlocked.Increment(ref calls) >= 2) refreshed.TrySetResult();
            return Topology(seed.Port, replicaEndpoint.Port);
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            ClusterTopologyRefreshInterval = null,
            Endpoints = [new RespireEndpoint("127.0.0.1", seed.Port)],
        });
        var router = client.Core.Cluster!;
        await Assert.That(router.GetReplicas().Single().Endpoint).IsEqualTo(replicaEndpoint);

        router.SignalTopologyRefresh(force: true);
        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var identities = (ClusterNodeIdentityIndex)typeof(ClusterRouter).GetField("_identities",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(router)!;
        lock (router.NodeStateGate)
        {
            // A connected primary answered first, so no transport exists for the replica or its alias.
            if (identities.TryGet(replicaEndpoint) is not null || identities.TryGet(new RespireEndpoint("replica", 1)) is not null)
                throw new InvalidOperationException("A healthy refresh created a replica transport.");
        }
    }

    [Test]
    public async Task ConcurrentSignalsShareRefreshAndPublishReplicaMetadata()
    {
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slotsCalls = 0;
        await using var seed = new FakeRespServer
        {
            SuppressReply = command =>
            {
                if (command != "CLUSTER SLOTS") return false;
                if (Interlocked.Increment(ref slotsCalls) == 1) return false;
                refreshStarted.TrySetResult();
                return true;
            },
        };
        var replicaPort = seed.Port + 1;
        seed.ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
            ? Topology(seed.Port, replicaPort)
            : null;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            ClusterTopologyRefreshInterval = null,
            Endpoints = [new RespireEndpoint("127.0.0.1", seed.Port)],
        });
        var router = client.Core.Cluster!;

        router.SignalTopologyRefresh();
        router.SignalTopologyRefresh();
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        router.SignalTopologyRefresh();
        router.SignalTopologyRefresh();
        await seed.SendRawAsync(Encoding.UTF8.GetBytes(
            "*1\r\n*4\r\n:0\r\n:16383\r\n"
            + $"*3\r\n$9\r\n127.0.0.1\r\n:{seed.Port}\r\n$9\r\nmaster-id\r\n"
            + $"*4\r\n$9\r\n127.0.0.1\r\n:{replicaPort}\r\n$10\r\nreplica-id\r\n%1\r\n+hostname\r\n$7\r\nreplica\r\n"));

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (router.GetReplicas().Length == 0) await Task.Delay(10, deadline.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        var replica = router.GetReplicas().Single();
        await Assert.That(replica.Endpoint).IsEqualTo(new RespireEndpoint("127.0.0.1", replicaPort));
        await Assert.That(replica.NodeId).IsEqualTo("replica-id");
        await Assert.That(replica.Aliases).Contains(new RespireEndpoint("replica", replicaPort));
        await Assert.That(seed.ReceivedCommands.Count(command => command == "CLUSTER SLOTS")).IsEqualTo(2);
    }

    [Test]
    public async Task ForcedRefreshRunsAfterReadonlyFlightCompletes()
    {
        var fullRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slotsCalls = 0;
        await using var seed = new FakeRespServer(FakeRespServer.OkReply);
        seed.ReplyOverride = (_, command) =>
        {
            if (command != "CLUSTER SLOTS") return null;
            if (Interlocked.Increment(ref slotsCalls) >= 2) fullRefresh.TrySetResult();
            return Topology(seed.Port, seed.Port);
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            ClusterTopologyRefreshInterval = null,
            Endpoints = [new RespireEndpoint("127.0.0.1", seed.Port)],
        });
        var router = client.Core.Cluster!;
        await router.EnsureConnectedAsync(CancellationToken.None, discovery: null);

        using var flightCancellation = new CancellationTokenSource();
        var flight = router.SharedRefreshCoordinator.JoinReadOnly(ClusterHash.GetSlot("key"),
            new RespireEndpoint("127.0.0.1", seed.Port), flightCancellation, discoveryLease: null).Flight;

        router.SignalTopologyRefresh(force: true);
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            while (Volatile.Read(ref flight.Waiters) < 2) await Task.Delay(1, timeout.Token);
        await Task.Delay(50);
        // The READONLY repair does not answer a topology request.
        await Assert.That(Volatile.Read(ref slotsCalls)).IsEqualTo(1);

        router.SharedRefreshCoordinator.Complete(flight, result: true, failure: null);

        await fullRefresh.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.That(Volatile.Read(ref slotsCalls)).IsEqualTo(2);
    }

    [Test]
    [Arguments(1, true)]
    [Arguments(2, false)]
    public async Task CancelledReadOnlyWaiterAbandonsFlightOnlyWhenLast(int waiters, bool expectAbandoned)
    {
        var policy = new RespireReconnectPolicy { InitialDelay = TimeSpan.Zero, MaxAttempts = 3 };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            ClusterTopologyRefreshInterval = null,
            Endpoints = [new RespireEndpoint("127.0.0.1", 1)],
            ReconnectPolicy = policy,
        });
        var router = client.Core.Cluster!;
        using var flightCancellation = new CancellationTokenSource();
        var flight = router.SharedRefreshCoordinator.JoinReadOnly(0, new RespireEndpoint("127.0.0.1", 1),
            flightCancellation, discoveryLease: null).Flight;
        // Model another joined caller when checking last-waiter cancellation.
        if (waiters == 2) flight.Waiters++;
        var round = new ClusterRouter.DiscoveryRound(router, policy);
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();

        var awaitShared = typeof(ClusterRouter).GetMethod("AwaitSharedRefreshAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var wait = (Task<bool>)awaitShared.Invoke(router, [flight, caller.Token, round])!;
        await Assert.That(async () => await wait).Throws<OperationCanceledException>();

        var published = router.SharedRefreshCoordinator.IsPublished(flight);
        await Assert.That(flight.Abandoned).IsEqualTo(expectAbandoned);
        await Assert.That(flightCancellation.IsCancellationRequested).IsEqualTo(expectAbandoned);
        // An abandoned flight never stays joinable.
        await Assert.That(published).IsEqualTo(!expectAbandoned);

        // The shared flight owns the discovery round until its work or cancellation unwinds.
        var canceled = new OperationCanceledException(caller.Token);
        round.RecordCommandFailure(canceled, discoveryPending: true, callerToken: caller.Token);
        await Assert.That(round.TerminalError).IsNull();
        round.Finish();
    }

    [Test]
    public async Task PartialRefreshKeepsUncoveredOwnersAndReplicaMetadata()
    {
        await using var replicaServer = new FakeRespServer(FakeRespServer.OkReply);
        await using var seed = new FakeRespServer(FakeRespServer.OkReply);
        var replicaPort = replicaServer.Port;
        var refreshReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slotsCalls = 0;
        var incompleteTopology = Encoding.UTF8.GetBytes(
            "*1\r\n*4\r\n:0\r\n:100\r\n"
            + $"*3\r\n$9\r\n127.0.0.1\r\n:{seed.Port}\r\n$9\r\nmaster-id\r\n"
            + $"*4\r\n$9\r\n127.0.0.1\r\n:{replicaPort}\r\n$10\r\nreplica-id\r\n%1\r\n+hostname\r\n$9\r\n127.0.0.1\r\n");
        seed.ReplyOverride = (_, command) =>
        {
            if (command != "CLUSTER SLOTS") return null;
            if (Interlocked.Increment(ref slotsCalls) == 1)
                return Topology(seed.Port, replicaPort, "127.0.0.1");
            refreshReceived.TrySetResult();
            return incompleteTopology;
        };
        replicaServer.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? incompleteTopology : null;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            ClusterTopologyRefreshInterval = null,
            Endpoints = [new RespireEndpoint("127.0.0.1", seed.Port)],
        });
        var router = client.Core.Cluster!;
        var originalOwner = router.GetSlotOwnerEndpoint(0);
        var originalReplica = router.GetReplicas().Single();
        var staleReplicaTransport = router.GetMultiplexer(originalReplica.Endpoint);

        router.SignalTopologyRefresh();
        await refreshReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);

        await Assert.That(router.GetSlotOwnerEndpoint(0)).IsEqualTo(originalOwner);
        await Assert.That(router.GetSlotOwnerEndpoint(16383)).IsEqualTo(originalOwner);
        var replica = router.GetReplicas().Single();
        await Assert.That(replica.Endpoint).IsEqualTo(originalReplica.Endpoint);
        await Assert.That(replica.NodeId).IsEqualTo(originalReplica.NodeId);
        await Assert.That(replica.Aliases).IsEquivalentTo(originalReplica.Aliases);
        await Assert.That(staleReplicaTransport.IsRetired).IsTrue();
        // The refresh continues through known replicas even after the seed returns a partial map.
        await Assert.That(replicaServer.ReceivedCommands).Contains("CLUSTER SLOTS");
    }

    [Test]
    public async Task PartialRefreshContinuesToCandidateWithCompleteTopology()
    {
        await using var second = new FakeRespServer(FakeRespServer.OkReply);
        await using var seed = new FakeRespServer(FakeRespServer.OkReply);
        var slotsCalls = 0;
        // Slots 0-100 moved to the second primary; the rest of the first primary's range is not
        // reported, as with a lost shard and cluster-require-full-coverage no.
        var partial = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:0\r\n:100\r\n*2\r\n$9\r\n127.0.0.1\r\n:{second.Port}\r\n");
        seed.ReplyOverride = (_, command) => command != "CLUSTER SLOTS" ? null
            : Interlocked.Increment(ref slotsCalls) == 1
                ? TwoMasterTopology(seed.Port, 8191, 8192, second.Port)
                : partial;
        second.ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
            ? Encoding.ASCII.GetBytes(
                $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{second.Port}\r\n")
            : null;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            ClusterTopologyRefreshInterval = null,
            Endpoints = [new RespireEndpoint("127.0.0.1", seed.Port)],
        });
        var router = client.Core.Cluster!;
        var secondEndpoint = new RespireEndpoint("127.0.0.1", second.Port);

        router.SignalTopologyRefresh(force: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!second.ReceivedCommands.Contains("CLUSTER SLOTS")
            || router.GetSlotOwnerEndpoint(101) != secondEndpoint)
            await Task.Delay(10, timeout.Token);

        await Assert.That(router.GetSlotOwnerEndpoint(0)).IsEqualTo(secondEndpoint);
        await Assert.That(router.GetSlotOwnerEndpoint(100)).IsEqualTo(secondEndpoint);
        await Assert.That(router.GetSlotOwnerEndpoint(101)).IsEqualTo(secondEndpoint);
        await Assert.That(router.GetSlotOwnerEndpoint(8191)).IsEqualTo(secondEndpoint);
        await Assert.That(router.GetSlotOwnerEndpoint(8192)).IsEqualTo(secondEndpoint);
        await Assert.That(second.ReceivedCommands).Contains("CLUSTER SLOTS");
        await Assert.That(router.GetSlotOwnerEndpoint(16383)).IsEqualTo(secondEndpoint);
    }

    [Test]
    public async Task PartialRefreshOwnerIsNotReversedByLaterStaleCompleteReply()
    {
        await using var seed = new FakeRespServer(FakeRespServer.OkReply);
        await using var second = new FakeRespServer(FakeRespServer.OkReply);
        var seedSlotsCalls = 0;
        var secondRefreshReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var partial = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:0\r\n:100\r\n*2\r\n$9\r\n127.0.0.1\r\n:{second.Port}\r\n");
        var initial = TwoMasterTopology(seed.Port, 8191, 8192, second.Port);
        seed.ReplyOverride = (_, command) => command != "CLUSTER SLOTS" ? null
            : Interlocked.Increment(ref seedSlotsCalls) == 1 ? initial : partial;
        second.ReplyOverride = (_, command) =>
        {
            if (command != "CLUSTER SLOTS") return null;
            secondRefreshReceived.TrySetResult();
            return initial;
        };

        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            ClusterTopologyRefreshInterval = null,
            Endpoints = [new RespireEndpoint("127.0.0.1", seed.Port)],
        });
        var router = client.Core.Cluster!;
        var initialGeneration = PublishedDiscoveryGeneration(router);
        var firstOwner = router.GetSlotOwnerEndpoint(0);
        var secondOwner = new RespireEndpoint("127.0.0.1", second.Port);

        router.SignalTopologyRefresh(force: true);
        await secondRefreshReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (PublishedDiscoveryGeneration(router) < initialGeneration + 2)
            await Task.Delay(10, timeout.Token);

        await Assert.That(router.GetSlotOwnerEndpoint(0)).IsEqualTo(secondOwner);
        await Assert.That(router.GetSlotOwnerEndpoint(100)).IsEqualTo(secondOwner);
        await Assert.That(router.GetSlotOwnerEndpoint(101)).IsEqualTo(firstOwner);
        await Assert.That(router.GetSlotOwnerEndpoint(8191)).IsEqualTo(firstOwner);
        await Assert.That(router.GetSlotOwnerEndpoint(8192)).IsEqualTo(secondOwner);
    }

    private static long PublishedDiscoveryGeneration(ClusterRouter router)
        => (long)typeof(ClusterRouter).GetField("_publishedDiscoveryGeneration",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(router)!;

    [Test]
    public async Task RefreshFallbacksStartAtADifferentCandidateOnEachPass()
    {
        // Each attempt has a minimum timeout, so many stalled fallbacks can use up the deadline
        // before the end of the list. Rotating the start lets later passes reach every fallback.
        var candidates = new List<ClusterRouter.TopologyRefreshCandidate>
        {
            new(null, new RespireEndpoint("seed", 1), IsConfiguredSeed: true),
        };
        for (var port = 10; port < 14; port++)
            candidates.Add(new(null, new RespireEndpoint("replica", port), IsConfiguredSeed: false));
        var random = new Random(1234);
        var firstFallbacks = new HashSet<int>();

        for (var pass = 0; pass < 64; pass++)
        {
            var ordered = ClusterRouter.OrderTopologyRefreshCandidates(candidates, random);
            await Assert.That(ordered.Count).IsEqualTo(5);
            await Assert.That(ordered[0].IsConfiguredSeed).IsTrue();
            await Assert.That(ordered.Skip(1).Select(static candidate => candidate.Endpoint.Port).Order().ToArray())
                .IsEquivalentTo(new[] { 10, 11, 12, 13 });
            firstFallbacks.Add(ordered[1].Endpoint.Port);
        }

        await Assert.That(firstFallbacks.Count).IsEqualTo(4);
    }

    private static byte[] Topology(int masterPort, int replicaPort, string replicaAlias = "replica")
        => Encoding.UTF8.GetBytes(
            "*1\r\n*4\r\n:0\r\n:16383\r\n"
            + $"*3\r\n$9\r\n127.0.0.1\r\n:{masterPort}\r\n$9\r\nmaster-id\r\n"
            + $"*4\r\n$9\r\n127.0.0.1\r\n:{replicaPort}\r\n$10\r\nreplica-id\r\n%1\r\n+hostname\r\n${Encoding.UTF8.GetByteCount(replicaAlias)}\r\n{replicaAlias}\r\n");

    private static byte[] TwoMasterTopology(int firstPort, int firstEnd, int secondStart, int secondPort)
        => Encoding.ASCII.GetBytes(
            $"*2\r\n*3\r\n:0\r\n:{firstEnd}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{firstPort}\r\n" +
            $"*3\r\n:{secondStart}\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{secondPort}\r\n");

    private sealed class ManualTopologyRefreshClock : TimeProvider
    {
        private readonly Channel<ManualTimer> _timers = Channel.CreateUnbounded<ManualTimer>();
        private long _timestamp;
        internal TaskCompletionSource<ManualTimer> Created { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Volatile.Read(ref _timestamp);
        internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref _timestamp, elapsed.Ticks);

        /// <summary>Returns the next timer created with <paramref name="dueTime"/>, skipping
        /// <paramref name="superseded"/> (a timer the caller already holds, which the worker may have
        /// disposed and whose <see cref="ManualTimer.Fire"/> would then do nothing).</summary>
        internal async Task<ManualTimer> NextTimerAsync(TimeSpan dueTime, ManualTimer? superseded = null)
        {
            while (true)
            {
                var timer = await _timers.Reader.ReadAsync();
                if (timer.DueTime == dueTime && !ReferenceEquals(timer, superseded)) return timer;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, dueTime);
            Created.TrySetResult(timer);
            _timers.Writer.TryWrite(timer);
            return timer;
        }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
    {
        private int _fired;
        internal TimeSpan DueTime { get; } = dueTime;
        internal void Fire()
        {
            if (Interlocked.Exchange(ref _fired, 1) == 0) callback(state);
        }
        public bool Change(TimeSpan dueTime, TimeSpan period) => Volatile.Read(ref _fired) == 0;
        public void Dispose() => Interlocked.Exchange(ref _fired, 1);
        public ValueTask DisposeAsync() { Dispose(); return default; }
    }
}
