using FluentAssertions;
using Microsoft.Extensions.VectorData;
using Respire.IntegrationTests;
using Respire.Samples.VectorData;
using Respire.VectorData;
using TUnit.Core;

namespace Respire.VectorData.Tests;

// These cover the supported collection/search contracts. Full official conformance remains #1262.
// Lifecycle/metadata contracts match dotnet/extensions CollectionManagementTests at
// 02107c65bab30aad9e35b5133ed643eaa77bccd8 (the 10.10.0 package source revision).
[ClassDataSource<ModernRedisTestContainer>(Shared = SharedType.PerTestSession)]
public class HashCollectionTests(ModernRedisTestContainer fixture)
{
    [Test, Arguments(2), Arguments(3)]
    public async Task LifecycleAndStoreNamespaces(int protocol)
    {
        await using var client = await Connect(protocol);
        using var store = Store(client);
        using var collection = store.GetHashCollection<Movie>("映画:a:b");
        (await collection.CollectionExistsAsync()).Should().BeFalse();
        await collection.EnsureCollectionExistsAsync();
        await collection.EnsureCollectionExistsAsync();
        (await store.CollectionExistsAsync(collection.Name)).Should().BeTrue();
        (await Collect(store.ListCollectionNamesAsync())).Should().ContainSingle().Which.Should().Be(collection.Name);
        await collection.UpsertAsync(Movie("id:1", 1, 0));
        await store.EnsureCollectionDeletedAsync(collection.Name);
        await collection.EnsureCollectionDeletedAsync();
        (await collection.GetAsync("id:1")).Should().BeNull();
        (await Collect(store.ListCollectionNamesAsync())).Should().BeEmpty();
    }

    [Test, Arguments(2), Arguments(3)]
    public async Task DeletingWithoutAnIndexRemovesOnlyTheCollectionsHashes(int protocol)
    {
        await using var client = await Connect(protocol);
        using var store = Store(client);
        using var collection = store.GetHashCollection<Movie>("movies");
        using var other = store.GetHashCollection<Movie>("movies:other");
        try
        {
            await collection.UpsertAsync(Movie("before-index", 1, 0));
            await other.UpsertAsync(Movie("before-index", 0, 1));
            await collection.EnsureCollectionDeletedAsync();
            (await collection.GetAsync("before-index")).Should().BeNull();
            (await other.GetAsync("before-index")).Should().NotBeNull();

            await collection.UpsertAsync(Movie("before-index", 1, 0));
            await store.EnsureCollectionDeletedAsync(collection.Name);
            (await collection.GetAsync("before-index")).Should().BeNull();
            (await other.GetAsync("before-index")).Should().NotBeNull();
            await collection.EnsureCollectionExistsAsync();
            (await Collect(collection.SearchAsync(new float[] { 1, 0 }, 1))).Should().BeEmpty();
        }
        finally
        {
            await collection.DeleteAsync("before-index");
            await other.DeleteAsync("before-index");
            await collection.EnsureCollectionDeletedAsync();
        }
    }

    [Test, Arguments(2), Arguments(3)]
    public async Task CrudBatchReplacementAndVectorInclusion(int protocol)
    {
        await using var client = await Connect(protocol);
        using var store = Store(client);
        using var collection = store.GetHashCollection<Movie>("movies");
        try
        {
            await collection.EnsureCollectionExistsAsync();
            (await collection.GetAsync("missing")).Should().BeNull();
            await collection.UpsertAsync(new[] { Movie("a", 1, 0) with { Tag = "old" }, Movie("b", 0, 1) });
            var first = (await collection.GetAsync("a", new() { IncludeVectors = true }))!;
            first.Vector.ToArray().Should().Equal(1, 0);
            first.Tag.Should().Be("old");
            (await collection.GetAsync("a"))!.Vector.IsEmpty.Should().BeTrue();
            await collection.UpsertAsync(Movie("a", -1, 0) with { Title = "new" });
            var updated = (await collection.GetAsync("a", new() { IncludeVectors = true }))!;
            updated.Title.Should().Be("new");
            updated.Tag.Should().BeNull();
            updated.Vector.ToArray().Should().Equal(-1, 0);
            (await Collect(collection.GetAsync(new[] { "a", "missing", "b" }))).Select(m => m.Id).Should().Equal("a", "b");
            await collection.DeleteAsync(new[] { "a", "b", "missing" });
            (await collection.GetAsync("a")).Should().BeNull();
            first.Vector.ToArray().Should().Equal(1, 0); // Owned after later pooled replies.
        }
        finally { await collection.EnsureCollectionDeletedAsync(); }
    }

