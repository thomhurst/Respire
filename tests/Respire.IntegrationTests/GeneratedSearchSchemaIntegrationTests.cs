using FluentAssertions;
using Respire.Json;
using Respire.Search;
using Respire.Tests.Models;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
[ClassDataSource<ModernRedisTestContainer>(Shared = SharedType.PerTestSession)]
public class GeneratedSearchSchemaIntegrationTests(ModernRedisTestContainer fixture)
{
    [Test]
    [Arguments(RespireHashExpiryMode.HSetEx)]
    [Arguments(RespireHashExpiryMode.HSetThenExpire)]
    public async Task BinaryVectorExpiryAndChangeTrackingUseRealModule(RespireHashExpiryMode mode)
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var key = "generated-vector-ttl:" + Guid.NewGuid().ToString("N");
        var value = new SearchExpiringHashModel("42", new byte[8]);
        try
        {
            var tracker = SearchExpiringHashModelHashMapper.Track(client, key, expiryMode: mode);
            await tracker.UpdateAsync(value);
            (await SearchExpiringHashModelHashMapper.GetAsync(client, key))!.Embedding.Should().Equal(value.Embedding);
            var expiry = await client.Hashes.ExpiryAsync(key, "Embedding");
            expiry[0].HasExpiry.Should().BeTrue();
            value.Embedding[0] = 128;
            await tracker.UpdateAsync(value);
            await tracker.UpdateAsync(value);
            (await SearchExpiringHashModelHashMapper.GetAsync(client, key))!.Embedding.Should().Equal(value.Embedding);
        }
        finally { await client.Keys.DeleteAsync(key); }
    }

    [Test]
    [Arguments(2, false)]
    [Arguments(3, false)]
    [Arguments(2, true)]
    [Arguments(3, true)]
    public async Task MappedSchemasIndexTextTagsNumbersAndVectorsWithRealModule(int protocol, bool json)
    {
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespProtocol)protocol });
        var index = "generated-schema:" + Guid.NewGuid().ToString("N");
        var key = index + ":doc:1";
        var vector = new byte[8];
        BitConverter.TryWriteBytes(vector.AsSpan(), 1f);
        BitConverter.TryWriteBytes(vector.AsSpan(4), 2f);
        var schema = (json ? SearchJsonModelSearchSchema.Definition : SearchHashModelSearchSchema.Definition)
            with { Prefixes = [index + ":doc:"] };
        var created = false;
        try
        {
            await client.Search.CreateIndexAsync(index, schema);
            created = true;
            if (json)
            {
                var jsonClient = new RespireJsonClient(client);
                await SearchJsonModelJsonMapper.SetAsync(jsonClient, key, new("1", "redis", "cache", 42, [1, 2]));
                (await SearchJsonModelJsonMapper.GetAsync(jsonClient, key)).Value!.Embedding.Should().Equal(1, 2);
            }
            else
            {
                await SearchHashModelHashMapper.SetAsync(client, key, new("1", "redis", "cache", 42, vector));
                (await SearchHashModelHashMapper.GetAsync(client, key))!.Embedding.Should().Equal(vector);
                var partial = await SearchHashModelHashMapper.GetPartialAsync(client, key, ["Embedding", "Rating"]);
                partial.Embedding.Value.Should().Equal(vector);
                partial.Rating.Value.Should().Be(42);
            }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (true)
            {
                var info = await client.Search.GetIndexInfoAsync(index, deadline.Token);
                var nearest = await client.Search.VectorSearchAsync(index, new("embedding", vector, 1), cancellationToken: deadline.Token);
                if (info.DocumentCount == 1 && nearest.Documents.Count == 1)
                {
                    nearest.Documents[0].Id.Should().Be(key);
                    break;
                }
                await Task.Delay(20, deadline.Token);
            }
            var filter = RespireSearchQueryBuilder.And(RespireSearchQueryBuilder.TextField("title", "redis"),
                RespireSearchQueryBuilder.Tag("category", "cache"), RespireSearchQueryBuilder.NumericRange("rating", 42L, 42L));
            var result = await client.Search.SearchAsync(index, new(filter));
            result.Documents.Should().ContainSingle().Which.Id.Should().Be(key);
            // Negative control: neither an absent tag nor an out-of-range number matches the document.
            (await client.Search.SearchAsync(index, new(RespireSearchQueryBuilder.Tag("category", "missing")))).Total.Should().Be(0);
            (await client.Search.SearchAsync(index, new(RespireSearchQueryBuilder.NumericRange("rating", 0L, 1L)))).Total.Should().Be(0);
            // A document outside the explicit prefix cannot enter this index.
            await client.Hashes.SetAsync(index + ":outside", ("Title", "redis"));
            (await client.Search.SearchAsync(index, new(RespireSearchExpression.FromRaw("*")))).Total.Should().Be(1);
        }
        finally
        {
            if (created) await client.Search.DropIndexAsync(index, deleteDocuments: true);
            await client.Keys.DeleteAsync(index + ":outside");
        }
    }
}
