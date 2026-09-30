using System.Text;
using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class MultiKeyListPopTests
{
    private static readonly byte[] ManyReply = "*2\r\n$3\r\nkey\r\n*2\r\n$1\r\na\r\n$1\r\nb\r\n"u8.ToArray();

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DeferredPop_SnapshotsKeyArgumentsAndOwnsResults(bool transactional)
    {
        byte[][] replies = transactional
            ? [FakeRespServer.OkReply, "+QUEUED\r\n"u8.ToArray(), "*1\r\n"u8.ToArray().Concat(ManyReply).ToArray()]
            : [ManyReply];
        await using var server = new FakeRespServer(replies);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var batch = transactional ? null : client.CreateBatch();
        await using var transaction = transactional ? client.CreateTransaction() : null;
        IRespireCommandQueue queue = transaction ?? (IRespireCommandQueue)batch!;
        RespireKey[] keys = ["missing", "key"];
        var pending = queue.Lists.PopMany(keys, 2, ListSide.Right);
        keys[1] = "changed";
        if (transaction is not null)
            await transaction.CommitAsync();
        else
            await batch!.ExecuteAsync();
        await Assert.That(pending.Result!.Value.Key).IsEqualTo((RespireKey)"key");
        await Assert.That(pending.Result.Value.Values).IsEquivalentTo(["a", "b"], CollectionOrdering.Matching);
        await Assert.That(server.ReceivedCommands.Where(command => command is not "MULTI" and not "EXEC"))
            .IsEquivalentTo(["LMPOP 2 missing key RIGHT COUNT 2"]);
    }

    [Test]
    public async Task Parser_DetachesBinaryKeysAndRejectsUnexpectedShapes()
    {
        byte[] key = [(byte)'p', (byte)':', 0xff, 0];
        using var reply = RespValue.Array(RespValue.BulkString(key), RespValue.Array(RespValue.BulkString("a")));
        var result = ListCommands.ParsePopMany(in reply, "p:"u8)!.Value;
        key[2] = 1;
        await Assert.That(result.Key).IsEqualTo((RespireKey)new byte[] { 0xff, 0 });
        await Assert.That(result.Values).IsEquivalentTo(["a"]);
        await Assert.That(() => ListCommands.ParsePopMany(in reply, "other:"u8))
            .Throws<RespireProtocolException>();
        using var malformed = RespValue.Array(RespValue.BulkString("key"));
        await Assert.That(() => ListCommands.ParsePopMany(in malformed, default))
            .Throws<RespireProtocolException>();
    }

    [Test]
    public async Task InvalidArguments_FailBeforeSendingOrEnqueueing()
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        Func<Task>[] invalid =
        [
            async () => { await client.Lists.PopManyAsync([]); },
            async () => { await client.Lists.PopManyAsync(["key"], 0); },
            async () => { await client.Lists.PopManyAsync(["key"], -1); },
            async () => { await client.Lists.PopManyAsync(["key"], side: (ListSide)99); },
            async () => { await client.Lists.PopManyAsync(["key"], waitFor: TimeSpan.FromSeconds(-2)); },
            async () => { await client.Lists.PopAsync([], TimeSpan.Zero); },
            async () => { await client.Lists.PopAsync(["key"], TimeSpan.Zero, (ListSide)99); },
            async () => { await client.Lists.PopAsync(["key"], TimeSpan.FromSeconds(-2)); },
        ];
        foreach (var command in invalid)
            await Assert.That(command).Throws<ArgumentException>();
        using var batch = client.CreateBatch();
        await using var transaction = client.CreateTransaction();
        foreach (var lists in new[] { batch.Lists, transaction.Lists })
        {
            await Assert.That(() => lists.PopMany([])).Throws<ArgumentException>();
            await Assert.That(() => lists.PopMany(["key"], 0)).Throws<ArgumentOutOfRangeException>();
            await Assert.That(() => lists.PopMany(["key"], -1)).Throws<ArgumentOutOfRangeException>();
            await Assert.That(() => lists.PopMany(["key"], side: (ListSide)99)).Throws<ArgumentOutOfRangeException>();
        }
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BlockingCancellation_DiscardsLeaseAndLeavesMultiplexedTrafficUsable(bool many)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(3, FakeRespServer.PongReply)
        {
            SuppressReply = command =>
            {
                if (!command.StartsWith("BL", StringComparison.Ordinal)) return false;
                received.TrySetResult();
                return true;
            },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var cancellation = new CancellationTokenSource();
        Task pending = many
            ? client.Lists.PopManyAsync(["a", "b"], waitFor: Timeout.InfiniteTimeSpan, cancellationToken: cancellation.Token).AsTask()
            : client.Lists.PopAsync(["a", "b"], Timeout.InfiniteTimeSpan, cancellationToken: cancellation.Token).AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var pong = await client.ExecuteAsync("PING").AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(pong.AsString()).IsEqualTo("PONG");
        cancellation.Cancel();
        await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5))).Throws<OperationCanceledException>();
        server.SuppressReply = null;
        var next = await client.Core.DedicatedPool.RentAsync(CancellationToken.None);
        using var response = await next.SendWithoutResponseTimeoutAsync(
            new Cmd(new Verb("PING")), CancellationToken.None);
        client.Core.DedicatedPool.Return(next);
        await Assert.That(response.AsString()).IsEqualTo("PONG");
        await Assert.That(server.ReceivedConnectionIds.Distinct().Count()).IsEqualTo(3);
    }

    [Test]
    [Arguments("immediate")]
    [Arguments("batch")]
    [Arguments("transaction")]
    [Arguments("many-blocking")]
    [Arguments("one-left")]
    [Arguments("one-right")]
    public async Task ClusterRouting_UsesFirstKeyAfterCountAndTimeout(string path)
    {
        const string selected = "tenant:{lists}:second";
        var scalar = path.StartsWith("one-", StringComparison.Ordinal);
        var payload = scalar ? "$1\r\na\r\n" : "*1\r\n$1\r\na\r\n";
        var response = Encoding.ASCII.GetBytes($"*2\r\n${selected.Length}\r\n{selected}\r\n{payload}");
        byte[][] replies = path == "transaction"
            ? [FakeRespServer.OkReply, "+QUEUED\r\n"u8.ToArray(), "*1\r\n"u8.ToArray().Concat(response).ToArray()]
            : [response];
        await using var owner = new FakeRespServer(2, replies);
        var slot = ClusterHash.GetSlot("tenant:{lists}:first");
        var topology = Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:{slot}\r\n:{slot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{owner.Port}\r\n");
        await using var seed = new FakeRespServer(topology, "-ERR wrong route\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            UseCluster = true, Connections = 1, Endpoints = [new("127.0.0.1", seed.Port)],
        });
        var view = client.WithKeyPrefix("tenant:");
        RespireKey[] keys = ["{lists}:first", "{lists}:second"];
        RespireKey returned;
        if (scalar)
            returned = (await view.Lists.PopAsync(keys, TimeSpan.FromSeconds(1), path == "one-left" ? ListSide.Left : ListSide.Right))!.Value.Key;
        else if (path is "immediate" or "many-blocking")
            returned = (await view.Lists.PopManyAsync(keys, waitFor: path == "many-blocking" ? TimeSpan.FromSeconds(1) : null))!.Value.Key;
        else if (path == "batch")
        {
            using var batch = view.CreateBatch();
            var pending = batch.Lists.PopMany(keys);
            await batch.ExecuteAsync();
            returned = pending.Result!.Value.Key;
        }
        else
        {
            await using var transaction = view.CreateTransaction();
            var pending = transaction.Lists.PopMany(keys);
            await transaction.CommitAsync();
            returned = pending.Result!.Value.Key;
        }
        await Assert.That(returned).IsEqualTo((RespireKey)"{lists}:second");
        await Assert.That(seed.ReceivedCommands).IsEquivalentTo(["CLUSTER SLOTS"]);
        var expected = path switch
        {
            "one-left" => "BLPOP tenant:{lists}:first tenant:{lists}:second 1",
            "one-right" => "BRPOP tenant:{lists}:first tenant:{lists}:second 1",
            "many-blocking" => "BLMPOP 1 2 tenant:{lists}:first tenant:{lists}:second LEFT COUNT 1",
            _ => "LMPOP 2 tenant:{lists}:first tenant:{lists}:second LEFT COUNT 1",
        };
        await Assert.That(owner.ReceivedCommands.Where(command => command is not "MULTI" and not "EXEC"))
            .IsEquivalentTo([expected]);
    }

    [Test]
    public async Task CrossSlotKeys_AreRejectedBeforeSendingOrEnqueueing()
    {
        await using var server = new FakeRespServer("*0\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            UseCluster = true, Connections = 1, Endpoints = [new("127.0.0.1", server.Port)],
        });
        var view = client.WithKeyPrefix("tenant:");
        RespireKey[] keys = ["{a}:first", "{b}:second"];
        await Assert.That(async () => await view.Lists.PopManyAsync(keys)).Throws<RespireServerException>().WithMessage("CROSSSLOT Keys in request don't hash to the same slot");
        await Assert.That(async () => await view.Lists.PopManyAsync(keys, waitFor: TimeSpan.Zero)).Throws<RespireServerException>().WithMessage("CROSSSLOT Keys in request don't hash to the same slot");
        await Assert.That(async () => await view.Lists.PopAsync(keys, TimeSpan.Zero)).Throws<RespireServerException>().WithMessage("CROSSSLOT Keys in request don't hash to the same slot");
        using var batch = view.CreateBatch();
        await using var transaction = view.CreateTransaction();
        await Assert.That(() => batch.Lists.PopMany(keys)).Throws<RespireServerException>().WithMessage("CROSSSLOT Keys in request don't hash to the same slot");
        await Assert.That(() => transaction.Lists.PopMany(keys)).Throws<RespireServerException>().WithMessage("CROSSSLOT Keys in request don't hash to the same slot");
        _ = transaction.Lists.PopMany(["{c}:first", "{c}:second"]);
        await Assert.That(server.ReceivedCommands.Where(command => command != "CLUSTER SLOTS")).IsEmpty();
    }

    [Test]
    public async Task CompletedBlockingPops_ReuseDedicatedLease()
    {
        await using var server = new FakeRespServer(2, "*-1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await Assert.That(await client.Lists.PopManyAsync(["key"], waitFor: TimeSpan.Zero)).IsNull();
        await Assert.That(await client.Lists.PopAsync(["key"], TimeSpan.Zero, ListSide.Right)).IsNull();
        await Assert.That(server.ReceivedConnectionIds.Distinct().Count()).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(
            ["BLMPOP 0.001 1 key LEFT COUNT 1", "BRPOP key 0.001"], CollectionOrdering.Matching);
    }
}
