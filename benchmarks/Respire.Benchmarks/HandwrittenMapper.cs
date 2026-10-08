using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Respire.Search;

namespace Respire.Benchmarks;

// Independently handwritten controls use the same public representation, validation,
// serializer entry points and owned-output contract. They never call generated code.
internal static class HandwrittenMapper
{
    private static readonly RespireSearchVectorOptions VectorOptions = new(
        RespireSearchVectorAlgorithm.Flat, RespireSearchVectorType.Float32, 16, RespireSearchDistanceMetric.Cosine);
    private static readonly RespireSearchField HashVectorField = new("Embedding", RespireSearchFieldType.Vector, "Embedding") { Vector = VectorOptions };
    private static readonly RespireSearchField JsonNameField = new("$[\"Name\"]", RespireSearchFieldType.Text, "Name") { Weight = 1 };
    private static readonly RespireSearchField JsonCountField = new("$[\"Count\"]", RespireSearchFieldType.Numeric, "Count", Sortable: true);
    private static readonly RespireSearchField JsonVectorField = new("$[\"Embedding\"]", RespireSearchFieldType.Vector, "Embedding") { Vector = VectorOptions };
    private static readonly JsonSerializerOptions Options = new() { TypeInfoResolver = JsonTypeInfoResolver.Combine() };
    private static readonly JsonTypeInfo<MapperJsonModel> TypeInfo = JsonMetadataServices.CreateValueInfo<MapperJsonModel>(Options, new ModelConverter());

    public static Dictionary<string, string> ToFields(MapperHashModel value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var fields = new Dictionary<string, string>(5, StringComparer.Ordinal)
        {
            ["Id"] = Required(value.Id), ["Name"] = Required(value.Name),
            ["Count"] = value.Count.ToString(CultureInfo.InvariantCulture), ["Enabled"] = value.Enabled ? "1" : "0",
        };
        if (value.Note is not null) fields["Note"] = value.Note;
        return fields;
    }

    public static MapperHashModel FromFields(IReadOnlyDictionary<string, string> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        return new(RequiredField(fields, "Id"), RequiredField(fields, "Name"),
            int.Parse(RequiredField(fields, "Count"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture),
            RequiredField(fields, "Enabled") switch { "1" => true, "0" => false, _ => throw new FormatException() },
            fields.TryGetValue("Note", out var note) ? note : null);
    }

    public static RespireKey HashKey(MapperHashModel value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new RespireKey("mapper:" + Required(value.Id));
    }

    public static RespireKey JsonKey(MapperJsonModel value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new RespireKey("mapper-json:" + Required(value.Id));
    }

    public static Dictionary<string, byte[]> ToFields(MapperVectorHashModel value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var fields = new Dictionary<string, byte[]>(2, StringComparer.Ordinal) { ["Id"] = Encoding.UTF8.GetBytes(Required(value.Id)) };
        if (value.Embedding is not null)
        {
            RespireSearchVectorValidation.ValidateHash(value.Embedding, VectorOptions);
            fields["Embedding"] = value.Embedding.ToArray();
        }
        return fields;
    }

    public static MapperVectorHashModel VectorFromFields(IReadOnlyDictionary<string, byte[]> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if (!fields.TryGetValue("Id", out var id)) throw new FormatException("Missing Id.");
        byte[]? vector = null;
        if (fields.TryGetValue("Embedding", out var bytes))
        {
            RespireSearchVectorValidation.ValidateHash(bytes, VectorOptions);
            vector = bytes.ToArray();
        }
        return new(Encoding.UTF8.GetString(id), vector);
    }

    // RespireSearchGenerator emits Definition => new(), including fresh read-only
    // prefix/field collections. Measure that construction contract on both sides.
    public static RespireSearchIndexDefinition HashDefinition() => new()
    {
        Source = RespireSearchSource.Hash,
        Prefixes = Array.AsReadOnly(new[] { "mapper-vector:" }),
        Fields = Array.AsReadOnly(new[] { HashVectorField }),
    };

    public static RespireSearchIndexDefinition JsonDefinition() => new()
    {
        Source = RespireSearchSource.Json,
        Prefixes = Array.AsReadOnly(new[] { "mapper-json:" }),
        Fields = Array.AsReadOnly(new[] { JsonNameField, JsonCountField, JsonVectorField }),
    };

    public static void Validate(MapperVectorHashModel value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Embedding is not null) RespireSearchVectorValidation.ValidateHash(value.Embedding, VectorOptions);
    }

