using System.Text;
using Respire.Protocol;

namespace Respire.Extensions.Search;

/// <summary>Search index source type.</summary>
public enum RespireSearchSource
{
    /// <summary>Index Redis hashes.</summary>
    Hash,
    /// <summary>Index RedisJSON documents.</summary>
    Json,
}

/// <summary>Search field type.</summary>
public enum RespireSearchFieldType
{
    /// <summary>Full text field.</summary>
    Text,
    /// <summary>Exact tag field.</summary>
    Tag,
    /// <summary>Numeric field.</summary>
    Numeric,
    /// <summary>Geospatial field.</summary>
    Geo,
    /// <summary>Geoshape field.</summary>
    GeoShape,
    /// <summary>Vector field.</summary>
    Vector,
}

/// <summary>Index schema field definition.</summary>
public sealed record RespireSearchField(string Identifier, RespireSearchFieldType Type, string? Alias = null, bool Sortable = false, bool NoIndex = false, IReadOnlyList<string>? Options = null)
{
    internal RespireValue[] ToArguments()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Identifier);
        var args = new List<RespireValue> { Identifier };
        if (!string.IsNullOrWhiteSpace(Alias))
        {
            args.Add("AS");
            args.Add(Alias);
        }

        args.Add(Type switch
        {
            RespireSearchFieldType.Text => "TEXT",
            RespireSearchFieldType.Tag => "TAG",
            RespireSearchFieldType.Numeric => "NUMERIC",
            RespireSearchFieldType.Geo => "GEO",
            RespireSearchFieldType.GeoShape => "GEOSHAPE",
            RespireSearchFieldType.Vector => "VECTOR",
            _ => throw new ArgumentOutOfRangeException(nameof(Type)),
        });
        if (Type == RespireSearchFieldType.Vector && (Options is null || Options.Count == 0))
            throw new ArgumentException("Vector fields require algorithm and vector options.", nameof(Options));
        if (Type == RespireSearchFieldType.Vector && (Sortable || NoIndex))
            throw new ArgumentException("Vector fields cannot be SORTABLE or NOINDEX.");
        if (Options is not null)
        {
            foreach (var option in Options) args.Add(option);
        }
        if (Sortable) args.Add("SORTABLE");
        if (NoIndex) args.Add("NOINDEX");
        return [.. args];
    }
}

/// <summary>Index source, key prefixes, and schema.</summary>
public sealed record RespireSearchIndexDefinition
{
    /// <summary>Source type. Defaults to HASH.</summary>
    public RespireSearchSource Source { get; init; } = RespireSearchSource.Hash;
    /// <summary>Prefixes included in index.</summary>
    public IReadOnlyList<string> Prefixes { get; init; } = [];
    /// <summary>Fields indexed by this definition.</summary>
    public IReadOnlyList<RespireSearchField> Fields { get; init; } = [];
    /// <summary>Builds a complete FT.CREATE argument list.</summary>
    internal RespireValue[] ToArguments()
    {
        ArgumentNullException.ThrowIfNull(Prefixes);
        ArgumentNullException.ThrowIfNull(Fields);
        if (Fields.Count == 0) throw new ArgumentException("At least one schema field is required.", nameof(Fields));
        var source = Source switch
        {
            RespireSearchSource.Hash => "HASH",
            RespireSearchSource.Json => "JSON",
            _ => throw new ArgumentOutOfRangeException(nameof(Source)),
        };
        var args = new List<RespireValue> { "ON", source };
        if (Prefixes.Count > 0)
        {
            args.Add("PREFIX");
            args.Add(Prefixes.Count);
            foreach (var prefix in Prefixes)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
                args.Add(prefix);
            }
        }
        args.Add("SCHEMA");
        foreach (var field in Fields)
        {
            ArgumentNullException.ThrowIfNull(field);
            args.AddRange(field.ToArguments());
        }
        return [.. args];
    }
}

