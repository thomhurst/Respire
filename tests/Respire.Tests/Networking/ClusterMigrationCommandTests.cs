using System.Text;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClusterMigrationCommandTests
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task EveryVariantUsesOnlySelectedNodeAndPreservesTaskIdBoundary(int protocol)
    {
        await using var seed = Server(1);
        await using var target = Server(6);
        await using var client = RespireClient.Create(Options(seed.Port, protocol));
        var node = client.WithKeyPrefix("ignored:").Server.OnNode(new("127.0.0.1", target.Port));
        RespireClusterSlotRange[] ranges = [new(20, 30), new(0, 10), new(16383, 16383)];
        await Assert.That(await node.ClusterMigrationImportAsync(ranges)).IsEqualTo("task");
        await Assert.That(ranges).IsEquivalentTo(
            [new RespireClusterSlotRange(20, 30), new(0, 10), new(16383, 16383)], CollectionOrdering.Matching);
        await Assert.That(await node.ClusterMigrationCancelAsync("task id")).IsEqualTo(1L);
        await Assert.That(await node.ClusterMigrationCancelAllAsync()).IsEqualTo(1L);
        await Assert.That(await node.ClusterMigrationStatusAsync()).IsEmpty();
        await Assert.That(await node.ClusterMigrationStatusAsync("task id")).IsEmpty();
        await Assert.That(await node.ClusterMigrationStatusAsync(RespireClusterMigrationStatusScope.Default)).IsEmpty();
        string[][] expected =
        [
            ["CLUSTER", "MIGRATION", "IMPORT", "0", "10", "20", "30", "16383", "16383"],
            ["CLUSTER", "MIGRATION", "CANCEL", "ID", "task id"],
            ["CLUSTER", "MIGRATION", "CANCEL", "ALL"],
            ["CLUSTER", "MIGRATION", "STATUS", "ALL"],
            ["CLUSTER", "MIGRATION", "STATUS", "ID", "task id"],
            ["CLUSTER", "MIGRATION", "STATUS"],
        ];
        var actual = target.ReceivedArguments.Where(row => Encoding.UTF8.GetString(row[0]) == "CLUSTER").ToArray();
        await Assert.That(actual.Length).IsEqualTo(expected.Length);
        for (var index = 0; index < actual.Length; index++)
            await Assert.That(actual[index].Select(bytes => Encoding.UTF8.GetString(bytes))).IsEquivalentTo(expected[index], CollectionOrdering.Matching);
        await Assert.That(seed.ConnectionAccepted.IsCompleted).IsFalse();
    }

    [Test]
    [Arguments(1)]
    [Arguments(16383)]
    public async Task ValidRangeCountsReachSelectedNode(int count)
    {
        await using var target = Server(1);
        await using var client = RespireClient.Create(Options(target.Port, 2));
        var node = client.Server.OnNode(new("127.0.0.1", target.Port));
        RespireClusterSlotRange[] ranges = count == 1
            ? [new(0, 16383)]
            : Enumerable.Range(0, count).Select(slot => new RespireClusterSlotRange(slot, slot)).ToArray();
        await Assert.That(await node.ClusterMigrationImportAsync(ranges)).IsEqualTo("task");
        await Assert.That(target.ReceivedArguments.Single().Length).IsEqualTo(count * 2 + 3);
    }

    [Test]
    [Arguments("empty")]
    [Arguments("negative")]
    [Arguments("large")]
    [Arguments("reverse")]
    [Arguments("overlap")]
    [Arguments("too-many")]
    [Arguments("cancel-id")]
    [Arguments("status-id")]
    [Arguments("scope")]
    public async Task InvalidArgumentsDoNotConnect(string shape)
    {
        await using var target = Server(1);
        await using var client = RespireClient.Create(Options(target.Port, 2));
        var node = client.Server.OnNode(new("127.0.0.1", target.Port));
        await Assert.That(async () =>
        {
            switch (shape)
            {
                case "cancel-id": await node.ClusterMigrationCancelAsync(" "); break;
                case "status-id": await node.ClusterMigrationStatusAsync((string)null!); break;
                case "scope": await node.ClusterMigrationStatusAsync((RespireClusterMigrationStatusScope)100); break;
                default:
                    RespireClusterSlotRange[] ranges = shape switch
                    {
                        "empty" => [], "negative" => [new(-1, 0)], "large" => [new(0, 16384)],
                        "reverse" => [new(2, 1)], "overlap" => [new(0, 2), new(2, 3)],
                        _ => Enumerable.Range(0, 16384).Select(slot => new RespireClusterSlotRange(slot, slot)).ToArray(),
                    };
                    await node.ClusterMigrationImportAsync(ranges); break;
            }
        }).Throws<ArgumentException>();
        await Assert.That(target.ConnectionAccepted.IsCompleted).IsFalse();
    }

    [Test]
    [Arguments("import")]
    [Arguments("cancel")]
    [Arguments("cancel-all")]
    public async Task MutationRequiresAdminBeforeConnection(string operation)
    {
        await using var target = Server(1);
        var options = Options(target.Port, 2) with { AllowAdmin = false };
        await using var client = RespireClient.Create(options);
        var node = client.Server.OnNode(new("127.0.0.1", target.Port));
        await Assert.That(async () => await Mutate(node, operation)).Throws<NotSupportedException>();
        await Assert.That(target.ConnectionAccepted.IsCompleted).IsFalse();
        await Assert.That(await node.ClusterMigrationStatusAsync()).IsEmpty();
    }

    [Test]
    [Arguments("import")]
    [Arguments("cancel")]
    [Arguments("cancel-all")]
    public async Task CancellationBeforeSubmissionDoesNotConnect(string operation)
    {
        await using var target = Server(1);
        await using var client = RespireClient.Create(Options(target.Port, 2));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var node = client.Server.OnNode(new("127.0.0.1", target.Port));
        await Assert.That(async () => await Mutate(node, operation, cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(target.ConnectionAccepted.IsCompleted).IsFalse();
    }

    [Test]
    [Arguments("-MOVED 0 127.0.0.1:1\r\n")]
    [Arguments("-ASK 0 127.0.0.1:1\r\n")]
    [Arguments("-ERR selected node refused\r\n")]
    public async Task ServerErrorsDoNotRedirectOrReplay(string error)
    {
        await using var seed = Server(1);
        await using var target = Server(1);
        target.ReplyOverride = (_, _) => Encoding.UTF8.GetBytes(error);
        await using var client = RespireClient.Create(Options(seed.Port, 2));
        var node = client.Server.OnNode(new("127.0.0.1", target.Port));
        await Assert.That(async () => await node.ClusterMigrationImportAsync([new(0, 1)])).Throws<RespireServerException>();
        await Assert.That(target.ReceivedCommands).IsEquivalentTo(["CLUSTER MIGRATION IMPORT 0 1"]);
        await Assert.That(seed.ConnectionAccepted.IsCompleted).IsFalse();
        await target.PeerClosed.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    [Arguments("import", false)]
    [Arguments("import", true)]
    [Arguments("cancel", false)]
    [Arguments("cancel", true)]
    [Arguments("cancel-all", false)]
    [Arguments("cancel-all", true)]
    public async Task MutationFencesCacheAndReleasesConnectionOnSuccessOrCancellation(string operation, bool cancel)
    {
        await using var seed = new FakeRespServer(1, FakeRespServer.OkReply);
        seed.ReplyOverride = (_, command) => command.StartsWith("HELLO ", StringComparison.Ordinal)
            ? "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray() : FakeRespServer.OkReply;
        await using var target = Server(1);
        var received = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        target.SuppressReply = command =>
        {
            if (!command.StartsWith("CLUSTER MIGRATION", StringComparison.Ordinal)) return false;
            received.TrySetResult(target.ReceivedConnectionIds[^1]); return true;
        };
        await using var client = await RespireClient.ConnectAsync(Options(seed.Port, 2) with { ClientSideCache = new() });
        var cache = client.Core.ClientCache!;
        Insert();
        using var cancellation = new CancellationTokenSource();
        var pending = Mutate(client.Server.OnNode(new("127.0.0.1", target.Port)), operation, cancellation.Token);
        var connection = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(cache.Count).IsEqualTo(0);
        Insert();
        if (cancel)
        {
            cancellation.Cancel();
            await Assert.That(async () => await pending).Throws<OperationCanceledException>();
        }
        else
        {
            await target.SendRawAsync(operation == "import" ? "$4\r\ntask\r\n"u8.ToArray() : ":1\r\n"u8.ToArray(), connection);
            await pending;
        }
        await Assert.That(cache.Count).IsEqualTo(0);
        await target.PeerClosed.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(target.ReceivedCommands.Count(command => command.StartsWith("CLUSTER MIGRATION", StringComparison.Ordinal))).IsEqualTo(1);

        void Insert()
        {
            RespireKey key = "cached";
            var read = cache.BeginRead(in key);
            using var reply = RespValue.BulkString("stale");
            cache.CompleteRead(in read, in reply, allowInsert: true);
        }
    }

    private static async Task Mutate(RespireServerNode node, string operation, CancellationToken token = default)
    {
        switch (operation)
        {
            case "import": await node.ClusterMigrationImportAsync([new(0, 1)], token); break;
            case "cancel": await node.ClusterMigrationCancelAsync("task", token); break;
            default: await node.ClusterMigrationCancelAllAsync(token); break;
        }
    }

    private static RespireOptions Options(int port, int protocol) => new()
    {
        Endpoints = [new("127.0.0.1", port)], AllowAdmin = true, Connections = 1,
        Protocol = protocol == 2 ? RespProtocol.Resp2 : RespProtocol.Resp3,
        MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled,
    };

    private static FakeRespServer Server(int connections)
    {
        var server = new FakeRespServer(connections, FakeRespServer.OkReply);
        server.ReplyOverride = (_, command) => command switch
        {
            "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
            _ when command.StartsWith("CLUSTER MIGRATION IMPORT", StringComparison.Ordinal) => "$4\r\ntask\r\n"u8.ToArray(),
            _ when command.StartsWith("CLUSTER MIGRATION CANCEL", StringComparison.Ordinal) => ":1\r\n"u8.ToArray(),
            _ => "*0\r\n"u8.ToArray(),
        };
        return server;
    }
}
