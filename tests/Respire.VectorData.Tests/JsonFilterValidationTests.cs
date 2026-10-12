using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Respire.Samples.VectorData;
using TUnit.Core;

namespace Respire.VectorData.Tests;

public class JsonFilterValidationTests
{
    private static string Getter => throw new InvalidOperationException("Getter must never execute");

    [Test, Arguments(0), Arguments(1), Arguments(2), Arguments(3), Arguments(4)]
    public async Task InvalidFilterMappingsFailBeforeIo(int variant)
    {
        await using var client = RespireClient.Create("redis://127.0.0.1:1");
        using var store = new RespireVectorStore(client);
        store.RegisterMapper(new InvalidFilterMapper(variant));
        var create = () => store.GetJsonCollection<JsonMovie>("movies");
        create.Should().Throw<ArgumentException>();
    }

    [Test]
    public async Task CapturedGettersNeverExecuteOnAnyJsonFilterApi()
    {
        await using var client = RespireClient.Create("redis://127.0.0.1:1");
        using var store = new RespireVectorStore(client);
        store.RegisterMapper(new JsonMovieMapper());
        using var collection = store.GetJsonCollection<JsonMovie>("movies");
        Func<Task> retrieval = () => Collect(collection.GetAsync(r => r.Details.Title == Getter, 1));
        Func<Task> search = () => Collect(collection.SearchAsync(new float[] { 1, 0 }, 1,
            new() { VectorProperty = r => r.Vector, Filter = r => r.Details.Title == Getter }));
        Func<Task> hybrid = () => Collect(collection.HybridSearchAsync(new float[] { 1, 0 }, ["arrival"], 1,
            new() { VectorProperty = r => r.Vector, Filter = r => r.Details.Title == Getter }));
        await retrieval.Should().ThrowAsync<NotSupportedException>().WithMessage("*getters*");
        await search.Should().ThrowAsync<NotSupportedException>().WithMessage("*getters*");
        await hybrid.Should().ThrowAsync<NotSupportedException>().WithMessage("*getters*");
    }

    [Test]
    public async Task NullCollectionElementsAndReservedExtensionDataFailBeforeIo()
    {
        await using var client = RespireClient.Create("redis://127.0.0.1:1");
        using var store = new RespireVectorStore(client);
        store.RegisterMapper(new JsonTypedFilterMapper());
        using var collection = store.GetJsonCollection<JsonTypedFilterRecord>("records");
        var record = new JsonTypedFilterRecord("one", 1, 1, null, true, [null!], []);
        Func<Task> upsertNull = () => collection.UpsertAsync(record);
        await upsertNull.Should().ThrowAsync<ArgumentException>().WithMessage("*Null collection elements*");
        var extra = new Dictionary<string, JsonElement> { [RespireVectorDataJsonFilters.RootProperty] = JsonSerializer.SerializeToElement(1, JsonFilterValidationContext.Default.Int32) };
        Func<Task> upsertReserved = () => collection.UpsertAsync(record with { Tags = [], Extra = extra });
        await upsertReserved.Should().ThrowAsync<ArgumentException>().WithMessage("*reserved filter property*");
    }

    [Test]
    public void DeclaredReservedPropertyIsRejected()
    {
        var create = () => new RespireVectorDataJsonFilters(JsonFilterValidationContext.Default.ReservedJsonFilterRecord,
            [new("Name", "Name", RespireVectorDataFilterKind.String)]);
        create.Should().Throw<ArgumentException>().WithMessage("*reserve the root property*");
    }

    [Test, Arguments("{}"), Arguments("{\"Bool\":null}")]
    public void NonnullableFilterValuesCannotBeOmittedOrNull(string json)
    {
        var filters = new RespireVectorDataJsonFilters(JsonFilterContext.Default.JsonTypedFilterRecord,
            [new("Bool", "boolean", RespireVectorDataFilterKind.Boolean) { JsonPath = "$.Bool" }]);
        var write = () => filters.Write(System.Text.Encoding.UTF8.GetBytes(json));
        write.Should().Throw<ArgumentException>().WithMessage("*must be serialized*default values*");
    }

    [Test]
    public void NormalizedFieldsAreRemovedBeforeStrictGeneratedDeserialization()
    {
        var context = new MovieJsonContext(new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow });
        var mapper = new StrictFilterMapper(context.JsonMovie);
        var filters = new RespireVectorDataJsonFilters(mapper.JsonTypeInfo, mapper.FilterFields.ToArray());
        var json = filters.Write(mapper.Write(new("one", new("arrival"), [1, 0])));
        mapper.Read(json, true, [["embedding"]], removeFilters: true).Vector.Should().Equal(1, 0);
        mapper.Read(json, false, [["embedding"]], removeFilters: true).Vector.Should().BeNull();
    }

    private sealed class StrictFilterMapper(System.Text.Json.Serialization.Metadata.JsonTypeInfo<JsonMovie> metadata)
        : RespireVectorDataJsonMapper<JsonMovie>(metadata)
    {
        public override IReadOnlyList<RespireVectorDataVectorField> VectorFields => new JsonMovieMapper().VectorFields;
        public override IReadOnlyList<RespireVectorDataFilterField> FilterFields => new JsonMovieMapper().FilterFields;
        public override string GetKey(JsonMovie record) => record.Id;
    }

    private sealed class InvalidFilterMapper(int variant) : RespireVectorDataJsonMapper<JsonMovie>(MovieJsonContext.Default.JsonMovie)
    {
        public override IReadOnlyList<RespireVectorDataVectorField> VectorFields => new JsonMovieMapper().VectorFields;
        public override IReadOnlyList<RespireVectorDataFilterField> FilterFields => variant switch
        {
            0 => [new("Details.Title", "title", RespireVectorDataFilterKind.String) { JsonPath = "$.details.missing" }],
            1 => [new("Details.Title", "title", RespireVectorDataFilterKind.Numeric) { JsonPath = "$.details.movie_title" }],
            2 => [new("Details.Title", "vector", RespireVectorDataFilterKind.String) { JsonPath = "$.details.movie_title" }],
            3 => [new("Details.Title", "bad alias", RespireVectorDataFilterKind.String) { JsonPath = "$.details.movie_title" }],
            _ => [new("Details.Title", "title", RespireVectorDataFilterKind.String) { JsonPath = "$.details[*]" }],
        };
        public override string GetKey(JsonMovie record) => record.Id;
    }

    private static async Task Collect<T>(IAsyncEnumerable<T> source)
    {
        await foreach (var item in source) { }
    }
}

public sealed record ReservedJsonFilterRecord(string Name, int __respire_filters);

[JsonSerializable(typeof(ReservedJsonFilterRecord))]
[JsonSerializable(typeof(int))]
public partial class JsonFilterValidationContext : JsonSerializerContext;