    [Test, Arguments(2), Arguments(3)]
    public async Task KnnSearchReturnsDistancesPagingAndSelectedVectors(int protocol)
    {
        await using var client = await Connect(protocol);
        using var store = Store(client);
        using var collection = store.GetHashCollection<Movie>("movies");
        try
        {
            await collection.EnsureCollectionExistsAsync();
            await collection.UpsertAsync(new[] { Movie("a", 1, 0), Movie("b", 0, 1), Movie("c", -1, 0) });
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            List<VectorSearchResult<Movie>> all;
            do
            {
                all = await Collect(collection.SearchAsync(new float[] { 1, 0 }, 3, cancellationToken: deadline.Token));
                if (all.Count < 3) await Task.Delay(20, deadline.Token);
            } while (all.Count < 3);
            all.Select(r => r.Record.Id).Should().Equal("a", "b", "c");
            all.Select(r => r.Score).Should().Equal(0, 2, 4);
            all.Should().OnlyContain(r => r.Record.Vector.IsEmpty);
            var page = await Collect(collection.SearchAsync(new ReadOnlyMemory<float>(new float[] { 1, 0 }), 1, new() { Skip = 1, IncludeVectors = true, VectorProperty = movie => movie.Vector }));
            page.Should().ContainSingle().Which.Record.Id.Should().Be("b");
            page[0].Record.Vector.ToArray().Should().Equal(0, 1);
            (await Collect(collection.SearchAsync(new float[] { 1, 0 }, 3, new() { ScoreThreshold = 2.1 }))).Select(r => r.Record.Id).Should().Equal("a", "b");
        }
        finally { await collection.EnsureCollectionDeletedAsync(); }
    }

    [Test, Arguments(2), Arguments(3)]
    public async Task SearchBatchesPreserveOrderVectorsAndEarlyDisposal(int protocol)
    {
        await using var client = await Connect(protocol);
        using var store = Store(client);
        using var collection = store.GetHashCollection<Movie>("movies");
        try
        {
            await collection.EnsureCollectionExistsAsync();
            var movies = Enumerable.Range(0, 40).Select(i => Movie(i.ToString(), i + 1, 0)).ToArray();
            await collection.UpsertAsync(movies);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            List<VectorSearchResult<Movie>> hits;
            do
            {
                hits = await Collect(collection.SearchAsync(new float[] { 1, 0 }, 40, new() { IncludeVectors = true }, deadline.Token));
                if (hits.Count < 40) await Task.Delay(20, deadline.Token);
            } while (hits.Count < 40);
            hits.Select(hit => hit.Record.Id).Should().Equal(movies.Select(movie => movie.Id));
            for (var i = 0; i < hits.Count; i++)
            {
                hits[i].Score.Should().Be(i * i);
                hits[i].Record.Vector.ToArray().Should().Equal(i + 1, 0);
            }
            await using (var iterator = collection.SearchAsync(new float[] { 1, 0 }, 40).GetAsyncEnumerator())
            {
                (await iterator.MoveNextAsync()).Should().BeTrue();
                iterator.Current.Record.Id.Should().Be("0");
            }
            await client.PingAsync();
        }
        finally { await collection.EnsureCollectionDeletedAsync(); }
    }