/// <summary>Search query with typed modifiers.</summary>
public sealed record RespireSearchQuery(string Expression, RespireSearchQueryOptions? Options = null)
{
    internal RespireValue[] ToArguments() => (Options ?? new RespireSearchQueryOptions()).ToArguments();
}

/// <summary>Composable helpers for common RediSearch query expressions.</summary>
public static class RespireSearchQueryBuilder
{
    /// <summary>Matches a text term across indexed text fields.</summary>
    public static string Text(string term) => Quote(term);
    /// <summary>Matches one text field.</summary>
    public static string TextField(string field, string term) => $"@{EscapeField(field)}:{Quote(term)}";
    /// <summary>Matches an exact tag value.</summary>
    public static string Tag(string field, string value) => $"@{EscapeField(field)}:{{{EscapeTag(value)}}}";
    /// <summary>Matches an inclusive numeric range.</summary>
    public static string NumericRange(string field, double minimum, double maximum)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        if (double.IsNaN(minimum)) throw new ArgumentOutOfRangeException(nameof(minimum));
        if (double.IsNaN(maximum)) throw new ArgumentOutOfRangeException(nameof(maximum));
        if (minimum > maximum) throw new ArgumentOutOfRangeException(nameof(minimum));
        var lower = FormatBound(minimum);
        var upper = FormatBound(maximum);
        return $"@{EscapeField(field)}:[{lower} {upper}]";
    }
    private static string FormatBound(double value) => value switch
    {
        double.NegativeInfinity => "-inf",
        double.PositiveInfinity => "+inf",
        _ => value.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
    /// <summary>Combines expressions with AND.</summary>
    public static string And(params string[] expressions) => Combine(" ", expressions);
    /// <summary>Combines expressions with OR.</summary>
    public static string Or(params string[] expressions) => Combine(" | ", expressions);
    private static string Combine(string separator, string[] expressions)
    {
        ArgumentNullException.ThrowIfNull(expressions);
        if (expressions.Length == 0) throw new ArgumentException("At least one expression is required.", nameof(expressions));
        foreach (var expression in expressions) ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        return expressions.Length == 1 ? expressions[0] : $"({string.Join(separator, expressions.Select(value => $"({value})"))})";
    }
    private static string Quote(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var escaped = value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }

    private static string EscapeTag(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var escaped = new System.Text.StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
        {
            if (!System.Text.Rune.IsLetterOrDigit(rune) && rune.Value != '_') escaped.Append('\\');
            escaped.Append(rune.ToString());
        }
        return escaped.ToString();
    }

    private static string EscapeField(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var escaped = new System.Text.StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
        {
            if (!System.Text.Rune.IsLetterOrDigit(rune) && rune.Value != '_') escaped.Append('\\');
            escaped.Append(rune.ToString());
        }
        return escaped.ToString();
    }

    private static string Require(string value) { ArgumentException.ThrowIfNullOrWhiteSpace(value); return value; }
}

