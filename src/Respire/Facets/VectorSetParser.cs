using Respire.Internal;
using Respire.Protocol;

namespace Respire;

internal static class VectorSetParser
{
    internal static bool Flag(in RespValue value)
    {
        if (value.Type == RespDataType.Boolean) return value.AsBoolean();
        if (value.Type == RespDataType.Integer && value.AsInteger() is 0 or 1) return value.AsInteger() == 1;
        throw Invalid("expected a boolean or 0/1 integer");
    }

    internal static long Number(in RespValue value)
        => value.Type == RespDataType.Integer && value.AsInteger() >= 0 ? value.AsInteger()
            : throw Invalid("expected a nonnegative integer");

    internal static byte[]? NullableBytes(in RespValue value) => value.IsNull ? null : Bytes(in value);

    internal static byte[] Bytes(in RespValue value)
    {
        RequireString(in value);
        return value.AsSpan().ToArray();
    }

    internal static byte[][] Members(in RespValue value)
    {
        var entries = Array(in value);
        var result = new byte[entries.Length][];
        for (var index = 0; index < entries.Length; index++) result[index] = Bytes(in entries[index]);
        return result;
    }

    internal static float[]? Embedding(in RespValue value)
    {
        if (value.IsNull) return null;
        var entries = Array(in value);
        var result = new float[entries.Length];
        for (var index = 0; index < entries.Length; index++)
        {
            var component = Double(in entries[index]);
            if (component is > float.MaxValue or < -float.MaxValue) throw Invalid("FP32 component out of range");
            result[index] = (float)component;
        }
        return result;
    }

    internal static RespireVectorMatch[] Matches(in RespValue value, RespireVectorSearchOptions options)
    {
        var scores = options.IncludeScores;
        var attributes = options.IncludeAttributes;
        var map = value.Type == RespDataType.Map;
        if (map && !scores && !attributes) throw Invalid("unexpected search map without details");
        var entries = map ? value.AsArray() : Array(in value);
        var stride = map ? 2 : 1 + (scores ? 1 : 0) + (attributes ? 1 : 0);
        if (entries.Length % stride != 0) throw Invalid("incomplete search entry");
        var result = new RespireVectorMatch[entries.Length / stride];
        for (var index = 0; index < result.Length; index++)
        {
            var offset = index * stride;
            var member = Bytes(in entries[offset]);
            double? score = null;
            byte[]? json = null;
            if (map && scores && attributes)
            {
                var details = Array(in entries[offset + 1]);
                if (details.Length != 2) throw Invalid("search map details must contain score and attributes");
                score = Double(in details[0]);
                json = NullableBytes(in details[1]);
            }
            else
            {
                if (scores) score = Double(in entries[++offset]);
                if (attributes) json = NullableBytes(in entries[++offset]);
            }
            result[index] = new(member, score, json);
        }
        return result;
    }

    internal static RespireVectorMatch[][]? Links(in RespValue value, bool scores)
    {
        if (value.IsNull) return null;
        var levels = Array(in value);
        var result = new RespireVectorMatch[levels.Length][];
        for (var index = 0; index < levels.Length; index++)
            result[index] = Matches(in levels[index], new() { IncludeScores = scores });
        return result;
    }

    internal static RespireVectorSetInfo? Info(in RespValue value)
    {
        if (value.IsNull) return null;
        if (value.Type is not (RespDataType.Array or RespDataType.Map)) throw Invalid("expected an info map");
        var entries = value.AsArray();
        if (entries.Length % 2 != 0) throw Invalid("incomplete info field");
        var fields = new Dictionary<string, RespValue>(StringComparer.Ordinal);
        for (var index = 0; index < entries.Length; index += 2)
        {
            RequireString(in entries[index]);
            if (!fields.TryAdd(entries[index].AsString(), entries[index + 1])) throw Invalid("duplicate info field");
        }
        var quant = Take(fields, "quant-type");
        RequireString(in quant);
        var dimensions = Number(Take(fields, "vector-dim"));
        var size = Number(Take(fields, "size"));
        var links = Number(Take(fields, "hnsw-m"));
        var level = Number(Take(fields, "max-level"));
        var count = Number(Take(fields, "attributes-count"));
        long? projection = fields.Remove("projection-input-dim", out var projected) ? Number(in projected) : null;
        var extra = new Dictionary<string, RespireResult>(fields.Count, StringComparer.Ordinal);
        foreach (var field in fields) extra.Add(field.Key, RespireResult.CreateOwned(field.Value));
        return new(quant.AsString(), dimensions, size, links, level, count, projection, extra);
    }

    private static RespValue Take(Dictionary<string, RespValue> fields, string name)
        => fields.Remove(name, out var value) ? value : throw Invalid($"missing info field {name}");

    private static double Double(in RespValue value)
    {
        if (value.Type is not (RespDataType.Double or RespDataType.Integer or RespDataType.BulkString or RespDataType.SimpleString))
            throw Invalid("expected a numeric value");
        var result = ResponseReader.Double(in value);
        return double.IsFinite(result) ? result : throw Invalid("expected a finite numeric value");
    }

    private static ReadOnlySpan<RespValue> Array(in RespValue value)
        => value.Type == RespDataType.Array ? value.AsArray() : throw Invalid("expected an array");

    internal static void RequireString(in RespValue value)
    {
        if (value.Type is not (RespDataType.BulkString or RespDataType.SimpleString or RespDataType.VerbatimString))
            throw Invalid("expected a string");
    }

    private static RespireProtocolException Invalid(string detail) => new($"Invalid vector-set reply: {detail}.");
}
