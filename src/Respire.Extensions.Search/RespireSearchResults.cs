using System.Globalization;
using Respire;
using Respire.Protocol;

namespace Redis.Search;

/// <summary>Search response document with identifier and projected fields.</summary>
public sealed record RespireSearchDocument(string Id, IReadOnlyDictionary<string, string?> Fields, double? Score = null)
{
    private string _id = Id;
    private RespireKey _documentKey = new(Id);

    /// <summary>Document identifier. Setting it also resets <see cref="DocumentKey"/> to its UTF-8 key.</summary>
    public string Id
    {
        get => _id;
        init
        {
            if (!string.Equals(_id, value, StringComparison.Ordinal))
                _documentKey = new RespireKey(value);
            _id = value;
        }
    }

    /// <summary>Binary-safe Redis document key. Use this when the identifier is not valid UTF-8.</summary>
    public RespireKey DocumentKey
    {
        get => _documentKey;
        init => _documentKey = value;
    }

    /// <summary>Typed projected values, including binary string fields.</summary>
    public IReadOnlyDictionary<string, RespireSearchValue> StructuredFields { get; init; } = RespireSearchEmpty.SearchValues;
}

/// <summary>Parsed FT.SEARCH or FT.HYBRID results.</summary>
public sealed record RespireSearchResult(long Total, IReadOnlyList<RespireSearchDocument> Documents, IReadOnlyList<string> Warnings)
{
    // RESP2 FT.SEARCH replies are positional ([total, id, score?, fields?, ...]); which slots exist
    // depends on the NOCONTENT and WITHSCORES request flags, so the parser needs them.
    internal static RespireSearchResult Parse(RespireResult result, bool noContent, bool withScores)
    {
        if (result.Type == RespDataType.Map) return ParseResp3(result);
        if (result.Count == 0) throw RespireSearchReply.Unexpected("FT.SEARCH", "an empty reply");
        var total = result[0].AsInteger();
        var docs = new List<RespireSearchDocument>();
        for (var i = 1; i < result.Count;)
        {
            var idValue = result[i++];
            var id = idValue.AsString();
            var documentKey = new RespireKey(idValue.AsBytes());
            double? score = null;
            if (withScores)
            {
                if (i == result.Count) throw RespireSearchReply.Unexpected("FT.SEARCH", "a document without its score");
                score = result[i++].AsDouble();
            }

            if (noContent)
            {
                docs.Add(new(id, RespireSearchEmpty.NullableStrings, score) { DocumentKey = documentKey });
                continue;
            }

            if (i == result.Count) throw RespireSearchReply.Unexpected("FT.SEARCH", "a document without its fields");
            var fieldsValue = result[i++];
            if (fieldsValue.IsNull)
            {
                docs.Add(new(id, RespireSearchEmpty.NullableStrings, score) { DocumentKey = documentKey });
                continue;
            }

            var fields = RespireSearchReply.ReadFields(fieldsValue, "FT.SEARCH");
            docs.Add(new(id, fields.Fields, score) { StructuredFields = fields.Structured, DocumentKey = documentKey });
        }

        return new(total, docs, []);
    }

    // FT.HYBRID returns the same key/value layout on both protocols: a flat array on RESP2 and a
    // map on RESP3, with total_results, results, warnings, and execution_time entries.
    internal static RespireSearchResult ParseHybrid(RespireResult result)
    {
        RespireSearchReply.RequirePairs(result, "FT.HYBRID");
        long total = 0;
        var documents = new List<RespireSearchDocument>();
        var warnings = new List<string>();
        for (var i = 0; i < result.Count; i += 2)
        {
            var key = result[i].AsString();
            var value = result[i + 1];
            switch (key)
            {
                case "total_results":
                    total = RespireSearchReply.ReadInteger(value);
                    break;
                case "warnings" or "warning":
                    RespireSearchReply.AddStrings(value, warnings);
                    break;
                case "results":
                    for (var j = 0; j < value.Count; j++) AddHybridDocument(value[j], documents);
                    break;
            }
        }

        return new(total, documents, warnings);
    }

    private static RespireSearchResult ParseResp3(RespireResult result)
    {
        long total = 0;
        var docs = new List<RespireSearchDocument>();
        var warnings = new List<string>();
        for (var i = 0; i + 1 < result.Count; i += 2)
        {
            var key = result[i].AsString();
            var value = result[i + 1];
            switch (key)
            {
                case "total_results":
                    total = RespireSearchReply.ReadInteger(value);
                    break;
                case "warnings" or "warning":
                    RespireSearchReply.AddStrings(value, warnings);
                    break;
                case "results":
                    for (var j = 0; j < value.Count; j++) AddResp3Document(value[j], docs);
                    break;
            }
        }

        return new(total, docs, warnings);
    }