/// <summary>Modifiers for FT.SEARCH.</summary>
public sealed record RespireSearchQueryOptions
{
    /// <summary>Field names to return. Empty means server default.</summary>
    public IReadOnlyList<string> ReturnFields { get; init; } = [];
    /// <summary>Return identifiers only.</summary>
    public bool NoContent { get; init; }
    /// <summary>Include scores in server response.</summary>
    public bool WithScores { get; init; }
    /// <summary>Zero-based offset and result count.</summary>
    public (int Offset, int Count)? Limit { get; init; }
    /// <summary>Optional sort field and direction.</summary>
    public (string Field, RespireSearchSortDirection Direction)? SortBy { get; init; }
    /// <summary>Query dialect version.</summary>
    public int? Dialect { get; init; }
    /// <summary>Maximum server execution time in milliseconds.</summary>
    public int? TimeoutMilliseconds { get; init; }
    /// <summary>Named query parameters.</summary>
    public IReadOnlyDictionary<string, RespireValue> Parameters { get; init; } = new Dictionary<string, RespireValue>();
    internal RespireValue[] ToArguments()
    {
        var args = new List<RespireValue>();
        if (NoContent) args.Add("NOCONTENT");
        if (WithScores) args.Add("WITHSCORES");
        ArgumentNullException.ThrowIfNull(ReturnFields);
        if (ReturnFields.Count > 0)
        {
            args.Add("RETURN");
            args.Add(ReturnFields.Count);
            foreach (var field in ReturnFields) args.Add(field);
        }
        if (SortBy is { } sort)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sort.Field);
            args.Add("SORTBY");
            args.Add(sort.Field);
            args.Add(sort.Direction switch
            {
                RespireSearchSortDirection.Ascending => "ASC",
                RespireSearchSortDirection.Descending => "DESC",
                _ => throw new ArgumentOutOfRangeException(nameof(SortBy)),
            });
        }
        if (Limit is { } limit)
        {
            if (limit.Offset < 0 || limit.Count < 0) throw new ArgumentOutOfRangeException(nameof(Limit));
            args.Add("LIMIT");
            args.Add(limit.Offset);
            args.Add(limit.Count);
        }
        if (TimeoutMilliseconds is { } timeout)
        {
            if (timeout <= 0) throw new ArgumentOutOfRangeException(nameof(TimeoutMilliseconds));
            args.Add("TIMEOUT");
            args.Add(timeout);
        }
        ArgumentNullException.ThrowIfNull(Parameters);
        AddParameters(args, Parameters);
        if (Parameters.Count > 0 && Dialect is < 2)
            throw new ArgumentOutOfRangeException(nameof(Dialect), Dialect, "Named parameters require dialect 2 or later.");
        if (Parameters.Count > 0 && Dialect is null)
        {
            args.Add("DIALECT");
            args.Add(2);
        }
        else if (Dialect is { } dialect)
        {
            if (dialect <= 0) throw new ArgumentOutOfRangeException(nameof(Dialect));
            args.Add("DIALECT");
            args.Add(dialect);
        }
        return [.. args];
    }
    internal static void AddParameters(List<RespireValue> args, IReadOnlyDictionary<string, RespireValue> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (parameters.Count == 0) return;
        args.Add("PARAMS");
        args.Add(checked(parameters.Count * 2));
        foreach (var parameter in parameters)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(parameter.Key);
            args.Add(parameter.Key);
            args.Add(parameter.Value);
        }
    }
}

