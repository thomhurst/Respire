using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class SetSortedSetCoverageTests
{
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
    public async Task Pop_RejectsNegativeCountsBeforeSending()
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await Assert.That(async () => await client.Sets.PopAsync("set", -1))
            .ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.SortedSets.PopAsync("scores", -1))
            .ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }
}
