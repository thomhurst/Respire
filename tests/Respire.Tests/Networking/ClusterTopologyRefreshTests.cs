using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClusterTopologyRefreshTests
{
    [Test]
    public async Task ConcurrentSignalsShareRefreshAndPublishReplicaMetadata()
    {
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var seed = new FakeRespServer
        {
            SuppressReply = command =>
            {
                if (command == "CLUSTER SLOTS") refreshStarted.TrySetResult();
                return command == "CLUSTER SLOTS";
            },
        };
        var replicaPort = seed.Port + 1;
        seed.ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
            ? Encoding.UTF8.GetBytes(
                "*1\r\n*4\r\n:0\r\n:16383\r\n"
                + $"*3\r\n$9\r\n127.0.0.1\r\n:{seed.Port}\r\n$9\r\nmaster-id\r\n"
                + $"*4\r\n$9\r\n127.0.0.1\r\n:{replicaPort}\r\n$10\r\nreplica-id\r\n%1\r\n+hostname\r\n$7\r\nreplica\r\n")
            : null;
        await using var client = RespireClient.Create(new RespireOptions
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
        await Assert.That(seed.ReceivedCommands.Count(command => command == "CLUSTER SLOTS")).IsEqualTo(1);
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
}
