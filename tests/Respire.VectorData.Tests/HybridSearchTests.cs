// Adapted hybrid contracts from dotnet/extensions HybridSearchTests.cs at
// 02107c65bab30aad9e35b5133ed643eaa77bccd8, licensed under MIT.
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.VectorData;
using Respire.IntegrationTests;
using Respire.Samples.VectorData;
using Respire.Search;
using Respire.VectorData;
using TUnit.Core;
using TUnit.Core.Interfaces;

namespace Respire.VectorData.Tests;

public sealed class HybridRedis84() : StandaloneRedisTestContainer("redis:8.4-alpine");
public sealed class HybridRedis82() : StandaloneRedisTestContainer("redis:8.2-alpine");

public sealed class HybridRedisVersions : IAsyncInitializer, IAsyncDisposable
{
    private readonly HybridRedis84 _floor = new();
    private readonly ModernRedisTestContainer _current = new();
    private readonly HybridRedis82 _missing = new();
    public string Connection(int version) => version switch { 84 => _floor.ConnectionString, 82 => _missing.ConnectionString, _ => _current.ConnectionString };
    public Task InitializeAsync() => Task.WhenAll(_floor.InitializeAsync(), _current.InitializeAsync(), _missing.InitializeAsync());
    public async ValueTask DisposeAsync() { await _floor.DisposeAsync(); await _current.DisposeAsync(); await _missing.DisposeAsync(); }
}

[ClassDataSource<HybridRedisVersions>(Shared = SharedType.PerTestSession)]
public class HybridSearchTests(HybridRedisVersions fixture)
{
    [Test, Arguments(2, 84), Arguments(3, 84), Arguments(2, 810), Arguments(3, 810)]
    public async Task HybridContractsRankFilterPageAndPreserveServerScores(int protocol, int version)
    {
        await using var client = await Connect(protocol, version);
        using var store = new RespireVectorStore(client, Guid.NewGuid().ToString("N"));
        store.RegisterMapper(new MovieMapper());
        using var collection = store.GetHashCollection<Movie>("hybrid");
        try
        {
            await collection.EnsureCollectionExistsAsync();
            // Distinct distances keep vector ranks stable across the separate score-comparison queries.
            Movie[] records = [new("1", "Apples are a healthy and nourishing snack", new float[] { 1.2f, 0 }, "allowed"),
                new("2", "Oranges are tangy and contain vitamin c", new float[] { 1.1f, 0 }, "allowed"),
                new("3", "Grapes are healthy sweet and juicy", new float[] { 1, 0 }, "excluded")];
            await collection.UpsertAsync(records);
            await WaitForIndex(collection, 3);
            var hybrid = (IKeywordHybridSearchable<Movie>)collection;
            collection.GetService(typeof(IKeywordHybridSearchable<Movie>)).Should().BeSameAs(collection);
            var query = new RespireHybridSearchQuery(RespireSearchQueryBuilder.TextField("title", "Grapes"), "embedding",
                RespireVectorDataFloat32.Encode(new float[] { 1, 0 }), 20, 3) { RrfWindow = 20, LoadFields = ["__key", "__score"] };
            var all = await Collect(hybrid.HybridSearchAsync(new float[] { 1, 0 }, ["Grapes"], 3));
            if (all.Count != records.Length)
            {
                // Preserve the failed assertion while capturing a subsequent server probe for diagnosis.
                try
                {
                    var probe = await client.Search.HybridSearchAsync(store.IndexName(collection.Name), query);
                    Console.WriteLine($"Hybrid probe: total={probe.Total}, keys={string.Join(", ", probe.Documents.Select(document => document.Id))}, warnings={string.Join(", ", probe.Warnings)}");
                    var info = await client.Search.GetIndexInfoAsync(store.IndexName(collection.Name));
                    Console.WriteLine($"Index probe: {JsonSerializer.Serialize(info.Properties)}");
                }
                catch (Exception error)
                {
                    Console.WriteLine($"Hybrid failure diagnostics failed: {error}");
                }
            }
            all.Should().HaveCount(3);
            all[0].Record.Id.Should().Be("3");
            all.Select(hit => hit.Score!.Value).Should().BeInDescendingOrder();
            all.Should().OnlyContain(hit => hit.Record.Vector.IsEmpty);
            var raw = await client.Search.HybridSearchAsync(store.IndexName(collection.Name), query);
            all.Select(hit => hit.Score).Should().Equal(raw.Documents.Select(document => document.Score));
            var top = await Collect(hybrid.HybridSearchAsync(new float[] { 1, 0 }, ["Oranges"], 1));
            top.Should().ContainSingle().Which.Record.Id.Should().Be("2");
            var page = await Collect(hybrid.HybridSearchAsync(new float[] { 1, 0 }, ["healthy"], 3, new() { Skip = 2, IncludeVectors = true }));
            page.Should().ContainSingle().Which.Record.Id.Should().Be("2");
            page[0].Record.Vector.ToArray().Should().Equal(1.1f, 0);
            var filtered = await Collect(hybrid.HybridSearchAsync(new float[] { 1, 0 }, ["Grapes"], 3, new() { Filter = movie => movie.Tag == "allowed" }));
            filtered.Should().HaveCount(2).And.OnlyContain(hit => hit.Record.Tag == "allowed");
            var mismatch = await Collect(hybrid.HybridSearchAsync(new float[] { 1, 0 }, ["Oranges"], 3, new() { Filter = movie => movie.Title == "Apples are a healthy and nourishing snack" }));
            mismatch.Should().ContainSingle().Which.Record.Id.Should().Be("1");
        }
        finally { await collection.EnsureCollectionDeletedAsync(); }
    }