    private static void AddResp3Document(RespireResult item, List<RespireSearchDocument> docs)
    {
        RespireSearchReply.RequirePairs(item, "FT.SEARCH");
        string? id = null;
        RespireKey documentKey = default;
        double? score = null;
        IReadOnlyDictionary<string, string?> fields = RespireSearchEmpty.NullableStrings;
        IReadOnlyDictionary<string, RespireSearchValue> structured = RespireSearchEmpty.SearchValues;
        for (var k = 0; k < item.Count; k += 2)
        {
            var itemKey = item[k].AsString();
            var itemValue = item[k + 1];
            switch (itemKey)
            {
                case "id" or "key" or "keyid" or "__key":
                    id = itemValue.AsString();
                    documentKey = new RespireKey(itemValue.AsBytes());
                    break;
                case "score" or "__score":
                    score = itemValue.AsDouble();
                    break;
                case "extra_attributes":
                    var parsed = RespireSearchReply.ReadFields(itemValue, "FT.SEARCH");
                    fields = parsed.Fields;
                    structured = parsed.Structured;
                    break;
            }
        }

        if (id is null) throw RespireSearchReply.Unexpected("FT.SEARCH", "a result row without an id");
        docs.Add(new(id, fields, score) { StructuredFields = structured, DocumentKey = documentKey });
    }

    // FT.HYBRID rows carry the reserved __key and __score names. When they are present, names such
    // as id or score are ordinary loaded fields and must not replace the document identity.
    private static void AddHybridDocument(RespireResult row, List<RespireSearchDocument> documents)
    {
        RespireSearchReply.RequirePairs(row, "FT.HYBRID");
        string? id = null;
        RespireKey documentKey = default;
        double? score = null;
        var fields = new Dictionary<string, string?>(StringComparer.Ordinal);
        var structuredFields = new Dictionary<string, RespireSearchValue>(StringComparer.Ordinal);
        var reserved = HasReservedHybridKey(row);
        for (var j = 0; j < row.Count; j += 2)
        {
            var key = row[j].AsString();
            var value = row[j + 1];
            if (reserved ? key == "__key" : key is "id" or "key" or "keyid")
            {
                id = value.AsString();
                documentKey = new RespireKey(value.AsBytes());
            }
            else if (reserved ? key == "__score" : key == "score")
            {
                score = value.AsDouble();
            }
            else if (key == "extra_attributes" && IsFieldList(value))
            {
                var parsed = RespireSearchReply.ReadFields(value, "FT.HYBRID");
                foreach (var (name, text) in parsed.Fields) fields[name] = text;
                foreach (var (name, structured) in parsed.Structured) structuredFields[name] = structured;
            }
            else
            {
                fields[key] = value.IsNull ? null : value.AsString();
                structuredFields[key] = RespireSearchValue.From(value);
            }
        }

        if (id is null) throw RespireSearchReply.Unexpected("FT.HYBRID", "a result row without __key");
        documents.Add(new(id, fields, score) { StructuredFields = structuredFields, DocumentKey = documentKey });
    }

    private static bool HasReservedHybridKey(RespireResult row)
    {
        for (var j = 0; j < row.Count; j += 2)
        {
            if (row[j].AsString() == "__key") return true;
        }

        return false;
    }

    // A loaded field may itself be named extra_attributes; only unwrap values shaped like a field list.
    private static bool IsFieldList(RespireResult value)
    {
        if (value.Type is not (RespDataType.Array or RespDataType.Map) || (value.Count & 1) != 0) return false;
        for (var i = 0; i < value.Count; i += 2)
        {
            if (value[i].Type is not (RespDataType.SimpleString or RespDataType.BulkString or RespDataType.VerbatimString))
                return false;
        }

        return true;
    }
}

/// <summary>Aggregation response rows.</summary>
public sealed record RespireSearchAggregateResult(long Total, IReadOnlyList<IReadOnlyDictionary<string, string?>> Rows)
{
    /// <summary>Rows with nested RESP collections preserved for collection-valued reducers such as TOLIST.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, RespireSearchValue>> StructuredRows { get; init; } = [];

    internal static RespireSearchAggregateResult Parse(RespireResult result)
    {
        var rows = new List<IReadOnlyDictionary<string, string?>>();
        var structuredRows = new List<IReadOnlyDictionary<string, RespireSearchValue>>();
        long total = 0;
        if (result.Type == RespDataType.Map)
        {
            for (var i = 0; i + 1 < result.Count; i += 2)
            {
                var key = result[i].AsString();
                var value = result[i + 1];
                if (key == "total_results")
                {
                    total = RespireSearchReply.ReadInteger(value);
                }
                else if (key == "results")
                {
                    for (var j = 0; j < value.Count; j++)
                        AddRow(UnwrapExtraAttributes(value[j]), rows, structuredRows);
                }
            }
        }
        else
        {
            if (result.Count == 0) throw RespireSearchReply.Unexpected("FT.AGGREGATE", "an empty reply");
            total = result[0].AsInteger();
            for (var i = 1; i < result.Count; i++) AddRow(result[i], rows, structuredRows);
        }

        return new(total, rows) { StructuredRows = structuredRows };
    }

