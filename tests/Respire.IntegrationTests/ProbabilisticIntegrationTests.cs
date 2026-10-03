using FluentAssertions;
using Respire.Probabilistic;
using TUnit.Core;

namespace Respire.IntegrationTests;

/// <summary>Redis 8 bundles the Bloom, Cuckoo, Count-Min, Top-K, and t-digest data types.</summary>
[ClassDataSource<ModernRedisTestContainer>(Shared = SharedType.PerTestSession)]
public class ProbabilisticIntegrationTests(ModernRedisTestContainer fixture)
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task BloomAndCuckooFiltersRoundTripThroughAKeyPrefixedView(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var prefix = $"probabilistic:{Guid.NewGuid():N}:";
        var probabilistic = new RespireProbabilisticClient(client.WithKeyPrefix(prefix));

        (await probabilistic.BloomReserveAsync("bloom", 0.01, 1_000)).Should().BeTrue();
        (await probabilistic.BloomAddAsync("bloom", "a")).Should().BeTrue();
        (await probabilistic.BloomAddAsync("bloom", "a")).Should().BeFalse();
        (await probabilistic.BloomExistsAsync("bloom", "a")).Should().BeTrue();
        (await probabilistic.BloomMultiAddAsync("bloom", ["b", "c"])).Should().Equal(true, true);
        (await probabilistic.BloomMultiExistsAsync("bloom", ["b", "missing"])).Should().Equal(true, false);
        (await probabilistic.BloomInsertAsync("inserted", ["x", "y"], new() { Capacity = 100 })).Should().Equal(true, true);
        (await probabilistic.BloomCardinalityAsync("bloom")).Should().Be(3);
        using (var info = await probabilistic.BloomInfoAsync("bloom"))
            info.Count.Should().BePositive();
        (await client.Keys.ExistsAsync(prefix + "bloom")).Should().BeTrue();

        var chunk = await probabilistic.BloomScanDumpAsync("bloom");
        while (chunk.Iterator != 0)
        {
            await probabilistic.BloomLoadChunkAsync("restored", chunk);
            chunk = await probabilistic.BloomScanDumpAsync("bloom", chunk.Iterator);
        }
        (await probabilistic.BloomExistsAsync("restored", "c")).Should().BeTrue();

        (await probabilistic.CuckooReserveAsync("cuckoo", 1_000, new() { BucketSize = 2 })).Should().BeTrue();
        (await probabilistic.CuckooAddAsync("cuckoo", "a")).Should().BeTrue();
        (await probabilistic.CuckooAddIfAbsentAsync("cuckoo", "a")).Should().BeFalse();
        (await probabilistic.CuckooInsertAsync("cuckoo", ["a", "b"]))
            .Should().Equal(RespireCuckooInsertResult.Inserted, RespireCuckooInsertResult.Inserted);
        (await probabilistic.CuckooInsertIfAbsentAsync("cuckoo", ["b", "c"]))
            .Should().Equal(RespireCuckooInsertResult.AlreadyExists, RespireCuckooInsertResult.Inserted);
        (await probabilistic.CuckooCountAsync("cuckoo", "a")).Should().Be(2);
        (await probabilistic.CuckooDeleteAsync("cuckoo", "a")).Should().BeTrue();
        (await probabilistic.CuckooExistsAsync("cuckoo", "c")).Should().BeTrue();
        (await probabilistic.CuckooMultiExistsAsync("cuckoo", ["b", "missing"])).Should().Equal(true, false);
        using (var info = await probabilistic.CuckooInfoAsync("cuckoo"))
            info.Count.Should().BePositive();

        chunk = await probabilistic.CuckooScanDumpAsync("cuckoo");
        while (chunk.Iterator != 0)
        {
            await probabilistic.CuckooLoadChunkAsync("cuckoo-restored", chunk);
            chunk = await probabilistic.CuckooScanDumpAsync("cuckoo", chunk.Iterator);
        }
        (await probabilistic.CuckooExistsAsync("cuckoo-restored", "b")).Should().BeTrue();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task SketchesAndDigestsRoundTripThroughAKeyPrefixedView(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var prefix = $"probabilistic:{Guid.NewGuid():N}:";
        var probabilistic = new RespireProbabilisticClient(client.WithKeyPrefix(prefix));

        await probabilistic.CountMinInitializeByDimensionsAsync("cms:a", 100, 5);
        await probabilistic.CountMinInitializeByDimensionsAsync("cms:b", 100, 5);
        await probabilistic.CountMinInitializeByDimensionsAsync("cms:merged", 100, 5);
        await probabilistic.CountMinInitializeByProbabilityAsync("cms:probability", 0.01, 0.01);
        (await probabilistic.CountMinIncrementAsync("cms:a", new Dictionary<RespireValue, long> { ["x"] = 2, ["y"] = 1 }))
            .Should().Equal(2, 1);
        await probabilistic.CountMinIncrementAsync("cms:b", new Dictionary<RespireValue, long> { ["x"] = 1 });
        await probabilistic.CountMinMergeAsync("cms:merged", ["cms:a", "cms:b"], new() { Weights = [1, 3] });
        (await probabilistic.CountMinQueryAsync("cms:merged", ["x", "y"])).Should().Equal(5, 1);
        using (var info = await probabilistic.CountMinInfoAsync("cms:merged"))
            info.Count.Should().BePositive();

        await probabilistic.TopKReserveAsync("topk", 2, new() { Width = 50, Depth = 4, Decay = 0.9 });
        (await probabilistic.TopKAddAsync("topk", ["a", "b"])).Should().HaveCount(2).And.OnlyContain(evicted => evicted == null);
        await probabilistic.TopKIncrementAsync("topk", new Dictionary<RespireValue, long> { ["a"] = 10 });
        (await probabilistic.TopKQueryAsync("topk", ["a", "missing"])).Should().Equal(true, false);
        (await probabilistic.TopKCountAsync("topk", ["a"])).Should().Equal(11);
        using (var list = await probabilistic.TopKListAsync("topk", withCount: true))
            list[0].AsString().Should().Be("a");
        using (var info = await probabilistic.TopKInfoAsync("topk"))
            info.Count.Should().BePositive();

        await probabilistic.TDigestCreateAsync("digest:a", new() { Compression = 100 });
        await probabilistic.TDigestCreateAsync("digest:b");
        await probabilistic.TDigestAddAsync("digest:a", [1, 2, 3, 4, 5]);
        await probabilistic.TDigestAddAsync("digest:b", [6, 7, 8, 9, 10]);
        await probabilistic.TDigestMergeAsync("digest", ["digest:a", "digest:b"]);
        (await probabilistic.TDigestMinimumAsync("digest")).Should().Be(1);
        (await probabilistic.TDigestMaximumAsync("digest")).Should().Be(10);
        (await probabilistic.TDigestQuantileAsync("digest", [0, 1])).Should().Equal(1, 10);
        (await probabilistic.TDigestCdfAsync("digest", [0, 11])).Should().Equal(0, 1);
        (await probabilistic.TDigestRankAsync("digest", [0])).Should().Equal(-1);
        (await probabilistic.TDigestReverseRankAsync("digest", [11])).Should().Equal(-1);
        (await probabilistic.TDigestByRankAsync("digest", [0])).Should().Equal(1);
        (await probabilistic.TDigestByReverseRankAsync("digest", [0])).Should().Equal(10);
        (await probabilistic.TDigestByRankAsync("digest", [100])).Should().Equal(double.PositiveInfinity);
        (await probabilistic.TDigestByReverseRankAsync("digest", [100])).Should().Equal(double.NegativeInfinity);
        (await probabilistic.TDigestTrimmedMeanAsync("digest", 0, 1)).Should().BeApproximately(5.5, 0.001);
        using (var info = await probabilistic.TDigestInfoAsync("digest"))
            info.Count.Should().BePositive();
        await probabilistic.TDigestResetAsync("digest");
        double.IsNaN(await probabilistic.TDigestMinimumAsync("digest")).Should().BeTrue();

        (await client.Keys.ExistsAsync(prefix + "cms:merged")).Should().BeTrue();
        (await client.Keys.ExistsAsync(prefix + "digest")).Should().BeTrue();
        (await client.Keys.ExistsAsync("digest")).Should().BeFalse();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ServerErrorsAndCancellationSurfaceAsExceptions(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var key = $"probabilistic:{Guid.NewGuid():N}:string";
        var probabilistic = new RespireProbabilisticClient(client);
        await client.SetAsync(key, "not a filter");

        await probabilistic.Awaiting(p => p.BloomAddAsync(key, "item").AsTask())
            .Should().ThrowAsync<RespireServerException>();
        await probabilistic.Awaiting(p => p.CountMinQueryAsync($"{key}:missing", ["item"]).AsTask())
            .Should().ThrowAsync<RespireServerException>();
        await probabilistic.Awaiting(p => p.CuckooAddAsync(key, "item").AsTask())
            .Should().ThrowAsync<RespireServerException>();
        await probabilistic.Awaiting(p => p.CountMinIncrementAsync(key, new Dictionary<RespireValue, long> { ["item"] = 1 }).AsTask())
            .Should().ThrowAsync<RespireServerException>();
        await probabilistic.Awaiting(p => p.TopKAddAsync(key, ["item"]).AsTask())
            .Should().ThrowAsync<RespireServerException>();
        await probabilistic.Awaiting(p => p.TDigestAddAsync(key, [1]).AsTask())
            .Should().ThrowAsync<RespireServerException>();

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        Func<Task>[] canceledWrites =
        [
            async () => { await probabilistic.BloomAddAsync($"{key}:bloom", "item", cancelled.Token); },
            async () => { await probabilistic.CuckooAddAsync($"{key}:cuckoo", "item", cancelled.Token); },
            async () => { await probabilistic.CountMinInitializeByDimensionsAsync($"{key}:cms", 100, 5, cancelled.Token); },
            async () => { await probabilistic.TopKReserveAsync($"{key}:topk", 2, cancellationToken: cancelled.Token); },
            async () => { await probabilistic.TDigestCreateAsync($"{key}:digest", cancellationToken: cancelled.Token); },
        ];
        foreach (var write in canceledWrites)
        {
            var error = await write.Should().ThrowAsync<OperationCanceledException>();
            error.Which.CancellationToken.Should().Be(cancelled.Token);
        }
        foreach (var family in new[] { "bloom", "cuckoo", "cms", "topk", "digest" })
            (await client.Keys.ExistsAsync($"{key}:{family}")).Should().BeFalse();
    }

    private Task<RespireClient> ConnectAsync(int protocol)
        => RespireClient.ConnectAsync(
            RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespProtocol)protocol }).AsTask();
}
