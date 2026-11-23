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

    internal static RespireSearchProfileNode Parse(RespireResult result, bool limited, bool inlineResp3 = false)
    {
        // Bound every collection before the owned-value copier or typed reader recurses.
        ValidateNesting(result, 0);
        var owned = RespireSearchValue.From(result);
        var layout = inlineResp3 ? ProfileLayout.InlineResp3 : ProfileLayout.Flat;

        if (owned.Type == RespireDataType.Array && owned.Items.Count > 0 && owned.Items[0].Type == RespireDataType.Array)
        {
            return Parse(owned, "Profile", ProfileLayout.GroupedResp2, limited, groupedFields: true);
        }

        return Parse(owned, "Profile", layout, limited);
    }

    private static void ValidateNesting(RespireResult value, int depth)
    {
        if (!RespireSearchReply.IsCollection(value))
        {
            return;
        }

        if (depth >= MaximumCollectionDepth)
        {
            throw Unexpected("profile nesting exceeds 64 collection levels");
        }

        for (var i = 0; i < value.Count; i++)
        {
            ValidateNesting(value[i], depth + 1);
        }
    }

    private static RespireSearchProfileNode Parse(
        RespireSearchValue value, string name, ProfileLayout layout, bool limited, bool groupedFields = false)
    {
        if (value.Type is not (RespireDataType.Array or RespireDataType.Map))
        {
            throw Unexpected("a value that is not a field/value array or map");
        }

        var properties = new Dictionary<string, RespireSearchValue>(StringComparer.Ordinal);
        var metrics = new Dictionary<string, double>(StringComparer.Ordinal);
        var children = new List<RespireSearchProfileNode>();
        var items = value.Items;

        for (int i = 0; i < items.Count;)
        {
            if (groupedFields)
            {
                var group = items[i++];

                if (group.Type != RespireDataType.Array || group.Items.Count == 0)
                {
                    throw Unexpected("an invalid grouped profile field");
                }

                string field = ReadFieldName(group.Items[0]);
                // Processors and warnings follow the field name directly in the same group.
                var fieldValue = group.Items.Count == 2 && field is not ("Result processors profile" or "Warning")
                    ? group.Items[1] : CopyList(group.Items, 1, group.Items.Count);
                AddField(field, fieldValue, missingValue: group.Items.Count == 1);
                continue;
            }

            string fieldName = ReadFieldName(items[i++]);

            if (layout == ProfileLayout.GroupedResp2 && fieldName == "Child iterators" &&
                (i == items.Count || items[i].Type is RespireDataType.Array or RespireDataType.Null))
            {
                // Legacy RESP2 nodes put child arrays and depth-limit nulls directly after the label.
                int childStart = i;

                while (i < items.Count && items[i].Type is RespireDataType.Array or RespireDataType.Null)
                {
                    i++;
                }

                AddField(fieldName, CopyList(items, childStart, i));
                continue;
            }

            if (i == items.Count)
            {
                throw Unexpected("a field without its value");
            }

            AddField(fieldName, items[i++]);
        }

        return new(name, properties, metrics, children);

        void AddField(string field, RespireSearchValue fieldValue, bool missingValue = false)
        {
            var kind = ProfileSchema.GetKind(field);

            if (field == "Type" && !IsText(fieldValue))
            {
                throw Unexpected("a non-string or empty node type");
            }

            if (!properties.TryAdd(field, fieldValue))
            {
                throw Unexpected("a duplicate field name");
            }

            if (TryReadNumber(fieldValue, out double number))
            {
                metrics.Add(field, number);
            }
            else if (kind == ProfileFieldKind.Metric && !missingValue)
            {
                throw Unexpected("a non-numeric metric " + field);
            }

            if (kind == ProfileFieldKind.ListNode)
            {
                if (field == "Child iterators" && limited && IsLimitedIteratorSummary(fieldValue))
                {
                    return;
                }

                if (fieldValue.Type != RespireDataType.Array)
                {
                    throw Unexpected("a non-array " + field);
                }

                for (int j = 0; j < fieldValue.Items.Count; j++)
                {
                    var item = fieldValue.Items[j];

                    // Older Redis servers can return null when an iterator reaches their depth limit.
                    if (layout == ProfileLayout.GroupedResp2 && field == "Child iterators" && item.Type == RespireDataType.Null)
                    {
                        continue;
                    }

                    children.Add(Parse(item, field + " #" + (j + 1).ToString(CultureInfo.InvariantCulture), layout, limited));
                }
            }
            else if (kind == ProfileFieldKind.ObjectNode || fieldValue.Type == RespireDataType.Map)
            {
                if (layout == ProfileLayout.GroupedResp2 && field == "Child iterator" && fieldValue.Type == RespireDataType.Null)
                {
                    return;
                }

                // Legacy RESP3 wraps the root iterator in a one-element array.
                var node = layout == ProfileLayout.InlineResp3 && field == "Iterators profile" &&
                    fieldValue.Type == RespireDataType.Array && fieldValue.Items.Count == 1
                    ? fieldValue.Items[0] : fieldValue;
                children.Add(Parse(node, field, layout, limited));
            }
        }
    }

    private enum ProfileLayout
    {
        Flat,
        GroupedResp2,
        InlineResp3
    }

    private enum ProfileFieldKind
    {
        Unknown,
        Metric,
        ListNode,
        ObjectNode
    }

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
            if (Fields.TryGetValue(field, out var kind))
            {
                return kind;
            }
            // Multi-shard RESP2 profiles wrap each shard in a labeled field/value array.
            return field.StartsWith("Shard #", StringComparison.Ordinal)
                && int.TryParse(field.AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture, out var shardNumber)
                && shardNumber > 0 ? ProfileFieldKind.ObjectNode : ProfileFieldKind.Unknown;
        }
    }

    private static bool TryReadNumber(RespireSearchValue value, out double number)
    {
        number = default;
        return value.Type is RespireDataType.Integer or RespireDataType.Double or RespireDataType.BulkString
                or RespireDataType.SimpleString or RespireDataType.BigNumber
            && double.TryParse(value.Scalar, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            && double.IsFinite(number);
    }

    private static string ReadFieldName(RespireSearchValue value)
    {
        if (!IsText(value))
        {
            throw Unexpected("a non-string or empty field name");
        }

        return value.Scalar!;
    }

    private static bool IsText(RespireSearchValue value)
    {
        return value.Type is RespireDataType.BulkString or RespireDataType.SimpleString &&
            !string.IsNullOrEmpty(value.Scalar);
    }

    private static bool IsLimitedIteratorSummary(RespireSearchValue value)
    {
        const string Prefix = "The number of iterators in the union is ";

        if (!IsText(value) || !value.Scalar!.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> count = value.Scalar.AsSpan(Prefix.Length);
        return !count.IsEmpty && count.IndexOfAnyExceptInRange('0', '9') == -1;
    }

    private static RespireSearchValue CopyList(IReadOnlyList<RespireSearchValue> items, int start, int end)
    {
        var copy = new RespireSearchValue[end - start];

        for (int i = 0; i < copy.Length; i++)
        {
            copy[i] = items[start + i];
        }

        return new(RespireDataType.Array, null, copy);
    }

    private static InvalidOperationException Unexpected(string detail) => RespireSearchReply.Unexpected("FT.PROFILE", detail);
}