/// <summary>Typed aggregation pipeline options.</summary>
public sealed record RespireSearchAggregateOptions
{
    /// <summary>Fields to load before pipeline operations.</summary>
    public IReadOnlyList<string> LoadFields { get; init; } = [];
    /// <summary>Filter expressions applied to pipeline rows.</summary>
    public IReadOnlyList<string> Filters { get; init; } = [];
    /// <summary>Field and expression pairs to add to rows.</summary>
    public IReadOnlyDictionary<string, string> Apply { get; init; } = new Dictionary<string, string>();
    /// <summary>Sort expressions, such as <c>@price DESC</c>.</summary>
    public IReadOnlyList<string> SortBy { get; init; } = [];
    /// <summary>Group-by stages with their reducer functions.</summary>
    public IReadOnlyList<RespireSearchAggregateGroup> Groups { get; init; } = [];
    /// <summary>Zero-based offset and row count.</summary>
    public (int Offset, int Count)? Limit { get; init; }
    /// <summary>Query dialect version.</summary>
    public int? Dialect { get; init; }
    /// <summary>Builds pipeline arguments in server order.</summary>
    internal RespireValue[] ToArguments()
    {
        var args = new List<RespireValue>();
        if (LoadFields.Count > 0) { args.Add("LOAD"); args.Add(LoadFields.Count); foreach (var field in LoadFields) args.Add(field); }
        foreach (var filter in Filters) { args.Add("FILTER"); args.Add(filter); }
        foreach (var item in Apply) { args.Add("APPLY"); args.Add(item.Value); args.Add("AS"); args.Add(item.Key); }
        foreach (var group in Groups)
        {
            ArgumentNullException.ThrowIfNull(group.Properties);
            args.Add("GROUPBY"); args.Add(group.Properties.Count);
            foreach (var property in group.Properties) args.Add(property);
            foreach (var reducer in group.Reducers)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(reducer.Function);
                args.Add("REDUCE"); args.Add(reducer.Function); args.Add(reducer.Arguments.Count);
                foreach (var argument in reducer.Arguments) args.Add(argument);
                if (!string.IsNullOrWhiteSpace(reducer.Alias)) { args.Add("AS"); args.Add(reducer.Alias); }
            }
        }
        if (SortBy.Count > 0)
        {
            var sortTokens = new List<RespireValue>();
            foreach (var expression in SortBy)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(expression);
                var trimmed = expression.Trim();
                var directionStart = trimmed.LastIndexOfAny([' ', '\t', '\r', '\n']);
                var direction = directionStart < 0 ? string.Empty : trimmed[(directionStart + 1)..];
                if (directionStart >= 0
                    && (direction.Equals("ASC", StringComparison.OrdinalIgnoreCase)
                        || direction.Equals("DESC", StringComparison.OrdinalIgnoreCase)))
                {
                    var field = trimmed[..directionStart].TrimEnd();
                    ArgumentException.ThrowIfNullOrWhiteSpace(field);
                    sortTokens.Add(field);
                    sortTokens.Add(direction.ToUpperInvariant());
                }
                else
                {
                    sortTokens.Add(trimmed);
                }
            }
            args.Add("SORTBY"); args.Add(sortTokens.Count);
            foreach (var token in sortTokens) args.Add(token);
        }
        if (Limit is { } limit) { if (limit.Offset < 0 || limit.Count < 0) throw new ArgumentOutOfRangeException(nameof(Limit)); args.Add("LIMIT"); args.Add(limit.Offset); args.Add(limit.Count); }
        if (Dialect is { } dialect) { if (dialect <= 0) throw new ArgumentOutOfRangeException(nameof(Dialect)); args.Add("DIALECT"); args.Add(dialect); }
        return [.. args];
    }
}

/// <summary>One aggregation GROUPBY stage.</summary>
public sealed record RespireSearchAggregateGroup(IReadOnlyList<string> Properties, IReadOnlyList<RespireSearchReducer> Reducers);

/// <summary>One Redis Search aggregation reducer.</summary>
public sealed record RespireSearchReducer(string Function, IReadOnlyList<string> Arguments, string? Alias = null);

/// <summary>Typed KNN vector query for FT.SEARCH.</summary>
public sealed record RespireVectorSearchRequest(string Field, ReadOnlyMemory<byte> Vector, int K)
{
    /// <summary>Alias used for distance score.</summary>
    public string ScoreField { get; init; } = "vector_score";
    /// <summary>Builds dialect 2 query expression.</summary>
    public string Expression
    {
        get
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(Field);
            ArgumentException.ThrowIfNullOrWhiteSpace(ScoreField);
            if (K <= 0) throw new ArgumentOutOfRangeException(nameof(K));
            if (Vector.IsEmpty) throw new ArgumentException("Vector bytes are required.", nameof(Vector));
            return $"*=>[KNN {K} @{Field} $vector AS {ScoreField}]";
        }
    }
}

