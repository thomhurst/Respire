using FluentAssertions;
using Respire.Search;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
// FT.HYBRID needs Redis 8.4 or later; indexes only cover their own per-test key prefix.
[ClassDataSource<ModernRedisTestContainer>(Shared = SharedType.PerTestSession)]
public class SearchIntegrationTests(ModernRedisTestContainer fixture)
{
    [ClassDataSource<ModernRedisTestContainer>(Shared = SharedType.Keyed, Key = TestConstraints.ClientCacheServer)]
    public required ModernRedisTestContainer CacheServer { get; init; }

    private static readonly RespireSearchExpression All = RespireSearchExpression.FromRaw("*");
    private static readonly byte[] Vector = CreateVector();

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task TypedQueriesAggregationVectorsAndHybridUseRealServerReplies(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var search = new RespireSearchClient(client);
        var index = NewIndex();
        try
        {
            await CreateDocumentsAsync(client, search, index);
            var expression = RespireSearchQueryBuilder.And(
                RespireSearchQueryBuilder.TextField("title", "redis"),
                RespireSearchQueryBuilder.Tag("category", "cache|client"),
                RespireSearchQueryBuilder.NumericRange("year", 2024, 2026));
            var result = await search.SearchAsync(index, new(expression,
                new() { ReturnFields = ["title", "year"], WithScores = true }));
            result.Total.Should().Be(1);
            result.Documents.Should().ContainSingle().Which.Id.Should().Be(index + ":doc:1");
            result.Documents[0].Fields["title"].Should().Be("redis");
            result.Documents[0].Score.Should().NotBeNull();

            var groups = await search.AggregateAsync(index, All, new()
            {
                Stages =
                [
                    RespireSearchAggregateStage.GroupBy([], new RespireSearchReducer("COUNT", [], "count")),
                    RespireSearchAggregateStage.Filter("@count > 1"),
                ],
            });
            groups.Rows.Should().ContainSingle().Which["count"].Should().Be("3");
            var nearest = await search.VectorSearchAsync(index, new("embedding", Vector, 3)
            {
                Filter = RespireSearchQueryBuilder.Tag("category", "cache|client"),
            });
            nearest.Documents.Should().ContainSingle().Which.Id.Should().Be(index + ":doc:1");
            var hybrid = await search.HybridSearchAsync(index,
                new(RespireSearchQueryBuilder.Text("redis"), "embedding", Vector, 3)
                { LoadFields = ["title"] });
            hybrid.Documents.Should().NotBeEmpty();
            hybrid.Documents.Should().OnlyContain(document => document.Fields["title"] == "redis");

            await search.AlterIndexAsync(index, new("extra", RespireSearchFieldType.Text));
            var info = await search.GetIndexInfoAsync(index);
            info.DocumentCount.Should().Be(3);
            info.Attributes.Should().Contain(attribute => attribute.Identifier == "extra");
            (await search.ExplainAsync(index, expression)).Should().NotBeNullOrWhiteSpace();
        }
        finally { await DropIndexIfPresentAsync(search, index); }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ErrorsPrefixesAndPreCancellationPreserveTheIndex(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var search = new RespireSearchClient(client);
        var index = NewIndex();
        try
        {
            await CreateDocumentsAsync(client, search, index);
            await search.Awaiting(s => s.SearchAsync(index + ":missing", new(All)).AsTask())
                .Should().ThrowAsync<RespireServerException>();
            await search.Awaiting(s => s.SearchAsync(index, new(RespireSearchExpression.FromRaw("@year:[invalid]"))).AsTask())
                .Should().ThrowAsync<RespireServerException>();
            await using var tenant = client.WithKeyPrefix("tenant:");
            var prefixed = new RespireSearchClient(tenant);
            await prefixed.Awaiting(s => s.SearchAsync(index, new(All)).AsTask())
                .Should().ThrowAsync<NotSupportedException>();
            await prefixed.Awaiting(s => s.DropIndexAsync(index, deleteDocuments: true).AsTask())
                .Should().ThrowAsync<NotSupportedException>();

            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            Func<Task>[] operations =
            [
                () => search.DropIndexAsync(index, true, canceled.Token).AsTask(),
                () => search.AlterIndexAsync(index, new("canceled", RespireSearchFieldType.Text), canceled.Token).AsTask(),
                () => search.SearchAsync(index, new(All), canceled.Token).AsTask(),
                () => search.AggregateAsync(index, All, cancellationToken: canceled.Token).AsTask(),
                () => search.VectorSearchAsync(index, new("embedding", Vector, 1), cancellationToken: canceled.Token).AsTask(),
                () => search.HybridSearchAsync(index, new(All, "embedding", Vector, 1), canceled.Token).AsTask(),
            ];
            foreach (var operation in operations)
            {
                var error = await operation.Should().ThrowAsync<OperationCanceledException>();
                error.Which.CancellationToken.Should().Be(canceled.Token);
            }
            var info = await search.GetIndexInfoAsync(index);
            info.DocumentCount.Should().Be(3);
            info.Attributes.Should().NotContain(attribute => attribute.Identifier == "canceled");
        }
        finally { await DropIndexIfPresentAsync(search, index); }
    }

    [Test, NotInParallel(TestConstraints.ClientCacheHits)]
    public async Task QueryAndCursorOperationsRetainCacheWhileIndexMutationsInvalidateIt()
    {
        await using var client = await ConnectAsync(3, cache: true, CacheServer);
        var search = new RespireSearchClient(client);
        var index = NewIndex();
        var key = index + ":unrelated";
        var dropped = false;
        try
        {
            await client.SetAsync(key, "cached");
            (await client.GetStringAsync(key)).Should().Be("cached");
            // Hits count served GET lookups; tracking pushes only invalidate entries.
            var creationHits = client.ClientSideCache!.GetStatistics().Hits;
            await CreateDocumentsAsync(client, search, index);
            (await client.GetStringAsync(key)).Should().Be("cached");
            client.ClientSideCache.GetStatistics().Hits.Should().Be(creationHits);
            Func<Task>[] reads =
            [
                () => search.SearchAsync(index, new(All)).AsTask(),
                () => search.GetIndexInfoAsync(index).AsTask(),
                () => search.ExplainAsync(index, All).AsTask(),
                () => search.AggregateAsync(index, All).AsTask(),
                () => search.VectorSearchAsync(index, new("embedding", Vector, 1)).AsTask(),
                () => search.HybridSearchAsync(index, new(All, "embedding", Vector, 1)).AsTask(),
            ];
            foreach (var read in reads)
                await AssertCacheRetainedAsync(client, key, read);

            var page = await search.AggregateWithCursorAsync(index, All,
                new() { Stages = [RespireSearchAggregateStage.Load("@title")] }, new() { Count = 1 });
            page.IsComplete.Should().BeFalse();
            await AssertCacheRetainedAsync(client, key, async () => { page = await search.ReadCursorAsync(page, 1); });
            page.IsComplete.Should().BeFalse();
            await AssertCacheRetainedAsync(client, key, () => search.DeleteCursorAsync(page).AsTask());

            await client.GetStringAsync(key);
            var hits = client.ClientSideCache!.GetStatistics().Hits;
            await search.AlterIndexAsync(index, new("extra", RespireSearchFieldType.Text));
            (await client.GetStringAsync(key)).Should().Be("cached");
            client.ClientSideCache.GetStatistics().Hits.Should().Be(hits);

            hits = client.ClientSideCache.GetStatistics().Hits;
            await search.DropIndexAsync(index, deleteDocuments: true);
            dropped = true;
            (await client.GetStringAsync(key)).Should().Be("cached");
            client.ClientSideCache.GetStatistics().Hits.Should().Be(hits);
        }
        finally
        {
            try { if (!dropped) await DropIndexIfPresentAsync(search, index); }
            finally { await client.Keys.DeleteAsync(key); }
        }
    }

    [Test]
    public async Task CleanupRemovesPartialSetupAndToleratesAnAbsentIndex()
    {
        await using var client = await ConnectAsync(3);
        var search = new RespireSearchClient(client);
        var index = NewIndex();
        var wrongTypeKey = index + ":doc:2";
        try
        {
            await client.SetAsync(wrongTypeKey, "not-a-hash");
            Func<Task> setup = async () =>
            {
                try { await CreateDocumentsAsync(client, search, index); }
                finally { await DropIndexIfPresentAsync(search, index); }
            };
            await setup.Should().ThrowAsync<RespireServerException>().WithMessage("*WRONGTYPE*");
            await search.Awaiting(s => s.GetIndexInfoAsync(index).AsTask()).Should().ThrowAsync<RespireServerException>();
            (await client.Keys.ExistsAsync(index + ":doc:1")).Should().BeFalse();
            await DropIndexIfPresentAsync(search, index);
        }
        finally
        {
            try { await DropIndexIfPresentAsync(search, index); }
            finally { await client.Keys.DeleteAsync(wrongTypeKey); }
        }
    }

    private static async Task AssertCacheRetainedAsync(RespireClient client, string key, Func<Task> operation)
    {
        (await client.GetStringAsync(key)).Should().Be("cached");
        // Search replies and background tracking pushes do not increment the GET hit count.
        var hits = client.ClientSideCache!.GetStatistics().Hits;
        await operation();
        (await client.GetStringAsync(key)).Should().Be("cached");
        client.ClientSideCache.GetStatistics().Hits.Should().Be(hits + 1);
    }

    private ValueTask<RespireClient> ConnectAsync(int protocol, bool cache = false, StandaloneRedisTestContainer? server = null)
        => RespireClient.ConnectAsync(RespireOptions.Parse((server ?? fixture).ConnectionString) with
        { Protocol = (RespProtocol)protocol, ClientSideCache = cache ? new() : null });

    private static string NewIndex() => "search:" + Guid.NewGuid().ToString("N");

    private static async Task DropIndexIfPresentAsync(RespireSearchClient search, string index)
    {
        try { await search.DropIndexAsync(index, deleteDocuments: true); }
        catch (RespireServerException error) when (error.Message.Equals($"{index}: no such index", StringComparison.Ordinal)
            || error.Message.Equals("Unknown Index name", StringComparison.OrdinalIgnoreCase)
            // Redis 8.10 reports a missing index with an error code prefix.
            || error.Message.EndsWith($"Index not found: {index}", StringComparison.Ordinal))
        {
            // Setup can fail before CREATE succeeds. Do not replace that failure during cleanup.
        }
    }

    /// <summary>Creates isolated documents and waits until all vectors are searchable before assertions run.</summary>
    private static async Task CreateDocumentsAsync(RespireClient client, RespireSearchClient search, string index)
    {
        await search.CreateIndexAsync(index, new()
        {
            Prefixes = [index + ":doc:"],
            Fields =
            [
                new("title", RespireSearchFieldType.Text),
                new("category", RespireSearchFieldType.Tag),
                new("year", RespireSearchFieldType.Numeric),
                new("embedding", RespireSearchFieldType.Vector)
                { Vector = new(RespireSearchVectorAlgorithm.Flat, RespireSearchVectorType.Float32, 2, RespireSearchDistanceMetric.L2) },
            ],
        });
        for (var i = 1; i <= 3; i++)
            await client.Hashes.SetAsync(index + ":doc:" + i,
                ("title", "redis"), ("category", i == 1 ? "cache|client" : "cache"),
                ("year", "2025"), ("embedding", Vector));
        // Vector indexing can finish after the hash writes and scalar indexes are visible.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while ((await search.VectorSearchAsync(index, new("embedding", Vector, 3),
                   cancellationToken: deadline.Token)).Documents.Count != 3)
            await Task.Delay(20, deadline.Token);
    }

    private static byte[] CreateVector()
    {
        var vector = new byte[2 * sizeof(float)];
        BitConverter.TryWriteBytes(vector.AsSpan(), 1f);
        return vector;
    }
}