    [Test, Arguments(2), Arguments(3)]
    public async Task CollectionsDoNotShareDocumentsAndDisposalDoesNotCloseClient(int protocol)
    {
        await using var client = await Connect(protocol);
        using var firstStore = Store(client);
        using var secondStore = Store(client);
        using var first = firstStore.GetHashCollection<Movie>("same");
        using var second = secondStore.GetHashCollection<Movie>("same");
        try
        {
            await first.EnsureCollectionExistsAsync();
            await second.EnsureCollectionExistsAsync();
            await first.UpsertAsync(Movie("same", 1, 0));
            (await second.GetAsync("same")).Should().BeNull();
            await first.EnsureCollectionDeletedAsync();
            (await second.CollectionExistsAsync()).Should().BeTrue();
        }
        finally { await second.EnsureCollectionDeletedAsync(); }
        first.Dispose();
        firstStore.Dispose();
        await client.PingAsync();
        var disposed = () => first.GetAsync("same");
        await disposed.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Test, Arguments(2), Arguments(3)]
    public async Task UnsupportedSearchContractsAndInvalidVectorsFail(int protocol)
    {
        await using var client = await Connect(protocol);
        using var store = Store(client);
        using var collection = store.GetHashCollection<Movie>("movies");
        Func<Task> filter = () => Collect(collection.SearchAsync(new float[] { 1, 0 }, 1, new() { Filter = m => m.Id == "a" }));
        await filter.Should().ThrowAsync<NotSupportedException>();
        Func<Task> input = () => Collect(collection.SearchAsync("generate embedding", 1));
        await input.Should().ThrowAsync<NotSupportedException>();
        Func<Task> dimensions = () => collection.UpsertAsync(new Movie("a", "bad", new float[] { 1 }));
        await dimensions.Should().ThrowAsync<ArgumentException>();
        Func<Task> queryDimensions = () => Collect(collection.SearchAsync(new float[] { 1 }, 1));
        await queryDimensions.Should().ThrowAsync<ArgumentException>();
        Func<Task> wrongElementType = () => Collect(collection.SearchAsync(new double[] { 1, 0 }, 1));
        await wrongElementType.Should().ThrowAsync<NotSupportedException>();
        Func<Task> nan = () => collection.UpsertAsync(new Movie("a", "bad", new float[] { float.NaN, 0 }));
        await nan.Should().ThrowAsync<ArgumentException>();
        Func<Task> selector = () => Collect(collection.SearchAsync(new float[] { 1, 0 }, 1, new() { VectorProperty = m => m.Title }));
        await selector.Should().ThrowAsync<ArgumentException>();
        Func<Task> top = () => Collect(collection.SearchAsync(new float[] { 1, 0 }, 0));
        await top.Should().ThrowAsync<ArgumentOutOfRangeException>();
        Func<Task> threshold = () => Collect(collection.SearchAsync(new float[] { 1, 0 }, 1, new() { ScoreThreshold = double.NaN }));
        await threshold.Should().ThrowAsync<ArgumentOutOfRangeException>();
        Func<Task> overflow = () => Collect(collection.SearchAsync(new float[] { 1, 0 }, 1, new() { Skip = int.MaxValue }));
        await overflow.Should().ThrowAsync<OverflowException>();
        var filteredGet = () => collection.GetAsync(movie => movie.Id == "a", 1);
        filteredGet.Should().Throw<NotSupportedException>();
    }

    [Test, Arguments(2), Arguments(3)]
    public async Task MultipleVectorModelsRequireAnExplicitProperty(int protocol)
    {
        await using var client = await Connect(protocol);
        using var store = Store(client);
        store.RegisterMapper(new TwinMapper());
        using var collection = store.GetHashCollection<TwinMovie>("two-vectors");
        try
        {
            await collection.EnsureCollectionExistsAsync();
            await collection.UpsertAsync(new[]
            {
                new TwinMovie("a", new float[] { 1, 0 }, new float[] { 0, 1 }),
                new TwinMovie("b", new float[] { 0, 1 }, new float[] { 1, 0 }),
            });
            Func<Task> ambiguous = () => Collect(collection.SearchAsync(new float[] { 1, 0 }, 1));
            await ambiguous.Should().ThrowAsync<InvalidOperationException>();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            List<VectorSearchResult<TwinMovie>> hits;
            do
            {
                hits = await Collect(collection.SearchAsync(new float[] { 1, 0 }, 2, new() { VectorProperty = m => m.Second, IncludeVectors = true }, deadline.Token));
                if (hits.Count < 2) await Task.Delay(20, deadline.Token);
            } while (hits.Count < 2);
            hits.Select(hit => hit.Record.Id).Should().Equal("b", "a");
            hits[0].Record.Second.ToArray().Should().Equal(1, 0);
            hits[0].Record.First.ToArray().Should().Equal(0, 1);
            var first = await Collect(collection.SearchAsync(new float[] { 1, 0 }, 1, new() { VectorProperty = m => m.First }));
            first.Should().ContainSingle().Which.Record.Id.Should().Be("a");
        }
        finally { await collection.EnsureCollectionDeletedAsync(); }
    }

    private sealed record TwinMovie(string Id, ReadOnlyMemory<float> First, ReadOnlyMemory<float> Second);

    private sealed class TwinMapper : RespireVectorDataHashMapper<TwinMovie>
    {
        public override IReadOnlyList<RespireVectorDataVectorField> VectorFields { get; } =
            [new(nameof(TwinMovie.First), "first", 2), new(nameof(TwinMovie.Second), "second", 2)];
        public override string GetKey(TwinMovie record) => record.Id;
        public override IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Write(TwinMovie record) => new Dictionary<string, ReadOnlyMemory<byte>>
        {
            ["first"] = RespireVectorDataFloat32.Encode(record.First.Span),
            ["second"] = RespireVectorDataFloat32.Encode(record.Second.Span),
        };
        public override TwinMovie Read(string key, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> fields)
            => new(key, fields.TryGetValue("first", out var first) ? RespireVectorDataFloat32.Decode(first.Span) : default,
                fields.TryGetValue("second", out var second) ? RespireVectorDataFloat32.Decode(second.Span) : default);
    }