/// <summary>Typed FT.HYBRID text and vector query.</summary>
public sealed record RespireHybridSearchQuery(string TextExpression, string VectorField, ReadOnlyMemory<byte> Vector, int K, int Limit = 10)
{
    /// <summary>Reciprocal-rank-fusion constant.</summary>
    public int RrfConstant { get; init; } = 60;
    /// <summary>Field names to load from matched documents.</summary>
    public IReadOnlyList<string> LoadFields { get; init; } = [];
    internal RespireValue[] ToArguments()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(TextExpression); ArgumentException.ThrowIfNullOrWhiteSpace(VectorField);
        if (Vector.IsEmpty) throw new ArgumentException("Vector bytes are required.", nameof(Vector));
        if (K <= 0 || Limit < 0 || RrfConstant <= 0) throw new ArgumentOutOfRangeException(nameof(K));
        var args = new List<RespireValue> { "SEARCH", TextExpression, "VSIM", "@" + VectorField, "$vector", "KNN", 2, "K", K, "COMBINE", "RRF", 2, "CONSTANT", RrfConstant, "LIMIT", 0, Limit };
        if (LoadFields.Count > 0) { args.Add("LOAD"); args.Add(LoadFields.Count); foreach (var field in LoadFields) args.Add(field); }
        args.Add("PARAMS"); args.Add(2); args.Add("vector"); args.Add(Vector);
        return [.. args];
    }
}

/// <summary>Sort direction for search results.</summary>
public enum RespireSearchSortDirection
{
    /// <summary>Sort low to high.</summary>
    Ascending,
    /// <summary>Sort high to low.</summary>
    Descending,
}

/// <summary>Search response document with identifier and projected fields.</summary>
public sealed record RespireSearchDocument(string Id, IReadOnlyDictionary<string, string?> Fields, double? Score = null);

/// <summary>Parsed FT.SEARCH or FT.HYBRID results.</summary>
public sealed record RespireSearchResult(long Total, IReadOnlyList<RespireSearchDocument> Documents, IReadOnlyList<string> Warnings)
{
    internal static RespireSearchResult Parse(RespireResult result, bool noContent = false, bool withScores = false, bool hybrid = false)
    {
        if (result.Type == RespDataType.Map) return ParseResp3(result, hybrid);
        if (result.Count == 0) return new(0, [], []);
        var total = result[0].AsInteger();
        var docs = new List<RespireSearchDocument>();
        for (var i = 1; i < result.Count;)
        {
            var id = result[i++].AsString();
            double? score = null;
            if (withScores && i < result.Count) score = double.Parse(result[i++].AsString(), System.Globalization.CultureInfo.InvariantCulture);
            if (noContent || i == result.Count) { docs.Add(new(id, new Dictionary<string, string?>(), score)); continue; }
            if (result[i].IsNull) { i++; docs.Add(new(id, new Dictionary<string, string?>(), score)); continue; }
            var fields = ParseFields(result[i++]);
            docs.Add(new(id, fields, score));
        }
        return new(total, docs, []);
    }

    private static RespireSearchResult ParseResp3(RespireResult result, bool hybrid)
    {
        long total = 0;
        var docs = new List<RespireSearchDocument>();
        var warnings = new List<string>();
        for (var i = 0; i + 1 < result.Count; i += 2)
        {
            var key = result[i].AsString(); var value = result[i + 1];
            if (key == "total_results") total = value.AsInteger();
            else if (key is "warnings" or "warning") for (var j = 0; j < value.Count; j++) warnings.Add(value[j].AsString());
            else if (key == "results")
            {
                for (var j = 0; j < value.Count; j++)
                {
                    var item = value[j]; string? id = null; double? score = null; var fields = new Dictionary<string, string?>();
                    for (var k = 0; k + 1 < item.Count; k += 2)
                    {
                        var itemKey = item[k].AsString(); var itemValue = item[k + 1];
                        if (itemKey is "id" or "key" or "keyid") id = itemValue.AsString();
                        else if (itemKey == "extra_attributes") fields = ParseFields(itemValue);
                        else if (itemKey == "score") score = itemValue.AsDouble();
                        else if (hybrid) fields[itemKey] = itemValue.IsNull ? null : itemValue.AsString();
                    }
                    if (id is not null) docs.Add(new(id, fields, score));
                }
            }
        }
        return new(total, docs, warnings);
    }

