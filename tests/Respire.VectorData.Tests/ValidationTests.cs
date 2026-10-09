using FluentAssertions;
using Microsoft.Extensions.VectorData;
using Respire.Samples.VectorData;
using Respire.Search;
using TUnit.Core;

namespace Respire.VectorData.Tests;

public class ValidationTests
{
    [Test, Arguments("%"), Arguments("a"), Arguments("_w")]
    public void MalformedSearchNamesHaveStoreDiagnostics(string encoded)
    {
        var read = () => RespireVectorDataOperations.DecodeName(encoded, "SearchAsync", "movies");
        var error = read.Should().Throw<VectorStoreException>().Which;
        error.InnerException.Should().Match<Exception>(cause => cause is FormatException || cause is ArgumentException);
        error.CollectionName.Should().Be("movies");
        error.OperationName.Should().Be("SearchAsync");
        error.VectorStoreSystemName.Should().Be("redis");
    }

    [Test, Arguments("id:1"), Arguments("映画:a:b")]
    public void EncodedSearchNamesRoundTrip(string name)
    {
        var encoded = RespireVectorStore.EncodeName(name);
        RespireVectorDataOperations.DecodeName(encoded, "SearchAsync", "movies").Should().Be(name);
    }

    [Test, Arguments(null), Arguments(""), Arguments("invalid"), Arguments("NaN"), Arguments("Infinity")]
    public void InvalidSearchScoresHaveStoreDiagnostics(string? score)
    {
        var document = new RespireSearchDocument("movies:key", new Dictionary<string, string?> { ["vector_score"] = score });
        var read = () => RespireVectorDataOperations.ReadSearchScore(document, "movies:", "movies");
        var error = read.Should().Throw<VectorStoreException>().Which;
        error.InnerException.Should().BeOfType<InvalidOperationException>();
        error.CollectionName.Should().Be("movies");
        error.OperationName.Should().Be("SearchAsync");
        error.VectorStoreSystemName.Should().Be("redis");
    }

    [Test]
    public void MissingSearchScoreHasStoreDiagnostics()
    {
        var document = new RespireSearchDocument("movies:key", new Dictionary<string, string?>());
        var read = () => RespireVectorDataOperations.ReadSearchScore(document, "movies:", "movies");
        read.Should().Throw<VectorStoreException>().WithInnerException<InvalidOperationException>();
    }

    [Test]
    public void SearchDocumentOutsideCollectionHasStoreDiagnostics()
    {
        var document = new RespireSearchDocument("other:key", new Dictionary<string, string?> { ["vector_score"] = "0.5" });
        var read = () => RespireVectorDataOperations.ReadSearchScore(document, "movies:", "movies");
        read.Should().Throw<VectorStoreException>().WithInnerException<InvalidOperationException>();
    }

    [Test]
    public void SearchScoreUsesInvariantCulture()
    {
        var document = new RespireSearchDocument("movies:key", new Dictionary<string, string?> { ["vector_score"] = "1.25e-2" });
        RespireVectorDataOperations.ReadSearchScore(document, "movies:", "movies").Should().Be(0.0125);
    }

    // A lazy, unreachable client proves validation does not require server dispatch.
    private static RespireClient Client() => RespireClient.Create("redis://127.0.0.1:1");

    [Test]
    public async Task NullRecordKeyUsesUpstreamParameterName()
    {
        await using var client = Client();
        using var store = Store(client);
        using var collection = store.GetHashCollection<Movie>("movies");
        Func<Task> get = () => collection.GetAsync((string)null!);
        (await get.Should().ThrowAsync<ArgumentNullException>()).Which.ParamName.Should().Be("key");
    }

    [Test]
    public async Task EmptyBatchesHonorCancellation()
    {
        await using var client = Client();
        using var store = Store(client);
        using var collection = store.GetHashCollection<Movie>("movies");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Func<Task> get = () => Collect(collection.GetAsync(Array.Empty<string>(), cancellationToken: cancelled.Token));
        Func<Task> delete = () => collection.DeleteAsync(Array.Empty<string>(), cancelled.Token);
        Func<Task> upsert = () => collection.UpsertAsync(Array.Empty<Movie>(), cancelled.Token);
        await get.Should().ThrowAsync<OperationCanceledException>();
        await delete.Should().ThrowAsync<OperationCanceledException>();
        await upsert.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test, Arguments(0), Arguments(1), Arguments(2), Arguments(3)]
    public async Task InvalidScalarSchemaFailsBeforeCollectionIo(int variant)
    {
        await using var client = Client();
        var field = variant switch
        {
            0 => new RespireSearchField("title", (RespireSearchFieldType)999),
            1 => new RespireSearchField("title", RespireSearchFieldType.Text, Options: ["AS", "bad name"]),
            2 => new RespireSearchField("title", RespireSearchFieldType.Numeric) { Weight = 1 },
            _ => new RespireSearchField("title", RespireSearchFieldType.Text) { Weight = double.NaN },
        };
        using var store = new RespireVectorStore(client);
        store.RegisterMapper(new ScalarMapper(field));
        var create = () => store.GetHashCollection<Movie>("movies");
        create.Should().Throw<ArgumentException>();
    }

