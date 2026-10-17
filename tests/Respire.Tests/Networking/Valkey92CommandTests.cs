using System.Text;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class Valkey92CommandTests
{
    [Test]
    [Arguments(-1)]
    [Arguments(0)]
    [Arguments(1)]
    public async Task MembershipPreservesThreeStates(int result)
    {
        await using var server = new FakeRespServer(Encoding.ASCII.GetBytes($":{result}\r\n"));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var member = await client.WithKeyPrefix("tenant:").Sets.ContainsIfExistsAsync("set", "member");
        await Assert.That(member).IsEqualTo(result == -1 ? (bool?)null : result == 1);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(["SISMEMBER tenant:set member XX"]);
    }

    [Test]
    [Arguments("$-1\r\n")]
    [Arguments("*-1\r\n")]
    [Arguments("_\r\n")]
    public async Task RangesPreserveNull(string nullReply)
    {
        await using var server = new FakeRespServer(Encoding.ASCII.GetBytes(nullReply));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var sorted = client.WithKeyPrefix("tenant:").SortedSets;
        await Assert.That(await sorted.RangeIfExistsAsync("sorted")).IsNull();
        await Assert.That(await sorted.RangeIfExistsAsync<byte[]>("sorted", descending: true)).IsNull();
        await Assert.That(await sorted.RangeIfExistsWithScoresAsync("sorted")).IsNull();
        await Assert.That(await sorted.RangeIfExistsWithScoresAsync<byte[]>("sorted", descending: true)).IsNull();
        await Assert.That(server.ReceivedCommands).IsEquivalentTo([
            "ZRANGE tenant:sorted 0 -1 XX", "ZRANGE tenant:sorted 0 -1 REV XX",
            "ZRANGE tenant:sorted 0 -1 WITHSCORES XX", "ZRANGE tenant:sorted 0 -1 REV WITHSCORES XX"]);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DeferredCommandsPreserveMissingAndEmpty(bool transaction)
    {
        byte[][] results = [":-1\r\n"u8.ToArray(), "_\r\n"u8.ToArray(), "*0\r\n"u8.ToArray(), ":1\r\n"u8.ToArray()];
        var replies = transaction
            ? new[] { FakeRespServer.OkReply }.Concat(Enumerable.Repeat("+QUEUED\r\n"u8.ToArray(), 4))
                .Append("*4\r\n"u8.ToArray().Concat(results.SelectMany(value => value)).ToArray()).ToArray()
            : results;
        await using var server = new FakeRespServer(replies);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var view = client.WithKeyPrefix("tenant:");
        using var batch = transaction ? null : view.CreateBatch();
        await using var pending = transaction ? view.CreateTransaction() : null;
        IRespireCommandQueue queue = pending ?? (IRespireCommandQueue)batch!;
        var member = queue.Sets.ContainsIfExists("set", "member");
        var missing = queue.SortedSets.RangeIfExists("sorted");
        var empty = queue.SortedSets.RangeIfExistsWithScores("sorted", 2, 1, true);
        var moved = queue.Keys.Move("key", 1, replace: true);
        if (pending is not null) await pending.CommitAsync();
        else await batch!.ExecuteAsync();
        await Assert.That(member.Result).IsNull();
        await Assert.That(missing.Result).IsNull();
        await Assert.That(empty.Result!).IsEmpty();
        await Assert.That(moved.Result).IsTrue();
        await Assert.That(server.ReceivedCommands.Where(value => value is not "MULTI" and not "EXEC"))
            .IsEquivalentTo(["SISMEMBER tenant:set member XX", "ZRANGE tenant:sorted 0 -1 XX",
                "ZRANGE tenant:sorted 2 1 REV WITHSCORES XX", "MOVE tenant:key 1 REPLACE"]);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ScoresAcceptBothProtocols(bool resp3)
    {
        var reply = resp3 ? "*1\r\n*2\r\n$1\r\na\r\n,2.5\r\n" : "*2\r\n$1\r\na\r\n$3\r\n2.5\r\n";
        await using var server = new FakeRespServer(Encoding.ASCII.GetBytes(reply));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var entries = (await client.SortedSets.RangeIfExistsWithScoresAsync("sorted"))!;
        await Assert.That(entries).IsEquivalentTo([new RespireSortedSetEntry("a", 2.5)]);
        var binary = (await client.SortedSets.RangeIfExistsWithScoresAsync<byte[]>("sorted"))!;
        await Assert.That(binary[0].Member).IsEquivalentTo("a"u8.ToArray());
    }

    [Test]
    [Arguments("*4\r\n:0\r\n$-1\r\n$-1\r\n*-1\r\n")]
    [Arguments("*4\r\n:0\r\n_\r\n_\r\n_\r\n")]
    [Arguments("*4\r\n:0\r\n$-1\r\n$-1\r\n*0\r\n")]
    public async Task EmptyPendingSummaryAcceptsOldAndNewSchemas(string reply)
    {
        await using var server = new FakeRespServer(Encoding.ASCII.GetBytes(reply));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var summary = await client.Streams.PendingSummaryAsync("events", "workers");
        await Assert.That(summary.Count).IsEqualTo(0);
        await Assert.That(summary.SmallestId).IsNull();
        await Assert.That(summary.GreatestId).IsNull();
        await Assert.That(summary.Consumers).IsEmpty();
    }

    [Test]
    public async Task InvalidMembershipReplyThrows()
    {
        await using var server = new FakeRespServer(":2\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await Assert.That(async () => await client.Sets.ContainsIfExistsAsync("set", "member")).Throws<RespireProtocolException>();
    }
}
