using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class SortedSetAlgebraCommandTests
{
    [Test]
    public async Task ImmediateCommands_EmitMultiScoreAndAlgebraFrames()
    {
        var membersReply = "*2\r\n$3\r\none\r\n$3\r\ntwo\r\n"u8.ToArray();
        await using var server = new FakeRespServer(
            "*3\r\n$3\r\n1.5\r\n$-1\r\n:2\r\n"u8.ToArray(),
            membersReply, membersReply, membersReply,
            ":1\r\n"u8.ToArray(), ":2\r\n"u8.ToArray(), ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var scores = await client.SortedSets.ScoresAsync("scores", "one", "missing", "two");
        _ = await client.SortedSets.IntersectAsync("first", "second");
        _ = await client.SortedSets.UnionAsync("first", "second");
        _ = await client.SortedSets.DifferenceAsync("first", "second");
        _ = await client.SortedSets.IntersectStoreAsync("dest", "first", "second");
        _ = await client.SortedSets.UnionStoreAsync("dest", "first", "second");
        _ = await client.SortedSets.DifferenceStoreAsync("dest", "first", "second");

        await Assert.That(scores).IsEquivalentTo(new double?[] { 1.5, null, 2 });
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "ZMSCORE scores one missing two",
            "ZINTER 2 first second",
            "ZUNION 2 first second",
            "ZDIFF 2 first second",
            "ZINTERSTORE dest 2 first second",
            "ZUNIONSTORE dest 2 first second",
            "ZDIFFSTORE dest 2 first second",
        });
    }

    [Test]
    public async Task DeferredCommands_QueueMultiScoreAndAlgebraFrames()
    {
        var membersReply = "*1\r\n$3\r\none\r\n"u8.ToArray();
        await using var server = new FakeRespServer(
            "*2\r\n$1\r\n1\r\n$-1\r\n"u8.ToArray(),
            membersReply, membersReply, membersReply,
            ":1\r\n"u8.ToArray(), ":1\r\n"u8.ToArray(), ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var batch = client.CreateBatch();

        var scores = batch.SortedSets.Scores("scores", "one", "missing");
        _ = batch.SortedSets.Intersect("first", "second");
        _ = batch.SortedSets.Union("first", "second");
        _ = batch.SortedSets.Difference("first", "second");
        _ = batch.SortedSets.IntersectStore("dest", "first", "second");
        _ = batch.SortedSets.UnionStore("dest", "first", "second");
        _ = batch.SortedSets.DifferenceStore("dest", "first", "second");

        await batch.ExecuteAsync();

        await Assert.That(scores.Result).IsEquivalentTo(new double?[] { 1, null });
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "ZMSCORE scores one missing",
            "ZINTER 2 first second",
            "ZUNION 2 first second",
            "ZDIFF 2 first second",
            "ZINTERSTORE dest 2 first second",
            "ZUNIONSTORE dest 2 first second",
            "ZDIFFSTORE dest 2 first second",
        });
    }

    [Test]
    public async Task TypedReads_DeserializeMembersAndPreserveScores()
    {
        var membersReply = "*2\r\n$1\r\n7\r\n$1\r\n9\r\n"u8.ToArray();
        var entriesReply = "*4\r\n$1\r\n7\r\n$3\r\n1.5\r\n$1\r\n9\r\n$1\r\n2\r\n"u8.ToArray();
        var pairedEntriesReply = "*2\r\n*2\r\n$1\r\n7\r\n,1.5\r\n*2\r\n$1\r\n9\r\n,2\r\n"u8.ToArray();
        await using var server = new FakeRespServer(
            membersReply, entriesReply, membersReply, pairedEntriesReply, entriesReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var byRank = await client.SortedSets.RangeAsync<int>("typed");
        var rankScores = await client.SortedSets.RangeWithScoresAsync<int>("typed");
        var byScore = await client.SortedSets.RangeByScoreAsync<int>("typed", 1, 2);
        var scoreEntries = await client.SortedSets.RangeByScoreWithScoresAsync<int>(
            "typed", new RespireScoreRange(1, 2));
        var popped = await client.SortedSets.PopAsync<int>("typed", 2);

        await Assert.That(byRank).IsEquivalentTo(new[] { 7, 9 });
        await Assert.That(byScore).IsEquivalentTo(new[] { 7, 9 });
        var expected = new[] { new SortedSetEntry<int>(7, 1.5), new SortedSetEntry<int>(9, 2) };
        await Assert.That(rankScores).IsEquivalentTo(expected);
        await Assert.That(scoreEntries).IsEquivalentTo(expected);
        await Assert.That(popped).IsEquivalentTo(expected);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "ZRANGE typed 0 -1",
            "ZRANGE typed 0 -1 WITHSCORES",
            "ZRANGE typed 1 2 BYSCORE",
            "ZRANGE typed 1 2 BYSCORE WITHSCORES",
            "ZPOPMIN typed 2",
        });
    }

    [Test]
    public async Task DeferredTypedReads_DeserializeMembersAndPreserveScores()
    {
        var membersReply = "*2\r\n$1\r\n7\r\n$1\r\n9\r\n"u8.ToArray();
        var entriesReply = "*4\r\n$1\r\n7\r\n$3\r\n1.5\r\n$1\r\n9\r\n$1\r\n2\r\n"u8.ToArray();
        await using var server = new FakeRespServer(
            membersReply, entriesReply, membersReply, entriesReply, entriesReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var batch = client.CreateBatch();

        var byRank = batch.SortedSets.Range<int>("typed");
        var rankScores = batch.SortedSets.RangeWithScores<int>("typed");
        var byScore = batch.SortedSets.RangeByScore<int>("typed", 1, 2);
        var scoreEntries = batch.SortedSets.RangeByScoreWithScores<int>(
            "typed", new RespireScoreRange(1, 2));
        var popped = batch.SortedSets.Pop<int>("typed", 2);

        await batch.ExecuteAsync();

        await Assert.That(byRank.Result).IsEquivalentTo(new[] { 7, 9 });
        await Assert.That(byScore.Result).IsEquivalentTo(new[] { 7, 9 });
        var expected = new[] { new SortedSetEntry<int>(7, 1.5), new SortedSetEntry<int>(9, 2) };
        await Assert.That(rankScores.Result).IsEquivalentTo(expected);
        await Assert.That(scoreEntries.Result).IsEquivalentTo(expected);
        await Assert.That(popped.Result).IsEquivalentTo(expected);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "ZRANGE typed 0 -1",
            "ZRANGE typed 0 -1 WITHSCORES",
            "ZRANGE typed 1 2 BYSCORE",
            "ZRANGE typed 1 2 BYSCORE WITHSCORES",
            "ZPOPMIN typed 2",
        });
    }
}
