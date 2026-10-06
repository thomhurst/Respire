using System.Text;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ValkeySlotMigrationCommandTests
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task EveryVariantUsesSelectedNodeAndExactArgumentOrder(int protocol)
    {
        await using var seed = Server(1);
        await using var target = Server(6);
        await using var client = RespireClient.Create(Options(seed.Port, protocol));
        var node = client.WithKeyPrefix("ignored:").Server.OnNode(new("127.0.0.1", target.Port));
        await node.ClusterMigrateSlotsAsync([new("target-a", [new(20, 30), new(0, 10)]), new("target-b", [new(16383, 16383)])]);
        await node.ClusterCancelSlotMigrationsAsync();
        await Assert.That(await node.ClusterGetSlotMigrationsAsync()).IsEmpty();
        await node.ClusterFlushSlotAsync(0);
        await node.ClusterFlushSlotAsync(1, ServerFlushMode.Sync);
        await node.ClusterFlushSlotAsync(16383, ServerFlushMode.Async);
        string[][] expected =
        [
            ["CLUSTER", "MIGRATESLOTS", "SLOTSRANGE", "20", "30", "0", "10", "NODE", "target-a", "SLOTSRANGE", "16383", "16383", "NODE", "target-b"],
            ["CLUSTER", "CANCELSLOTMIGRATIONS"], ["CLUSTER", "GETSLOTMIGRATIONS"],
            ["CLUSTER", "FLUSHSLOT", "0"], ["CLUSTER", "FLUSHSLOT", "1", "SYNC"], ["CLUSTER", "FLUSHSLOT", "16383", "ASYNC"],
        ];
        var actual = target.ReceivedArguments.Where(row => Encoding.UTF8.GetString(row[0]) == "CLUSTER").ToArray();
        await Assert.That(actual.Length).IsEqualTo(expected.Length);
        for (var index = 0; index < actual.Length; index++)
            await Assert.That(actual[index].Select(bytes => Encoding.UTF8.GetString(bytes)))
                .IsEquivalentTo(expected[index], CollectionOrdering.Matching);
        await Assert.That(seed.ConnectionAccepted.IsCompleted).IsFalse();
    }

    [Test]
    [Arguments("empty")]
    [Arguments("null-group")]
    [Arguments("null-slots")]
    [Arguments("empty-slots")]
    [Arguments("empty-id")]
    [Arguments("whitespace-id")]
    [Arguments("negative")]
    [Arguments("large")]
    [Arguments("reverse")]
    [Arguments("overlap")]
    [Arguments("cross-overlap")]
    [Arguments("flush-negative")]
    [Arguments("flush-large")]
    [Arguments("flush-mode")]
    public async Task InvalidArgumentsDoNotConnect(string shape)
    {
        await using var target = Server(1);
        await using var client = RespireClient.Create(Options(target.Port, 2));
        var node = client.Server.OnNode(new("127.0.0.1", target.Port));
        await Assert.That(async () =>
        {
            if (shape.StartsWith("flush-", StringComparison.Ordinal))
            {
                await node.ClusterFlushSlotAsync(shape == "flush-negative" ? -1 : shape == "flush-large" ? 16384 : 0,
                    shape == "flush-mode" ? (ServerFlushMode)99 : ServerFlushMode.Default);
                return;
            }
            RespireValkeySlotMigrationGroup[] groups = shape switch
            {
                "empty" => [], "null-group" => [null!], "null-slots" => [new("id", null!)],
                "empty-slots" => [new("id", [])], "empty-id" => [new("", [new(0, 0)])],
                "whitespace-id" => [new("id\tother", [new(0, 0)])],
                "negative" => [new("id", [new(-1, 0)])], "large" => [new("id", [new(0, 16384)])],
                "reverse" => [new("id", [new(2, 1)])], "overlap" => [new("id", [new(0, 2), new(2, 3)])],
                _ => [new("id", [new(0, 2)]), new("other", [new(1, 3)])],
            };
            await node.ClusterMigrateSlotsAsync(groups);
        }).Throws<ArgumentException>();
        await Assert.That(target.ConnectionAccepted.IsCompleted).IsFalse();
    }

    [Test]
    [Arguments("start")]
    [Arguments("cancel")]
    [Arguments("flush")]
    public async Task MutationsRequireAdminButStatusDoesNot(string operation)
    {
        await using var target = Server(1);
        await using var client = RespireClient.Create(Options(target.Port, 2) with { AllowAdmin = false });
        var node = client.Server.OnNode(new("127.0.0.1", target.Port));
        await Assert.That(async () => await Call(node, operation)).Throws<NotSupportedException>();
        await Assert.That(target.ConnectionAccepted.IsCompleted).IsFalse();
        await Assert.That(await node.ClusterGetSlotMigrationsAsync()).IsEmpty();
    }

    [Test]
    [Arguments("start")]
    [Arguments("cancel")]
    [Arguments("flush")]
    [Arguments("status")]
    public async Task PreCancellationDoesNotConnect(string operation)
    {
        await using var target = Server(1);
        await using var client = RespireClient.Create(Options(target.Port, 2));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(async () => await Call(client.Server.OnNode(new("127.0.0.1", target.Port)), operation, cancellation.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(target.ConnectionAccepted.IsCompleted).IsFalse();
    }

    [Test]
    [Arguments("MOVED")]
    [Arguments("ASK")]
    [Arguments("ERR")]
    public async Task ErrorsDoNotRedirectOrReplay(string error)
    {
        await using var seed = Server(1);
        await using var target = Server(1);
        target.ReplyOverride = (_, _) => Encoding.UTF8.GetBytes($"-{error} 0 127.0.0.1:{seed.Port}\r\n");
        await using var client = RespireClient.Create(Options(seed.Port, 2));
        await Assert.That(async () => await Call(client.Server.OnNode(new("127.0.0.1", target.Port)), "start"))
            .Throws<RespireServerException>();
        await Assert.That(target.ReceivedCommands.Count).IsEqualTo(1);
        await Assert.That(seed.ConnectionAccepted.IsCompleted).IsFalse();
        await target.PeerClosed.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    [Arguments("start", false)]
    [Arguments("start", true)]
    [Arguments("cancel", false)]
    [Arguments("cancel", true)]
    [Arguments("flush", false)]
    [Arguments("flush", true)]
    public async Task MutationsFenceCacheAndReleaseSockets(string operation, bool cancel)
    {
        await using var seed = Server(1);
        await using var target = Server(1);
        var received = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        target.SuppressReply = command =>
        {
            if (!command.StartsWith("CLUSTER ", StringComparison.Ordinal)) return false;
            received.TrySetResult(target.ReceivedConnectionIds[^1]); return true;
        };
        await using var client = await RespireClient.ConnectAsync(Options(seed.Port, 3) with { ClientSideCache = new() });
        var cache = client.Core.ClientCache!;
        Insert();
        using var cancellation = new CancellationTokenSource();
        var pending = Call(client.Server.OnNode(new("127.0.0.1", target.Port)), operation, cancellation.Token);
        var connection = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(cache.Count).IsEqualTo(0);
        Insert();
        if (cancel)
        {
            cancellation.Cancel();
            await Assert.That(async () => await pending).Throws<OperationCanceledException>();
        }
        else { await target.SendRawAsync(FakeRespServer.OkReply, connection); await pending; }
        await Assert.That(cache.Count).IsEqualTo(0);
        await target.PeerClosed.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(target.ReceivedCommands.Count(command => command.StartsWith("CLUSTER ", StringComparison.Ordinal))).IsEqualTo(1);

        void Insert()
        {
            RespireKey key = "cached";
            var read = cache.BeginRead(in key);
            using var reply = RespValue.BulkString("stale");
            cache.CompleteRead(in read, in reply, allowInsert: true);
        }
    }

    private static async Task Call(RespireServerNode node, string operation, CancellationToken token = default)
    {
        switch (operation)
        {
            case "start": await node.ClusterMigrateSlotsAsync([new("target", [new(0, 1)])], token); break;
            case "cancel": await node.ClusterCancelSlotMigrationsAsync(token); break;
            case "flush": await node.ClusterFlushSlotAsync(0, cancellationToken: token); break;
            default: await node.ClusterGetSlotMigrationsAsync(token); break;
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
            "CLUSTER GETSLOTMIGRATIONS" => "*0\r\n"u8.ToArray(),
            _ => FakeRespServer.OkReply,
        };
        return server;
    }
}
