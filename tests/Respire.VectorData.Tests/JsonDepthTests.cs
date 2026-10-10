using System.Text.Json.Serialization;
using FluentAssertions;
using TUnit.Core;

namespace Respire.VectorData.Tests;

public class JsonDepthTests
{
    [Test]
    public void GeneratedDepthAppliesToIntermediateReadAndValidation()
    {
        var mapper = new DeepJsonMapper();
        var record = CreateRecord();
        var bytes = mapper.Write(record);
        RespireVectorDataJsonPaths.ValidateVectors(bytes, mapper.VectorFields.ToArray(), [["Vector"]], mapper.JsonTypeInfo.Options.MaxDepth);
        var withoutVectors = mapper.Read(bytes, false, [["Vector"]]);
        withoutVectors.Vector.Should().BeNull();
        Depth(withoutVectors.Data).Should().Be(100);
        mapper.Read(bytes, true, [["Vector"]]).Vector.Should().Equal(1, 0);
    }

    internal static DeepJsonRecord CreateRecord()
    {
        DeepJsonNode? node = null;
        for (var i = 0; i < 100; i++) node = new(i, node);
        return new("deep", [1, 0], node);
    }

    internal static int Depth(DeepJsonNode? node)
    {
        var count = 0;
        for (; node is not null; node = node.Next) count++;
        return count;
    }
}

public sealed record DeepJsonRecord(string Id, float[]? Vector, DeepJsonNode? Data);
public sealed record DeepJsonNode(int Value, DeepJsonNode? Next);

[JsonSourceGenerationOptions(MaxDepth = 128)]
[JsonSerializable(typeof(DeepJsonRecord))]
public partial class DeepJsonContext : JsonSerializerContext;

internal sealed class DeepJsonMapper() : RespireVectorDataJsonMapper<DeepJsonRecord>(DeepJsonContext.Default.DeepJsonRecord)
{
    public override IReadOnlyList<RespireVectorDataVectorField> VectorFields => [new(nameof(DeepJsonRecord.Vector), "Vector", 2)];
    public override string GetKey(DeepJsonRecord record) => record.Id;
}
