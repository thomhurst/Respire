using System.Text;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class SetMembershipCommandTests
{
    [Test]
    public async Task ImmediateCommands_PreserveOrderPrefixAndLimit()
    {
        await using var server = new FakeRespServer(
            "*3\r\n:1\r\n:0\r\n:1\r\n"u8.ToArray(), ":1\r\n"u8.ToArray(),
            ":3\r\n"u8.ToArray(), ":2\r\n"u8.ToArray(), ":3\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var view = client.WithKeyPrefix("tenant:");

        var members = await view.Sets.ContainsManyAsync("first", ["a", "missing", "a"], CancellationToken.None);
        var moved = await view.Sets.MoveAsync("first", "second", "a");
        var count = await view.Sets.IntersectCountAsync("first", "second");
        var limited = await view.Sets.IntersectCountAsync(limit: 2, "first", "second");
        var unlimited = await view.Sets.IntersectCountAsync(["first", "second"], default);

        await Assert.That(members).IsEquivalentTo([true, false, true], CollectionOrdering.Matching);
        await Assert.That(moved).IsTrue();
        await Assert.That(count).IsEqualTo(3);
        await Assert.That(limited).IsEqualTo(2);
        await Assert.That(unlimited).IsEqualTo(3);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "SMISMEMBER tenant:first a missing a", "SMOVE tenant:first tenant:second a",
            "SINTERCARD 2 tenant:first tenant:second", "SINTERCARD 2 tenant:first tenant:second LIMIT 2",
            "SINTERCARD 2 tenant:first tenant:second",
        }, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DeferredCommands_PreserveResultsAndSnapshotArguments(bool transactional)
    {
        byte[][] results = ["*3\r\n#t\r\n#f\r\n#t\r\n"u8.ToArray(), ":0\r\n"u8.ToArray(),
            ":3\r\n"u8.ToArray(), ":2\r\n"u8.ToArray()];
        var replies = transactional
            ? new[] { FakeRespServer.OkReply }
                .Concat(Enumerable.Repeat("+QUEUED\r\n"u8.ToArray(), results.Length))
                .Append("*4\r\n"u8.ToArray().Concat(results.SelectMany(result => result)).ToArray()).ToArray()
            : results;
        await using var server = new FakeRespServer(replies);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var view = client.WithKeyPrefix("tenant:");
        using var batch = transactional ? null : view.CreateBatch();
        await using var transaction = transactional ? view.CreateTransaction() : null;
        IRespireCommandQueue queue = transaction ?? (IRespireCommandQueue)batch!;
        RespireValue[] requestedMembers = ["a", "missing", "a"];
        RespireKey[] keys = ["first", "second"];
        var members = queue.Sets.ContainsMany("first", requestedMembers);
        var moved = queue.Sets.Move("first", "second", "absent");
        var count = queue.Sets.IntersectCount(keys);
        var limited = queue.Sets.IntersectCount(2, keys);
        requestedMembers[0] = "changed";
        keys[0] = "changed";
        if (transaction is not null)
            await transaction.CommitAsync();
        else
            await batch!.ExecuteAsync();

        await Assert.That(members.Result).IsEquivalentTo([true, false, true], CollectionOrdering.Matching);
        await Assert.That(moved.Result).IsFalse();
        await Assert.That(count.Result).IsEqualTo(3);
        await Assert.That(limited.Result).IsEqualTo(2);
        await Assert.That(server.ReceivedCommands.Where(command => command is not "MULTI" and not "EXEC"))
            .IsEquivalentTo(new[]
            {
                "SMISMEMBER tenant:first a missing a", "SMOVE tenant:first tenant:second absent",
                "SINTERCARD 2 tenant:first tenant:second", "SINTERCARD 2 tenant:first tenant:second LIMIT 2",
            }, CollectionOrdering.Matching);
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
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Connections = 1,
            Endpoints = { new("127.0.0.1", seed.Port) },
        });
        var view = client.WithKeyPrefix("tenant:");
        long result;
        if (path == "immediate")
        {
            result = await view.Sets.IntersectCountAsync(1, "{sets}:first", "{sets}:second");
        }
        else if (path == "batch")
        {
            using var batch = view.CreateBatch();
            var pending = batch.Sets.IntersectCount(1, "{sets}:first", "{sets}:second");
            await batch.ExecuteAsync();
            result = pending.Result;
        }
        else
        {
            await using var transaction = view.CreateTransaction();
            var pending = transaction.Sets.IntersectCount(1, "{sets}:first", "{sets}:second");
            await transaction.CommitAsync();
            result = pending.Result;
        }
        await Assert.That(result).IsEqualTo(1);
        await Assert.That(seed.ReceivedCommands).IsEquivalentTo(["CLUSTER SLOTS"]);
        await Assert.That(owner.ReceivedCommands.Where(command => command is not "MULTI" and not "EXEC"))
            .IsEquivalentTo(["SINTERCARD 2 tenant:{sets}:first tenant:{sets}:second LIMIT 1"]);
    }

    [Test]
    public async Task InvalidInputs_AreRejectedBeforeSendingOrQueueing()
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var batch = client.CreateBatch();
        await Assert.That(async () => await client.Sets.ContainsManyAsync("key", []))
            .ThrowsExactly<ArgumentException>();
        await Assert.That(async () => await client.Sets.IntersectCountAsync([]))
            .ThrowsExactly<ArgumentException>();
        await Assert.That(async () => await client.Sets.IntersectCountAsync(-1, ["key"]))
            .ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(() => batch.Sets.ContainsMany("key", [])).ThrowsExactly<ArgumentException>();
        await Assert.That(() => batch.Sets.IntersectCount([])).ThrowsExactly<ArgumentException>();
        await Assert.That(() => batch.Sets.IntersectCount(-1, ["key"])).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(server.CommandsSeen).IsEqualTo(0);
    }

    [Test]
    public async Task ClusterTransaction_ValidatesAllKeysAndRoutesPastTheCount()
    {
        await using var client = RespireClient.Create(new RespireOptions { Protocol = RespProtocol.Resp2, UseCluster = true, Endpoints = { new("localhost") } });
        await using var transaction = client.CreateTransaction();
        await Assert.That(() => transaction.Sets.Move("{a}:first", "{b}:second", "value"))
            .Throws<InvalidOperationException>();
        await Assert.That(() => transaction.Sets.IntersectCount(1, ["{a}:first", "{b}:second"]))
            .Throws<InvalidOperationException>();
        _ = transaction.Sets.IntersectCount("{b}:first", "{b}:second");
        _ = transaction.Sets.Move("{b}:first", "{b}:second", "value");
        _ = transaction.Sets.ContainsMany("{b}:first", "value");
        await Assert.That(() => transaction.Sets.ContainsMany("{a}:first", "value"))
            .Throws<InvalidOperationException>();
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ImmediateCommands_ForwardCancellation(int command)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer
        {
            SuppressReply = _ => { received.TrySetResult(); return true; },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var cancellation = new CancellationTokenSource();
        Task pending = command switch
        {
            0 => client.Sets.ContainsManyAsync("key", ["value"], cancellation.Token).AsTask(),
            1 => client.Sets.MoveAsync("key", "destination", "value", cancellation.Token).AsTask(),
            2 => client.Sets.IntersectCountAsync(["key"], cancellation.Token).AsTask(),
            _ => client.Sets.IntersectCountAsync(1, ["key"], cancellation.Token).AsTask(),
        };
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.That(async () => await pending).Throws<OperationCanceledException>();
    }
}
