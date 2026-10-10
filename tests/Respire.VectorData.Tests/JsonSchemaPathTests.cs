using System.Text.Json.Serialization;
using FluentAssertions;
using Respire.Search;
using TUnit.Core;

namespace Respire.VectorData.Tests;

public class JsonSchemaPathTests
{
    [Test, Arguments("$.Values.First"), Arguments("$.values.First"), Arguments("$.values.missing"), Arguments("$.values.renamed_first.missing")]
    public async Task VectorPathsMustMatchGeneratedSerializedNames(string path)
    {
        await using var client = RespireClient.Create("redis://127.0.0.1:1");
        using var store = new RespireVectorStore(client);
        store.RegisterMapper(new NestedMultiVectorMapper(path));
        var create = () => store.GetJsonCollection<NestedMultiVectorRecord>("records");
        create.Should().Throw<ArgumentException>().WithMessage("*serialized property*");
    }

    [Test]
    public async Task ScalarPathsMustMatchGeneratedSerializedNames()
    {
        await using var client = RespireClient.Create("redis://127.0.0.1:1");
        using var store = new RespireVectorStore(client);
        store.RegisterMapper(new NestedMultiVectorMapper(scalarPath: "$.Id"));
        var create = () => store.GetJsonCollection<NestedMultiVectorRecord>("records");
        create.Should().Throw<ArgumentException>().WithMessage("*serialized property*");
    }

    [Test]
    public async Task RenamedNestedPathsAcceptNullOptionalVectors()
    {
        await using var client = RespireClient.Create("redis://127.0.0.1:1");
        using var store = new RespireVectorStore(client);
        var mapper = new NestedMultiVectorMapper();
        store.RegisterMapper(mapper);
        using var collection = store.GetJsonCollection<NestedMultiVectorRecord>("records");
        var bytes = mapper.Write(new("one", new(null, null)));
        RespireVectorDataJsonPaths.ValidateVectors(bytes, mapper.VectorFields.ToArray(), [["values", "renamed_first"], ["values", "second"]]);
        mapper.Read(bytes, false, [["values", "renamed_first"], ["values", "second"]]).Values.Should().Be(new NestedMultiVectorValues(null, null));
    }
}

public sealed record NestedMultiVectorRecord(string Id, NestedMultiVectorValues Values);
public sealed record NestedMultiVectorValues([property: JsonPropertyName("renamed_first")] float[]? First, float[]? Second);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(NestedMultiVectorRecord))]
public partial class NestedMultiVectorContext : JsonSerializerContext;

internal sealed class NestedMultiVectorMapper(string vectorPath = "$.values.renamed_first", string scalarPath = "$.id")
    : RespireVectorDataJsonMapper<NestedMultiVectorRecord>(NestedMultiVectorContext.Default.NestedMultiVectorRecord)
{
    public override IReadOnlyList<RespireVectorDataVectorField> VectorFields { get; } =
    [
        new("Values.First", "first", 2) { JsonPath = vectorPath, Algorithm = RespireSearchVectorAlgorithm.Flat, DistanceMetric = RespireSearchDistanceMetric.L2 },
        new("Values.Second", "second", 2) { JsonPath = "$.values.second", Algorithm = RespireSearchVectorAlgorithm.Flat, DistanceMetric = RespireSearchDistanceMetric.L2 },
    ];
    public override IReadOnlyList<RespireSearchField> DataFields => [new(scalarPath, RespireSearchFieldType.Tag, Alias: "id")];
    public override string GetKey(NestedMultiVectorRecord record) => record.Id;
}
