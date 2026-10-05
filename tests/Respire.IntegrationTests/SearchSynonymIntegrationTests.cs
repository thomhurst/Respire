using FluentAssertions;
using Respire.Search;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
[ClassDataSource<ModernRedisTestContainer>(Shared = SharedType.PerTestSession)]
public class SearchSynonymIntegrationTests(ModernRedisTestContainer fixture)
{
    [ClassDataSource<ModernRedisTestContainer>(Shared = SharedType.Keyed, Key = TestConstraints.ClientCacheServer)]
    public required ModernRedisTestContainer CacheServer { get; init; }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task UpdatesPreserveGroupsAndControlExistingDocumentScan(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var search = client.Search;
        var index = await CreateIndexAsync(search);
        try
        {
            (await search.GetSynonymsAsync(index)).Should().BeEmpty();
            (await search.GetTagValuesAsync(index, "tags")).Should().BeEmpty();
            await client.Hashes.SetAsync(index + ":doc:1", ("title", "car"), ("tags", "Red,Blue"));
            await search.UpdateSynonymsAsync(index, "vehicles", ["car", "automobile"]);
            await WaitForCountAsync(search, index, "automobile", 1);
            await search.UpdateSynonymsAsync(index, "vehicles", ["vehicle"]);
            await search.UpdateSynonymsAsync(index, "body", ["car", "sedan"]);
            var retained = await search.GetSynonymsAsync(index);
            retained["car"].Should().BeEquivalentTo(["vehicles", "body"]);
            retained["automobile"].Should().ContainSingle().Which.Should().Be("vehicles");
            retained["vehicle"].Should().ContainSingle().Which.Should().Be("vehicles");
            await WaitForCountAsync(search, index, "vehicle", 1);
            await WaitForCountAsync(search, index, "sedan", 1);
            await search.UpdateSynonymsAsync(index, "new-only", ["car", "motorcar"], skipInitialScan: true);
            (await search.SearchAsync(index, new(RespireSearchExpression.FromRaw("motorcar")))).Total.Should().Be(0);
            await client.Hashes.SetAsync(index + ":doc:2", ("title", "car"), ("tags", "Green"));
            await WaitForCountAsync(search, index, "motorcar", 1);
            var tags = await search.GetTagValuesAsync(index, "tags");
            tags.Should().BeEquivalentTo(["red", "blue", "green"]);
            await search.UpdateSynonymsAsync(index, "literal", ["car", "SKIPINITIALSCAN"]);
            var current = await search.GetSynonymsAsync(index);
            current["skipinitialscan"].Should().Contain("literal");
            current["car"].Should().BeEquivalentTo(["vehicles", "body", "new-only", "literal"]);
            await client.Hashes.SetAsync(index + ":doc:3", ("title", "plane"), ("tags", "Purple"));
            await WaitForCountAsync(search, index, "*", 3);
            (await search.GetTagValuesAsync(index, "tags")).Should().BeEquivalentTo(["red", "blue", "green", "purple"]);
            // Server state now differs from both earlier snapshots; owned replies must retain their original values.
            retained["car"].Should().BeEquivalentTo(["vehicles", "body"]);
            tags.Should().BeEquivalentTo(["red", "blue", "green"]);
        }
        finally { await search.DropIndexAsync(index, deleteDocuments: true); }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task PrefixCancellationAndErrorsLeaveSynonymsUnchanged(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var search = client.Search;
        var index = await CreateIndexAsync(search);
        await using var prefixed = client.WithKeyPrefix("tenant:");
        Func<RespireSearchClient, string, CancellationToken, Task>[] calls = [
            (s, name, token) => s.UpdateSynonymsAsync(name, "g", ["car", "auto"], cancellationToken: token).AsTask(),
            (s, name, token) => s.GetSynonymsAsync(name, token).AsTask(),
            (s, name, token) => s.GetTagValuesAsync(name, "tags", token).AsTask()];
        try
        {
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            foreach (var call in calls)
            {
                Func<Task> prefix = () => call(prefixed.Search, index, default);
                await prefix.Should().ThrowAsync<NotSupportedException>();
                Func<Task> cancellation = () => call(search, index, canceled.Token);
                var error = await cancellation.Should().ThrowAsync<OperationCanceledException>();
                error.Which.CancellationToken.Should().Be(canceled.Token);
                Func<Task> missing = () => call(search, index + ":missing", default);
                await missing.Should().ThrowAsync<RespireServerException>();
            }
            await search.Awaiting(s => s.GetTagValuesAsync(index, "title").AsTask()).Should().ThrowAsync<RespireServerException>();
            (await search.GetSynonymsAsync(index)).Should().BeEmpty();
        }
        finally { await search.DropIndexAsync(index, deleteDocuments: true); }
    }

    [Test, NotInParallel(TestConstraints.ClientCacheHits)]
    public async Task ReadsRetainCacheAndSynonymUpdateInvalidatesIt()
    {
        await using var client = await ConnectAsync(3, cache: true, CacheServer);
        var search = client.Search;
        var index = await CreateIndexAsync(search);
        var cached = index + ":cached";
        try
        {
            await client.SetAsync(cached, "cached");
            Func<Task>[] reads = [() => search.GetSynonymsAsync(index).AsTask(), () => search.GetTagValuesAsync(index, "tags").AsTask()];
            foreach (var read in reads)
            {
                await client.GetStringAsync(cached);
                var hits = client.ClientSideCache!.GetStatistics().Hits;
                await read();
                (await client.GetStringAsync(cached)).Should().Be("cached");
                client.ClientSideCache.GetStatistics().Hits.Should().Be(hits + 1);
            }
            var beforeMutation = client.ClientSideCache!.GetStatistics().Hits;
            await search.UpdateSynonymsAsync(index, "g", ["car", "auto"]);
            (await client.GetStringAsync(cached)).Should().Be("cached");
            client.ClientSideCache.GetStatistics().Hits.Should().Be(beforeMutation);
        }
        finally
        {
            try { await search.DropIndexAsync(index, deleteDocuments: true); }
            finally { await client.Keys.DeleteAsync(cached); }
        }
    }

    private static async Task<string> CreateIndexAsync(RespireSearchClient search)
    {
        var index = "synonyms:" + Guid.NewGuid().ToString("N");
        await search.CreateIndexAsync(index, new()
        {
            Prefixes = [index + ":doc:"],
            Fields = [new("title", RespireSearchFieldType.Text) { NoStem = true }, new("tags", RespireSearchFieldType.Tag)],
        });
        return index;
    }

    private static async Task WaitForCountAsync(RespireSearchClient search, string index, string query, long expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while ((await search.SearchAsync(index, new(RespireSearchExpression.FromRaw(query)), timeout.Token)).Total != expected
            || (await search.GetIndexInfoAsync(index, timeout.Token)).Properties["indexing"].Scalar != "0")
            await Task.Delay(20, timeout.Token);
    }

    private ValueTask<RespireClient> ConnectAsync(int protocol, bool cache = false, StandaloneRedisTestContainer? server = null)
        => RespireClient.ConnectAsync(RespireOptions.Parse((server ?? fixture).ConnectionString) with
        { Protocol = (RespProtocol)protocol, ClientSideCache = cache ? new() : null });
}
