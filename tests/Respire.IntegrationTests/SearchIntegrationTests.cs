using FluentAssertions;
using Redis.Search;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<SearchRedisTestContainer>(Shared = SharedType.PerTestSession)]
public class SearchIntegrationTests(SearchRedisTestContainer fixture)
{
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
        await CreateDocumentsAsync(client, search, index);
        try
        {
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
        finally { await search.DropIndexAsync(index, deleteDocuments: true); }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ErrorsPrefixesAndPreCancellationPreserveTheIndex(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var search = new RespireSearchClient(client);
        var index = NewIndex();
        await CreateDocumentsAsync(client, search, index);
        try
        {
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
        finally { await search.DropIndexAsync(index, deleteDocuments: true); }
    }

    [Test, NotInParallel]
    public async Task QueryAndCursorOperationsRetainCacheWhileIndexMutationsInvalidateIt()
    {
        await using var client = await ConnectAsync(3, cache: true);
        var search = new RespireSearchClient(client);
        var index = NewIndex();
        var key = index + ":unrelated";
        await client.SetAsync(key, "cached");
        (await client.GetStringAsync(key)).Should().Be("cached");
        var creationHits = client.ClientSideCache!.GetStatistics().Hits;
        await CreateDocumentsAsync(client, search, index);
        var dropped = false;
        try
        {
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
            if (!dropped) await search.DropIndexAsync(index, deleteDocuments: true);
            await client.Keys.DeleteAsync(key);
        }
    }

    private static async Task AssertCacheRetainedAsync(RespireClient client, string key, Func<Task> operation)
    {
        (await client.GetStringAsync(key)).Should().Be("cached");
        var hits = client.ClientSideCache!.GetStatistics().Hits;
        await operation();
        (await client.GetStringAsync(key)).Should().Be("cached");
        client.ClientSideCache.GetStatistics().Hits.Should().Be(hits + 1);
    }

    private ValueTask<RespireClient> ConnectAsync(int protocol, bool cache = false)
        => RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString) with
        { Protocol = (RespProtocol)protocol, ClientSideCache = cache ? new() : null });

    private static string NewIndex() => "search:" + Guid.NewGuid().ToString("N");

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
    }

    private static byte[] CreateVector()
    {
        var vector = new byte[2 * sizeof(float)];
        BitConverter.TryWriteBytes(vector.AsSpan(), 1f);
        return vector;
    }
}
