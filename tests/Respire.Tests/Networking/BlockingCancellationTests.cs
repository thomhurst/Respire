using System.Text;
using Respire.Internal;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class BlockingCancellationTests
{
    [Test]
    [Arguments("standalone", RespireProtocol.Resp2)]
    [Arguments("standalone", RespireProtocol.Resp3)]
    [Arguments("cluster", RespireProtocol.Resp2)]
    [Arguments("sentinel", RespireProtocol.Resp2)]
    public async Task ThousandCancelledPopsReuseOneDedicatedConnection(string mode, RespireProtocol protocol)
    {
        await using var server = new FakeRespServer(3, FakeRespServer.PongReply);
        var received = NewSignal();
        var blockedId = -1;
        Task unblock = Task.CompletedTask;
        server.SuppressReply = command => command.StartsWith("BLPOP", StringComparison.Ordinal);
        server.ReplyOverride = (id, command) =>
        {
            if (command.StartsWith("HELLO", StringComparison.Ordinal))
                return Encoding.ASCII.GetBytes($"%2\r\n+proto\r\n:3\r\n+id\r\n:{id + 100}\r\n");
            if (command == "ROLE") return "*3\r\n+master\r\n:0\r\n*0\r\n"u8.ToArray();
            if (command == "CLUSTER SLOTS")
                return Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{server.Port}\r\n");
            if (command == "CLIENT ID") return Encoding.ASCII.GetBytes($":{id + 100}\r\n");
            if (command.StartsWith("CLIENT UNBLOCK", StringComparison.Ordinal))
            {
                unblock = server.SendRawAsync("*-1\r\n"u8.ToArray(), blockedId);
                return ":1\r\n"u8.ToArray();
            }
            return null;
        };
        server.GateCommand("BLPOP", Task.CompletedTask, () =>
        {
            blockedId = server.ReceivedConnectionIds[^1];
            received.TrySetResult();
        });
        await using var sentinel = new FakeRespServer(4, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("SENTINEL GET-MASTER-ADDR", StringComparison.Ordinal)
                ? Encoding.ASCII.GetBytes($"*2\r\n$9\r\n127.0.0.1\r\n${server.Port.ToString().Length}\r\n{server.Port}\r\n")
                : "*0\r\n"u8.ToArray(),
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = protocol, Connections = 1,
            Endpoints = [new("127.0.0.1", mode == "sentinel" ? sentinel.Port : server.Port)],
            UseCluster = mode == "cluster", SentinelPrimaryName = mode == "sentinel" ? "primary" : null,
            DedicatedPool = new() { UnblockOnCancel = true },
        });
        for (var index = 0; index < 1000; index++)
        {
            received = NewSignal();
            using var cancellation = new CancellationTokenSource();
            var pending = client.Lists.LeftPopAsync("jobs", waitFor: null, cancellation.Token).AsTask();
            await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            var error = await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(10)))
                .Throws<OperationCanceledException>();
            await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
            await unblock;
        }
        using var pong = await client.ExecuteAsync("PING");
        await Assert.That(pong.AsString()).IsEqualTo("PONG");
        await Assert.That(server.ReceivedConnectionIds.Distinct().Count()).IsEqualTo(2);
        await Assert.That(server.ReceivedCommands.Count(command => command == "CLIENT ID"))
            .IsEqualTo(protocol == RespireProtocol.Resp3 ? 0 : 1);
        await Assert.That(server.ReceivedCommands.Count(command => command == $"CLIENT UNBLOCK {blockedId + 100} TIMEOUT"))
            .IsEqualTo(1000);
    }

    [Test]
    [Arguments("-NOPERM denied\r\n")]
    [Arguments("-ERR unknown subcommand\r\n")]
    [Arguments(":0\r\n")]
    [Arguments(":1\r\n")]
    [Arguments("")]
    public async Task FailedUnblockOrMissingReplyDiscardsConnection(string unblockReply)
    {
        await using var server = new FakeRespServer(3, FakeRespServer.PongReply);
        var received = NewSignal();
        server.SuppressReply = command => command.StartsWith("BLPOP", StringComparison.Ordinal)
            || unblockReply.Length == 0 && command.StartsWith("CLIENT UNBLOCK", StringComparison.Ordinal);
        server.GateCommand("BLPOP", Task.CompletedTask, () => received.TrySetResult());
        server.ReplyOverride = (_, command) => command switch
        {
            "CLIENT ID" => ":123\r\n"u8.ToArray(),
            "CLIENT UNBLOCK 123 TIMEOUT" => Encoding.ASCII.GetBytes(unblockReply),
            _ => null,
        };
        await using var client = await ConnectAsync(server.Port, enabled: true);
        using var cancellation = new CancellationTokenSource();
        var pending = client.Lists.LeftPopAsync("jobs", waitFor: null, cancellation.Token).AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(10))).Throws<OperationCanceledException>();
        await server.PeerClosed.WaitAsync(TimeSpan.FromSeconds(10));
        server.SuppressReply = null;
        server.ReplyOverride = (_, command) => command.StartsWith("BLPOP", StringComparison.Ordinal)
            ? "*-1\r\n"u8.ToArray() : null;
        using var response = await client.ExecuteAsync("BLPOP", ["jobs", "0.001"]);
        await Assert.That(response.IsNull).IsTrue();
        await Assert.That(server.ReceivedConnectionIds.Distinct().Count()).IsEqualTo(3);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Test]
    public async Task SentinelFailoverNeverUnblocksClientIdOnNewPrimary()
    {
        await using var oldPrimary = new FakeRespServer(2, FakeRespServer.PongReply);
        await using var newPrimary = new FakeRespServer(2, FakeRespServer.PongReply);
        var primaryPort = oldPrimary.Port;
        var received = NewSignal();
        oldPrimary.SuppressReply = command => command.StartsWith("BLPOP", StringComparison.Ordinal);
        oldPrimary.GateCommand("BLPOP", Task.CompletedTask, () => received.TrySetResult());
        oldPrimary.ReplyOverride = (_, command) => command switch
        {
            "ROLE" => "*3\r\n+master\r\n:0\r\n*0\r\n"u8.ToArray(),
            "CLIENT ID" => ":123\r\n"u8.ToArray(),
            _ => null,
        };
        newPrimary.ReplyOverride = (_, command) => command == "ROLE"
            ? "*3\r\n+master\r\n:0\r\n*0\r\n"u8.ToArray() : null;
        await using var sentinel = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("SENTINEL GET-MASTER-ADDR", StringComparison.Ordinal)
                ? Encoding.ASCII.GetBytes($"*2\r\n$9\r\n127.0.0.1\r\n${primaryPort.ToString().Length}\r\n{primaryPort}\r\n")
                : "*0\r\n"u8.ToArray(),
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespireProtocol.Resp2, Connections = 1, SentinelPrimaryName = "primary",
            Endpoints = [new("127.0.0.1", sentinel.Port)],
            DedicatedPool = new() { UnblockOnCancel = true },
        });
        using var cancellation = new CancellationTokenSource();
        var pending = client.Lists.LeftPopAsync("jobs", waitFor: null, cancellation.Token).AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        primaryPort = newPrimary.Port;
        await client.Core.Sentinel!.GetGenerationAsync(default, forceDiscovery: true);
        cancellation.Cancel();
        await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(10))).Throws<OperationCanceledException>();
        using var pong = await client.ExecuteAsync("PING");
        await Assert.That(pong.AsString()).IsEqualTo("PONG");
        await Assert.That(newPrimary.ReceivedCommands.Contains("PING")).IsTrue();
        await Assert.That(newPrimary.ReceivedCommands.Any(command => command.StartsWith("CLIENT UNBLOCK", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    [Arguments("ASK")]
    [Arguments("MOVED")]
    public async Task ClusterRedirectUnblocksOnTargetNode(string redirect)
    {
        await using var target = new FakeRespServer(2, FakeRespServer.OkReply);
        await using var source = new FakeRespServer(2, FakeRespServer.OkReply);
        var received = NewSignal();
        var blockedId = -1;
        Task drain = Task.CompletedTask;
        var slot = ClusterHash.GetSlot("jobs");
        source.ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
            ? Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{source.Port}\r\n")
            : command.StartsWith("BLPOP", StringComparison.Ordinal)
                ? Encoding.ASCII.GetBytes($"-{redirect} {slot} 127.0.0.1:{target.Port}\r\n")
                : command == "CLIENT ID" ? ":101\r\n"u8.ToArray() : null;
        target.SuppressReply = command => command.StartsWith("BLPOP", StringComparison.Ordinal);
        target.GateCommand("BLPOP", Task.CompletedTask, () =>
        {
            blockedId = target.ReceivedConnectionIds[^1];
            received.TrySetResult();
        });
        target.ReplyOverride = (_, command) =>
        {
            if (command == "CLIENT ID") return ":202\r\n"u8.ToArray();
            if (command == "CLIENT UNBLOCK 202 TIMEOUT")
            {
                drain = target.SendRawAsync("*-1\r\n"u8.ToArray(), blockedId);
                return ":1\r\n"u8.ToArray();
            }
            return null;
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespireProtocol.Resp2, Connections = 1, UseCluster = true,
            Endpoints = [new("127.0.0.1", source.Port)],
            DedicatedPool = new() { UnblockOnCancel = true },
        });
        using var cancellation = new CancellationTokenSource();
        var pending = client.Lists.LeftPopAsync("jobs", waitFor: null, cancellation.Token).AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(10))).Throws<OperationCanceledException>();
        await drain;
        await Assert.That(target.ReceivedCommands.Contains("CLIENT UNBLOCK 202 TIMEOUT")).IsTrue();
        await Assert.That(source.ReceivedCommands.Any(command => command.StartsWith("CLIENT UNBLOCK", StringComparison.Ordinal))).IsFalse();
        var pool = client.Core.Cluster!.GetDedicatedPool(new("127.0.0.1", target.Port));
        var reused = await pool.RentAsync(default);
        pool.Return(reused);
        await Assert.That(target.ReceivedConnectionIds.Distinct().Count()).IsEqualTo(2);
        await Assert.That(target.ReceivedCommands.Contains("ASKING")).IsEqualTo(redirect == "ASK");
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, true)]
    public async Task DisabledOptionOrDeniedIdentityKeepsAbandonBehavior(bool enabled, bool denyIdentity)
    {
        await using var server = new FakeRespServer(2, FakeRespServer.PongReply);
        var received = NewSignal();
        server.SuppressReply = command => command.StartsWith("BLPOP", StringComparison.Ordinal);
        server.GateCommand("BLPOP", Task.CompletedTask, () => received.TrySetResult());
        server.ReplyOverride = (_, command) => command == "CLIENT ID" && denyIdentity
            ? "-NOPERM denied\r\n"u8.ToArray() : null;
        await using var client = await ConnectAsync(server.Port, enabled);
        using var cancellation = new CancellationTokenSource();
        var pending = client.Lists.LeftPopAsync("jobs", waitFor: null, cancellation.Token).AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(10))).Throws<OperationCanceledException>();
        await server.PeerClosed.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("CLIENT UNBLOCK", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CancellationRacingWithValueDrainsReplyBeforeReusingLease(bool successfulUnblock)
    {
        await using var server = new FakeRespServer(2, FakeRespServer.PongReply);
        var received = NewSignal();
        var controlReceived = NewSignal();
        var releaseControl = NewSignal();
        var blockedId = -1;
        server.SuppressReply = command => command.StartsWith("BLPOP", StringComparison.Ordinal);
        server.GateCommand("BLPOP", Task.CompletedTask, () =>
        {
            blockedId = server.ReceivedConnectionIds[^1];
            received.TrySetResult();
        });
        server.GateCommand("CLIENT UNBLOCK", releaseControl.Task, () => controlReceived.TrySetResult());
        server.ReplyOverride = (_, command) => command switch
        {
            "CLIENT ID" => ":123\r\n"u8.ToArray(),
            "CLIENT UNBLOCK 123 TIMEOUT" => Encoding.ASCII.GetBytes(successfulUnblock ? ":1\r\n" : ":0\r\n"),
            _ => null,
        };
        await using var client = await ConnectAsync(server.Port, enabled: true);
        using var cancellation = new CancellationTokenSource();
        var pending = client.Lists.LeftPopAsync("jobs", waitFor: null, cancellation.Token).AsTask();
        try
        {
            await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            await controlReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // The server consumed a value before processing UNBLOCK. Cancellation wins this
            // deliberately controlled race; the value is drained, as documented for destructive pops.
            await server.SendRawAsync("*2\r\n$4\r\njobs\r\n$5\r\nvalue\r\n"u8.ToArray(), blockedId);
            await Assert.That(pending.IsCompleted).IsFalse(); // Acknowledgement still owns the lease.
            releaseControl.TrySetResult();
            await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(10))).Throws<OperationCanceledException>();
            server.SuppressReply = null;
            server.ReplyOverride = (_, command) => command.StartsWith("BLPOP", StringComparison.Ordinal)
                ? "*-1\r\n"u8.ToArray() : null;
            using var next = await client.ExecuteAsync("BLPOP", ["jobs", "0.001"]);
            await Assert.That(next.IsNull).IsTrue();
            await Assert.That(server.ReceivedConnectionIds[^1]).IsEqualTo(blockedId);
        }
        finally { releaseControl.TrySetResult(); }
    }

    private static ValueTask<RespireClient> ConnectAsync(int port, bool enabled)
        => RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespireProtocol.Resp2,
            Connections = 1,
            Endpoints = { new("127.0.0.1", port) },
            DedicatedPool = new() { UnblockOnCancel = enabled },
        });
}
