using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using Respire.Search;
using Record = VectorData.ConformanceTests.CollectionManagementTests<string>.Record;

namespace Respire.VectorData.Conformance.Tests;

// Match CollectionManagementTests.CreateRecordDefinition: string key, text/number data,
// and ten FLOAT32 elements with the official fixture's default Flat index.
internal sealed class LifecycleHashMapper : RespireVectorDataHashMapper<Record>
{
    public override IReadOnlyList<RespireVectorDataVectorField> VectorFields { get; } =
        [new(nameof(Record.Floats), nameof(Record.Floats), 10) { Algorithm = RespireSearchVectorAlgorithm.Flat }];

    public override IReadOnlyList<RespireSearchField> DataFields { get; } =
        [new("text", RespireSearchFieldType.Text), new("number", RespireSearchFieldType.Numeric)];

    public override string GetKey(Record record) => record.Key;

    public override IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Write(Record record)
    {
        var fields = new Dictionary<string, ReadOnlyMemory<byte>>
        {
            ["number"] = Encoding.UTF8.GetBytes(record.Number.ToString(CultureInfo.InvariantCulture)),
            [nameof(Record.Floats)] = RespireVectorDataFloat32.Encode(record.Floats.Span),
        };
        if (record.Text is not null) fields["text"] = Encoding.UTF8.GetBytes(record.Text);
        return fields;
    }

    public override Record Read(string key, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> fields) => new()
    {
        Key = key,
        Text = fields.TryGetValue("text", out var text) ? Encoding.UTF8.GetString(text.Span) : null,
        Number = int.Parse(Encoding.UTF8.GetString(fields["number"].Span), CultureInfo.InvariantCulture),
        Floats = fields.TryGetValue(nameof(Record.Floats), out var vector) ? RespireVectorDataFloat32.Decode(vector.Span) : default,
    };
}

internal sealed class LifecycleJsonMapper() : RespireVectorDataJsonMapper<Record>(LifecycleJsonContext.Default.LifecycleRecord)
{
    public override IReadOnlyList<RespireVectorDataVectorField> VectorFields { get; } =
        [new(nameof(Record.Floats), nameof(Record.Floats), 10) { Algorithm = RespireSearchVectorAlgorithm.Flat }];

    public override IReadOnlyList<RespireSearchField> DataFields { get; } =
        [new("$.Text", RespireSearchFieldType.Text, Alias: "text"), new("$.Number", RespireSearchFieldType.Numeric, Alias: "number")];

    public override string GetKey(Record record) => record.Key;
}

[JsonSerializable(typeof(Record), TypeInfoPropertyName = "LifecycleRecord")]
internal partial class LifecycleJsonContext : JsonSerializerContext;