    private static Dictionary<string, string?> ParseFields(RespireResult values)
    {
        var fields = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < values.Count; i += 2)
            fields[values[i].AsString()] = values[i + 1].IsNull ? null : values[i + 1].AsString();
        return fields;
    }
}

/// <summary>Aggregation response rows.</summary>
public sealed record RespireSearchAggregateResult(long Total, IReadOnlyList<IReadOnlyDictionary<string, string?>> Rows)
{
    /// <summary>Rows with nested RESP collections preserved for collection-valued reducers such as TOLIST.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, RespireSearchAggregateValue>> StructuredRows { get; init; } = [];

    internal static RespireSearchAggregateResult Parse(RespireResult result)
    {
        if (result.Count == 0) return new(0, []);
        if (result.Type == RespDataType.Map)
        {
            long total = 0;
            var mappedRows = new List<IReadOnlyDictionary<string, string?>>();
            var structuredRows = new List<IReadOnlyDictionary<string, RespireSearchAggregateValue>>();
            for (var i = 0; i + 1 < result.Count; i += 2)
            {
                var key = result[i].AsString();
                var value = result[i + 1];
                if (key == "total_results") total = value.AsInteger();
                else if (key == "results")
                {
                    for (var j = 0; j < value.Count; j++)
                    {
                        var item = value[j];
                        var fields = item;
                        for (var k = 0; k + 1 < item.Count; k += 2)
                        {
                            if (item[k].AsString() != "extra_attributes") continue;
                            fields = item[k + 1];
                            break;
                        }
                        var parsedFields = ParseFields(fields);
                        mappedRows.Add(parsedFields.Scalar);
                        structuredRows.Add(parsedFields.Structured);
                    }
                }
            }
            return new(total, mappedRows) { StructuredRows = structuredRows };
        }
        var rows = new List<IReadOnlyDictionary<string, string?>>();
        var structured = new List<IReadOnlyDictionary<string, RespireSearchAggregateValue>>();
        for (var i = 1; i < result.Count; i++)
        {
            var parsedFields = ParseFields(result[i]);
            rows.Add(parsedFields.Scalar);
            structured.Add(parsedFields.Structured);
        }
        return new(result[0].AsInteger(), rows) { StructuredRows = structured };
    }

    private static (IReadOnlyDictionary<string, string?> Scalar, IReadOnlyDictionary<string, RespireSearchAggregateValue> Structured) ParseFields(RespireResult values)
    {
        var scalar = new Dictionary<string, string?>(StringComparer.Ordinal);
        var structured = new Dictionary<string, RespireSearchAggregateValue>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < values.Count; i += 2)
        {
            var value = values[i + 1];
            var name = values[i].AsString();
            scalar[name] = value.IsNull ? null : value.AsString();
            structured[name] = RespireSearchAggregateValue.From(value);
        }
        return (scalar, structured);
    }
}

/// <summary>An immutable RESP value in a structured aggregate row.</summary>
/// <param name="Type">RESP wire type.</param>
/// <param name="Scalar">Decoded scalar value, or null for null and collection types.</param>
/// <param name="Items">Collection elements in wire order; map items alternate keys and values.</param>
public sealed record RespireSearchAggregateValue(
    RespDataType Type,
    string? Scalar,
    IReadOnlyList<RespireSearchAggregateValue> Items)
{
    internal static RespireSearchAggregateValue From(RespireResult value)
    {
        if (value.Type is RespDataType.Array or RespDataType.Map or RespDataType.Set or RespDataType.Push or RespDataType.Attribute)
        {
            var items = new RespireSearchAggregateValue[value.Count];
            for (var i = 0; i < items.Length; i++) items[i] = From(value[i]);
            return new(value.Type, null, items);
        }

        return new(value.Type, value.IsNull ? null : value.AsString(), []);
    }
}
