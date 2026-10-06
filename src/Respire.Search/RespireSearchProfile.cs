using System.Collections.Frozen;
using System.Globalization;
using Respire.Protocol;

namespace Respire.Search;

/// <summary>A normal query result together with its owned FT.PROFILE tree.</summary>
/// <typeparam name="TResult">The existing typed Search, Aggregate, or Hybrid result.</typeparam>
/// <param name="Result">Query results parsed with the normal query semantics.</param>
/// <param name="Profile">Performance details, including shard and coordinator children.</param>
public sealed record RespireSearchProfileResult<TResult>(TResult Result, RespireSearchProfileNode Profile);

/// <summary>An owned profile section, iterator, or result processor.</summary>
/// <remarks>Profile values support at most 64 nested collection levels, including unknown fields.</remarks>
/// <param name="Name">The enclosing field name, or the numbered name of a list entry.</param>
/// <param name="Properties">Every reported field, including unknown fields and nested values.</param>
/// <param name="Metrics">All finite numeric scalar fields, including numeric strings and unknown names, decoded independently of the RESP protocol.</param>
/// <param name="Children">Nested profile sections in server order. Raw nested fields remain in Properties.</param>
public sealed record RespireSearchProfileNode(
    string Name,
    IReadOnlyDictionary<string, RespireSearchValue> Properties,
    IReadOnlyDictionary<string, double> Metrics,
    IReadOnlyList<RespireSearchProfileNode> Children)
{
    private const int MaximumCollectionDepth = 64;

    /// <summary>Iterator or processor type, when reported by the server.</summary>
    public string? Type => Properties.TryGetValue("Type", out var value) ? value.Scalar : null;

    /// <summary>Execution time in milliseconds, when this node reports Time.</summary>
    public double? TimeMilliseconds => Metrics.TryGetValue("Time", out var value) ? value : null;

    internal static RespireSearchProfileNode Parse(RespireResult result, string name = "Profile")
    {
        // Bound every collection before the owned-value copier or typed reader recurses.
        ValidateNesting(result, 0);
        return Parse(result, name, RespireSearchValue.From(result));
    }

    private static void ValidateNesting(RespireResult value, int depth)
    {
        if (!RespireSearchReply.IsCollection(value)) return;
        if (depth >= MaximumCollectionDepth)
            throw Unexpected("profile nesting exceeds 64 collection levels");
        for (var i = 0; i < value.Count; i++)
            ValidateNesting(value[i], depth + 1);
    }

    private static RespireSearchProfileNode Parse(RespireResult result, string name, RespireSearchValue owned)
    {
        RequireObject(result);
        var properties = new Dictionary<string, RespireSearchValue>(StringComparer.Ordinal);
        var metrics = new Dictionary<string, double>(StringComparer.Ordinal);
        var children = new List<RespireSearchProfileNode>();
        for (var i = 0; i < result.Count; i += 2)
        {
            var key = result[i];
            if (!IsName(key)) throw Unexpected("a non-string or empty field name");
            var field = key.AsString();
            var kind = ProfileSchema.GetKind(field);
            var value = result[i + 1];
            if (field == "Type" && !IsName(value)) throw Unexpected("a non-string or empty node type");
            // Copy the tree once, then reuse its owned values in both raw properties and typed children.
            var copied = owned.Items[i + 1];
            if (!properties.TryAdd(field, copied))
                throw Unexpected("a duplicate field name");

            if (TryReadNumber(value, out var number))
                metrics.Add(field, number);
            else if (kind == ProfileFieldKind.Metric)
                throw Unexpected("a non-numeric metric " + field);

            if (kind == ProfileFieldKind.ListNode)
            {
                if (value.IsNull || value.Type != RespDataType.Array)
                    throw Unexpected("a non-array " + field);
                for (var j = 0; j < value.Count; j++)
                    children.Add(Parse(value[j], field + " #" + (j + 1).ToString(CultureInfo.InvariantCulture), copied.Items[j]));
            }
            else if (kind == ProfileFieldKind.ObjectNode || value.Type == RespDataType.Map)
            {
                children.Add(Parse(value, field, copied));
            }
        }

        return new(name, properties, metrics, children);
    }

    private enum ProfileFieldKind { Unknown, Metric, ListNode, ObjectNode }

    private static class ProfileSchema
    {
        // Redis 8.10 field names. Unknown fields retain their raw values and numeric metrics.
        private static readonly FrozenDictionary<string, ProfileFieldKind> Fields =
            new Dictionary<string, ProfileFieldKind>(StringComparer.Ordinal)
            {
                ["Total profile time"] = ProfileFieldKind.Metric,
                ["Parsing time"] = ProfileFieldKind.Metric,
                ["Workers queue time"] = ProfileFieldKind.Metric,
                ["Pipeline creation time"] = ProfileFieldKind.Metric,
                ["Total GIL time"] = ProfileFieldKind.Metric,
                ["Time"] = ProfileFieldKind.Metric,
                ["GIL-Time"] = ProfileFieldKind.Metric,
                ["Number of reading operations"] = ProfileFieldKind.Metric,
                ["Estimated number of matches"] = ProfileFieldKind.Metric,
                ["Results processed"] = ProfileFieldKind.Metric,
                ["Internal cursor reads"] = ProfileFieldKind.Metric,
                ["Shards"] = ProfileFieldKind.ListNode,
                ["Result processors profile"] = ProfileFieldKind.ListNode,
                ["Child iterators"] = ProfileFieldKind.ListNode,
                ["Coordinator"] = ProfileFieldKind.ObjectNode,
                ["Iterators profile"] = ProfileFieldKind.ObjectNode,
                ["Child iterator"] = ProfileFieldKind.ObjectNode,
                ["SEARCH"] = ProfileFieldKind.ObjectNode,
                ["VSIM"] = ProfileFieldKind.ObjectNode,
            }.ToFrozenDictionary(StringComparer.Ordinal);

        internal static ProfileFieldKind GetKind(string field)
        {
            if (Fields.TryGetValue(field, out var kind)) return kind;
            // Multi-shard RESP2 profiles wrap each shard in a labeled field/value array.
            return field.StartsWith("Shard #", StringComparison.Ordinal)
                && int.TryParse(field.AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture, out var shardNumber)
                && shardNumber > 0 ? ProfileFieldKind.ObjectNode : ProfileFieldKind.Unknown;
        }
    }

    private static bool TryReadNumber(RespireResult value, out double number)
    {
        if (value.IsNull || RespireSearchReply.IsCollection(value))
        {
            number = default;
            return false;
        }

        switch (value.Type)
        {
            case RespDataType.Integer:
                number = value.AsInteger();
                return true;
            case RespDataType.Double:
                number = value.AsDouble();
                return double.IsFinite(number);
            case RespDataType.BulkString or RespDataType.SimpleString or RespDataType.BigNumber:
                return double.TryParse(value.AsString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                    && double.IsFinite(number);
            default:
                number = default;
                return false;
        }
    }

    private static bool IsName(RespireResult value) => !value.IsNull &&
        value.Type is RespDataType.BulkString or RespDataType.SimpleString && value.AsString().Length != 0;

    private static void RequireObject(RespireResult value)
    {
        if (value.IsNull || value.Type is not (RespDataType.Array or RespDataType.Map) || (value.Count & 1) != 0)
            throw Unexpected("a value that is not a field/value array or map");
    }

    private static InvalidOperationException Unexpected(string detail) => RespireSearchReply.Unexpected("FT.PROFILE", detail);
}
