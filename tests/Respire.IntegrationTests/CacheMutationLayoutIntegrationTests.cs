using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.Keyed, Key = TestConstraints.ClientCacheServer)]
[NotInParallel(TestConstraints.ClientCacheHits)]
public class CacheMutationLayoutIntegrationTests(RedisTestContainer fixture)
{
    [Test]
    [Arguments("SDIFFSTORE")]
    [Arguments("SINTERSTORE")]
    [Arguments("SUNIONSTORE")]
    [Arguments("PFMERGE")]
    public async Task DestinationWritesInvalidateCachedMissAndRetainSourceEntries(string operation)
    {
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString) with
        { Protocol = RespProtocol.Resp3, ClientSideCache = new() });
        var prefix = "cache-layout:" + Guid.NewGuid().ToString("N") + ":";
        RespireKey first = prefix + "first", second = prefix + "second", destination = prefix + "destination";
        try
        {
            var add = RespireCommand.Create(operation == "PFMERGE" ? "PFADD" : "SADD");
            using (await client.ExecuteAsync(add, [first, "common", "only-first"])) { }
            using (await client.ExecuteAsync(add, [second, "common"])) { }
            (await client.Keys.ExistsAsync(first)).Should().BeTrue();
            (await client.Keys.ExistsAsync(second)).Should().BeTrue();
            (await client.Keys.ExistsAsync(destination)).Should().BeFalse();
            var cache = client.ClientSideCache!;
            var hits = cache.GetStatistics().Hits;
            (await client.Keys.ExistsAsync(first)).Should().BeTrue();
            (await client.Keys.ExistsAsync(second)).Should().BeTrue();
            (await client.Keys.ExistsAsync(destination)).Should().BeFalse();
            cache.GetStatistics().Hits.Should().Be(hits + 3);

            using (await client.ExecuteAsync(RespireCommand.Create(operation), [destination, first, second])) { }

            hits = cache.GetStatistics().Hits;
            (await client.Keys.ExistsAsync(destination)).Should().BeTrue();
            cache.GetStatistics().Hits.Should().Be(hits);
            (await client.Keys.ExistsAsync(first)).Should().BeTrue();
            (await client.Keys.ExistsAsync(second)).Should().BeTrue();
            cache.GetStatistics().Hits.Should().Be(hits + 2);
        }
        finally { await client.Keys.DeleteAsync(first, second, destination); }
    }
}
