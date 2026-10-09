using FluentAssertions;
using Microsoft.Extensions.VectorData;
using Respire.Samples.VectorData;
using Respire.Search;
using TUnit.Core;

namespace Respire.VectorData.Tests;

public class ValidationTests
{
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