    [Test, Arguments(2, 84), Arguments(3, 84), Arguments(2, 810), Arguments(3, 810)]
    public async Task HybridMultipleKeywordsAndEscaping(int protocol, int version)
    {
        await using var client = await Connect(protocol, version);
        using var store = new RespireVectorStore(client, Guid.NewGuid().ToString("N"));
        store.RegisterMapper(new MovieMapper());
        using var collection = store.GetHashCollection<Movie>("hybrid");
        try
        {
            await collection.EnsureCollectionExistsAsync();
            // Distinct text relevance and vector distances keep the reused threshold stable across queries.
            await collection.UpsertAsync(new Movie[] { new("1", "nourishing nourishing", new float[] { 1, 0 }), new("2", "tangy", new float[] { 1.1f, 0 }), new("3", "grapes", new float[] { 1.2f, 0 }) });
            await WaitForIndex(collection, 3);
            var results = await Collect(collection.HybridSearchAsync(new float[] { 1, 0 }, ["tangy", "nourishing"], 3));
            results.Take(2).Select(hit => hit.Record.Id).Should().BeEquivalentTo(new[] { "1", "2" });
            results[2].Record.Id.Should().Be("3");
            var injected = await Collect(collection.HybridSearchAsync(new float[] { 1, 0 }, ["\") | * | (\""], 3));
            injected.Should().HaveCount(3); // Escaped text has no matches; the vector leg still matches all three.
            injected.Should().OnlyContain(hit => hit.Score < results[0].Score);
            var threshold = await Collect(collection.HybridSearchAsync(new float[] { 1, 0 }, ["tangy", "nourishing"], 3,
                new() { ScoreThreshold = results[1].Score }));
            threshold.Select(hit => hit.Record.Id).Should().BeEquivalentTo(new[] { "1", "2" });
        }
        finally { await collection.EnsureCollectionDeletedAsync(); }
    }

    [Test, Arguments(2, 84), Arguments(3, 84), Arguments(2, 810), Arguments(3, 810)]
    public async Task JsonHybridSelectsNestedTextAndAlternateVector(int protocol, int version)
    {
        await using var client = await Connect(protocol, version);
        using var store = new RespireVectorStore(client, Guid.NewGuid().ToString("N"));
        store.RegisterMapper(new JsonMovieMapper());
        using var collection = store.GetJsonCollection<JsonMovie>("hybrid");
        try
        {
            await collection.EnsureCollectionExistsAsync();
            await collection.UpsertAsync(new JsonMovie[] { new("1", new("apples"), [1, 0], [0, 1]), new("2", new("oranges"), [0, 1], [1, 0]) });
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while ((await Collect(collection.SearchAsync(new float[] { 1, 0 }, 2, new() { VectorProperty = movie => movie.Vector }, deadline.Token))).Count < 2)
                await Task.Delay(20, deadline.Token);
            var hits = await Collect(collection.HybridSearchAsync(new float[] { 1, 0 }, ["oranges"], 2,
                new() { AdditionalProperty = movie => movie.Details.Title, VectorProperty = movie => movie.AlternateVector, IncludeVectors = true }));
            hits[0].Record.Id.Should().Be("2");
            hits[0].Record.AlternateVector.Should().Equal(1, 0);
            var without = await Collect(collection.HybridSearchAsync(new float[] { 1, 0 }, ["oranges"], 1, new() { VectorProperty = movie => movie.AlternateVector }));
            without[0].Record.Vector.Should().BeNull();
            without[0].Record.AlternateVector.Should().BeNull();
        }
        finally { await collection.EnsureCollectionDeletedAsync(); }
    }

    [Test, Arguments(2), Arguments(3)]
    public async Task MissingCommandHasCapabilityDiagnostic(int protocol)
    {
        await using var client = await Connect(protocol, 82);
        using var store = new RespireVectorStore(client);
        store.RegisterMapper(new MovieMapper());
        using var collection = store.GetHashCollection<Movie>("missing");
        Func<Task> search = () => Collect(collection.HybridSearchAsync(new float[] { 1, 0 }, ["apples"], 1));
        await search.Should().ThrowAsync<NotSupportedException>().WithMessage("*FT.HYBRID*8.4.0*");
        await client.PingAsync();
    }

