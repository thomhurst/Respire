using System.Text;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ServerClusterAdministrationCommandTests
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task CommandsUseExplicitEndpointAndExactGrammar(int protocol)
    {
        await using var seed = Server(1);
        await using var target = Server(32);
        await using var client = Client(seed.Port, protocol, true);
        var node = client.WithKeyPrefix("ignored:").Server.OnNode(new("127.0.0.1", target.Port));
        await node.ClusterAddSlotsAsync([0, 16383]);
        await node.ClusterAddSlotsRangeAsync([new(0, 1), new(2, 3)]);
        await node.ClusterDeleteSlotsAsync([0, 16383]);
        await node.ClusterDeleteSlotsRangeAsync([new(0, 1), new(2, 3)]);
        await Assert.That((await node.ClusterBumpEpochAsync()).Epoch).IsEqualTo(42UL);
        await Assert.That(await node.ClusterCountFailureReportsAsync("node")).IsEqualTo(2L);
        await node.ClusterFailoverAsync();
        await node.ClusterFailoverAsync(RespireClusterFailoverMode.Force);
        await node.ClusterFailoverAsync(RespireClusterFailoverMode.Takeover);
        await node.ClusterForgetAsync("node");
        await Assert.That((await node.ClusterGetKeysInSlotAsync(16383, 2))[0]).IsEquivalentTo(new byte[] { 255, 0 });
        await node.ClusterGetKeysInSlotAsync(0, 0);
        await node.ClusterMeetAsync(new("127.0.0.1", 7000));
        await node.ClusterMeetAsync(new("127.0.0.1", 7001), 17001);
        await Assert.That((await node.ClusterReplicasAsync("node")).Single().PrimaryId).IsEqualTo("node");
        await node.ClusterReplicateAsync("node");
        await node.ClusterResetAsync();
        await node.ClusterResetAsync(RespireClusterResetMode.Hard);
        await node.ClusterSaveConfigAsync();
        await node.ClusterSetConfigEpochAsync(long.MaxValue);
        await node.ClusterSetSlotAsync(1, RespireClusterSlotState.Importing, "node");
        await node.ClusterSetSlotAsync(1, RespireClusterSlotState.Migrating, "node");
        await node.ClusterSetSlotAsync(1, RespireClusterSlotState.Node, "node");
        await node.ClusterSetSlotAsync(1, RespireClusterSlotState.Stable);
        await node.ClusterFlushSlotsAsync();
        await Assert.That((await node.ClusterSlotsAsync()).Single().Slots).IsEqualTo(new RespireClusterSlotRange(0, 16383));
        await Assert.That(target.ReceivedCommands.Where(command => !command.StartsWith("HELLO ", StringComparison.Ordinal)))
            .IsEquivalentTo(new[]
            {
                "CLUSTER ADDSLOTS 0 16383", "CLUSTER ADDSLOTSRANGE 0 1 2 3", "CLUSTER DELSLOTS 0 16383",
                "CLUSTER DELSLOTSRANGE 0 1 2 3", "CLUSTER BUMPEPOCH", "CLUSTER COUNT-FAILURE-REPORTS node",
                "CLUSTER FAILOVER", "CLUSTER FAILOVER FORCE", "CLUSTER FAILOVER TAKEOVER", "CLUSTER FORGET node",
                "CLUSTER GETKEYSINSLOT 16383 2", "CLUSTER GETKEYSINSLOT 0 0", "CLUSTER MEET 127.0.0.1 7000",
                "CLUSTER MEET 127.0.0.1 7001 17001", "CLUSTER REPLICAS node", "CLUSTER REPLICATE node",
                "CLUSTER RESET SOFT", "CLUSTER RESET HARD", "CLUSTER SAVECONFIG", "CLUSTER SET-CONFIG-EPOCH 9223372036854775807",
                "CLUSTER SETSLOT 1 IMPORTING node", "CLUSTER SETSLOT 1 MIGRATING node", "CLUSTER SETSLOT 1 NODE node",
                "CLUSTER SETSLOT 1 STABLE", "CLUSTER FLUSHSLOTS", "CLUSTER SLOTS",
            });
        await Assert.That(seed.ConnectionAccepted.IsCompleted).IsFalse();
    }

    [Test]
    [Arguments(0)] [Arguments(1)] [Arguments(2)] [Arguments(3)] [Arguments(4)] [Arguments(5)] [Arguments(6)]
    [Arguments(7)] [Arguments(8)] [Arguments(9)] [Arguments(10)] [Arguments(11)] [Arguments(12)] [Arguments(13)]
    public async Task EveryMutationRequiresAdminBeforeConnecting(int operation)
    {
        await using var target = Server(1);
        await using var client = Client(target.Port, 2, false);
        var node = client.Server.OnNode(new("127.0.0.1", target.Port));
        await Assert.That(async () => await Mutate(node, operation)).ThrowsExactly<NotSupportedException>();
        await Assert.That(target.ConnectionAccepted.IsCompleted).IsFalse();
    }

    [Test]
    [Arguments(0)] [Arguments(1)] [Arguments(2)] [Arguments(3)] [Arguments(4)] [Arguments(5)] [Arguments(6)]
    [Arguments(7)] [Arguments(8)] [Arguments(9)] [Arguments(10)] [Arguments(11)] [Arguments(12)] [Arguments(13)]
    public async Task EveryMutationFencesCacheEvenOnServerError(int operation)
    {
        await using var target = Server(1);
        var received = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        target.SuppressReply = command =>
        {
            if (!command.StartsWith("CLUSTER ", StringComparison.Ordinal)) return false;
            received.TrySetResult(target.ReceivedConnectionIds[^1]);
            return true;
        };
        await using var client = Client(target.Port, 2, true, cache: true);
        var cache = client.Core.ClientCache!;
        Insert();
        var pending = Mutate(client.Server.OnNode(new("127.0.0.1", target.Port)), operation);
        var connection = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(cache.Count).IsEqualTo(0);
        Insert();
        await target.SendRawAsync("-ERR rejected\r\n"u8.ToArray(), connection);
        await Assert.That(async () => await pending).Throws<RespireServerException>();
        await Assert.That(cache.Count).IsEqualTo(0);
        void Insert()
        {
            RespireKey key = "cached";
            var token = cache.BeginRead(in key);
            var value = RespValue.BulkString("stale");
            cache.CompleteRead(in token, in value, allowInsert: true);
        }
    }

    [Test]
    public async Task InvalidArgumentsFailBeforeConnecting()
    {
        await using var target = Server(1);
        await using var client = Client(target.Port, 2, true);
        var node = client.Server.OnNode(new("127.0.0.1", target.Port));
        Func<Task>[] invalid =
        [
            () => node.ClusterAddSlotsAsync([]).AsTask(), () => node.ClusterAddSlotsAsync([-1]).AsTask(),
            () => node.ClusterDeleteSlotsAsync([]).AsTask(), () => node.ClusterDeleteSlotsAsync([16384]).AsTask(),
            () => node.ClusterAddSlotsRangeAsync([]).AsTask(), () => node.ClusterAddSlotsRangeAsync([new(2, 1)]).AsTask(),
            () => node.ClusterDeleteSlotsRangeAsync([]).AsTask(), () => node.ClusterDeleteSlotsRangeAsync([new(0, 16384)]).AsTask(),
            () => node.ClusterCountFailureReportsAsync("").AsTask(), () => node.ClusterForgetAsync(" ").AsTask(),
            () => node.ClusterReplicasAsync(null!).AsTask(), () => node.ClusterReplicateAsync("").AsTask(),
            () => node.ClusterFailoverAsync((RespireClusterFailoverMode)99).AsTask(),
            () => node.ClusterResetAsync((RespireClusterResetMode)99).AsTask(),
            () => node.ClusterGetKeysInSlotAsync(-1, 1).AsTask(), () => node.ClusterGetKeysInSlotAsync(0, -1).AsTask(),
            () => node.ClusterMeetAsync(new("", 7000)).AsTask(), () => node.ClusterMeetAsync(new("host", 0)).AsTask(),
            () => node.ClusterMeetAsync(new("host", 65536)).AsTask(), () => node.ClusterMeetAsync(new("host", 7000), 0).AsTask(),
            () => node.ClusterMeetAsync(new("host", 7000), 65536).AsTask(),
            () => node.ClusterSetConfigEpochAsync(-1).AsTask(), () => node.ClusterSetSlotAsync(16384, RespireClusterSlotState.Stable).AsTask(),
            () => node.ClusterSetSlotAsync(0, (RespireClusterSlotState)99).AsTask(),
            () => node.ClusterSetSlotAsync(0, RespireClusterSlotState.Stable, "node").AsTask(),
            () => node.ClusterSetSlotAsync(0, RespireClusterSlotState.Node).AsTask(),
            () => node.ClusterSetSlotAsync(0, RespireClusterSlotState.Importing, "").AsTask(),
            () => node.ClusterSetSlotAsync(0, RespireClusterSlotState.Migrating, " ").AsTask(),
        ];
        foreach (var action in invalid) await Assert.That(action).Throws<ArgumentException>();
        await Assert.That(target.ConnectionAccepted.IsCompleted).IsFalse();
    }

    [Test]
    [Arguments(false)] [Arguments(true)]
    public async Task CancellationAndLifetimeApplyToReadsAndMutations(bool read)
    {
        await using var target = Server(1);
        await using var client = Client(target.Port, 2, true);
        var node = client.Server.OnNode(new("127.0.0.1", target.Port));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.That(async () => await Invoke(cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(target.ConnectionAccepted.IsCompleted).IsFalse();
        await client.DisposeAsync();
        await Assert.That(async () => await Invoke(default)).ThrowsExactly<ObjectDisposedException>();
        await Assert.That(target.ConnectionAccepted.IsCompleted).IsFalse();
        Task Invoke(CancellationToken token) => read ? node.ClusterSlotsAsync(token).AsTask() : node.ClusterFlushSlotsAsync(token).AsTask();
    }

    [Test]
    [Arguments("-MOVED 0 elsewhere:6379\r\n")]
    [Arguments("-ASK 0 elsewhere:6379\r\n")]
    public async Task ServerErrorsAreNotRedirectedOrReplayed(string error)
    {
        await using var seed = Server(1);
        await using var target = new FakeRespServer(Encoding.ASCII.GetBytes(error));
        await using var client = Client(seed.Port, 2, true);
        await Assert.That(async () => await client.Server.OnNode(new("127.0.0.1", target.Port)).ClusterFlushSlotsAsync())
            .Throws<RespireServerException>();
        await Assert.That(target.ReceivedCommands).IsEquivalentTo(["CLUSTER FLUSHSLOTS"]);
        await Assert.That(seed.ConnectionAccepted.IsCompleted).IsFalse();
    }

    [Test]
    public async Task ReadsDoNotRequireAdminOrInvalidateCache()
    {
        await using var target = Server(4);
        await using var client = Client(target.Port, 3, false, cache: true);
        var node = client.Server.OnNode(new("127.0.0.1", target.Port));
        var cache = client.Core.ClientCache!;
        RespireKey key = "cached";
        var token = cache.BeginRead(in key);
        var value = RespValue.BulkString("retained");
        cache.CompleteRead(in token, in value, allowInsert: true);
        await node.ClusterSlotsAsync();
        await node.ClusterReplicasAsync("node");
        await node.ClusterCountFailureReportsAsync("node");
        await node.ClusterGetKeysInSlotAsync(0, 0);
        await Assert.That(cache.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)] [Arguments(true)]
    public async Task OutstandingCommandClosesSocketOnCancellationOrClientDisposal(bool dispose)
    {
        await using var target = Server(1);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        target.SuppressReply = command =>
        {
            if (!command.StartsWith("CLUSTER ", StringComparison.Ordinal)) return false;
            received.TrySetResult();
            return true;
        };
        await using var client = Client(target.Port, 2, true);
        using var cancellation = new CancellationTokenSource();
        var pending = client.Server.OnNode(new("127.0.0.1", target.Port)).ClusterSlotsAsync(cancellation.Token).AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (dispose)
        {
            await client.DisposeAsync();
            await Assert.That(async () => await pending).Throws<RespireConnectionException>();
        }
        else
        {
            cancellation.Cancel();
            await Assert.That(async () => await pending).Throws<OperationCanceledException>();
        }
        await target.PeerClosed.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(target.ReceivedCommands).IsEquivalentTo(["CLUSTER SLOTS"]);
    }

    private static Task Mutate(RespireServerNode node, int operation) => operation switch
    {
        0 => node.ClusterAddSlotsAsync([0]).AsTask(), 1 => node.ClusterAddSlotsRangeAsync([new(0, 1)]).AsTask(),
        2 => node.ClusterDeleteSlotsAsync([0]).AsTask(), 3 => node.ClusterDeleteSlotsRangeAsync([new(0, 1)]).AsTask(),
        4 => node.ClusterBumpEpochAsync().AsTask(), 5 => node.ClusterFailoverAsync().AsTask(),
        6 => node.ClusterForgetAsync("node").AsTask(), 7 => node.ClusterMeetAsync(new("127.0.0.1", 7000)).AsTask(),
        8 => node.ClusterReplicateAsync("node").AsTask(), 9 => node.ClusterResetAsync().AsTask(),
        10 => node.ClusterSaveConfigAsync().AsTask(), 11 => node.ClusterSetConfigEpochAsync(0).AsTask(),
        12 => node.ClusterSetSlotAsync(0, RespireClusterSlotState.Stable).AsTask(),
        _ => node.ClusterFlushSlotsAsync().AsTask(),
    };

    private static RespireClient Client(int port, int protocol, bool admin, bool cache = false)
        => RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", port)], Connections = 1, AllowAdmin = admin,
            Protocol = protocol == 2 ? RespProtocol.Resp2 : RespProtocol.Resp3,
            ClientSideCache = cache ? new() : null,
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled,
        });

    private static FakeRespServer Server(int connections)
    {
        var server = new FakeRespServer(connections, FakeRespServer.OkReply);
        server.ReplyOverride = (_, command) => command switch
        {
            "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
            "CLUSTER BUMPEPOCH" => "+BUMPED 42\r\n"u8.ToArray(),
            "CLUSTER COUNT-FAILURE-REPORTS node" => ":2\r\n"u8.ToArray(),
            "CLUSTER SLOTS" => "*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$1\r\n?\r\n:0\r\n"u8.ToArray(),
            "CLUSTER REPLICAS node" => Encoding.ASCII.GetBytes("*1\r\n+replica :0@0 slave node 0 0 0 connected\r\n"),
            "CLUSTER GETKEYSINSLOT 16383 2" => [.. "*1\r\n$2\r\n"u8, 255, 0, 13, 10],
            "CLUSTER GETKEYSINSLOT 0 0" => "*0\r\n"u8.ToArray(),
            _ => FakeRespServer.OkReply,
        };
        return server;
    }
}