    // RESP3 rows wrap their columns as { extra_attributes: {...}, values: [...] }.
    private static RespireResult UnwrapExtraAttributes(RespireResult item)
    {
        RespireSearchReply.RequirePairs(item, "FT.AGGREGATE");
        for (var k = 0; k < item.Count; k += 2)
        {
            if (item[k].AsString() == "extra_attributes") return item[k + 1];
        }

        return item;
    }

    private static void AddRow(
        RespireResult values,
        List<IReadOnlyDictionary<string, string?>> rows,
        List<IReadOnlyDictionary<string, RespireSearchValue>> structuredRows)
    {
        var parsed = RespireSearchReply.ReadFields(values, "FT.AGGREGATE");
        rows.Add(parsed.Fields);
        structuredRows.Add(parsed.Structured);
    }
}

/// <summary>One page of a cursor-based aggregation.</summary>
/// <param name="Result">Rows in this page.</param>
/// <param name="CursorId">Cursor for the next page, or 0 when the server has no more rows.</param>
public sealed record RespireSearchAggregateCursorPage(RespireSearchAggregateResult Result, long CursorId)
{
    /// <summary>True when the server returned the last page and released the cursor.</summary>
    public bool IsComplete => CursorId == 0;

    /// <summary>
    /// Index the cursor belongs to. Pages returned by <see cref="RespireSearchClient"/> always set it,
    /// so the page-based cursor overloads cannot pair a cursor with the wrong index.
    /// </summary>
    public string? Index { get; init; }

    internal static RespireSearchAggregateCursorPage Parse(RespireResult result, string command, string index)
    {
        if (result.Type != RespDataType.Array || result.Count != 2)
            throw RespireSearchReply.Unexpected(command, "a reply that is not [results, cursor]");
        return new(RespireSearchAggregateResult.Parse(result[0]), result[1].AsInteger()) { Index = index };
    }

    /// <summary>Reads the cursor ID from a <c>[results, cursor]</c> reply whose results may be malformed; 0 when there is none.</summary>
    internal static long ReadCursorId(RespireResult result)
        => result.Type == RespDataType.Array && result.Count == 2 && result[1].Type == RespDataType.Integer
            ? result[1].AsInteger()
            : 0;
}

/// <summary>Parsed FT.INFO response.</summary>
/// <param name="Name">Index name.</param>
/// <param name="DocumentCount">Number of indexed documents (<c>num_docs</c>).</param>
/// <param name="Attributes">Schema attributes.</param>
public sealed record RespireSearchIndexInfo(string Name, long DocumentCount, IReadOnlyList<RespireSearchIndexAttribute> Attributes)
{
    /// <summary>Every top-level FT.INFO entry by name, copied so it outlives the response.</summary>
    public IReadOnlyDictionary<string, RespireSearchValue> Properties { get; init; } = RespireSearchEmpty.SearchValues;

    internal static RespireSearchIndexInfo Parse(RespireResult result)
    {
        RespireSearchReply.RequirePairs(result, "FT.INFO");
        string? name = null;
        long documents = 0;
        var attributes = new List<RespireSearchIndexAttribute>();
        var properties = new Dictionary<string, RespireSearchValue>(StringComparer.Ordinal);
        for (var i = 0; i < result.Count; i += 2)
        {
            var key = result[i].AsString();
            var value = result[i + 1];
            properties[key] = RespireSearchValue.From(value);
            switch (key)
            {
                case "index_name":
                    name = value.AsString();
                    break;
                case "num_docs":
                    documents = RespireSearchReply.ReadInteger(value);
                    break;
                case "attributes":
                    for (var j = 0; j < value.Count; j++) attributes.Add(RespireSearchIndexAttribute.Parse(value[j]));
                    break;
            }
        }

        if (name is null) throw RespireSearchReply.Unexpected("FT.INFO", "a reply without index_name");
        return new(name, documents, attributes) { Properties = properties };
    }
}

/// <summary>One schema attribute reported by FT.INFO.</summary>
/// <param name="Identifier">Hash field name or JSON path.</param>
/// <param name="Attribute">Attribute name used in queries.</param>
/// <param name="Type">Field type token, such as <c>TEXT</c> or <c>VECTOR</c>.</param>
public sealed record RespireSearchIndexAttribute(string Identifier, string Attribute, string Type)
{
    private static readonly HashSet<string> KnownFlags = new(StringComparer.Ordinal)
    {
        "SORTABLE", "UNF", "NOSTEM", "NOINDEX", "CASESENSITIVE", "WITHSUFFIXTRIE", "INDEXEMPTY", "INDEXMISSING",
    };