    public static void Validate(MapperJsonModel value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Embedding is not null) RespireSearchVectorValidation.ValidateJson(value.Embedding, VectorOptions);
    }

    public static byte[] ToJson(MapperJsonModel? value) => JsonSerializer.SerializeToUtf8Bytes(value!, TypeInfo);
    public static MapperJsonModel? FromJson(ReadOnlySpan<byte> bytes) => JsonSerializer.Deserialize(bytes, TypeInfo);

    private static string Required(string? value) => value ?? throw new ArgumentException("Null required property.", nameof(value));
    private static string RequiredField(IReadOnlyDictionary<string, string> fields, string name)
        => fields.TryGetValue(name, out var value) ? value : throw new FormatException("Missing " + name);

    private sealed class ModelConverter : JsonConverter<MapperJsonModel>
    {
        public override bool HandleNull => true;

        public override MapperJsonModel Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return null!;
            if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException();
            string? id = null, name = null, note = null;
            int count = 0;
            bool enabled = false, countFound = false, enabledFound = false;
            float[]? vector = null;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException();
                var property = reader.GetString();
                if (!reader.Read()) throw new JsonException();
                switch (property)
                {
                    case "Id": id = reader.GetString() ?? throw new JsonException(); break;
                    case "Name": name = reader.GetString() ?? throw new JsonException(); break;
                    case "Count": count = reader.GetInt32(); countFound = true; break;
                    case "Enabled": enabled = reader.GetBoolean(); enabledFound = true; break;
                    case "Note": note = reader.GetString(); break;
                    case "Embedding":
                        if (reader.TokenType == JsonTokenType.Null) { vector = null; break; }
                        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException();
                        vector = new float[VectorOptions.Dimensions];
                        var elementCount = 0;
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                        {
                            if (reader.TokenType != JsonTokenType.Number || elementCount >= vector.Length) throw new JsonException();
                            vector[elementCount++] = reader.GetSingle();
                        }
                        if (reader.TokenType != JsonTokenType.EndArray) throw new JsonException();
                        if (elementCount != vector.Length) throw new ArgumentException("JSON float vector must match FLOAT32 schema dimensions.");
                        RespireSearchVectorValidation.ValidateJson(vector, VectorOptions);
                        break;
                    default: reader.Skip(); break;
                }
            }
            if (reader.TokenType != JsonTokenType.EndObject || id is null || name is null || !countFound || !enabledFound) throw new JsonException();
            return new(id, name, count, enabled, note, vector);
        }

        public override void Write(Utf8JsonWriter writer, MapperJsonModel value, JsonSerializerOptions options)
        {
            if (value is null) { writer.WriteNullValue(); return; }
            if (value.Id is null || value.Name is null) throw new JsonException();
            writer.WriteStartObject();
            writer.WriteString("Id", value.Id);
            writer.WriteString("Name", value.Name);
            writer.WriteNumber("Count", value.Count);
            writer.WriteBoolean("Enabled", value.Enabled);
            writer.WriteString("Note", value.Note);
            writer.WritePropertyName("Embedding");
            if (value.Embedding is null) writer.WriteNullValue();
            else
            {
                RespireSearchVectorValidation.ValidateJson(value.Embedding, VectorOptions);
                writer.WriteStartArray();
                foreach (var element in value.Embedding) writer.WriteNumberValue(element);
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
    }
}
