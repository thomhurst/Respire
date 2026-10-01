using System.Text;
using System.Threading.Channels;
using Respire.Internal;
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
            Endpoints = [new RespireEndpoint("127.0.0.1", seed.Port)],
        });
        var router = client.Core.Cluster!;
        router.TopologyRefreshClock = clock;

        await Assert.That(seed.ReceivedCommands).IsEmpty();
        await Assert.That(clock.Created.Task.IsCompleted).IsFalse();

        await router.EnsureConnectedAsync(CancellationToken.None, discovery: null);
        var timer = await clock.Created.Task.WaitAsync(TimeSpan.FromSeconds(2));
        timer.Fire();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (seed.ReceivedCommands.Count(command => command == "CLUSTER SLOTS") < 2)
            await Task.Delay(10, timeout.Token);
    }

    [Test]
    public async Task PeriodicDeadlineInterruptsMovedDebounce()
    {
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            ClusterTopologyRefreshInterval = TimeSpan.FromMilliseconds(50),
            Endpoints = [new RespireEndpoint("127.0.0.1", 1)],
        });
        var clock = new ManualTopologyRefreshClock();
        var router = client.Core.Cluster!;
        router.TopologyRefreshClock = clock;
        var method = typeof(ClusterRouter).GetMethod("WaitForTopologyRefreshDelayAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var wait = (Task<bool>)method.Invoke(router, [5_000, TimeSpan.FromMilliseconds(50)])!;

        var periodic = await clock.NextTimerAsync(TimeSpan.FromMilliseconds(50)).WaitAsync(TimeSpan.FromSeconds(2));
        periodic.Fire();

        await Assert.That(await wait).IsTrue();
    }

    [Test]
    public async Task LongRefreshIntervalUsesTimerSafeSegments()
    {
        await using var seed = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            ClusterTopologyRefreshInterval = TimeSpan.FromDays(90),
            Endpoints = [new RespireEndpoint("127.0.0.1", seed.Port)],
        });
        var clock = new ManualTopologyRefreshClock();
        var router = client.Core.Cluster!;
        router.TopologyRefreshClock = clock;
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
            Endpoints = [new RespireEndpoint("127.0.0.1", seed.Port)],
        });
        var router = client.Core.Cluster!;
        router.TopologyRefreshClock = new ManualTopologyRefreshClock();
        await router.EnsureConnectedAsync(CancellationToken.None, discovery: null);

        router.SignalTopologyRefresh();
        await secondRefresh.Task.WaitAsync(TimeSpan.FromSeconds(2));
        router.SignalTopologyRefresh(force: true);
        await thirdRefresh.Task.WaitAsync(TimeSpan.FromSeconds(2));
        router.SignalTopologyRefresh(delayMilliseconds: 5000);
        await Task.Delay(50);
        router.SignalTopologyRefresh(force: true);
        await fourthRefresh.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task RepeatedPrimaryDisconnectsWithinSpacingShareOneForcedRefresh()
    {
        var refreshes = 0;
        var secondRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var seed = new FakeRespServer(FakeRespServer.OkReply);
        seed.ReplyOverride = (_, command) =>
        {
            if (command != "CLUSTER SLOTS") return null;
            if (Interlocked.Increment(ref refreshes) == 2) secondRefresh.TrySetResult();
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
        router.TopologyRefreshClock = new ManualTopologyRefreshClock();
        await router.EnsureConnectedAsync(CancellationToken.None, discovery: null);

        router.SignalPrimaryDisconnectRefresh();
        await secondRefresh.Task.WaitAsync(TimeSpan.FromSeconds(2));
        router.SignalPrimaryDisconnectRefresh();
        router.SignalPrimaryDisconnectRefresh();
        await Task.Delay(200);

        await Assert.That(Volatile.Read(ref refreshes)).IsEqualTo(2);
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
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            ClusterTopologyRefreshInterval = null,
            Endpoints = [new RespireEndpoint("127.0.0.1", seed.Port)],
        });
        var router = client.Core.Cluster!;
        // The manual clock never ends the MOVED debounce on its own.
        var clock = new ManualTopologyRefreshClock();
        router.TopologyRefreshClock = clock;
        await router.EnsureConnectedAsync(CancellationToken.None, discovery: null);

        router.SignalPrimaryDisconnectRefresh();
        await secondRefresh.Task.WaitAsync(TimeSpan.FromSeconds(2));
        router.SignalTopologyRefresh(delayMilliseconds: 5000);
        await clock.NextTimerAsync(TimeSpan.FromSeconds(5)).WaitAsync(TimeSpan.FromSeconds(2));
        var lastSignal = typeof(ClusterRouter).GetField("_lastPrimaryDisconnectRefreshSignal",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        lastSignal.SetValue(router, System.Diagnostics.Stopwatch.GetTimestamp());
        // Force the spacing branch while the worker is known to be inside MOVED debounce.
        router.SignalPrimaryDisconnectRefresh();

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
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            ClusterTopologyRefreshInterval = null,
            Endpoints = [new RespireEndpoint("127.0.0.1", seed.Port)],
        });
        var clock = new ManualTopologyRefreshClock();
        var router = client.Core.Cluster!;
        router.TopologyRefreshClock = clock;
        await router.EnsureConnectedAsync(CancellationToken.None, discovery: null);

        router.SignalTopologyRefresh(delayMilliseconds: 5000);
        _ = await clock.NextTimerAsync(TimeSpan.FromSeconds(5)).WaitAsync(TimeSpan.FromSeconds(2));
        router.SignalTopologyRefresh(force: true);
        await secondRefresh.Task.WaitAsync(TimeSpan.FromSeconds(2));

        router.SignalTopologyRefresh(delayMilliseconds: 5000);
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
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            ClusterTopologyRefreshInterval = null,
            Endpoints = [new RespireEndpoint("127.0.0.1", seed.Port)],
        });
        var clock = new ManualTopologyRefreshClock();
        var router = client.Core.Cluster!;
        router.TopologyRefreshClock = clock;
        await router.EnsureConnectedAsync(CancellationToken.None, discovery: null);

        router.SignalTopologyRefresh(delayMilliseconds: 5000);
        _ = await clock.NextTimerAsync(TimeSpan.FromSeconds(5)).WaitAsync(TimeSpan.FromSeconds(2));
        clock.Advance(TimeSpan.FromSeconds(1));
        router.SignalTopologyRefresh(delayMilliseconds: 5000);
        _ = await clock.NextTimerAsync(TimeSpan.FromSeconds(4)).WaitAsync(TimeSpan.FromSeconds(2));
        clock.Advance(TimeSpan.FromSeconds(1));
        router.SignalTopologyRefresh(delayMilliseconds: 5000);
        var finalDelay = await clock.NextTimerAsync(TimeSpan.FromSeconds(3)).WaitAsync(TimeSpan.FromSeconds(2));
        clock.Advance(TimeSpan.FromSeconds(3));
        finalDelay.Fire();
        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task WatchedMovedRouteSchedulesTopologyRefresh()
    {
        await using var seed = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            ClusterTopologyRefreshInterval = null,
            Endpoints = [new RespireEndpoint("127.0.0.1", seed.Port)],
        });
        var clock = new ManualTopologyRefreshClock();
        var router = client.Core.Cluster!;
        router.TopologyRefreshClock = clock;
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

        var buildCandidates = typeof(ClusterRouter).GetMethod("GetTopologyRefreshCandidates",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var candidates = ((System.Collections.IEnumerable)buildCandidates.Invoke(router, null)!).Cast<object>();
        await Assert.That(ReferenceEquals(candidates.First(), router.GetMultiplexer(seedEndpoint))).IsTrue();
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

        var refreshTask = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var flightCancellation = new CancellationTokenSource();
        var flightType = typeof(ClusterRouter).GetNestedType(
            "ReadOnlyRefreshFlight", System.Reflection.BindingFlags.NonPublic)!;
        var flight = Activator.CreateInstance(flightType,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            args: [flightCancellation, ClusterHash.GetSlot("key"), new RespireEndpoint("127.0.0.1", seed.Port)],
            culture: null)!;
        flightType.GetField("SharedTask", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(flight, refreshTask.Task);
        flightType.GetField("Waiters", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(flight, 1);
        var sharedGate = typeof(ClusterRouter).GetField("_sharedRefreshGate",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(router)!;
        lock (sharedGate)
        {
            typeof(ClusterRouter).GetField("_sharedRefreshTask",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(router, refreshTask.Task);
            typeof(ClusterRouter).GetField("_readOnlyRefreshFlight",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(router, flight);
        }

        router.SignalTopologyRefresh(force: true);
        var waiters = flightType.GetField("Waiters", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            while ((int)waiters.GetValue(flight)! < 2) await Task.Delay(1, timeout.Token);
        lock (sharedGate)
        {
            flightType.GetField("Completed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(flight, true);
            typeof(ClusterRouter).GetField("_sharedRefreshTask",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(router, null);
            typeof(ClusterRouter).GetField("_readOnlyRefreshFlight",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(router, null);
        }
        refreshTask.SetResult(true);

        await fullRefresh.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.That(Volatile.Read(ref slotsCalls)).IsEqualTo(2);
    }

    [Test]
    public async Task IncompleteRefreshKeepsPublishedTopologyAndReplicaMetadata()
    {
        await using var replicaServer = new FakeRespServer(FakeRespServer.OkReply);
        await using var seed = new FakeRespServer(FakeRespServer.OkReply);
        var replicaPort = replicaServer.Port;
        var refreshReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slotsCalls = 0;
        var incompleteTopology = Encoding.UTF8.GetBytes(
            "*1\r\n*4\r\n:0\r\n:100\r\n"
            + $"*3\r\n$9\r\n127.0.0.1\r\n:{seed.Port}\r\n$9\r\nmaster-id\r\n"
            + $"*4\r\n$9\r\n127.0.0.1\r\n:{replicaPort}\r\n$10\r\nreplica-id\r\n%1\r\n+hostname\r\n$7\r\nreplica\r\n");
        seed.ReplyOverride = (_, command) =>
        {
            if (command != "CLUSTER SLOTS") return null;
            if (Interlocked.Increment(ref slotsCalls) == 1)
                return Topology(seed.Port, replicaPort);
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

        router.SignalTopologyRefresh();
        await refreshReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);

        await Assert.That(router.GetSlotOwnerEndpoint(0)).IsEqualTo(originalOwner);
        await Assert.That(router.GetReplicas().Single()).IsEqualTo(originalReplica);
        await Assert.That(replicaServer.ReceivedCommands).Contains("CLUSTER SLOTS");
    }

    private static byte[] Topology(int masterPort, int replicaPort)
        => Encoding.UTF8.GetBytes(
            "*1\r\n*4\r\n:0\r\n:16383\r\n"
            + $"*3\r\n$9\r\n127.0.0.1\r\n:{masterPort}\r\n$9\r\nmaster-id\r\n"
            + $"*4\r\n$9\r\n127.0.0.1\r\n:{replicaPort}\r\n$10\r\nreplica-id\r\n%1\r\n+hostname\r\n$7\r\nreplica\r\n");

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

        internal async Task<ManualTimer> NextTimerAsync(TimeSpan dueTime)
        {
            while (true)
            {
                var timer = await _timers.Reader.ReadAsync();
                if (timer.DueTime == dueTime) return timer;
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
