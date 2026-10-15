using System.Text;
using System.Text.Json;
using FluentAssertions;
using Respire.Serialization;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
[ClassDataSource<VersionedServerFixture>(Shared = SharedType.PerTestSession)]
public class VectorSetIntegrationTests(VersionedServerFixture servers)
{
    [ClassDataSource<ModernRedisTestContainer>(Shared = SharedType.PerTestSession)]
    public required ModernRedisTestContainer Redis8 { get; init; }

    [ClassDataSource<ModernRedisTestContainer>(Shared = SharedType.Keyed, Key = TestConstraints.ClientCacheServer)]
    public required ModernRedisTestContainer CacheServer { get; init; }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task SimilarityCollectionEvaluationPreservesReplacementScoresAndLifecycle(int protocol)
    {
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(Redis8.ConnectionString) with
        {
            Connections = 1,
            Protocol = (RespireProtocol)protocol,
        });
        var key = $"vector-evaluation:{Guid.NewGuid():N}";
        var options = new RespireVectorAddOptions
        {
            Quantization = RespireVectorQuantization.None,
            AttributesJson = "{\"category\":\"old\",\"removed\":true}",
        };
        try
        {
            (await client.VectorSets.AddAsync(key, new float[] { 0, 1, 0 }, "same", options)).Should().BeTrue();
            (await client.VectorSets.AddAsync(key, new float[] { 1, 0, 0 }, "same", options with
            {
                AttributesJson = "{\"category\":\"tools\",\"number\":5}",
            })).Should().BeFalse(); // VADD reports insertion, not whether replacement succeeded.
            (await client.VectorSets.EmbeddingAsync(key, "same")).Should().Equal(1f, 0f, 0f);
            var attributes = await client.VectorSets.GetAttributesJsonAsync(key, "same");
            using var json = JsonDocument.Parse(attributes!);
            json.RootElement.GetProperty("category").GetString().Should().Be("tools");
            json.RootElement.TryGetProperty("removed", out _).Should().BeFalse();

            var noAttributes = options with { AttributesJson = default };
            await client.VectorSets.AddAsync(key, new float[] { 0, 1, 0 }, "orthogonal", noAttributes);
            await client.VectorSets.AddAsync(key, new float[] { -1, 0, 0 }, "opposite", noAttributes);
            var search = new RespireVectorSearchOptions { Count = 3, IncludeScores = true, Exact = true, NoThread = true };
            var hits = await client.VectorSets.SearchAsync(key, new float[] { 1, 0, 0 }, search);
            hits.Select(hit => Encoding.UTF8.GetString(hit.Member)).Should().Equal("same", "orthogonal", "opposite");
            hits.Select(hit => hit.Score).Should().Equal(1d, 0.5d, 0d);
            // VectorData cosine similarity is 2*s-1; cosine distance is 2*(1-s).
            hits.Select(hit => 2 * hit.Score - 1).Should().Equal(1d, 0d, -1d);
            hits.Select(hit => 2 * (1 - hit.Score)).Should().Equal(0d, 1d, 2d);
            var filtered = await client.VectorSets.SearchAsync(key, new float[] { 1, 0, 0 }, search with
            {
                Filter = ".category == \"tools\" && .number >= 5",
            });
            filtered.Should().ContainSingle();
            Encoding.UTF8.GetString(filtered[0].Member).Should().Be("same");

            Func<Task> invalidReplacement = async () =>
                await client.VectorSets.AddAsync(key, new float[] { 1, 0 }, "same", noAttributes);
            await invalidReplacement.Should().ThrowAsync<RespireServerException>();
            (await client.VectorSets.CountAsync(key)).Should().Be(3);
            (await client.VectorSets.EmbeddingAsync(key, "same")).Should().Equal(1f, 0f, 0f);
            (await client.VectorSets.GetAttributesJsonAsync(key, "same")).Should().Equal(attributes!);
            foreach (var member in new[] { "same", "orthogonal", "opposite" })
                (await client.VectorSets.RemoveAsync(key, member)).Should().BeTrue();
            (await client.Keys.ExistsAsync(key)).Should().BeFalse();
            (await client.VectorSets.SearchAsync(key, new float[] { 1, 0, 0 }, search)).Should().BeEmpty();
        }
        finally
        {
            await client.Keys.DeleteAsync(key);
        }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task RedisVectorSetsRoundTripEveryCommandAndDeferredSurface(int protocol)
    {
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(Redis8.ConnectionString) with
        {
            Connections = 1,
            Protocol = (RespireProtocol)protocol,
            Serializer = new RespireSystemTextJsonSerializer(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
        });
        // The Redis 8 fixture is shared, so every key carries this row's prefix.
        var prefix = $"vectors:{Guid.NewGuid():N}:";
        var redis = client.WithKeyPrefix(prefix);
        byte[] member = [0, 255, 128];
        float[] vector = [1, 0, 0];
        foreach (var quantization in Enum.GetValues<RespireVectorQuantization>())
        {
            var key = "index:" + quantization;
            (await redis.VectorSets.AddAsync(key, vector, member, new()
            {
                Quantization = quantization, Links = 4, ExplorationFactor = 50,
                AttributesJson = "{\"category\":\"yes\"}",
            })).Should().BeTrue();
            Func<Task> mismatchedDefaults = async () => await redis.VectorSets.AddAsync(key, new[] { 0f, 1, 0 }, "b");
            await mismatchedDefaults.Should().ThrowAsync<RespireServerException>();
            (await redis.VectorSets.AddAsync(key, new[] { 0f, 1, 0 }, "b",
                new() { Quantization = quantization, Links = 4 }, RespireVectorEncoding.Values)).Should().BeTrue();
            (await redis.VectorSets.CountAsync(key)).Should().Be(2);
            (await redis.VectorSets.DimensionsAsync(key)).Should().Be(3);
            (await redis.VectorSets.ContainsAsync(key, member)).Should().BeTrue();
            (await redis.VectorSets.ContainsAsync(key, "missing")).Should().BeFalse();
            var embedding = await redis.VectorSets.EmbeddingAsync(key, member);
            embedding.Should().HaveCount(3);
            embedding![0].Should().BeApproximately(1, 0.01f);
            (await redis.VectorSets.EmbeddingAsync(key, "missing")).Should().BeNull();
            (await redis.VectorSets.GetAttributesAsync<Attributes>(key, member))!.Category.Should().Be("yes");
            (await redis.VectorSets.SetAttributesAsync(key, member, new Attributes { Category = "yes" })).Should().BeTrue();
            Encoding.UTF8.GetString((await redis.VectorSets.GetAttributesJsonAsync(key, member))!).Should().Be("{\"category\":\"yes\"}");

            foreach (var details in Enumerable.Range(0, 4))
            {
                var options = new RespireVectorSearchOptions { Count = 2, Exact = true, NoThread = true,
                    IncludeScores = (details & 1) != 0, IncludeAttributes = (details & 2) != 0 };
                var results = await redis.VectorSets.SearchAsync(key, vector, options);
                results.Should().HaveCount(2);
                results[0].Member.Should().Equal(member);
                if (options.IncludeScores) results[0].Score.Should().BeApproximately(1, 0.01);
                else results[0].Score.Should().BeNull();
                if (options.IncludeAttributes)
                {
                    results[0].AttributesJson.Should().NotBeNull();
                    results[1].AttributesJson.Should().BeNull();
                }
                else results[0].AttributesJson.Should().BeNull();
            }
            var filtered = await redis.VectorSets.SearchAsync(key, vector, new()
            {
                IncludeScores = true, IncludeAttributes = true, Count = 2, Epsilon = 0.1,
                ExplorationFactor = 30, Filter = ".category == \"yes\"", FilterExplorationFactor = 200,
                Exact = true, NoThread = true,
            }, RespireVectorEncoding.Values);
            filtered.Should().ContainSingle();
            filtered[0].Member.Should().Equal(member);
            (await redis.VectorSets.SearchByMemberAsync(key, member, new() { Count = 1, Exact = true, NoThread = true }))[0].Member.Should().Equal(member);
            var info = (await redis.VectorSets.InfoAsync(key))!;
            info.Dimensions.Should().Be(3); info.Size.Should().Be(2); info.Links.Should().Be(4);
            info.AttributesCount.Should().Be(1);
            (await redis.VectorSets.LinksAsync(key, member, true)).Should().NotBeNull();
            (await redis.VectorSets.LinksAsync(key, member)).Should().NotBeNull();
            (await redis.VectorSets.LinksAsync(key, "missing")).Should().BeNull();
            (await redis.VectorSets.RandomMemberAsync(key)).Should().NotBeNull();
            (await redis.VectorSets.RandomMembersAsync(key, 2)).Should().HaveCount(2);
            (await redis.VectorSets.RandomMembersAsync(key, -3)).Should().HaveCount(3);
            (await redis.VectorSets.RandomMembersAsync(key, 0)).Should().BeEmpty();
            (await redis.VectorSets.RangeAsync(key, "-", "+")).Should().HaveCount(2);
            (await redis.VectorSets.RangeAsync(key, "[b", "+", 1)).Single().Should().Equal("b"u8.ToArray());
            (await redis.VectorSets.SetAttributesJsonAsync(key, member, "")).Should().BeTrue();
            (await redis.VectorSets.GetAttributesJsonAsync(key, member)).Should().BeNull();
            (await redis.VectorSets.SetAttributesJsonAsync(key, "missing", "{}")).Should().BeFalse();
            (await redis.VectorSets.RemoveAsync(key, "b")).Should().BeTrue();
            (await redis.VectorSets.RemoveAsync(key, "b")).Should().BeFalse();
            (await client.Keys.ExistsAsync(prefix + key)).Should().BeTrue();
        }

        (await redis.VectorSets.AddAsync("reduced", new[] { 1f, 2, 3, 4 }, "m", new() { ReduceDimensions = 2, CheckAndSet = true })).Should().BeTrue();
        (await redis.VectorSets.DimensionsAsync("reduced")).Should().Be(2);
        (await redis.VectorSets.InfoAsync("reduced"))!.ProjectionInputDimensions.Should().Be(4);

        foreach (var transactional in new[] { false, true })
        {
            using var batch = transactional ? null : redis.CreateBatch();
            await using var transaction = transactional ? redis.CreateTransaction() : null;
            IRespireCommandQueue queue = transaction ?? (IRespireCommandQueue)batch!;
            var key = transactional ? "transaction" : "batch";
            float[] borrowed = [1, 0, 0];
            var attributes = new Attributes { Category = "snapshot" };
            var added = queue.VectorSets.Add(key, borrowed, "m", new() { Quantization = RespireVectorQuantization.None });
            var set = queue.VectorSets.SetAttributes(key, "m", attributes);
            var get = queue.VectorSets.GetAttributes<Attributes>(key, "m");
            var found = queue.VectorSets.Search(key, borrowed, new() { IncludeScores = true, Exact = true, NoThread = true });
            borrowed[0] = 0; borrowed[1] = 1; attributes.Category = "changed";
            if (batch is not null) await batch.ExecuteAsync();
            if (transaction is not null) await transaction.CommitAsync();
            added.Result.Should().BeTrue(); set.Result.Should().BeTrue();
            get.Result!.Category.Should().Be("snapshot");
            found.Result.Single().Score.Should().BeApproximately(1, 0.0001);
            (await redis.VectorSets.EmbeddingAsync(key, "m"))![0].Should().BeApproximately(1, 0.0001f);
        }

        (await redis.VectorSets.InfoAsync("missing")).Should().BeNull();
        (await redis.VectorSets.RandomMemberAsync("missing")).Should().BeNull();
        (await redis.VectorSets.RangeAsync("missing", "-", "+")).Should().BeEmpty();
        (await redis.VectorSets.SearchAsync("missing", vector)).Should().BeEmpty();
        Func<Task> missingDimensions = async () => await redis.VectorSets.DimensionsAsync("missing");
        await missingDimensions.Should().ThrowAsync<RespireServerException>();
        await client.DisposeAsync();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task OlderRedisReturnsItsUnsupportedCommandError(int protocol)
    {
        // Unsupported commands change nothing, so the shared Redis 7.2 server is safe.
        var lease = await servers.LeaseAsync("redis:7.2-alpine");
        await using var client = await RespireClient.ConnectAsync(lease.ConnectionString(protocol) + "&connections=1");
        Func<Task> add = async () => await client.VectorSets.AddAsync("v", new[] { 1f }, "m");
        await add.Should().ThrowAsync<RespireServerException>();
        Func<Task> read = async () => await client.VectorSets.SearchAsync("v", new[] { 1f });
        await read.Should().ThrowAsync<RespireServerException>();
        await client.PingAsync();
    }

    [Test]
    [NotInParallel(TestConstraints.ClientCacheHits)]
    public async Task CachedAttributesOwnTheirBytesAndVectorMutationsInvalidateTheirKey()
    {
        await using var root = await RespireClient.ConnectAsync(RespireOptions.Parse(CacheServer.ConnectionString) with
        {
            Connections = 1,
            ClientSideCache = new RespireClientSideCacheOptions(),
        });
        var client = root.WithKeyPrefix($"vectors:{Guid.NewGuid():N}:");
        await client.VectorSets.AddAsync("cached", new[] { 1f, 0, 0 }, "m", new() { AttributesJson = "{\"n\":1}" });
        await client.SetAsync("unrelated", "retained");
        await client.GetStringAsync("unrelated");
        var first = (await client.VectorSets.GetAttributesJsonAsync("cached", "m"))!;
        first[0] = 0;
        var hits = root.ClientSideCache!.GetStatistics().Hits;
        Encoding.UTF8.GetString((await client.VectorSets.GetAttributesJsonAsync("cached", "m"))!).Should().Be("{\"n\":1}");
        root.ClientSideCache.GetStatistics().Hits.Should().BeGreaterThan(hits);
        await client.VectorSets.SetAttributesJsonAsync("cached", "m", "{\"n\":2}");
        Encoding.UTF8.GetString((await client.VectorSets.GetAttributesJsonAsync("cached", "m"))!).Should().Be("{\"n\":2}");
        hits = root.ClientSideCache.GetStatistics().Hits;
        (await client.GetStringAsync("unrelated")).Should().Be("retained");
        root.ClientSideCache.GetStatistics().Hits.Should().BeGreaterThan(hits);
        await client.VectorSets.RemoveAsync("cached", "m");
        (await client.VectorSets.GetAttributesJsonAsync("cached", "m")).Should().BeNull();
        await client.VectorSets.AddAsync("cached", new[] { 1f, 0, 0 }, "m", new() { AttributesJson = "{\"n\":3}" });
        Encoding.UTF8.GetString((await client.VectorSets.GetAttributesJsonAsync("cached", "m"))!).Should().Be("{\"n\":3}");
    }

    public sealed class Attributes { public string Category { get; set; } = ""; }
}
