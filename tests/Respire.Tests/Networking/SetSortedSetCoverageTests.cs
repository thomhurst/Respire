using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class SetSortedSetCoverageTests
{
    [Test]
    public async Task SortedSetAdvancedRanges_EmitBoundsPagingScoresLexAndStore()
    {
        var membersReply = "*2\r\n$3\r\none\r\n$3\r\ntwo\r\n"u8.ToArray();
        var entriesReply = "*2\r\n$3\r\none\r\n$3\r\n1.5\r\n"u8.ToArray();
        await using var server = new FakeRespServer(
            membersReply, entriesReply, ":2\r\n"u8.ToArray(), ":1\r\n"u8.ToArray(),
            membersReply, ":3\r\n"u8.ToArray(), ":2\r\n"u8.ToArray(), ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var scoreRange = new RespireScoreRange(
            RespireScoreBound.Exclusive(1.5), RespireScoreBound.Max);
        var lexRange = new RespireLexRange("alpha", RespireLexBound.Exclusive("omega"));

        _ = await client.SortedSets.RangeByScoreAsync(
            "scores", scoreRange, offset: 2, count: 3, descending: true);
        _ = await client.SortedSets.RangeByScoreWithScoresAsync(
            "scores", scoreRange, count: 2);
        _ = await client.SortedSets.CountByScoreAsync("scores", scoreRange);
        _ = await client.SortedSets.RemoveRangeByScoreAsync("scores", scoreRange);
        _ = await client.SortedSets.RangeByLexAsync("scores", lexRange, offset: 1, count: 4);
        _ = await client.SortedSets.StoreRangeAsync("dest", "scores", 0, 9, descending: true);
        _ = await client.SortedSets.StoreRangeByScoreAsync("dest", "scores", scoreRange, count: 2);
        _ = await client.SortedSets.StoreRangeByLexAsync("dest", "scores", lexRange, count: 2);

        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "ZRANGE scores +inf (1.5 BYSCORE REV LIMIT 2 3",
            "ZRANGE scores (1.5 +inf BYSCORE LIMIT 0 2 WITHSCORES",
            "ZCOUNT scores (1.5 +inf",
            "ZREMRANGEBYSCORE scores (1.5 +inf",
            "ZRANGE scores [alpha (omega BYLEX LIMIT 1 4",
            "ZRANGESTORE dest scores 0 9 REV",
            "ZRANGESTORE dest scores (1.5 +inf BYSCORE LIMIT 0 2",
            "ZRANGESTORE dest scores [alpha (omega BYLEX LIMIT 0 2",
        });
    }

    [Test]
    public async Task DeferredSortedSetAdvancedRanges_QueueMatchingCommands()
    {
        var membersReply = "*1\r\n$3\r\none\r\n"u8.ToArray();
        var entriesReply = "*2\r\n$3\r\none\r\n$3\r\n1.5\r\n"u8.ToArray();
        await using var server = new FakeRespServer(
            membersReply, entriesReply, ":2\r\n"u8.ToArray(), ":1\r\n"u8.ToArray(),
            membersReply, ":3\r\n"u8.ToArray(), ":2\r\n"u8.ToArray(), ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var batch = client.CreateBatch();
        var scoreRange = new RespireScoreRange(
            RespireScoreBound.Exclusive(1.5), RespireScoreBound.Max);
        var lexRange = new RespireLexRange("alpha", RespireLexBound.Exclusive("omega"));

        _ = batch.SortedSets.RangeByScore("scores", scoreRange, offset: 2, count: 3, descending: true);
        _ = batch.SortedSets.RangeByScoreWithScores("scores", scoreRange, count: 2);
        _ = batch.SortedSets.CountByScore("scores", scoreRange);
        _ = batch.SortedSets.RemoveRangeByScore("scores", scoreRange);
        _ = batch.SortedSets.RangeByLex("scores", lexRange, offset: 1, count: 4);
        _ = batch.SortedSets.StoreRange("dest", "scores", 0, 9, descending: true);
        _ = batch.SortedSets.StoreRangeByScore("dest", "scores", scoreRange, count: 2);
        _ = batch.SortedSets.StoreRangeByLex("dest", "scores", lexRange, count: 2);

        await batch.ExecuteAsync();

        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "ZRANGE scores +inf (1.5 BYSCORE REV LIMIT 2 3",
            "ZRANGE scores (1.5 +inf BYSCORE LIMIT 0 2 WITHSCORES",
            "ZCOUNT scores (1.5 +inf",
            "ZREMRANGEBYSCORE scores (1.5 +inf",
            "ZRANGE scores [alpha (omega BYLEX LIMIT 1 4",
            "ZRANGESTORE dest scores 0 9 REV",
            "ZRANGESTORE dest scores (1.5 +inf BYSCORE LIMIT 0 2",
            "ZRANGESTORE dest scores [alpha (omega BYLEX LIMIT 0 2",
        });
    }

    [Test]
    public async Task SortedSetAdvancedRanges_RejectInvalidBoundsAndPagingBeforeSending()
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var range = RespireScoreRange.All;

        await Assert.That(async () => await client.SortedSets.RangeByScoreAsync("scores", range, offset: -1))
            .ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.SortedSets.RangeByScoreAsync("scores", range, count: -1))
            .ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.SortedSets.RangeByScoreAsync("scores", range, offset: 1))
            .ThrowsExactly<ArgumentException>();
        await Assert.That(() => RespireScoreBound.Inclusive(double.NaN))
            .ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(() => RespireScoreBound.Exclusive(double.PositiveInfinity))
            .ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(() => RespireLexBound.Inclusive(null!))
            .ThrowsExactly<ArgumentNullException>();

        var batch = client.CreateBatch();
        await Assert.That(() => batch.SortedSets.RangeByScore("scores", range, offset: 1))
            .ThrowsExactly<ArgumentException>();
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task SortedSetPop_SelectsMinOrMaxAndParsesScores()
    {
        await using var server = new FakeRespServer(
            "*4\r\n$3\r\none\r\n$3\r\n1.5\r\n$3\r\ntwo\r\n$1\r\n2\r\n"u8.ToArray(),
            "*1\r\n*2\r\n$5\r\nthree\r\n$3\r\n3.5\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var minimum = await client.SortedSets.PopAsync("scores", count: 2);
        var maximum = await client.SortedSets.PopAsync("scores", count: 1, descending: true);

        await Assert.That(minimum).IsEquivalentTo(new[]
        {
            new SortedSetEntry("one", 1.5),
            new SortedSetEntry("two", 2),
        });
        await Assert.That(maximum).IsEquivalentTo(new[] { new SortedSetEntry("three", 3.5) });
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "ZPOPMIN scores 2",
            "ZPOPMAX scores 1",
        });
    }

    [Test]
    public async Task SortedSetRemoveRange_UsesScoreAndRankCommands()
    {
        await using var server = new FakeRespServer(":2\r\n"u8.ToArray(), ":3\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var byScore = await client.SortedSets.RemoveRangeByScoreAsync("scores", 1.5, 3.5);
        var byRank = await client.SortedSets.RemoveRangeByRankAsync("scores", 0, 2);

        await Assert.That(byScore).IsEqualTo(2);
        await Assert.That(byRank).IsEqualTo(3);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "ZREMRANGEBYSCORE scores 1.5 3.5",
            "ZREMRANGEBYRANK scores 0 2",
        });
    }

    [Test]
    public async Task SetPopAndRandomMembers_ReturnArrays()
    {
        await using var server = new FakeRespServer(
            "*2\r\n$3\r\none\r\n$3\r\ntwo\r\n"u8.ToArray(),
            "*2\r\n$3\r\none\r\n$3\r\none\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var popped = await client.Sets.PopAsync("set", count: 2);
        var random = await client.Sets.RandomMembersAsync("set", count: -2);

        await Assert.That(popped).IsEquivalentTo(new[] { "one", "two" });
        await Assert.That(random).IsEquivalentTo(new[] { "one", "one" });
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "SPOP set 2",
            "SRANDMEMBER set -2",
        });
    }

    [Test]
    public async Task DeferredSetAndSortedSetCoverage_QueuesAndParsesCommands()
    {
        await using var server = new FakeRespServer(
            "*2\r\n$3\r\none\r\n$3\r\ntwo\r\n"u8.ToArray(),
            "*2\r\n$3\r\none\r\n$3\r\none\r\n"u8.ToArray(),
            "*2\r\n$3\r\none\r\n$3\r\n1.5\r\n"u8.ToArray(),
            ":2\r\n"u8.ToArray(),
            ":3\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var batch = client.CreateBatch();

        var popped = batch.Sets.Pop("set", count: 2);
        var random = batch.Sets.RandomMembers("set", count: -2);
        var sorted = batch.SortedSets.Pop("scores", count: 1);
        var byScore = batch.SortedSets.RemoveRangeByScore("scores", 1.5, 3.5);
        var byRank = batch.SortedSets.RemoveRangeByRank("scores", 0, 2);

        await batch.ExecuteAsync();

        await Assert.That(popped.Result).IsEquivalentTo(new[] { "one", "two" });
        await Assert.That(random.Result).IsEquivalentTo(new[] { "one", "one" });
        await Assert.That(sorted.Result).IsEquivalentTo(new[] { new SortedSetEntry("one", 1.5) });
        await Assert.That(byScore.Result).IsEqualTo(2);
        await Assert.That(byRank.Result).IsEqualTo(3);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "SPOP set 2",
            "SRANDMEMBER set -2",
            "ZPOPMIN scores 1",
            "ZREMRANGEBYSCORE scores 1.5 3.5",
            "ZREMRANGEBYRANK scores 0 2",
        });
    }

    [Test]
    public async Task Pop_RejectsNegativeCountsBeforeSending()
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await Assert.That(async () => await client.Sets.PopAsync("set", -1))
            .ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.SortedSets.PopAsync("scores", -1))
            .ThrowsExactly<ArgumentOutOfRangeException>();
        var batch = client.CreateBatch();
        await Assert.That(() => batch.Sets.Pop("set", -1))
            .ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(() => batch.SortedSets.Pop("scores", -1))
            .ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }
}
