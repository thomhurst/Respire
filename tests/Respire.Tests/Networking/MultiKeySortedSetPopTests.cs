using System.Text;
using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class MultiKeySortedSetPopTests
{
    private static readonly byte[] ManyReply = "*2\r\n$3\r\nkey\r\n*2\r\n*2\r\n$1\r\na\r\n$1\r\n1\r\n*2\r\n$1\r\nb\r\n$1\r\n2\r\n"u8.ToArray();

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
        var pending = queue.SortedSets.PopMany(keys, 2, true);
        keys[1] = "changed";
        if (transaction is not null)
            await transaction.CommitAsync();
        else
            await batch!.ExecuteAsync();
        await Assert.That(pending.Result!.Value.Key).IsEqualTo((RespireKey)"key");
        await Assert.That(pending.Result.Value.Entries).IsEquivalentTo([new SortedSetEntry("a", 1), new SortedSetEntry("b", 2)], CollectionOrdering.Matching);
        await Assert.That(server.ReceivedCommands.Where(command => command is not "MULTI" and not "EXEC"))
            .IsEquivalentTo(["ZMPOP 2 missing key MAX COUNT 2"]);
    }

    [Test]
    public async Task Parser_DetachesBinaryKeysAndRejectsUnexpectedShapes()
    {
        byte[] key = [(byte)'p', (byte)':', 0xff, 0];
        using var reply = RespValue.Array(RespValue.BulkString(key), RespValue.Array(RespValue.Array(RespValue.BulkString("a"), RespValue.BulkString("1"))));
        var result = SortedSetCommands.ParsePopMany(in reply, "p:"u8)!.Value;
        key[2] = 1;
        await Assert.That(result.Key).IsEqualTo((RespireKey)new byte[] { 0xff, 0 });
        await Assert.That(result.Entries).IsEquivalentTo([new SortedSetEntry("a", 1)]);
        await Assert.That(() => SortedSetCommands.ParsePopMany(in reply, "other:"u8))
            .Throws<RespireProtocolException>();
        using var malformed = RespValue.Array(RespValue.BulkString("key"));
        await Assert.That(() => SortedSetCommands.ParsePopMany(in malformed, default))
            .Throws<RespireProtocolException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TypedBinaryResults_OwnKeyAndMemberBytes(bool many)
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        byte[] key = [0xff, 0];
        byte[] member = [0xfe, 0, 0x42];
        RespireKey selected;
        byte[] value;
        using (var reply = many
            ? RespValue.Array(RespValue.BulkString(key), RespValue.Array(
                RespValue.Array(RespValue.BulkString(member), RespValue.Double(1.5))))
            : RespValue.Array(RespValue.BulkString(key), RespValue.BulkString(member), RespValue.Double(1.5)))
        {
            if (many)
            {
                var result = SortedSetCommands.ParsePopMany<byte[]>(client, in reply)!.Value;
                selected = result.Key;
                value = result.Entries[0].Member;
            }
            else
            {
                var result = SortedSetCommands.ParsePopOne<byte[]>(client, in reply)!.Value;
                selected = result.Key;
                value = result.Entry.Member;
            }
        }
        Array.Clear(key);
        Array.Clear(member);
        await Assert.That(selected).IsEqualTo((RespireKey)new byte[] { 0xff, 0 });
        await Assert.That(value).IsEquivalentTo(new byte[] { 0xfe, 0, 0x42 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Parsers_RejectMalformedEntriesAndAcceptNull()
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        RespValue[] malformed =
        [
            RespValue.Integer(7),
            RespValue.Array(RespValue.BulkString("key"), RespValue.Integer(1)),
            RespValue.Array(RespValue.BulkString("key"), RespValue.Array(Array.Empty<RespValue>())),
            RespValue.Array(RespValue.BulkString("key"), RespValue.Array(RespValue.BulkString("7"))),
            RespValue.Array(RespValue.BulkString("key"), RespValue.Array(RespValue.Array(RespValue.BulkString("7")))),
        ];
        foreach (var reply in malformed)
        {
            using (reply)
            {
                await Assert.That(() => SortedSetCommands.ParsePopMany(in reply, default)).Throws<RespireProtocolException>();
                await Assert.That(() => SortedSetCommands.ParsePopMany<int>(client, in reply)).Throws<RespireProtocolException>();
            }
        }
        var missing = RespValue.Null;
        await Assert.That(SortedSetCommands.ParsePopMany(in missing, default)).IsNull();
        await Assert.That(SortedSetCommands.ParsePopMany<int>(client, in missing)).IsNull();
        await Assert.That(SortedSetCommands.ParsePopOne(in missing, default)).IsNull();
        await Assert.That(SortedSetCommands.ParsePopOne<int>(client, in missing)).IsNull();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MalformedScore_PreservesFollowingReply(bool many)
    {
        var prefix = many ? "*2\r\n$3\r\nkey\r\n*1\r\n*2\r\n" : "*3\r\n$3\r\nkey\r\n";
        var invalid = Encoding.ASCII.GetBytes(prefix + "$1\r\n7\r\n$7\r\n1.5junk\r\n");
        var valid = Encoding.ASCII.GetBytes(prefix + "$1\r\n7\r\n$3\r\n1.5\r\n");
        await using var server = new FakeRespServer(2, invalid, valid);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        if (many)
        {
            await Assert.That(async () => await client.SortedSets.PopManyAsync<int>(["key"]))
                .Throws<RespireProtocolException>();
            var result = await client.SortedSets.PopManyAsync<int>(["key"]);
            await Assert.That(result!.Value.Entries).IsEquivalentTo([new SortedSetEntry<int>(7, 1.5)]);
        }
        else
        {
            await Assert.That(async () => await client.SortedSets.PopAsync<int>(["key"], TimeSpan.Zero))
                .Throws<RespireProtocolException>();
            var result = await client.SortedSets.PopAsync<int>(["key"], TimeSpan.Zero);
            await Assert.That(result!.Value.Entry).IsEqualTo(new SortedSetEntry<int>(7, 1.5));
        }
    }

    [Test]
    public async Task InvalidArguments_FailBeforeSendingOrEnqueueing()
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        Func<Task>[] invalid =
        [
            async () => { await client.SortedSets.PopManyAsync([]); },
            async () => { await client.SortedSets.PopManyAsync(["key"], 0); },
            async () => { await client.SortedSets.PopManyAsync(["key"], -1); },
            async () => { await client.SortedSets.PopManyAsync(["key"], waitFor: TimeSpan.FromSeconds(-2)); },
            async () => { await client.SortedSets.PopAsync([], TimeSpan.Zero); },
            async () => { await client.SortedSets.PopAsync(["key"], TimeSpan.FromSeconds(-2)); },
        ];
        foreach (var command in invalid)
            await Assert.That(command).Throws<ArgumentException>();
        using var batch = client.CreateBatch();
        await using var transaction = client.CreateTransaction();
        foreach (var sortedSets in new[] { batch.SortedSets, transaction.SortedSets })
        {
            await Assert.That(() => sortedSets.PopMany([])).Throws<ArgumentException>();
            await Assert.That(() => sortedSets.PopMany(["key"], 0)).Throws<ArgumentOutOfRangeException>();
            await Assert.That(() => sortedSets.PopMany(["key"], -1)).Throws<ArgumentOutOfRangeException>();
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
                if (!command.StartsWith("BZ", StringComparison.Ordinal)) return false;
                received.TrySetResult();
                return true;
            },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var cancellation = new CancellationTokenSource();
        Task pending = many
            ? client.SortedSets.PopManyAsync(["a", "b"], waitFor: Timeout.InfiniteTimeSpan, cancellationToken: cancellation.Token).AsTask()
            : client.SortedSets.PopAsync(["a", "b"], Timeout.InfiniteTimeSpan, cancellationToken: cancellation.Token).AsTask();
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
    [Arguments("one-min")]
    [Arguments("one-max")]
    public async Task ClusterRouting_UsesFirstKeyAfterCountAndTimeout(string path)
    {
        const string selected = "tenant:{sets}:second";
        var scalar = path.StartsWith("one-", StringComparison.Ordinal);
        var payload = scalar ? "$1\r\na\r\n$1\r\n1\r\n" : "*1\r\n*2\r\n$1\r\na\r\n$1\r\n1\r\n";
        var response = Encoding.ASCII.GetBytes($"*{(scalar ? 3 : 2)}\r\n${selected.Length}\r\n{selected}\r\n{payload}");
        byte[][] replies = path == "transaction"
            ? [FakeRespServer.OkReply, "+QUEUED\r\n"u8.ToArray(), "*1\r\n"u8.ToArray().Concat(response).ToArray()]
            : [response];
        await using var owner = new FakeRespServer(2, replies);
        var slot = ClusterHash.GetSlot("tenant:{sets}:first");
        var topology = Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:{slot}\r\n:{slot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{owner.Port}\r\n");
        await using var seed = new FakeRespServer(topology, "-ERR wrong route\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            UseCluster = true, Connections = 1, Endpoints = [new("127.0.0.1", seed.Port)],
        });
        var view = client.WithKeyPrefix("tenant:");
        RespireKey[] keys = ["{sets}:first", "{sets}:second"];
        RespireKey returned;
        if (scalar)
            returned = (await view.SortedSets.PopAsync(keys, TimeSpan.FromSeconds(1), path == "one-max"))!.Value.Key;
        else if (path is "immediate" or "many-blocking")
            returned = (await view.SortedSets.PopManyAsync(keys, waitFor: path == "many-blocking" ? TimeSpan.FromSeconds(1) : null))!.Value.Key;
        else if (path == "batch")
        {
            using var batch = view.CreateBatch();
            var pending = batch.SortedSets.PopMany(keys);
            await batch.ExecuteAsync();
            returned = pending.Result!.Value.Key;
        }
        else
        {
            await using var transaction = view.CreateTransaction();
            var pending = transaction.SortedSets.PopMany(keys);
            await transaction.CommitAsync();
            returned = pending.Result!.Value.Key;
        }
        await Assert.That(returned).IsEqualTo((RespireKey)"{sets}:second");
        await Assert.That(seed.ReceivedCommands).IsEquivalentTo(["CLUSTER SLOTS"]);
        var expected = path switch
        {
            "one-min" => "BZPOPMIN tenant:{sets}:first tenant:{sets}:second 1",
            "one-max" => "BZPOPMAX tenant:{sets}:first tenant:{sets}:second 1",
            "many-blocking" => "BZMPOP 1 2 tenant:{sets}:first tenant:{sets}:second MIN COUNT 1",
            _ => "ZMPOP 2 tenant:{sets}:first tenant:{sets}:second MIN COUNT 1",
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
        await Assert.That(async () => await view.SortedSets.PopManyAsync(keys)).Throws<RespireServerException>().WithMessage("CROSSSLOT Keys in request don't hash to the same slot");
        await Assert.That(async () => await view.SortedSets.PopManyAsync(keys, waitFor: TimeSpan.Zero)).Throws<RespireServerException>().WithMessage("CROSSSLOT Keys in request don't hash to the same slot");
        await Assert.That(async () => await view.SortedSets.PopAsync(keys, TimeSpan.Zero)).Throws<RespireServerException>().WithMessage("CROSSSLOT Keys in request don't hash to the same slot");
        using var batch = view.CreateBatch();
        await using var transaction = view.CreateTransaction();
        await Assert.That(() => batch.SortedSets.PopMany(keys)).Throws<RespireServerException>().WithMessage("CROSSSLOT Keys in request don't hash to the same slot");
        await Assert.That(() => transaction.SortedSets.PopMany(keys)).Throws<RespireServerException>().WithMessage("CROSSSLOT Keys in request don't hash to the same slot");
        _ = transaction.SortedSets.PopMany(["{c}:first", "{c}:second"]);
        await Assert.That(server.ReceivedCommands.Where(command => command != "CLUSTER SLOTS")).IsEmpty();
    }

    [Test]
    public async Task CompletedBlockingPops_ReuseDedicatedLease()
    {
        await using var server = new FakeRespServer(2, "*-1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await Assert.That(await client.SortedSets.PopManyAsync(["key"], waitFor: TimeSpan.Zero)).IsNull();
        await Assert.That(await client.SortedSets.PopAsync(["key"], TimeSpan.Zero, true)).IsNull();
        await Assert.That(server.ReceivedConnectionIds.Distinct().Count()).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(
            ["BZMPOP 0.001 1 key MIN COUNT 1", "BZPOPMAX key 0.001"], CollectionOrdering.Matching);
    }
}
