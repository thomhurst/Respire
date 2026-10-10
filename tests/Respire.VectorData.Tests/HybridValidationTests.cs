using FluentAssertions;
using Microsoft.Extensions.VectorData;
using Respire.Samples.VectorData;
using Respire.Search;
using Respire.VectorData;
using TUnit.Core;

namespace Respire.VectorData.Tests;

public class HybridValidationTests
{
    [Test, Arguments(0), Arguments(1), Arguments(2), Arguments(3), Arguments(4), Arguments(5), Arguments(6), Arguments(7), Arguments(8), Arguments(9)]
    public async Task InvalidHybridInputsFailBeforeIo(int variant)
    {
        await using var client = RespireClient.Create("redis://127.0.0.1:1");
        using var store = new RespireVectorStore(client);
        store.RegisterMapper(new MovieMapper());
        using var collection = store.GetHashCollection<Movie>("movies");
        if (variant == 3)
        {
            var invalidSkip = () => new HybridSearchOptions<Movie> { Skip = -1 };
            invalidSkip.Should().Throw<ArgumentOutOfRangeException>();
            return;
        }
        var options = variant switch
        {
            4 => new HybridSearchOptions<Movie> { ScoreThreshold = double.NaN },
            5 => new() { AdditionalProperty = movie => movie.Tag },
            6 => new() { VectorProperty = movie => movie.Title },
            7 => new() { Filter = movie => movie.Id == "unmapped" },
            _ => null,
        };
        Func<Task> search = () => Collect(collection.HybridSearchAsync(
            variant == 8 ? new float[] { 1 } : variant == 9 ? new float[] { float.NaN, 0 } : new float[] { 1, 0 },
            variant == 1 ? [] : variant == 2 ? [" "] : ["apples"], variant == 0 ? 0 : 1, options));
        if (variant == 7) await search.Should().ThrowAsync<NotSupportedException>();
        else await search.Should().ThrowAsync<ArgumentException>();
    }

    [Test, Arguments(null), Arguments(double.NaN), Arguments(double.PositiveInfinity)]
    public void InvalidFusionScoreHasOperationMetadata(double? score)
    {
        var document = new RespireSearchDocument("movies:key", new Dictionary<string, string?>(), score);
        var read = () => RespireVectorDataOperations.ReadHybridScore(document, "movies");
        var error = read.Should().Throw<VectorStoreException>().Which;
        error.OperationName.Should().Be("HybridSearchAsync");
        error.CollectionName.Should().Be("movies");
    }

    [Test]
    public async Task JsonFilterAndAmbiguousVectorFailBeforeIo()
    {
        await using var client = RespireClient.Create("redis://127.0.0.1:1");
        using var store = new RespireVectorStore(client);
        store.RegisterMapper(new JsonMovieMapper());
        using var collection = store.GetJsonCollection<JsonMovie>("movies");
        Func<Task> filter = () => Collect(collection.HybridSearchAsync(new float[] { 1, 0 }, ["apples"], 1, new() { Filter = movie => movie.Id == "x" }));
        await filter.Should().ThrowAsync<NotSupportedException>().WithMessage("*JSON*");
        Func<Task> ambiguous = () => Collect(collection.HybridSearchAsync(new float[] { 1, 0 }, ["apples"], 1));
        await ambiguous.Should().ThrowAsync<InvalidOperationException>().WithMessage("*multiple vector*");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Func<Task> cancellation = () => Collect(collection.HybridSearchAsync(new float[] { 1, 0 }, ["apples"], 1, cancellationToken: cancelled.Token));
        await cancellation.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test, Arguments(0), Arguments(1), Arguments(2), Arguments(3), Arguments(4)]
    public async Task TextMappingsRequireUniqueIndexedTextFields(int variant)
    {
        await using var client = RespireClient.Create("redis://127.0.0.1:1");
        using var store = new RespireVectorStore(client);
        store.RegisterMapper(new InvalidTextMapper(variant));
        var create = () => store.GetHashCollection<Movie>("movies");
        create.Should().Throw<ArgumentException>();
    }

    [Test]
    public async Task NoTextMappingFailsClearlyWithoutIo()
    {
        await using var client = RespireClient.Create("redis://127.0.0.1:1");
        using var store = new RespireVectorStore(client);
        store.RegisterMapper(new InvalidTextMapper(5));
        using var collection = store.GetHashCollection<Movie>("movies");
        Func<Task> search = () => Collect(collection.HybridSearchAsync(new float[] { 1, 0 }, ["apples"], 1));
        await search.Should().ThrowAsync<InvalidOperationException>().WithMessage("*TextFields*");
    }

    private sealed class InvalidTextMapper(int variant) : RespireVectorDataHashMapper<Movie>
    {
        private readonly MovieMapper _inner = new();
        public override IReadOnlyList<RespireVectorDataVectorField> VectorFields => _inner.VectorFields;
        public override IReadOnlyList<RespireSearchField> DataFields =>
            [new("title", variant == 1 ? RespireSearchFieldType.Tag : RespireSearchFieldType.Text, NoIndex: variant == 2)];
        public override IReadOnlyList<RespireVectorDataTextField> TextFields => variant switch
        {
            0 => [new(nameof(Movie.Title), "missing")],
            3 => [new(nameof(Movie.Title), "title"), new(nameof(Movie.Title), "title")],
            4 => [new(nameof(Movie.Title), "title"), new(nameof(Movie.Tag), "title")],
            5 => [],
            _ => [new(nameof(Movie.Title), "title")],
        };
        public override string GetKey(Movie record) => _inner.GetKey(record);
        public override IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Write(Movie record) => _inner.Write(record);
        public override Movie Read(string key, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> fields) => _inner.Read(key, fields);
    }

    private static async Task Collect<T>(IAsyncEnumerable<T> values) { await foreach (var value in values) { } }
}