    [Test, Arguments(2, 84), Arguments(3, 84), Arguments(2, 810), Arguments(3, 810)]
    public async Task LargeHybridPagesExpandFusionWindowAndBatchRetrieval(int protocol, int version)
    {
        await using var client = await Connect(protocol, version);
        using var store = new RespireVectorStore(client, Guid.NewGuid().ToString("N"));
        store.RegisterMapper(new MovieMapper());
        using var collection = store.GetHashCollection<Movie>("pages");
        try
        {
            await collection.EnsureCollectionExistsAsync();
            var records = Enumerable.Range(0, 40).Select(i => new Movie(i.ToString(), "plain", new float[] { i + 1, 0 })).ToArray();
            await collection.UpsertAsync(records);
            await WaitForIndex(collection, 40);
            var all = await Collect(collection.HybridSearchAsync(new float[] { 1, 0 }, ["unmatched"], 40, new() { IncludeVectors = true }));
            all.Select(hit => hit.Record.Id).Should().Equal(records.Select(record => record.Id));
            all.Should().OnlyContain(hit => hit.Record.Vector.Length == 2);
            var page = await Collect(collection.HybridSearchAsync(new float[] { 1, 0 }, ["unmatched"], 5, new() { Skip = 25 }));
            page.Select(hit => hit.Record.Id).Should().Equal("25", "26", "27", "28", "29");
            await using (var iterator = collection.HybridSearchAsync(new float[] { 1, 0 }, ["unmatched"], 40).GetAsyncEnumerator())
            {
                (await iterator.MoveNextAsync()).Should().BeTrue();
                iterator.Current.Record.Id.Should().Be("0");
            }
            await client.PingAsync();
        }
        finally { await collection.EnsureCollectionDeletedAsync(); }
    }

    [Test, Arguments(2, 84), Arguments(3, 84), Arguments(2, 810), Arguments(3, 810)]
    public async Task MultipleTextPropertiesRequireSelectionAndHonorExplicitProperties(int protocol, int version)
    {
        await using var client = await Connect(protocol, version);
        using var store = new RespireVectorStore(client, Guid.NewGuid().ToString("N"));
        store.RegisterMapper(new MultiTextMapper());
        using var collection = store.GetHashCollection<Movie>("multiple");
        Func<Task> ambiguous = () => Collect(collection.HybridSearchAsync(new float[] { 1, 0 }, ["Apples"], 3));
        await ambiguous.Should().ThrowAsync<InvalidOperationException>().WithMessage("*AdditionalProperty*");
        try
        {
            await collection.EnsureCollectionExistsAsync();
            await collection.UpsertAsync(new Movie[] { new("1", "Apples", new float[] { 1, 0 }, "Oranges"), new("2", "Oranges", new float[] { 1, 0 }, "Apples") });
            await WaitForIndex(collection, 2);
            var apples = await Collect(collection.HybridSearchAsync(new float[] { 1, 0 }, ["Apples"], 4, new() { AdditionalProperty = movie => movie.Tag }));
            apples.Select(hit => hit.Record.Id).Should().Equal("2", "1");
            var oranges = await Collect(collection.HybridSearchAsync(new float[] { 1, 0 }, ["Oranges"], 4, new() { AdditionalProperty = movie => movie.Tag, VectorProperty = movie => movie.Vector }));
            oranges.Select(hit => hit.Record.Id).Should().Equal("1", "2");
        }
        finally { await collection.EnsureCollectionDeletedAsync(); }
    }

    private sealed class MultiTextMapper : RespireVectorDataHashMapper<Movie>
    {
        private readonly MovieMapper _inner = new();
        public override IReadOnlyList<RespireVectorDataVectorField> VectorFields => _inner.VectorFields;
        public override IReadOnlyList<RespireSearchField> DataFields => [new("title", RespireSearchFieldType.Text), new("tag", RespireSearchFieldType.Text)];
        public override IReadOnlyList<RespireVectorDataTextField> TextFields => [new(nameof(Movie.Title), "title"), new(nameof(Movie.Tag), "tag")];
        public override string GetKey(Movie record) => _inner.GetKey(record);
        public override IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Write(Movie record) => _inner.Write(record);
        public override Movie Read(string key, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> fields) => _inner.Read(key, fields);
    }

    private async Task<RespireClient> Connect(int protocol, int version)
    {
        return await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.Connection(version)) with { Protocol = (RespProtocol)protocol });
    }

    private static async Task WaitForIndex(RespireVectorStoreCollection<Movie> collection, int count)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while ((await Collect(collection.SearchAsync(new float[] { 1, 0 }, count, cancellationToken: deadline.Token))).Count < count)
            await Task.Delay(20, deadline.Token);
    }

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> values)
    {
        var result = new List<T>();
        await foreach (var value in values) result.Add(value);
        return result;
    }
}
