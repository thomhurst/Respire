using FluentAssertions;
using Respire.Probabilistic;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
// Rows share one cluster; every key carries a per-test prefix.
[ClassDataSource<BloomRedisClusterFixture>(Shared = SharedType.PerTestSession)]
public class ProbabilisticClusterIntegrationTests(BloomRedisClusterFixture fixture)
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ClusterRoutesPrefixedKeysAndEnforcesMergeSlots(int protocol)
    {
        var cluster = fixture.Cluster;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            UseCluster = true, Protocol = (RespProtocol)protocol,
            Endpoints = [new(cluster.Host, cluster.Port(0))],
        });
        var prefix = $"probabilistic:{Guid.NewGuid():N}:";
        await using var tenant = client.WithKeyPrefix(prefix);
        var probabilistic = new RespireProbabilisticClient(tenant);
        await probabilistic.BloomAddAsync("bloom", "item");
        await probabilistic.CuckooAddAsync("cuckoo", "item");
        await probabilistic.TopKReserveAsync("topk", 2);
        await probabilistic.TopKAddAsync("topk", ["item"]);
        foreach (var key in new[] { "{cms}:source", "{cms}:destination", "{other}:source" })
            await probabilistic.CountMinInitializeByDimensionsAsync(key, 100, 5);
        await probabilistic.CountMinIncrementAsync("{cms}:source", new Dictionary<RespireValue, long> { ["item"] = 3 });
        await probabilistic.CountMinMergeAsync("{cms}:destination", ["{cms}:source"]);
        await probabilistic.TDigestCreateAsync("{digest}:source");
        await probabilistic.TDigestAddAsync("{digest}:source", [2, 4]);
        await probabilistic.TDigestMergeAsync("{digest}:destination", ["{digest}:source"]);

        (await probabilistic.BloomExistsAsync("bloom", "item")).Should().BeTrue();
        (await probabilistic.CuckooExistsAsync("cuckoo", "item")).Should().BeTrue();
        (await probabilistic.TopKQueryAsync("topk", ["item"])).Should().Equal(true);
        (await probabilistic.CountMinQueryAsync("{cms}:destination", ["item"])).Should().Equal(3);
        (await probabilistic.TDigestMaximumAsync("{digest}:destination")).Should().Be(4);
        (await client.Keys.ExistsAsync(prefix + "bloom")).Should().BeTrue();
        (await client.Keys.ExistsAsync("bloom")).Should().BeFalse();

        var cmsError = await probabilistic.Awaiting(p => p.CountMinMergeAsync("{cms}:destination", ["{other}:source"]).AsTask())
            .Should().ThrowAsync<RespireServerException>();
        cmsError.Which.Code.Should().Be("CROSSSLOT");
        var digestError = await probabilistic.Awaiting(p => p.TDigestMergeAsync("{digest}:destination", ["{other}:source"]).AsTask())
            .Should().ThrowAsync<RespireServerException>();
        digestError.Which.Code.Should().Be("CROSSSLOT");
        (await probabilistic.CountMinQueryAsync("{cms}:destination", ["item"])).Should().Equal(3);
        (await probabilistic.TDigestMaximumAsync("{digest}:destination")).Should().Be(4);
    }
}