    [Test]
    public async Task SortableGeoShapeFailsBeforeCollectionIo()
    {
        await using var client = Client();
        using var store = new RespireVectorStore(client);
        store.RegisterMapper(new ScalarMapper(new("shape", RespireSearchFieldType.GeoShape, Sortable: true)));
        var create = () => store.GetHashCollection<Movie>("movies");
        create.Should().Throw<ArgumentException>().WithMessage("*GEOSHAPE*SORTABLE*").Which.ParamName.Should().Be("field");
    }

    [Test, Arguments(0), Arguments(31), Arguments(127), Arguments(128), Arguments(233), Arguments(0xD800), Arguments(0xFFFF)]
    public async Task InvalidTagSeparatorsFailBeforeCollectionIo(int separator)
    {
        await using var client = Client();
        using var store = new RespireVectorStore(client);
        store.RegisterMapper(new ScalarMapper(new("tags", RespireSearchFieldType.Tag) { Separator = (char)separator }));
        var create = () => store.GetHashCollection<Movie>("movies");
        create.Should().Throw<ArgumentException>().WithMessage("*TAG*printable ASCII*").Which.ParamName.Should().Be("field");
    }

    [Test]
    public async Task PrintableAsciiAndDefaultTagSeparatorsPassBeforeCollectionIo()
    {
        await using var client = Client();
        for (var separator = 32; separator <= 126; separator++)
        {
            using var store = new RespireVectorStore(client);
            store.RegisterMapper(new ScalarMapper(new("tags", RespireSearchFieldType.Tag) { Separator = (char)separator }));
            using var collection = store.GetHashCollection<Movie>("movies");
            collection.Name.Should().Be("movies");
        }
        using var defaultStore = new RespireVectorStore(client);
        defaultStore.RegisterMapper(new ScalarMapper(new("tags", RespireSearchFieldType.Tag)));
        using var defaultCollection = defaultStore.GetHashCollection<Movie>("movies");
        defaultCollection.Name.Should().Be("movies");
    }

    [Test]
    [Arguments(RespireSearchFieldType.GeoShape, false)]
    [Arguments(RespireSearchFieldType.Text, true)]
    [Arguments(RespireSearchFieldType.Tag, true)]
    [Arguments(RespireSearchFieldType.Numeric, true)]
    [Arguments(RespireSearchFieldType.Geo, true)]
    public async Task SupportedScalarSortOptionsPassBeforeCollectionIo(RespireSearchFieldType type, bool sortable)
    {
        await using var client = Client();
        using var store = new RespireVectorStore(client);
        store.RegisterMapper(new ScalarMapper(new("scalar", type, Sortable: sortable)));
        using var collection = store.GetHashCollection<Movie>("movies");
        collection.Name.Should().Be("movies");
    }

    [Test]
    public async Task UnsupportedKeysAndDefinitionsFailBeforeCollectionIo()
    {
        await using var client = Client();
        using var store = Store(client);
        var keyType = () => store.GetCollection<int, Movie>("movies");
        var definition = () => store.GetCollection<string, Movie>("movies", new());
        var dynamicCollection = () => store.GetDynamicCollection("movies", new());
        keyType.Should().Throw<NotSupportedException>();
        definition.Should().Throw<NotSupportedException>();
        dynamicCollection.Should().Throw<NotSupportedException>();
        using var collection = store.GetCollection<string, Movie>("movies");
        collection.Name.Should().Be("movies");
    }

    // BasicModelTests null/empty batch contracts from the upstream revision named in UpstreamContractTests.
    [Test]
    public async Task NullAndEmptyBatchContracts()
    {
        await using var client = Client();
        using var store = Store(client);
        using var collection = store.GetHashCollection<Movie>("movies");
        Func<Task> get = () => Collect(collection.GetAsync(keys: null!));
        Func<Task> delete = () => collection.DeleteAsync(keys: null!);
        Func<Task> upsert = () => collection.UpsertAsync(records: null!);
        (await get.Should().ThrowAsync<ArgumentNullException>()).Which.ParamName.Should().Be("keys");
        (await delete.Should().ThrowAsync<ArgumentNullException>()).Which.ParamName.Should().Be("keys");
        (await upsert.Should().ThrowAsync<ArgumentNullException>()).Which.ParamName.Should().Be("records");
        await Collect(collection.GetAsync(Array.Empty<string>()));
        await collection.DeleteAsync(Array.Empty<string>());
        await collection.UpsertAsync(Array.Empty<Movie>());
    }

    private sealed class ScalarMapper(RespireSearchField scalarField) : RespireVectorDataHashMapper<Movie>
    {
        public override IReadOnlyList<RespireSearchField> DataFields => [scalarField];
        public override IReadOnlyList<RespireVectorDataVectorField> VectorFields => [new(nameof(Movie.Vector), "embedding", 2)];
        public override string GetKey(Movie record) => record.Id;
        public override IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Write(Movie record) => throw new NotSupportedException();
        public override Movie Read(string key, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> fields) => throw new NotSupportedException();
    }

    private static RespireVectorStore Store(IRespireClient client)
    {
        var store = new RespireVectorStore(client);
        store.RegisterMapper(new MovieMapper());
        return store;
    }

    private static async Task Collect<T>(IAsyncEnumerable<T> values)
    {
        await foreach (var value in values) { }
    }
}
