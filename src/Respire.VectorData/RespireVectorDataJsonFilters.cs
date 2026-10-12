using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Respire.VectorData;

// Index normalized copies instead of altering serializer-owned values. Raw JSON TAGs trim whitespace,
// TEXT is tokenized, Boolean JSON values are not NUMERIC, and FLOAT32 JSON spelling can round boundaries.
internal sealed class RespireVectorDataJsonFilters
{
    internal const string RootProperty = "__respire_filters";
    private readonly (RespireVectorDataFilterField Field, string[] Path, Type Type, bool Required)[] _fields;
    private readonly int _maxDepth;

    internal RespireVectorDataJsonFilters(JsonTypeInfo metadata, RespireVectorDataFilterField[] fields)
    {
        if (metadata.Properties.Any(property => property.Name == RootProperty))
            throw new ArgumentException($"JSON filter mappings reserve the root property '{RootProperty}'.", nameof(metadata));
        _maxDepth = metadata.Options.MaxDepth;
        _fields = fields.Select(field =>
        {
            var path = RespireVectorDataJsonPaths.Parse(field.JsonPath ?? "$." + field.StorageName);
            var property = RespireVectorDataJsonPaths.ValidateMetadata(metadata, path, forFilter: true);
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            var supported = field.Kind switch
            {
                RespireVectorDataFilterKind.String => type == typeof(string),
                RespireVectorDataFilterKind.Boolean => type == typeof(bool),
                RespireVectorDataFilterKind.Numeric => RespireVectorDataFilter<object>.NumericType(type),
                RespireVectorDataFilterKind.StringCollection => type == typeof(string[]) || type == typeof(List<string>),
                _ => false,
            };
            if (!supported) throw new ArgumentException($"JSON property '{property.Name}' does not match filter kind '{field.Kind}'.", nameof(fields));
            return (field, path, type, property.PropertyType.IsValueType && Nullable.GetUnderlyingType(property.PropertyType) is null);
        }).ToArray();
    }

    internal static string IndexPath(RespireVectorDataFilterField field) => $"$.{RootProperty}.{field.StorageName}";

    // Normalized string collections occupy three levels: record, filter object, array.
    internal static int DocumentMaxDepth(int modelMaxDepth) => Math.Max(modelMaxDepth == 0 ? 64 : modelMaxDepth, 3);

    internal byte[] Write(ReadOnlyMemory<byte> json)
    {
        using var document = JsonDocument.Parse(json, new() { MaxDepth = _maxDepth });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("A vector record must be a JSON object.");
        // Extension data and custom converters can introduce properties absent from the generated metadata.
        if (root.TryGetProperty(RootProperty, out _))
            throw new ArgumentException($"JSON records cannot contain reserved filter property '{RootProperty}'.", nameof(json));
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new() { MaxDepth = DocumentMaxDepth(_maxDepth) }))
        {
            writer.WriteStartObject();
            foreach (var property in root.EnumerateObject()) property.WriteTo(writer);
            writer.WriteStartObject(RootProperty);
            foreach (var (field, path, type, required) in _fields)
            {
                var value = root;
                foreach (var segment in path)
                {
                    if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value))
                    {
                        value = default;
                        break;
                    }
                }
                // JSON null and omitted properties share hash null/missing semantics. Empty strings are present.
                if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    if (required) throw new ArgumentException($"Nonnullable JSON filter property '{field.PropertyName}' must be serialized, including default values.", nameof(json));
                    continue;
                }
                writer.WritePropertyName(field.StorageName);
                switch (field.Kind)
                {
                    case RespireVectorDataFilterKind.String:
                        writer.WriteStringValue(RespireVectorDataFilterEncoding.EncodeTag(value.GetString()!));
                        break;
                    case RespireVectorDataFilterKind.StringCollection:
                        writer.WriteStartArray();
                        foreach (var element in value.EnumerateArray())
                            writer.WriteStringValue(RespireVectorDataFilterEncoding.EncodeTag(element.GetString()
                                ?? throw new ArgumentException("Null collection elements are unsupported.", nameof(json))));
                        writer.WriteEndArray();
                        break;
                    case RespireVectorDataFilterKind.Boolean:
                        writer.WriteNumberValue(value.GetBoolean() ? 1 : 0);
                        break;
                    case RespireVectorDataFilterKind.Numeric:
                        double number = type == typeof(float) ? value.GetSingle() : value.GetDouble();
                        if (!double.IsFinite(number)) throw new ArgumentException("Filter numbers must be finite.", nameof(json));
                        writer.WriteNumberValue(number);
                        break;
                }
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }
}
