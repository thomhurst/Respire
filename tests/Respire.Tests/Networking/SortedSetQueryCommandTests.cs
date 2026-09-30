using System.Text;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class SortedSetQueryCommandTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task DeferredCommands_PreserveWirePrefixDuplicatesAndOwnership(bool transactionMode, bool resp3)
    {
        byte[][] results = ["$1\r\na\r\n"u8.ToArray(), "*2\r\n$1\r\na\r\n$1\r\na\r\n"u8.ToArray(),
            Encoding.ASCII.GetBytes(resp3 ? "*2\r\n*2\r\n$1\r\na\r\n,1.5\r\n*2\r\n$1\r\na\r\n,1.5\r\n" : "*4\r\n$1\r\na\r\n$3\r\n1.5\r\n$1\r\na\r\n$3\r\n1.5\r\n"),
            ":2\r\n"u8.ToArray(), ":1\r\n"u8.ToArray(), ":3\r\n"u8.ToArray(), ":1\r\n"u8.ToArray()];
        var replies = transactionMode
            ? new[] { FakeRespServer.OkReply }.Concat(Enumerable.Repeat("+QUEUED\r\n"u8.ToArray(), results.Length))
                .Append(Encoding.ASCII.GetBytes($"*{results.Length}\r\n").Concat(results.SelectMany(x => x)).ToArray()).ToArray()
            : results;
        await using var server = new FakeRespServer(replies);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var view = client.WithKeyPrefix("tenant:");
        using var batch = transactionMode ? null : view.CreateBatch();
        await using var transaction = transactionMode ? view.CreateTransaction() : null;
        IRespireCommandQueue queue = transaction ?? (IRespireCommandQueue)batch!;
        var scalar = queue.SortedSets.RandomMember("key");
        var members = queue.SortedSets.RandomMembers("key", -2);
        var scored = queue.SortedSets.RandomMembersWithScores("key", -2);
        var count = queue.SortedSets.CountByLex("key", new RespireLexRange(RespireLexBound.Exclusive("a"), RespireLexBound.Max));
        var removed = queue.SortedSets.RemoveRangeByLex("key", new RespireLexRange(RespireLexBound.Min, RespireLexBound.Inclusive("c")));
        RespireKey[] keys = ["first", "second"];
        var intersection = queue.SortedSets.IntersectCount(keys);
        var limited = queue.SortedSets.IntersectCount(1, keys);
        keys[0] = "changed";
        if (transaction is not null) await transaction.CommitAsync(); else await batch!.ExecuteAsync();
        await Assert.That(scalar.Result).IsEqualTo("a");
        await Assert.That(members.Result).IsEquivalentTo(["a", "a"], CollectionOrdering.Matching);
        await Assert.That(scored.Result).IsEquivalentTo([new SortedSetEntry("a", 1.5), new SortedSetEntry("a", 1.5)], CollectionOrdering.Matching);
        await Assert.That(count.Result).IsEqualTo(2);
        await Assert.That(removed.Result).IsEqualTo(1);
        await Assert.That(intersection.Result).IsEqualTo(3);
        await Assert.That(limited.Result).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands.Where(command => command is not "MULTI" and not "EXEC"))
            .IsEquivalentTo(["ZRANDMEMBER tenant:key", "ZRANDMEMBER tenant:key -2", "ZRANDMEMBER tenant:key -2 WITHSCORES",
                "ZLEXCOUNT tenant:key (a +", "ZREMRANGEBYLEX tenant:key - [c", "ZINTERCARD 2 tenant:first tenant:second",
                "ZINTERCARD 2 tenant:first tenant:second LIMIT 1"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task InvalidArguments_FailBeforeNetworkOrQueueing()
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var batch = client.CreateBatch();
        await using var transaction = client.CreateTransaction();
        await Assert.That(async () => await client.SortedSets.IntersectCountAsync([])).ThrowsExactly<ArgumentException>();
        await Assert.That(async () => await client.SortedSets.IntersectCountAsync(-1, ["key"])).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.SortedSets.RandomMembersAsync("key", long.MinValue)).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.SortedSets.RandomMembersWithScoresAsync<int>("key", long.MinValue)).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.SortedSets.CountByLexAsync("key", default)).ThrowsExactly<InvalidOperationException>();
        foreach (IRespireCommandQueue queue in new IRespireCommandQueue[] { batch, transaction })
        {
            await Assert.That(() => queue.SortedSets.IntersectCount([])).ThrowsExactly<ArgumentException>();
            await Assert.That(() => queue.SortedSets.IntersectCount(-1, ["key"])).ThrowsExactly<ArgumentOutOfRangeException>();
            await Assert.That(() => queue.SortedSets.RandomMembers<int>("key", long.MinValue)).ThrowsExactly<ArgumentOutOfRangeException>();
            await Assert.That(() => queue.SortedSets.RandomMembersWithScores("key", long.MinValue)).ThrowsExactly<ArgumentOutOfRangeException>();
            await Assert.That(() => queue.SortedSets.RemoveRangeByLex("key", default)).ThrowsExactly<InvalidOperationException>();
        }
        await Assert.That(server.CommandsSeen).IsEqualTo(0);
    }

    [Test]
    public async Task Cluster_RejectsCrossSlotBeforeSendingAndDoesNotPoisonTransaction()
    {
        await using var server = new FakeRespServer();
        await using var client = RespireClient.Create(new RespireOptions
        {
            UseCluster = true, Connections = 1, Endpoints = [new("127.0.0.1", server.Port)]
        });
        var view = client.WithKeyPrefix("tenant:");
        RespireKey[] keys = ["{a}:one", "{b}:two"];
        await Assert.That(async () => await view.SortedSets.IntersectCountAsync(keys)).ThrowsExactly<RespireServerException>();
        using var batch = view.CreateBatch();
        await using var transaction = view.CreateTransaction();
        await Assert.That(() => batch.SortedSets.IntersectCount(keys)).ThrowsExactly<RespireServerException>();
        await Assert.That(() => transaction.SortedSets.IntersectCount(keys)).ThrowsExactly<RespireServerException>();
        _ = transaction.SortedSets.IntersectCount("{c}:one", "{c}:two");
        await Assert.That(() => transaction.SortedSets.CountByLex("{a}:one", RespireLexRange.All)).ThrowsExactly<InvalidOperationException>();
        await Assert.That(server.CommandsSeen).IsEqualTo(0);
    }

    [Test]
    [Arguments("immediate")]
    [Arguments("batch")]
    [Arguments("transaction")]
    public async Task IntersectCount_DispatchesToFirstKeyOwner(string path)
    {
        var slot = ClusterHash.GetSlot("tenant:{sets}:first");
        await Assert.That(slot).IsNotEqualTo(ClusterHash.GetSlot("2"));
        byte[][] replies = path == "transaction"
            ? [FakeRespServer.OkReply, "+QUEUED\r\n"u8.ToArray(), "*1\r\n:1\r\n"u8.ToArray()]
            : [":1\r\n"u8.ToArray()];
        await using var owner = new FakeRespServer(replies);
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:{slot}\r\n:{slot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{owner.Port}\r\n");
        await using var seed = new FakeRespServer(topology, "-ERR command reached seed instead of slot owner\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            UseCluster = true,
            Connections = 1,
            Endpoints = { new("127.0.0.1", seed.Port) },
        });
        var view = client.WithKeyPrefix("tenant:");
        long result;
        if (path == "immediate")
        {
            result = await view.SortedSets.IntersectCountAsync(1, "{sets}:first", "{sets}:second");
        }
        else if (path == "batch")
        {
            using var batch = view.CreateBatch();
            var pending = batch.SortedSets.IntersectCount(1, "{sets}:first", "{sets}:second");
            await batch.ExecuteAsync();
            result = pending.Result;
        }
        else
        {
            await using var transaction = view.CreateTransaction();
            var pending = transaction.SortedSets.IntersectCount(1, "{sets}:first", "{sets}:second");
            await transaction.CommitAsync();
            result = pending.Result;
        }
        await Assert.That(result).IsEqualTo(1);
        await Assert.That(seed.ReceivedCommands).IsEquivalentTo(["CLUSTER SLOTS"]);
        await Assert.That(owner.ReceivedCommands.Where(command => command is not "MULTI" and not "EXEC"))
            .IsEquivalentTo(["ZINTERCARD 2 tenant:{sets}:first tenant:{sets}:second LIMIT 1"]);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    [Arguments(5)]
    [Arguments(6)]
    [Arguments(7)]
    [Arguments(8)]
    [Arguments(9)]
    public async Task Commands_ForwardCancellation(int command)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer { SuppressReply = _ => { received.TrySetResult(); return true; } };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var cancellation = new CancellationTokenSource();
        Task pending = command switch
        {
            0 => client.SortedSets.RandomMemberAsync("key", cancellation.Token).AsTask(),
            1 => client.SortedSets.RandomMemberAsync<int>("key", cancellation.Token).AsTask(),
            2 => client.SortedSets.RandomMembersAsync("key", 2, cancellation.Token).AsTask(),
            3 => client.SortedSets.RandomMembersAsync<int>("key", 2, cancellation.Token).AsTask(),
            4 => client.SortedSets.RandomMembersWithScoresAsync("key", 2, cancellation.Token).AsTask(),
            5 => client.SortedSets.RandomMembersWithScoresAsync<int>("key", 2, cancellation.Token).AsTask(),
            6 => client.SortedSets.CountByLexAsync("key", RespireLexRange.All, cancellation.Token).AsTask(),
            7 => client.SortedSets.RemoveRangeByLexAsync("key", RespireLexRange.All, cancellation.Token).AsTask(),
            8 => client.SortedSets.IntersectCountAsync(["key"], cancellation.Token).AsTask(),
            _ => client.SortedSets.IntersectCountAsync(1, ["key"], cancellation.Token).AsTask(),
        };
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.That(async () => await pending).Throws<OperationCanceledException>();
    }
}
