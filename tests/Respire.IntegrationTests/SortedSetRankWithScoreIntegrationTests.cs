using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
[ClassDataSource<ModernRedisTestContainer>(Shared = SharedType.PerTestSession)]
public class SortedSetRankWithScoreIntegrationTests(ModernRedisTestContainer fixture)
{
    [Test]
    [Arguments("immediate", 2, false)]
    [Arguments("batch", 2, false)]
    [Arguments("transaction", 2, false)]
    [Arguments("immediate", 3, false)]
    [Arguments("batch", 3, false)]
    [Arguments("transaction", 3, false)]
    [Arguments("immediate", 2, true)]
    [Arguments("batch", 2, true)]
    [Arguments("transaction", 2, true)]
    [Arguments("immediate", 3, true)]
    [Arguments("batch", 3, true)]
    [Arguments("transaction", 3, true)]
    public async Task RealAndFakeServers_ReturnRankScoreAndMissingResults(string mode, int protocol, bool fake)
    {
        await using var server = new RespireFakeServer();
        var options = fake ? server.CreateOptions() : RespireOptions.Parse(fixture.ConnectionString);
        await using var client = await RespireClient.ConnectAsync(options with { Protocol = (RespProtocol)protocol });
        var view = client.WithKeyPrefix($"rank:{Guid.NewGuid():N}:");
        await view.SortedSets.AddAsync("key", ("a", -1.5), ("b", 2.5), ("c", 2.5));
        (await Rank(view, mode, "key", "a", false)).Should().Be(new SortedSetRank(0, -1.5));
        (await Rank(view, mode, "key", "b", false)).Should().Be(new SortedSetRank(1, 2.5));
        (await Rank(view, mode, "key", "b", true)).Should().Be(new SortedSetRank(1, 2.5));
        (await Rank(view, mode, "key", "c", true)).Should().Be(new SortedSetRank(0, 2.5));
        (await Rank(view, mode, "key", "missing", false)).Should().BeNull();
        (await Rank(view, mode, "missing", "a", true)).Should().BeNull();
        (await view.SortedSets.RankAsync("key", "c")).Should().Be(2);
        await view.DeleteAsync("key");
    }

    [Test]
    public async Task CachedRankAndScoredRank_AreDistinctAndInvalidateTogether()
    {
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString)
            with { Protocol = RespProtocol.Resp3, ClientSideCache = new() });
        var view = client.WithKeyPrefix($"rank-cache:{Guid.NewGuid():N}:");
        await view.SortedSets.AddAsync("key", ("a", 1), ("b", 2));
        (await view.SortedSets.RankAsync("key", "b")).Should().Be(1);
        (await view.SortedSets.RankWithScoreAsync("key", "b")).Should().Be(new SortedSetRank(1, 2));
        (await view.SortedSets.RankWithScoreAsync("key", "b")).Should().Be(new SortedSetRank(1, 2));
        (await view.SortedSets.RankWithScoreAsync("key", "b", true)).Should().Be(new SortedSetRank(0, 2));
        await view.SortedSets.AddAsync("key", "b", 0.5);
        (await view.SortedSets.RankWithScoreAsync("key", "b")).Should().Be(new SortedSetRank(0, 0.5));
        (await view.SortedSets.RankAsync("key", "b")).Should().Be(0);
        await view.SortedSets.RemoveAsync("key", "b");
        (await view.SortedSets.RankWithScoreAsync("key", "b")).Should().BeNull();
        await view.DeleteAsync("key");
    }

    private static async Task<SortedSetRank?> Rank(IRespireClient client, string mode,
        string key, string member, bool descending)
    {
        if (mode == "immediate") return await client.SortedSets.RankWithScoreAsync(key, member, descending);
        if (mode == "batch")
        {
            using var batch = client.CreateBatch();
            var pending = batch.SortedSets.RankWithScore(key, member, descending);
            await batch.ExecuteAsync();
            return pending.Result;
        }
        await using var transaction = client.CreateTransaction();
        var result = transaction.SortedSets.RankWithScore(key, member, descending);
        await transaction.CommitAsync();
        return result.Result;
    }
}
