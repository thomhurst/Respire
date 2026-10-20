using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
[ClassDataSource<VersionedServerFixture>(Shared = SharedType.PerTestSession)]
public class SortedSetCombineOptionsIntegrationTests(VersionedServerFixture servers)
{
    [Test]
    [Arguments(2, "immediate")]
    [Arguments(2, "batch")]
    [Arguments(2, "transaction")]
    [Arguments(3, "immediate")]
    [Arguments(3, "batch")]
    [Arguments(3, "transaction")]
    public async Task WeightedAggregates_ProduceScoresAndInvalidateDestination(int protocol, string mode)
    {
        var lease = await servers.LeaseAsync("redis:8.10-alpine");
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(lease.ConnectionString(protocol))
            with { ClientSideCache = protocol == 3 ? new() : null });
        var view = client.WithKeyPrefix($"combine:{Guid.NewGuid():N}:");
        await view.SortedSets.AddAsync("a", ("shared", 2), ("only-a", 8));
        await view.SortedSets.AddAsync("b", ("shared", 5), ("only-b", 1));
        foreach (var aggregate in new[] { RespireSortedSetAggregate.Sum, RespireSortedSetAggregate.Min, RespireSortedSetAggregate.Max })
        {
            var options = new RespireSortedSetCombineOptions { Weights = new double[] { 2, 3 }, Aggregate = aggregate };
            var sharedScore = aggregate switch { RespireSortedSetAggregate.Sum => 19, RespireSortedSetAggregate.Min => 4, _ => 15 };
            var expected = new[] { new RespireSortedSetEntry("only-b", 3), new("only-a", 16), new("shared", sharedScore) }
                .OrderBy(e => e.Score).ThenBy(e => e.Member, StringComparer.Ordinal).ToArray();
            (await Run(view, mode, s => s.UnionAsync(["a", "b"], options), s => s.Union(["a", "b"], options)))
                .Should().Equal(expected.Select(e => e.Member));
            (await Run(view, mode, s => s.IntersectAsync(["a", "b"], options), s => s.Intersect(["a", "b"], options)))
                .Should().Equal("shared");
            // Warm a destination read before each overwrite to cover local cache invalidation.
            await view.SortedSets.AddAsync("dest", "stale", -100);
            _ = await view.SortedSets.RangeWithScoresAsync("dest");
            (await Run(view, mode, s => s.UnionStoreAsync("dest", ["a", "b"], options), s => s.UnionStore("dest", ["a", "b"], options)))
                .Should().Be(3);
            (await view.SortedSets.RangeWithScoresAsync("dest")).Should().Equal(expected);
            (await Run(view, mode, s => s.IntersectStoreAsync("dest", ["a", "b"], options), s => s.IntersectStore("dest", ["a", "b"], options)))
                .Should().Be(1);
            (await view.SortedSets.RangeWithScoresAsync("dest")).Should().Equal(new RespireSortedSetEntry("shared", sharedScore));
        }
        await view.DeleteAsync("a", "b", "dest");
    }

    [Test]
    [Arguments(2, "immediate", "redis:8.10-alpine")]
    [Arguments(2, "batch", "redis:8.10-alpine")]
    [Arguments(2, "transaction", "redis:8.10-alpine")]
    [Arguments(3, "immediate", "redis:8.10-alpine")]
    [Arguments(3, "batch", "redis:8.10-alpine")]
    [Arguments(3, "transaction", "redis:8.10-alpine")]
    [Arguments(2, "immediate", "redis:7-alpine")]
    public async Task Count_IgnoresScoresAndSumsMembershipWeights(int protocol, string mode, string image)
    {
        var lease = await servers.LeaseAsync(image);
        await using var client = await RespireClient.ConnectAsync(lease.ConnectionString(protocol));
        var info = await client.Server.InfoAsync("server");
        var versionLine = info.Split('\n').Single(l => l.StartsWith("redis_version:", StringComparison.Ordinal));
        var version = Version.Parse(versionLine["redis_version:".Length..].Trim());
        if (image == "redis:8.10-alpine") version.Should().BeGreaterThanOrEqualTo(new Version(8, 10));
        Skip.Unless(version >= new Version(8, 8), "AGGREGATE COUNT requires Redis 8.8 or later.");
        var view = client.WithKeyPrefix($"count:{Guid.NewGuid():N}:");
        await view.SortedSets.AddAsync("a", ("shared", -100), ("only-a", 200));
        await view.SortedSets.AddAsync("b", ("shared", 900), ("only-b", -500));
        foreach (var weighted in new[] { false, true })
        {
            var options = new RespireSortedSetCombineOptions
            {
                Aggregate = RespireSortedSetAggregate.Count,
                Weights = weighted ? new double[] { 2, 3 } : default,
            };
            var expected = new[] { new RespireSortedSetEntry("only-a", weighted ? 2 : 1), new("only-b", weighted ? 3 : 1), new("shared", weighted ? 5 : 2) };
            (await Run(view, mode, s => s.UnionAsync(["a", "b"], options), s => s.Union(["a", "b"], options)))
                .Should().Equal(expected.Select(e => e.Member));
            (await Run(view, mode, s => s.IntersectAsync(["a", "b"], options), s => s.Intersect(["a", "b"], options)))
                .Should().Equal("shared");
            (await Run(view, mode, s => s.UnionStoreAsync("dest", ["a", "b"], options), s => s.UnionStore("dest", ["a", "b"], options))).Should().Be(3);
            (await view.SortedSets.RangeWithScoresAsync("dest")).Should().Equal(expected);
            (await Run(view, mode, s => s.IntersectStoreAsync("dest", ["a", "b"], options), s => s.IntersectStore("dest", ["a", "b"], options))).Should().Be(1);
            (await view.SortedSets.RangeWithScoresAsync("dest")).Should().Equal(expected[2]);
        }
        await view.DeleteAsync("a", "b", "dest");
    }

    private static async Task<T> Run<T>(IRespireClient client, string mode,
        Func<IRespireSortedSetCommands, ValueTask<T>> immediate, Func<IRespireBatchSortedSetCommands, RespirePending<T>> deferred)
    {
        if (mode == "immediate") return await immediate(client.SortedSets);
        if (mode == "batch")
        {
            using var batch = client.CreateBatch();
            var pending = deferred(batch.SortedSets);
            await batch.ExecuteAsync();
            return pending.Result;
        }
        await using var transaction = client.CreateTransaction();
        var result = deferred(transaction.SortedSets);
        await transaction.CommitAsync();
        return result.Result;
    }
}
