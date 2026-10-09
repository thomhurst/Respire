using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Respire.VectorData;

/// <summary>JSON record mapping with explicit serializer metadata and no reflection fallback.</summary>
/// <remarks>Supply source-generated metadata. Models must deserialize when vector properties are absent.
/// Mappings must be thread-safe. Schema paths select single object properties, never array elements or wildcards.</remarks>
public abstract class RespireVectorDataJsonMapper<TRecord> : RespireVectorDataMapper<TRecord> where TRecord : class
{
    /// <summary>Creates a mapping using explicit generated serialization and deserialization metadata.</summary>
    protected RespireVectorDataJsonMapper(JsonTypeInfo<TRecord> jsonTypeInfo)
    {
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);
        JsonTypeInfo = jsonTypeInfo;
    }

    /// <summary>Explicit metadata used for both writes and reads.</summary>
    public JsonTypeInfo<TRecord> JsonTypeInfo { get; }

    internal byte[] Write(TRecord record) => JsonSerializer.SerializeToUtf8Bytes(record, JsonTypeInfo);

    internal TRecord Read(ReadOnlySpan<byte> json, bool includeVectors, string[][] vectorPaths)
    {
        if (includeVectors) return Deserialize(json);
        var root = JsonNode.Parse(json) as JsonObject ?? throw new JsonException("A vector record must be a JSON object.");
        foreach (var path in vectorPaths)
        {
            JsonObject? parent = root;
            for (var i = 0; i < path.Length - 1 && parent is not null; i++)
                parent = parent[path[i]] as JsonObject;
            parent?.Remove(path[^1]);
        }
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) root.WriteTo(writer);
        return Deserialize(buffer.WrittenSpan);
    }

    private TRecord Deserialize(ReadOnlySpan<byte> json)
        => JsonSerializer.Deserialize(json, JsonTypeInfo) ?? throw new JsonException("A vector record must not be null.");
}

internal static class RespireVectorDataJsonPaths
{
    // Deliberately restrict schema paths to properties so omission and validation select exactly the indexed value.
    internal static string[] Parse(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!path.StartsWith("$.", StringComparison.Ordinal)) throw new ArgumentException("JSON schema paths must start with $. and select object properties.", nameof(path));
        var segments = path[2..].Split('.');
        if (segments.Any(segment => segment.Length == 0 || segment.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')))
            throw new ArgumentException("JSON path properties must contain only ASCII letters, digits and underscores.", nameof(path));
        return segments;
    }

    internal static void ValidateVectors(ReadOnlyMemory<byte> json, RespireVectorDataVectorField[] vectors, string[][] paths)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("A vector record must be a JSON object.");
        for (var i = 0; i < vectors.Length; i++)
        {
            var value = document.RootElement;
            foreach (var segment in paths[i])
            {
                if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value))
                {
                    value = default;
                    break;
                }
            }
            if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) continue;
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != vectors[i].Dimensions)
                throw new ArgumentException("JSON vector must be a numeric array with the mapped dimensions.", nameof(json));
            foreach (var element in value.EnumerateArray())
                if (element.ValueKind != JsonValueKind.Number || !element.TryGetSingle(out var number) || !float.IsFinite(number))
                    throw new ArgumentException("JSON vector elements must be finite FLOAT32 numbers.", nameof(json));
        }
    }
}
