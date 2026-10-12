using System.Linq.Expressions;
using System.Text.Json.Serialization;
using FluentAssertions;
using Respire.IntegrationTests;
using Respire.Samples.VectorData;
using TUnit.Core;

namespace Respire.VectorData.Tests;

[ClassDataSource<ModernRedisTestContainer>(Shared = SharedType.PerTestSession)]
public class JsonFilterTests(ModernRedisTestContainer fixture)
{
    [Test, Arguments(2), Arguments(3)]
    public async Task CollectionFiltersRespectTightModelDepth(int protocol)
    {
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespProtocol)protocol });
        using var store = new RespireVectorStore(client, "json-filter-depth:" + Guid.NewGuid().ToString("N"));
        var context = new JsonFilterContext(new System.Text.Json.JsonSerializerOptions { MaxDepth = 2 });
        store.RegisterMapper(new JsonTypedFilterMapper(context.JsonTypedFilterRecord));
        using var collection = store.GetJsonCollection<JsonTypedFilterRecord>("records");
        try
        {
            await collection.EnsureCollectionExistsAsync();
            await collection.UpsertAsync(new JsonTypedFilterRecord("one", 1, 1, null, true, ["tag"], []));
            var included = await collection.GetAsync("one", new() { IncludeVectors = true });
            included!.Tags.Should().Equal("tag");
            included.Vector.Should().Equal(1, 0);
            included.Extra.Should().BeNull();
            (await collection.GetAsync("one"))!.Vector.Should().BeNull();
            (await Collect(collection.GetAsync(r => r.Tags.Contains("tag"), 1, new() { IncludeVectors = true })))
                .Should().ContainSingle().Which.Vector.Should().Equal(1, 0);
        }
        finally { await collection.EnsureCollectionDeletedAsync(); }
    }

    [Test, Arguments(2), Arguments(3)]
    public async Task NestedOrdinalStringsFilterRetrievalKnnAndHybrid(int protocol)
    {
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespProtocol)protocol });
        using var store = new RespireVectorStore(client, "json-filters:" + Guid.NewGuid().ToString("N"));
        store.RegisterMapper(new JsonMovieMapper());
        using var collection = store.GetJsonCollection<JsonMovie>("movies");
        try
        {
            await collection.EnsureCollectionExistsAsync();
            string[] values = ["", "foo", "FOO", " foo ", "a,b|c", "映画😀", "@number:[-inf +inf] | *", "} | * =>[KNN 99 @v $vector]", "a\r\nb\t\\'\""];
            await collection.UpsertAsync(values.Select((value, i) => new JsonMovie(i.ToString(), new(value, value), [1, 0])));
            foreach (var value in values)
            {
                (await Collect(collection.GetAsync(r => r.Details.Title == value, 100))).Should().ContainSingle().Which.Details.Title.Should().Be(value);
                (await Collect(collection.GetAsync(r => r.Details.Tag == value, 100))).Should().ContainSingle().Which.Details.Tag.Should().Be(value);
                (await Collect(collection.SearchAsync(new float[] { 1, 0 }, 100,
                    new() { VectorProperty = r => r.Vector, Filter = r => r.Details.Title == value }))).Should().ContainSingle().Which.Record.Details.Title.Should().Be(value);
            }
            (await Collect(collection.GetAsync(r => r.Details.Title == "missing } | *", 100))).Should().BeEmpty();
            await collection.UpsertAsync(new JsonMovie("0", new("arrival"), [1, 0]));
            await collection.UpsertAsync(new JsonMovie("1", new("arrival", "other"), [1, 0]));
            (await Collect(collection.GetAsync(r => r.Details.Tag == null, 100))).Should().ContainSingle().Which.Id.Should().Be("0");
            (await Collect(collection.GetAsync(r => r.Details.Title == "", 100))).Should().BeEmpty();
            var hybrid = await Collect(collection.HybridSearchAsync(new float[] { 1, 0 }, ["arrival"], 100,
                new() { VectorProperty = r => r.Vector, Filter = r => r.Details.Tag == null, IncludeVectors = true }));
            hybrid.Should().ContainSingle().Which.Record.Id.Should().Be("0");
            hybrid[0].Record.Vector.Should().Equal(1, 0);
        }
        finally { await collection.EnsureCollectionDeletedAsync(); }
    }

    [Test, Arguments(2), Arguments(3)]
    public async Task JsonNumericBooleanCollectionAndMissingSemantics(int protocol)
    {
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespProtocol)protocol });
        using var store = new RespireVectorStore(client, "json-filter-types:" + Guid.NewGuid().ToString("N"));
        store.RegisterMapper(new JsonTypedFilterMapper());
        using var collection = store.GetJsonCollection<JsonTypedFilterRecord>("records");
        try
        {
            await collection.EnsureCollectionExistsAsync();
            JsonTypedFilterRecord[] records =
            [
                new("0", 0.1f, 0.1, null, true, ["", " x ", "a,b"], ["映画😀"]),
                new("1", 0.2f, 0.2, 9, false, ["x"], []),
                new("2", 0.3f, 0.3, 10, true, [], ["x"]),
            ];
            await collection.UpsertAsync(records);
            string[] choices = ["", "映画😀"];
            Expression<Func<JsonTypedFilterRecord, bool>>[] filters =
            [
                r => r.Float == 0.1f, r => r.Float > 0.1f, r => r.Float <= 0.2f,
                r => r.Double == 0.1, r => r.Double >= 0.2, r => 0.3 > r.Double,
                r => r.Optional == null, r => r.Optional != null, r => r.Optional != 9,
                r => r.Optional < 10, r => !(r.Optional >= 9), r => r.Bool, r => !r.Bool,
                r => r.Tags.Contains(""), r => r.Tags.Contains(" x "), r => r.Tags.Contains("a,b"),
                r => r.List.Contains("映画😀"), r => r.Tags.Any(t => choices.Contains(t)),
                r => r.Bool && r.Optional == null || r.Double > 0.2,
                r => true, r => false,
            ];
            foreach (var filter in filters)
            {
                var expected = records.Where(filter.Compile()).Select(r => r.Id).Order().ToArray();
                (await Collect(collection.GetAsync(filter, 100))).Select(r => r.Id).Order().Should().Equal(expected, filter.ToString());
                (await Collect(collection.SearchAsync(new float[] { 1, 0 }, 100, new() { Filter = filter })))
                    .Select(r => r.Record.Id).Order().Should().Equal(expected, filter.ToString());
            }
            var page = await Collect(collection.GetAsync(r => true, 1, new() { Skip = 1, IncludeVectors = true }));
            page.Should().ContainSingle().Which.Vector.Should().Equal(1, 0);
            var read = await collection.GetAsync("0", new() { IncludeVectors = true });
            read!.Extra.Should().BeNull("internal filter data must not reach generated extension-data mapping");
            read.Float.Should().Be(0.1f);
        }
        finally { await collection.EnsureCollectionDeletedAsync(); }
    }

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> source)
    {
        var result = new List<T>();
        await foreach (var item in source) result.Add(item);
        return result;
    }
}

