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
    public async Task TextPresentationAndScorerMetadataUseRealServerReplies(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var search = client.Search;
        var index = NewIndex();
        try
        {
            await search.CreateIndexAsync(index, new()
            {
                Prefixes = [index + ":doc:"], StopWords = [], Language = "english", LanguageField = "language",
                Score = 0.5, SkipInitialScan = true,
                Fields =
                [
                    new("title", RespireSearchFieldType.Text) { Phonetic = RespireSearchPhoneticMatcher.English },
                    new("body", RespireSearchFieldType.Text),
                    new("rating", RespireSearchFieldType.Numeric, Sortable: true),
                ],
            });
            await client.Hashes.SetAsync(index + ":doc:1", ("title", "Dogs and cats"),
                ("body", "Dogs chase cats in the garden. Cats chase dogs."), ("rating", "5"), ("language", "english"));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while ((await search.GetIndexInfoAsync(index, deadline.Token)).DocumentCount != 1)
                await Task.Delay(20, deadline.Token);
            var options = new RespireSearchQueryOptions
            {
                InKeys = [index + ":doc:1"], InFields = ["title", "body"], WithScores = true, ExplainScore = true,
                WithPayloads = true, WithSortKeys = true, SortBy = ("rating", RespireSearchSortDirection.Descending),
                Scorer = "DISMAX", Language = "english", Verbatim = true, NoStopWords = true, Slop = 0, InOrder = true,
                Highlight = new() { Fields = ["title"], Tags = ("<mark>", "</mark>") },
                Summarize = new() { Fields = ["body"], Fragments = 1, Length = 5, Separator = " | " },
                ReturnFields = ["title", "body", "rating"],
            };
            var result = await search.SearchAsync(index, new(RespireSearchQueryBuilder.Text("dogs"), options));
            var document = result.Documents.Should().ContainSingle().Which;
            document.Score.Should().BeGreaterThan(0);
            document.ScoreExplanation.Should().NotBeNull();
            document.TextResults["title"].Text.Should().Be("<mark>Dogs</mark> and cats");
            document.TextResults["title"].HighlightRequested.Should().BeTrue();
            document.TextResults["body"].SummaryRequested.Should().BeTrue();
            document.TextResults["body"].Text.Should().Contain("Dogs").And.EndWith(" | ");
            document.TextResults.Should().NotContainKey("rating");
            document.SortKey!.Scalar.Should().Be("#5");
            document.Payload.Should().BeNull();
            await client.PingAsync();
            document.TextResults["title"].Text.Should().Contain("<mark>Dogs</mark>");
            // Profile uses the same query options and result parser as a normal search.
            var profiled = await search.ProfileSearchAsync(index, new(RespireSearchQueryBuilder.Text("dogs"), options));
            profiled.Result.Documents.Should().ContainSingle().Which.ScoreExplanation.Should().NotBeNull();
        }
        finally { await DropIndexIfPresentAsync(search, index); }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task CollectProjectsSortsLimitsAndDeduplicatesRealGroupEntries(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var search = client.Search;
        var index = NewIndex();
        try
        {
            await CreateDocumentsAsync(client, search, index);
            var result = await search.AggregateAsync(index, All, new()
            {
                Stages =
                [
                    RespireSearchAggregateStage.Load("@title", "@year"),
                    RespireSearchAggregateStage.GroupBy([], RespireSearchReducer.Collect(new()
                    {
                        Fields = ["title", "year"], Distinct = true,
                        SortBy = [new("year", RespireSearchSortDirection.Descending)], Limit = (0, 1),
                    }, "top")),
                ],
            });
            var entries = result.StructuredRows.Should().ContainSingle().Which["top"].Items;
            entries.Should().ContainSingle();
            entries[0].Items.Should().HaveCount(4);
            var fields = entries[0].Items.Chunk(2).ToDictionary(pair => pair[0].Scalar!, pair => pair[1].Scalar);
            fields["title"].Should().Be("redis");
            fields["year"].Should().Be("2025");
            // FIELDS * means loaded pipeline fields, not an implicit full-document load.
            var distinct = await search.AggregateAsync(index, All, new()
            {
                Stages = [RespireSearchAggregateStage.Load("@title"),
                    RespireSearchAggregateStage.GroupBy([], RespireSearchReducer.Collect(new() { AllFields = true, Distinct = true }, "all"))],
            });
            distinct.StructuredRows[0]["all"].Items.Should().ContainSingle();
            distinct.StructuredRows[0]["all"].Items[0].Items.Should().HaveCount(2);
        }
        finally { await DropIndexIfPresentAsync(search, index); }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task IndexInventoryAndAliasSwapUseRealServerReplies(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var search = client.Search;
        var first = NewIndex();
        var second = NewIndex();
        var alias = first + ":alias";
        try
        {
            await CreateDocumentsAsync(client, search, first);
            await CreateDocumentsAsync(client, search, second);
            (await search.ListIndexesAsync()).Should().Contain([first, second]);
            (await search.ListAliasesAsync(first)).Should().BeEmpty();
            await search.AddAliasAsync(alias, first);
            (await search.ListAliasesAsync(first)).Should().ContainSingle().Which.Should().Be(alias);
            var original = await search.SearchAsync(alias, new(All));
            original.Total.Should().Be(3);
            original.Documents.Should().OnlyContain(d => d.Id.StartsWith(first, StringComparison.Ordinal));
            await search.Awaiting(s => s.AddAliasAsync(alias, second).AsTask()).Should().ThrowAsync<RespireServerException>();
            await search.UpdateAliasAsync(alias, second);
            (await search.ListAliasesAsync(first)).Should().BeEmpty();
            (await search.ListAliasesAsync(second)).Should().ContainSingle().Which.Should().Be(alias);
            var swapped = await search.SearchAsync(alias, new(All));
            swapped.Total.Should().Be(3);
            swapped.Documents.Should().OnlyContain(d => d.Id.StartsWith(second, StringComparison.Ordinal));
            await search.DeleteAliasAsync(alias);
            (await search.ListAliasesAsync(second)).Should().BeEmpty();
            await search.Awaiting(s => s.SearchAsync(alias, new(All)).AsTask()).Should().ThrowAsync<RespireServerException>();
            (await search.GetIndexInfoAsync(second)).DocumentCount.Should().Be(3);
            await search.UpdateAliasAsync(alias, first);
            (await search.ListAliasesAsync(first)).Should().Contain(alias);
        }
        finally
        {
            try { await DropIndexIfPresentAsync(search, first); }
            finally { await DropIndexIfPresentAsync(search, second); }
        }
    }

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

            Func<RespireSearchClient, CancellationToken, Task>[] aliasOperations =
            [
                (s, token) => s.ListIndexesAsync(token).AsTask(),
                (s, token) => s.ListAliasesAsync(index, token).AsTask(),
                (s, token) => s.AddAliasAsync(index + ":alias", index, token).AsTask(),
                (s, token) => s.UpdateAliasAsync(index + ":alias", index, token).AsTask(),
                (s, token) => s.DeleteAliasAsync(index + ":alias", token).AsTask(),
            ];
            foreach (var operation in aliasOperations)
            {
                Func<Task> prefixedOperation = () => operation(prefixed, default);
                await prefixedOperation.Should().ThrowAsync<NotSupportedException>();
            }

            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            foreach (var operation in aliasOperations)
            {
                Func<Task> canceledOperation = () => operation(search, canceled.Token);
                var error = await canceledOperation.Should().ThrowAsync<OperationCanceledException>();
                error.Which.CancellationToken.Should().Be(canceled.Token);
            }
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
                () => search.ListIndexesAsync().AsTask(),
                () => search.ListAliasesAsync(index).AsTask(),
                () => search.SearchAsync(index, new(All)).AsTask(),
                () => search.GetIndexInfoAsync(index).AsTask(),
                () => search.ExplainAsync(index, All).AsTask(),
                () => search.AggregateAsync(index, All).AsTask(),
                () => search.VectorSearchAsync(index, new("embedding", Vector, 1)).AsTask(),
                () => search.HybridSearchAsync(index, new(All, "embedding", Vector, 1)).AsTask(),
            ];
            foreach (var read in reads)
                await AssertCacheRetainedAsync(client, key, read);

            Func<Task>[] aliasMutations =
            [
                () => search.AddAliasAsync(index + ":alias", index).AsTask(),
                () => search.UpdateAliasAsync(index + ":alias", index).AsTask(),
                () => search.DeleteAliasAsync(index + ":alias").AsTask(),
            ];
            foreach (var mutation in aliasMutations)
            {
                await client.GetStringAsync(key);
                var beforeMutation = client.ClientSideCache!.GetStatistics().Hits;
                await mutation();
                (await client.GetStringAsync(key)).Should().Be("cached");
                client.ClientSideCache.GetStatistics().Hits.Should().Be(beforeMutation);
            }

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

    /// <summary>Creates isolated documents after the initial scan and waits for vector visibility before assertions run.</summary>
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
        // Preserve the initial-scan barrier before writes: a scan racing HSET can index a hash twice.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while ((await search.GetIndexInfoAsync(index, deadline.Token)).Properties["indexing"].Scalar != "0")
            await Task.Delay(20, deadline.Token);
        for (var i = 1; i <= 3; i++)
            await client.Hashes.SetAsync(index + ":doc:" + i,
                ("title", "redis"), ("category", i == 1 ? "cache|client" : "cache"),
                ("year", "2025"), ("embedding", Vector));
        // Wait for the initial scan and vector visibility; HSET completion alone proves neither.
        while (true)
        {
            var info = await search.GetIndexInfoAsync(index, deadline.Token);
            if (info.DocumentCount == 3 && info.Properties["indexing"].Scalar == "0"
                && (await search.VectorSearchAsync(index, new("embedding", Vector, 3),
                    cancellationToken: deadline.Token)).Documents.Count == 3)
                return;
            await Task.Delay(20, deadline.Token);
        }
    }

    private static byte[] CreateVector()
    {
        var vector = new byte[2 * sizeof(float)];
        BitConverter.TryWriteBytes(vector.AsSpan(), 1f);
        return vector;
    }
}
