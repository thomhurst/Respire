using FluentAssertions;
using Respire.Probabilistic;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<ModernRedisTestContainer>(Shared = SharedType.PerTestSession)]
public class ProbabilisticCacheIntegrationTests(ModernRedisTestContainer fixture)
{
    [Test, NotInParallel]
    [Arguments("bloom")]
    [Arguments("cuckoo")]
    [Arguments("cms")]
    [Arguments("topk")]
    [Arguments("digest")]
    public async Task LocalMutationsInvalidateOnlyTheirPrefixedKey(string family)
    {
        await using var client = await ConnectAsync();
        await using var tenant = client.WithKeyPrefix($"probabilistic:{Guid.NewGuid():N}:");
        var probabilistic = new RespireProbabilisticClient(tenant);
        var cache = client.ClientSideCache!;
        await tenant.SetAsync("unrelated", "retained");
        (await tenant.GetStringAsync("unrelated")).Should().Be("retained");
        (await tenant.Keys.ExistsAsync("structure")).Should().BeFalse();
        var hits = cache.GetStatistics().Hits;
        (await tenant.Keys.ExistsAsync("structure")).Should().BeFalse();
        cache.GetStatistics().Hits.Should().Be(hits + 1);

        switch (family)
        {
            case "bloom": await probabilistic.BloomReserveAsync("structure", 0.01, 100); break;
            case "cuckoo": await probabilistic.CuckooReserveAsync("structure", 100); break;
            case "cms": await probabilistic.CountMinInitializeByDimensionsAsync("structure", 100, 5); break;
            case "topk": await probabilistic.TopKReserveAsync("structure", 2); break;
            case "digest": await probabilistic.TDigestCreateAsync("structure"); break;
        }
        // Creation must remove the cached miss even though tracking uses NOLOOP.
        (await tenant.Keys.ExistsAsync("structure")).Should().BeTrue();
        hits = cache.GetStatistics().Hits;
        (await tenant.Keys.ExistsAsync("structure")).Should().BeTrue();
        cache.GetStatistics().Hits.Should().Be(hits + 1);
        switch (family)
        {
            case "bloom": await probabilistic.BloomAddAsync("structure", "item"); break;
            case "cuckoo": await probabilistic.CuckooAddAsync("structure", "item"); break;
            case "cms": await probabilistic.CountMinIncrementAsync("structure", new Dictionary<RespireValue, long> { ["item"] = 1 }); break;
            case "topk": await probabilistic.TopKAddAsync("structure", ["item"]); break;
            case "digest": await probabilistic.TDigestAddAsync("structure", [1]); break;
        }
        hits = cache.GetStatistics().Hits;
        (await tenant.Keys.ExistsAsync("structure")).Should().BeTrue();
        cache.GetStatistics().Hits.Should().Be(hits); // A server read, not the cached result.
        (await tenant.GetStringAsync("unrelated")).Should().Be("retained");
        cache.GetStatistics().Hits.Should().Be(hits + 1);
    }

    [Test, NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MergeInvalidatesDestinationAndRetainsCachedSource(bool digest)
    {
        await using var client = await ConnectAsync();
        await using var tenant = client.WithKeyPrefix($"probabilistic:{Guid.NewGuid():N}:");
        var probabilistic = new RespireProbabilisticClient(tenant);
        if (digest)
        {
            await probabilistic.TDigestCreateAsync("source");
            await probabilistic.TDigestAddAsync("source", [7]);
            await probabilistic.TDigestCreateAsync("destination");
        }
        else
        {
            await probabilistic.CountMinInitializeByDimensionsAsync("source", 100, 5);
            await probabilistic.CountMinIncrementAsync("source", new Dictionary<RespireValue, long> { ["item"] = 7 });
            await probabilistic.CountMinInitializeByDimensionsAsync("destination", 100, 5);
        }
        (await tenant.Keys.ExistsAsync("source")).Should().BeTrue();
        (await tenant.Keys.ExistsAsync("destination")).Should().BeTrue();
        var cache = client.ClientSideCache!;
        var hits = cache.GetStatistics().Hits;
        (await tenant.Keys.ExistsAsync("source")).Should().BeTrue();
        (await tenant.Keys.ExistsAsync("destination")).Should().BeTrue();
        cache.GetStatistics().Hits.Should().Be(hits + 2);

        if (digest) await probabilistic.TDigestMergeAsync("destination", ["source"]);
        else await probabilistic.CountMinMergeAsync("destination", ["source"]);

        hits = cache.GetStatistics().Hits;
        (await tenant.Keys.ExistsAsync("destination")).Should().BeTrue();
        cache.GetStatistics().Hits.Should().Be(hits);
        (await tenant.Keys.ExistsAsync("source")).Should().BeTrue();
        cache.GetStatistics().Hits.Should().Be(hits + 1);
        if (digest) (await probabilistic.TDigestMaximumAsync("destination")).Should().Be(7);
        else (await probabilistic.CountMinQueryAsync("destination", ["item"])).Should().Equal(7);
    }

    private ValueTask<RespireClient> ConnectAsync()
        => RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString) with
        {
            Protocol = RespProtocol.Resp3, ClientSideCache = new(),
        });
}