public sealed record JsonTypedFilterRecord(string Id, float Float, double Double, int? Optional, bool Bool, string[] Tags, List<string> List)
{
    public float[]? Vector { get; init; } = [1, 0];
    [JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}

[JsonSerializable(typeof(JsonTypedFilterRecord))]
public partial class JsonFilterContext : JsonSerializerContext;

internal sealed class JsonTypedFilterMapper(System.Text.Json.Serialization.Metadata.JsonTypeInfo<JsonTypedFilterRecord>? metadata = null)
    : RespireVectorDataJsonMapper<JsonTypedFilterRecord>(metadata ?? JsonFilterContext.Default.JsonTypedFilterRecord)
{
    public override IReadOnlyList<RespireVectorDataVectorField> VectorFields => [new("Vector", "Vector", 2) { Algorithm = Respire.Search.RespireSearchVectorAlgorithm.Flat }];
    public override IReadOnlyList<RespireVectorDataFilterField> FilterFields =>
    [
        new("Float", "float", RespireVectorDataFilterKind.Numeric) { JsonPath = "$.Float" },
        new("Double", "double", RespireVectorDataFilterKind.Numeric) { JsonPath = "$.Double" },
        new("Optional", "optional", RespireVectorDataFilterKind.Numeric) { JsonPath = "$.Optional" },
        new("Bool", "bool", RespireVectorDataFilterKind.Boolean) { JsonPath = "$.Bool" },
        new("Tags", "tags", RespireVectorDataFilterKind.StringCollection) { JsonPath = "$.Tags" },
        new("List", "list", RespireVectorDataFilterKind.StringCollection) { JsonPath = "$.List" },
    ];
    public override string GetKey(JsonTypedFilterRecord record) => record.Id;
}