    [Test, Arguments(2), Arguments(3)]
    public async Task CancellationAndServerErrorsArePreserved(int protocol)
    {
        await using var client = await Connect(protocol);
        using var store = Store(client);
        using var collection = store.GetHashCollection<Movie>("missing-index");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Func<Task> cancellation = () => collection.GetAsync("a", cancellationToken: cancelled.Token);
        await cancellation.Should().ThrowAsync<OperationCanceledException>();
        Func<Task> missingIndex = () => Collect(collection.SearchAsync(new float[] { 1, 0 }, 1));
        var failure = (await missingIndex.Should().ThrowAsync<VectorStoreException>()).Which;
        failure.InnerException.Should().BeOfType<RespireServerException>();
        failure.VectorStoreSystemName.Should().Be("redis");
        failure.CollectionName.Should().Be("missing-index");
        failure.OperationName.Should().Be("SearchAsync");
        await client.PingAsync();
    }

    [Test]
    public void FloatEncodingIsPortableAndRejectsInvalidBytes()
    {
        RespireVectorDataFloat32.Encode(new float[] { 1 }).Should().Equal(0, 0, 128, 63);
        RespireVectorDataFloat32.Decode(RespireVectorDataFloat32.Encode(new float[] { -2, 3 })).ToArray().Should().Equal(-2, 3);
        var invalid = () => RespireVectorDataFloat32.Decode(new byte[3]);
        invalid.Should().Throw<ArgumentException>();
    }

    [Test, Arguments(0), Arguments(-1)]
    public async Task InvalidSchemaFailsBeforeCollectionIo(int dimensions)
    {
        await using var client = await Connect(3);
        using var store = new RespireVectorStore(client);
        store.RegisterMapper(new InvalidMapper(new(nameof(Respire.Samples.VectorData.Movie.Vector), "embedding", dimensions)));
        var create = () => store.GetHashCollection<Movie>("movies");
        create.Should().Throw<ArgumentException>();
        await client.PingAsync();
    }

    [Test]
    public async Task CollectionMetadataAndUnderlyingClientAreAvailable()
    {
        await using var client = await Connect(3);
        using var store = Store(client);
        using var collection = store.GetHashCollection<Movie>("movies");
        var metadata = (VectorStoreCollectionMetadata)collection.GetService(typeof(VectorStoreCollectionMetadata))!;
        metadata.VectorStoreSystemName.Should().Be("redis");
        metadata.CollectionName.Should().Be("movies");
        ((VectorStoreMetadata)store.GetService(typeof(VectorStoreMetadata))!).VectorStoreSystemName.Should().Be("redis");
        collection.GetService(typeof(IRespireClient)).Should().BeSameAs(client);
        store.GetService(typeof(IRespireClient)).Should().BeSameAs(client);
        collection.GetService(typeof(IRespireClient), "other").Should().BeNull();
    }

    [Test, Arguments("bad name"), Arguments("vector_score"), Arguments("embedding; DROP")]
    public async Task UnsafeSchemaNamesCannotEnterQuerySyntax(string field)
    {
        await using var client = await Connect(3);
        using var store = new RespireVectorStore(client);
        store.RegisterMapper(new InvalidMapper(new(nameof(Respire.Samples.VectorData.Movie.Vector), field, 2)));
        var create = () => store.GetHashCollection<Movie>("movies");
        create.Should().Throw<ArgumentException>();
    }

    private sealed class InvalidMapper(RespireVectorDataVectorField vector) : RespireVectorDataHashMapper<Movie>
    {
        public override IReadOnlyList<RespireVectorDataVectorField> VectorFields => [vector];
        public override string GetKey(Movie record) => record.Id;
        public override IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Write(Movie record) => throw new NotSupportedException();
        public override Movie Read(string key, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> fields) => throw new NotSupportedException();
    }

    private static Movie Movie(string key, float x, float y) => new(key, "Title " + key, new float[] { x, y });
    private static RespireVectorStore Store(IRespireClient client)
    {
        var store = new RespireVectorStore(client, "test:vector:" + Guid.NewGuid().ToString("N") + ":");
        store.RegisterMapper(new MovieMapper());
        return store;
    }

    private ValueTask<RespireClient> Connect(int protocol)
        => RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespProtocol)protocol });

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> values)
    {
        var items = new List<T>();
        await foreach (var value in values) items.Add(value);
        return items;
    }
}
