using System.Text;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class StreamConfigurationTests
{
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task ExactWireAndKeySnapshotAcrossSurfaces(int surface)
    {
        byte[][] replies = [FakeRespServer.OkReply, FakeRespServer.OkReply, FakeRespServer.OkReply];
        if (surface == 2) replies = [FakeRespServer.OkReply, .. Enumerable.Repeat("+QUEUED\r\n"u8.ToArray(), 3), "*3\r\n+OK\r\n+OK\r\n+OK\r\n"u8.ToArray()];
        await using var server = new FakeRespServer(replies);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var view = client.WithKeyPrefix("tenant:");
        using var batch = surface == 1 ? view.CreateBatch() : null;
        await using var transaction = surface == 2 ? view.CreateTransaction() : null;
        IRespireCommandQueue? queue = transaction ?? (IRespireCommandQueue?)batch;
        byte[] key = [0, 255, 32];
        StreamConfigurationOptions[] options = [new() { IdempotencyDurationSeconds = 1 }, new() { IdempotencyMaxSize = 10000 },
            new() { IdempotencyDurationSeconds = 86400, IdempotencyMaxSize = 1 }];
        List<RespirePending<bool>> pending = [];
        foreach (var option in options)
        {
            if (queue is null) await Assert.That(await view.Streams.ConfigureAsync(key, option)).IsTrue();
            else pending.Add(queue.Streams.Configure(key, option));
        }
        Array.Fill(key, (byte)'x');
        if (transaction is not null) await transaction.CommitAsync();
        else if (batch is not null) await batch.ExecuteAsync();
        foreach (var result in pending) await Assert.That(result.Result).IsTrue();
        var commands = server.ReceivedArguments.Where(x => Encoding.ASCII.GetString(x[0]) == "XCFGSET").ToArray();
        await Assert.That(commands.Length).IsEqualTo(3);
        string[][] tails = [["IDMP-DURATION", "1"], ["IDMP-MAXSIZE", "10000"], ["IDMP-DURATION", "86400", "IDMP-MAXSIZE", "1"]];
        for (var index = 0; index < commands.Length; index++)
        {
            await Assert.That(commands[index][1]).IsEquivalentTo("tenant:"u8.ToArray().Concat(new byte[] { 0, 255, 32 }), CollectionOrdering.Matching);
            await Assert.That(commands[index].Skip(2).Select(Encoding.ASCII.GetString)).IsEquivalentTo(tails[index], CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task InvalidOptionsAndCancellationDoNotConnectOrEnqueue()
    {
        await using var server = new FakeRespServer();
        await using var client = RespireClient.Create(new RespireOptions { Protocol = RespProtocol.Resp2, Connections = 1, Endpoints = [new("127.0.0.1", server.Port)] });
        using var batch = client.CreateBatch();
        await using var transaction = client.CreateTransaction();
        StreamConfigurationOptions[] invalid = [default, new() { IdempotencyDurationSeconds = 0 }, new() { IdempotencyDurationSeconds = 86401 },
            new() { IdempotencyMaxSize = 0 }, new() { IdempotencyMaxSize = 10001 }, new() { IdempotencyDurationSeconds = 1, IdempotencyMaxSize = -1 }];
        foreach (var options in invalid)
        {
            await Assert.That(async () => await client.Streams.ConfigureAsync("s", options)).Throws<ArgumentException>();
            await Assert.That(() => batch.Streams.Configure("s", options)).Throws<ArgumentException>();
            await Assert.That(() => transaction.Streams.Configure("s", options)).Throws<ArgumentException>();
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(async () => await client.Streams.ConfigureAsync("s", new() { IdempotencyMaxSize = 1 }, cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(batch.Count + transaction.Count).IsEqualTo(0);
        await Assert.That(server.CommandsSeen).IsEqualTo(0);
    }

    [Test]
    [Arguments(":1\r\n")]
    [Arguments("+NO\r\n")]
    [Arguments("-ERR unknown command 'XCFGSET'\r\n")]
    public async Task InvalidReplyDoesNotPoisonFollowingCommand(string reply)
    {
        await using var server = new FakeRespServer(Encoding.ASCII.GetBytes(reply), FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var options = new StreamConfigurationOptions { IdempotencyMaxSize = 1 };
        await Assert.That(async () => await client.Streams.ConfigureAsync("s", options)).Throws<RespireException>();
        await Assert.That(await client.Streams.ConfigureAsync("s", options)).IsTrue();
        await Assert.That(server.CommandsSeen).IsEqualTo(2);
    }

    [Test]
    public async Task ClusterRoutesPrefixedStreamKey()
    {
        await using var owner = new FakeRespServer(FakeRespServer.OkReply);
        var slot = ClusterHash.GetSlot("{tenant}:events");
        await using var seed = new FakeRespServer(Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:{slot}\r\n:{slot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{owner.Port}\r\n"));
        await using var client = await RespireClient.ConnectAsync(new RespireOptions { Protocol = RespProtocol.Resp2, UseCluster = true, Connections = 1, Endpoints = [new("127.0.0.1", seed.Port)] });
        await client.WithKeyPrefix("{tenant}:").Streams.ConfigureAsync("events", new() { IdempotencyMaxSize = 1 });
        await Assert.That(owner.ReceivedCommands).IsEquivalentTo(new[] { "XCFGSET {tenant}:events IDMP-MAXSIZE 1" });
        await Assert.That(seed.ReceivedCommands).IsEquivalentTo(new[] { "CLUSTER SLOTS" });
    }

    [Test]
    public async Task CancellationAfterDispatchDoesNotReplayConfiguration()
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray())
        {
            SuppressReply = command => { if (!command.StartsWith("XCFGSET ")) return false; received.TrySetResult(); return true; },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var cancellation = new CancellationTokenSource();
        var operation = client.Streams.ConfigureAsync("s", new() { IdempotencyMaxSize = 1 }, cancellation.Token).AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.That(async () => await operation.WaitAsync(TimeSpan.FromSeconds(5))).Throws<OperationCanceledException>();
        await server.SendRawAsync(FakeRespServer.OkReply, server.ReceivedConnectionIds[^1]);
        await Assert.That(await client.Streams.CountAsync("s")).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[] { "XCFGSET s IDMP-MAXSIZE 1", "XLEN s" }, CollectionOrdering.Matching);
    }
}
