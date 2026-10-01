using System.Text;
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
    public async Task IncompleteRefreshKeepsPublishedTopologyAndReplicaMetadata()
    {
        await using var seed = new FakeRespServer(FakeRespServer.OkReply);
        var replicaPort = seed.Port == 65535 ? seed.Port - 1 : seed.Port + 1;
        var refreshReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slotsCalls = 0;
        seed.ReplyOverride = (_, command) =>
        {
            if (command != "CLUSTER SLOTS") return null;
            if (Interlocked.Increment(ref slotsCalls) == 1)
                return Topology(seed.Port, replicaPort);
            refreshReceived.TrySetResult();
            return Encoding.UTF8.GetBytes(
                "*1\r\n*4\r\n:0\r\n:100\r\n"
                + $"*3\r\n$9\r\n127.0.0.1\r\n:{seed.Port}\r\n$9\r\nmaster-id\r\n"
                + $"*4\r\n$9\r\n127.0.0.1\r\n:{replicaPort}\r\n$10\r\nreplica-id\r\n%1\r\n+hostname\r\n$7\r\nreplica\r\n");
        };
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
    }

    private static byte[] Topology(int masterPort, int replicaPort)
        => Encoding.UTF8.GetBytes(
            "*1\r\n*4\r\n:0\r\n:16383\r\n"
            + $"*3\r\n$9\r\n127.0.0.1\r\n:{masterPort}\r\n$9\r\nmaster-id\r\n"
            + $"*4\r\n$9\r\n127.0.0.1\r\n:{replicaPort}\r\n$10\r\nreplica-id\r\n%1\r\n+hostname\r\n$7\r\nreplica\r\n");

    private sealed class ManualTopologyRefreshClock : TimeProvider
    {
        internal TaskCompletionSource<ManualTimer> Created { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state);
            Created.TrySetResult(timer);
            return timer;
        }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        private int _fired;
        internal void Fire()
        {
            if (Interlocked.Exchange(ref _fired, 1) == 0) callback(state);
        }
        public bool Change(TimeSpan dueTime, TimeSpan period) => Volatile.Read(ref _fired) == 0;
        public void Dispose() => Interlocked.Exchange(ref _fired, 1);
        public ValueTask DisposeAsync() { Dispose(); return default; }
    }
}
