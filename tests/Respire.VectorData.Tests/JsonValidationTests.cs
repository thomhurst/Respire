using FluentAssertions;
using Respire.Samples.VectorData;
using Respire.Search;
using TUnit.Core;

namespace Respire.VectorData.Tests;

public class JsonValidationTests
{
    [Test, Arguments("$"), Arguments("$.embedding[*]"), Arguments("$.details..embedding"), Arguments("embedding")]
    public async Task InvalidPathsFailBeforeIo(string path)
    {
        await using var client = RespireClient.Create("redis://127.0.0.1:1");
        using var store = new RespireVectorStore(client);
        store.RegisterMapper(new SchemaMapper(path, "vector"));
        var create = () => store.GetJsonCollection<JsonMovie>("movies");
        create.Should().Throw<ArgumentException>();
    }

    [Test, Arguments(null), Arguments("vector_score"), Arguments("bad alias")]
    public async Task MissingOrInvalidScalarAliasesFailBeforeIo(string? alias)
    {
        await using var client = RespireClient.Create("redis://127.0.0.1:1");
        using var store = new RespireVectorStore(client);
        store.RegisterMapper(new SchemaMapper("$.embedding", alias, scalar: true));
        var create = () => store.GetJsonCollection<JsonMovie>("movies");
        create.Should().Throw<ArgumentException>();
    }

    [Test]
    public async Task StorageSelectionRejectsWrongMapperKind()
    {
        await using var client = RespireClient.Create("redis://127.0.0.1:1");
        using var store = new RespireVectorStore(client);
        store.RegisterMapper(new JsonMovieMapper());
        store.RegisterMapper(new MovieMapper());
        var getHash = () => store.GetHashCollection<JsonMovie>("movies");
        var getJson = () => store.GetJsonCollection<Movie>("movies");
        getHash.Should().Throw<InvalidOperationException>();
        getJson.Should().Throw<InvalidOperationException>();
        using var json = store.GetCollection<string, JsonMovie>("movies");
        json.Name.Should().Be("movies");
    }

    [Test]
    public async Task JsonExpressionFiltersRejectBeforeIo()
    {
        await using var client = RespireClient.Create("redis://127.0.0.1:1");
        using var store = new RespireVectorStore(client);
        store.RegisterMapper(new JsonMovieMapper());
        using var collection = store.GetJsonCollection<JsonMovie>("movies");
        Func<Task> retrieval = () => Collect(collection.GetAsync(movie => true, top: 1));
        Func<Task> search = () => Collect(collection.SearchAsync(new float[] { 1, 0 }, 1,
            new() { VectorProperty = movie => movie.Vector, Filter = movie => true }));
        await retrieval.Should().ThrowAsync<NotSupportedException>().WithMessage("*JSON storage*");
        await search.Should().ThrowAsync<NotSupportedException>().WithMessage("*JSON storage*");
    }

    // Null/empty batch assertions preserve BasicModelTests contracts at the revision in JsonCollectionTests.
    [Test]
    public async Task NullAndEmptyBatchContractsAndCancellation()
    {
        await using var client = RespireClient.Create("redis://127.0.0.1:1");
        using var store = new RespireVectorStore(client);
        store.RegisterMapper(new JsonMovieMapper());
        using var collection = store.GetJsonCollection<JsonMovie>("movies");
        Func<Task> getNullKey = () => collection.GetAsync((string)null!);
        (await getNullKey.Should().ThrowAsync<ArgumentNullException>()).Which.ParamName.Should().Be("key");
        Func<Task> getNull = () => Collect(collection.GetAsync(keys: null!));
        Func<Task> upsertNull = () => collection.UpsertAsync(records: null!);
        Func<Task> deleteNull = () => collection.DeleteAsync(keys: null!);
        (await getNull.Should().ThrowAsync<ArgumentNullException>()).Which.ParamName.Should().Be("keys");
        (await upsertNull.Should().ThrowAsync<ArgumentNullException>()).Which.ParamName.Should().Be("records");
        (await deleteNull.Should().ThrowAsync<ArgumentNullException>()).Which.ParamName.Should().Be("keys");
        await Collect(collection.GetAsync(Array.Empty<string>()));
        await collection.UpsertAsync(Array.Empty<JsonMovie>());
        await collection.DeleteAsync(Array.Empty<string>());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Func<Task> get = () => Collect(collection.GetAsync(Array.Empty<string>(), cancellationToken: cancellation.Token));
        Func<Task> upsert = () => collection.UpsertAsync(Array.Empty<JsonMovie>(), cancellation.Token);
        Func<Task> delete = () => collection.DeleteAsync(Array.Empty<string>(), cancellation.Token);
        await get.Should().ThrowAsync<OperationCanceledException>();
        await upsert.Should().ThrowAsync<OperationCanceledException>();
        await delete.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test, Arguments("{\"embedding\":\"AACAPwAAAAA=\"}"), Arguments("{\"embedding\":[1]}"), Arguments("{\"embedding\":[1,1e50]}"), Arguments("{\"embedding\":[1,\"0\"]}")]
    public void InvalidRepresentationRejected(string json)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        var validate = () => RespireVectorDataJsonPaths.ValidateVectors(bytes, [new("Vector", "embedding", 2)], [["embedding"]]);
        validate.Should().Throw<ArgumentException>();
    }

    [Test]
    public void NullSerializerMetadataRejected()
    {
        var create = () => new SchemaMapper("$.embedding", "vector", metadataMissing: true);
        create.Should().Throw<ArgumentNullException>();
    }

    private sealed class SchemaMapper(string path, string? alias, bool scalar = false, bool metadataMissing = false)
        : RespireVectorDataJsonMapper<JsonMovie>(metadataMissing ? null! : MovieJsonContext.Default.JsonMovie)
    {
        public override IReadOnlyList<RespireVectorDataVectorField> VectorFields => [new("Vector", "vector", 2) { JsonPath = path }];
        public override IReadOnlyList<RespireSearchField> DataFields => scalar ? [new("$.details.movie_title", RespireSearchFieldType.Text, Alias: alias)] : [];
        public override string GetKey(JsonMovie record) => record.Id;
    }

    private static async Task Collect<T>(IAsyncEnumerable<T> source)
    {
        await foreach (var value in source) { }
    }
}