    /// <summary>Flags such as <c>SORTABLE</c> or <c>NOSTEM</c>.</summary>
    public IReadOnlyList<string> Flags { get; init; } = [];

    /// <summary>Other attribute settings by name, such as <c>WEIGHT</c>, <c>SEPARATOR</c>, or <c>dim</c>.</summary>
    public IReadOnlyDictionary<string, RespireSearchValue> Options { get; init; } = RespireSearchEmpty.SearchValues;

    internal static RespireSearchIndexAttribute Parse(RespireResult value)
    {
        string identifier = "", attribute = "", type = "";
        var flags = new List<string>();
        var options = new Dictionary<string, RespireSearchValue>(StringComparer.Ordinal);
        var map = value.Type == RespDataType.Map;
        for (var i = 0; i < value.Count;)
        {
            var key = value[i].AsString();
            var hasValue = i + 1 < value.Count;
            if (hasValue && key is "identifier" or "attribute" or "type")
            {
                var text = value[i + 1].AsString();
                if (key == "identifier") identifier = text;
                else if (key == "attribute") attribute = text;
                else type = text;
                i += 2;
            }
            else if (map && key == "flags")
            {
                RespireSearchReply.AddStrings(value[i + 1], flags);
                i += 2;
            }
            else if (!map && (KnownFlags.Contains(key) || !hasValue))
            {
                // RESP2 lists flags as standalone tokens between key/value settings.
                flags.Add(key);
                i++;
            }
            else
            {
                options[key] = RespireSearchValue.From(value[i + 1]);
                i += 2;
            }
        }

        return new(identifier, attribute, type) { Flags = flags, Options = options };
    }
}

/// <summary>A copied RESP value for a typed search field or aggregate value.</summary>
/// <param name="Type">RESP wire type.</param>
/// <param name="Scalar">Decoded scalar value, or null for null and collection types.</param>
/// <param name="Items">Collection elements in wire order; map items alternate keys and values.</param>
/// <param name="Bytes">Copied raw bytes for RESP string-like values.</param>
public sealed record RespireSearchValue(
    RespDataType Type,
    string? Scalar,
    IReadOnlyList<RespireSearchValue> Items,
    ReadOnlyMemory<byte>? Bytes = null)
{
    internal static RespireSearchValue From(RespireResult value)
    {
        if (RespireSearchReply.IsCollection(value))
        {
            var items = new RespireSearchValue[value.Count];
            for (var i = 0; i < items.Length; i++) items[i] = From(value[i]);
            return new(value.Type, null, items);
        }

        var bytes = value.Type is RespDataType.BulkString or RespDataType.VerbatimString or RespDataType.BulkError
            ? value.AsBytes().AsMemory()
            : (ReadOnlyMemory<byte>?)null;
        return new(value.Type, value.IsNull ? null : value.AsString(), [], bytes);
    }
}

/// <summary>Shared readers for Redis Search replies. Malformed shapes throw instead of dropping data.</summary>
internal static class RespireSearchReply
{
    internal static bool IsCollection(RespireResult value)
        => value.Type is RespDataType.Array or RespDataType.Map or RespDataType.Set or RespDataType.Push or RespDataType.Attribute;

    internal static void RequirePairs(RespireResult value, string command)
    {
        if (!IsCollection(value) || (value.Count & 1) != 0)
            throw Unexpected(command, $"a {value.Type} where key/value pairs were expected");
    }

    internal static (Dictionary<string, string?> Fields, Dictionary<string, RespireSearchValue> Structured) ReadFields(RespireResult values, string command)
    {
        RequirePairs(values, command);
        var fields = new Dictionary<string, string?>(StringComparer.Ordinal);
        var structured = new Dictionary<string, RespireSearchValue>(StringComparer.Ordinal);
        for (var i = 0; i < values.Count; i += 2)
        {
            var name = values[i].AsString();
            var value = values[i + 1];
            fields[name] = value.IsNull ? null : value.AsString();
            structured[name] = RespireSearchValue.From(value);
        }

        return (fields, structured);
    }

    internal static void AddStrings(RespireResult values, List<string> target)
    {
        for (var j = 0; j < values.Count; j++) target.Add(values[j].AsString());
    }

    // Some servers report counts as integers, others as numeric strings or doubles.
    internal static long ReadInteger(RespireResult value) => value.Type switch
    {
        RespDataType.Integer => value.AsInteger(),
        RespDataType.Double => checked((long)value.AsDouble()),
        _ => long.Parse(value.AsString(), NumberStyles.Integer, CultureInfo.InvariantCulture),
    };

    internal static InvalidOperationException Unexpected(string command, string detail)
        => new($"Unexpected {command} reply: {detail}.");
}
