using System.Text;
using Respire.Protocol;

namespace Respire.Search;

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
        => EscapeIdentifier(value);

    internal static string EscapeField(string value)
        => EscapeIdentifier(value);

    private static string EscapeIdentifier(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var escaped = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
        {
            if (!Rune.IsLetterOrDigit(rune) && rune.Value != '_') escaped.Append('\\');
            escaped.Append(rune.ToString());
        }
        return escaped.ToString();
    }
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
        if (Parameters.Count > 0 && Dialect is null or < 2)
            throw new ArgumentOutOfRangeException(nameof(Dialect), Dialect, "Named parameters require an explicit dialect 2 or later.");
        if (Dialect is { } dialect)
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
    public IReadOnlyList<RespireSearchAggregateSort> SortBy { get; init; } = [];
    /// <summary>Group-by stages with their reducer functions.</summary>
    public IReadOnlyList<RespireSearchAggregateGroup> Groups { get; init; } = [];
    /// <summary>Explicitly ordered pipeline stages for operations with dependencies.</summary>
    public IReadOnlyList<RespireSearchAggregateStage> Stages { get; init; } = [];
    /// <summary>Zero-based offset and row count.</summary>
    public (int Offset, int Count)? Limit { get; init; }
    /// <summary>Query dialect version.</summary>
    public int? Dialect { get; init; }
    /// <summary>Builds pipeline arguments in server order.</summary>
    internal RespireValue[] ToArguments()
    {
        var args = new List<RespireValue>();
        if (Stages.Count > 0)
        {
            if (LoadFields.Count > 0 || Filters.Count > 0 || Apply.Count > 0 || Groups.Count > 0)
                throw new ArgumentException("Use either Stages or the convenience pipeline properties, not both.");
            foreach (var stage in Stages)
            {
                ArgumentNullException.ThrowIfNull(stage);
                switch (stage)
                {
                    case RespireSearchAggregateLoad load:
                        ArgumentNullException.ThrowIfNull(load.Fields);
                        args.Add("LOAD"); args.Add(load.Fields.Count);
                        foreach (var field in load.Fields) args.Add(field);
                        break;
                    case RespireSearchAggregateFilter filter:
                        ArgumentException.ThrowIfNullOrWhiteSpace(filter.Expression);
                        args.Add("FILTER"); args.Add(filter.Expression);
                        break;
                    case RespireSearchAggregateApply apply:
                        ArgumentException.ThrowIfNullOrWhiteSpace(apply.Expression);
                        ArgumentException.ThrowIfNullOrWhiteSpace(apply.Alias);
                        args.Add("APPLY"); args.Add(apply.Expression); args.Add("AS"); args.Add(apply.Alias);
                        break;
                    case RespireSearchAggregateGroupStage group:
                        AddGroup(args, group.Group);
                        break;
                    case RespireSearchAggregateSort sort:
                        AddSort(args, [sort]);
                        break;
                    case RespireSearchAggregateLimit stageLimit:
                        AddLimit(args, stageLimit.Offset, stageLimit.Count);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(Stages), stage, "Unknown aggregation stage type.");
                }
            }
        }
        else
        {
            if (LoadFields.Count > 0) { args.Add("LOAD"); args.Add(LoadFields.Count); foreach (var field in LoadFields) args.Add(field); }
            foreach (var filter in Filters) { args.Add("FILTER"); args.Add(filter); }
            foreach (var item in Apply) { args.Add("APPLY"); args.Add(item.Value); args.Add("AS"); args.Add(item.Key); }
            foreach (var group in Groups) AddGroup(args, group);
        }
        if (SortBy.Count > 0)
        {
            AddSort(args, SortBy);
        }
        if (Limit is { } limit) AddLimit(args, limit.Offset, limit.Count);
        if (Dialect is { } dialect) { if (dialect <= 0) throw new ArgumentOutOfRangeException(nameof(Dialect)); args.Add("DIALECT"); args.Add(dialect); }
        return [.. args];
    }

    private static void AddSort(List<RespireValue> args, IReadOnlyList<RespireSearchAggregateSort> sorts)
    {
        args.Add("SORTBY");
        args.Add(sorts.Count * 2);
        foreach (var sort in sorts)
        {
            ArgumentNullException.ThrowIfNull(sort);
            ArgumentException.ThrowIfNullOrWhiteSpace(sort.Field);
            args.Add(sort.Field);
            args.Add(sort.Direction switch
            {
                RespireSearchSortDirection.Ascending => "ASC",
                RespireSearchSortDirection.Descending => "DESC",
                _ => throw new ArgumentOutOfRangeException(nameof(sort.Direction)),
            });
        }
    }

    private static void AddLimit(List<RespireValue> args, int offset, int count)
    {
        if (offset < 0 || count < 0) throw new ArgumentOutOfRangeException(nameof(Limit));
        args.Add("LIMIT"); args.Add(offset); args.Add(count);
    }

    private static void AddGroup(List<RespireValue> args, RespireSearchAggregateGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(group.Properties);
        ArgumentNullException.ThrowIfNull(group.Reducers);
        args.Add("GROUPBY"); args.Add(group.Properties.Count);
        foreach (var property in group.Properties) args.Add(property);
        foreach (var reducer in group.Reducers)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(reducer.Function);
            ArgumentNullException.ThrowIfNull(reducer.Arguments);
            args.Add("REDUCE"); args.Add(reducer.Function); args.Add(reducer.Arguments.Count);
            foreach (var argument in reducer.Arguments) args.Add(argument);
            if (!string.IsNullOrWhiteSpace(reducer.Alias)) { args.Add("AS"); args.Add(reducer.Alias); }
        }
    }
}

/// <summary>Base type for ordered FT.AGGREGATE operations.</summary>
public abstract record RespireSearchAggregateStage;

/// <summary>Loads fields before later stages.</summary>
public sealed record RespireSearchAggregateLoad(IReadOnlyList<string> Fields) : RespireSearchAggregateStage;

/// <summary>Filters rows at this point in the pipeline.</summary>
public sealed record RespireSearchAggregateFilter(string Expression) : RespireSearchAggregateStage;

/// <summary>Adds an expression result at this point in the pipeline.</summary>
public sealed record RespireSearchAggregateApply(string Expression, string Alias) : RespireSearchAggregateStage;

/// <summary>Groups rows at this point in the pipeline.</summary>
public sealed record RespireSearchAggregateGroupStage(RespireSearchAggregateGroup Group) : RespireSearchAggregateStage;

/// <summary>Sorts rows at this point in the pipeline.</summary>
public sealed record RespireSearchAggregateSort(string Field, RespireSearchSortDirection Direction = RespireSearchSortDirection.Ascending) : RespireSearchAggregateStage;

/// <summary>Limits rows at this point in the pipeline.</summary>
/// <param name="Offset">Number of rows to skip.</param>
/// <param name="Count">Maximum number of rows to keep.</param>
public sealed record RespireSearchAggregateLimit(int Offset, int Count) : RespireSearchAggregateStage;

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
            return $"*=>[KNN {K} @{RespireSearchQueryBuilder.EscapeField(Field)} $vector AS {RespireSearchQueryBuilder.EscapeField(ScoreField)}]";
        }
    }
}

/// <summary>Typed FT.HYBRID text and vector query.</summary>
public sealed record RespireHybridSearchQuery(string TextExpression, string VectorField, ReadOnlyMemory<byte> Vector, int K, int Limit = 10)
{
    /// <summary>Reciprocal-rank-fusion constant.</summary>
    public int RrfConstant { get; init; } = 60;
    /// <summary>Reciprocal-rank-fusion window size.</summary>
    public int? RrfWindow { get; init; }
    /// <summary>Field names to load from matched documents.</summary>
    public IReadOnlyList<string> LoadFields { get; init; } = [];
    /// <summary>Additional named parameters used by the text expression.</summary>
    public IReadOnlyDictionary<string, RespireValue> Parameters { get; init; } = new Dictionary<string, RespireValue>();
    /// <summary>Maximum server execution time in milliseconds.</summary>
    public int? TimeoutMilliseconds { get; init; }
    internal RespireValue[] ToArguments()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(TextExpression); ArgumentException.ThrowIfNullOrWhiteSpace(VectorField);
        if (Vector.IsEmpty) throw new ArgumentException("Vector bytes are required.", nameof(Vector));
        if (K <= 0) throw new ArgumentOutOfRangeException(nameof(K));
        if (Limit < 0) throw new ArgumentOutOfRangeException(nameof(Limit));
        if (RrfConstant <= 0) throw new ArgumentOutOfRangeException(nameof(RrfConstant));
        if (RrfWindow is <= 0) throw new ArgumentOutOfRangeException(nameof(RrfWindow));
        if (TimeoutMilliseconds is <= 0) throw new ArgumentOutOfRangeException(nameof(TimeoutMilliseconds));
        ArgumentNullException.ThrowIfNull(LoadFields);
        ArgumentNullException.ThrowIfNull(Parameters);
        var args = new List<RespireValue>
        {
            "SEARCH", TextExpression,
            "VSIM", "@" + RespireSearchQueryBuilder.EscapeField(VectorField), "$vector",
            "KNN", 2, "K", K,
        };
        var combineCount = 2 + (RrfWindow is null ? 0 : 2);
        args.Add("COMBINE");
        args.Add("RRF");
        args.Add(combineCount);
        args.Add("CONSTANT");
        args.Add(RrfConstant);
        if (RrfWindow is { } window)
        {
            args.Add("WINDOW");
            args.Add(window);
        }
        args.Add("LIMIT");
        args.Add(0);
        args.Add(Limit);
        if (LoadFields.Count > 0)
        {
            var fields = new List<string> { "@__key", "@__score" };
            foreach (var field in LoadFields)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(field);
                if (field is "__key" or "@__key" or "__score" or "@__score") continue;
                fields.Add(field.StartsWith('@') ? field : "@" + field);
            }
            args.Add("LOAD");
            args.Add(fields.Count);
            foreach (var field in fields) args.Add(field);
        }
        args.Add("PARAMS");
        args.Add(checked((Parameters.Count + 1) * 2));
        args.Add("vector");
        args.Add(Vector);
        foreach (var parameter in Parameters)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(parameter.Key);
            if (parameter.Key.Equals("vector", StringComparison.Ordinal))
                throw new ArgumentException("The 'vector' parameter name is reserved for the vector query.", nameof(Parameters));
            args.Add(parameter.Key);
            args.Add(parameter.Value);
        }
        if (TimeoutMilliseconds is { } timeout)
        {
            args.Add("TIMEOUT");
            args.Add(timeout);
        }
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
public sealed record RespireSearchDocument(string Id, IReadOnlyDictionary<string, string?> Fields, double? Score = null)
{
    /// <summary>Typed projected values, including binary string fields.</summary>
    public IReadOnlyDictionary<string, RespireSearchValue> StructuredFields { get; init; } = new Dictionary<string, RespireSearchValue>();
}

/// <summary>Parsed FT.SEARCH or FT.HYBRID results.</summary>
public sealed record RespireSearchResult(long Total, IReadOnlyList<RespireSearchDocument> Documents, IReadOnlyList<string> Warnings)
{
    internal static RespireSearchResult Parse(RespireResult result, bool noContent = false, bool withScores = false, bool hybrid = false)
    {
        if (result.Type == RespDataType.Map) return ParseResp3(result, hybrid);
        if (result.Count == 0) return new(0, [], []);
        if (hybrid) return ParseResp2Hybrid(result);
        var total = result[0].AsInteger();
        var docs = new List<RespireSearchDocument>();
        for (var i = 1; i < result.Count;)
        {
            var id = result[i++].AsString();
            double? score = null;
            if (withScores && i < result.Count) score = result[i++].AsDouble();
            if (noContent || i == result.Count) { docs.Add(new(id, new Dictionary<string, string?>(), score)); continue; }
            if (result[i].IsNull) { i++; docs.Add(new(id, new Dictionary<string, string?>(), score)); continue; }
            var fields = ParseTypedFields(result[i++]);
            docs.Add(new(id, fields.Fields, score) { StructuredFields = fields.Structured });
        }
        return new(total, docs, []);
    }

    private static RespireSearchResult ParseResp2Hybrid(RespireResult result)
    {
        long total = 0;
        var documents = new List<RespireSearchDocument>();
        var warnings = new List<string>();
        if (result[0].Type == RespDataType.Integer)
        {
            total = result[0].AsInteger();
            for (var i = 1; i < result.Count; i++)
            {
                var value = result[i];
                if (value.Count > 0 && value[0].Type == RespDataType.Array)
                {
                    for (var j = 0; j < value.Count; j++) AddHybridDocument(value[j], documents);
                }
                else
                {
                    AddHybridDocument(value, documents);
                }
            }
            return new(total, documents, warnings);
        }

        for (var i = 0; i + 1 < result.Count; i += 2)
        {
            var key = result[i].AsString();
            var value = result[i + 1];
            if (key == "total_results") total = value.AsInteger();
            else if (key is "warnings" or "warning")
                for (var j = 0; j < value.Count; j++) warnings.Add(value[j].AsString());
            else if (key == "results")
                for (var j = 0; j < value.Count; j++) AddHybridDocument(value[j], documents);
        }
        return new(total, documents, warnings);
    }

    // FT.HYBRID rows carry the reserved __key and __score names. When they are present, names such
    // as id or score are ordinary loaded fields and must not replace the document identity.
    private static bool HasReservedHybridKey(RespireResult row)
    {
        for (var j = 0; j + 1 < row.Count; j += 2)
            if (row[j].AsString() == "__key") return true;
        return false;
    }

    private static bool IsHybridId(string key, bool reserved)
        => reserved ? key == "__key" : key is "id" or "key" or "keyid";

    private static bool IsHybridScore(string key, bool reserved)
        => reserved ? key == "__score" : key == "score";

    private static void AddHybridDocument(RespireResult row, List<RespireSearchDocument> documents)
    {
        string? id = null;
        double? score = null;
        var fields = new Dictionary<string, string?>(StringComparer.Ordinal);
        var structuredFields = new Dictionary<string, RespireSearchValue>(StringComparer.Ordinal);
        var reserved = HasReservedHybridKey(row);
        for (var j = 0; j + 1 < row.Count; j += 2)
        {
            var key = row[j].AsString();
            var value = row[j + 1];
            if (IsHybridId(key, reserved)) id = value.AsString();
            else if (IsHybridScore(key, reserved)) score = value.AsDouble();
            else if (key == "extra_attributes")
            {
                var parsed = ParseTypedFields(value);
                fields = parsed.Fields;
                structuredFields = parsed.Structured;
            }
            else
            {
                fields[key] = value.IsNull ? null : value.AsString();
                structuredFields[key] = RespireSearchValue.From(value);
            }
        }
        if (id is not null) documents.Add(new(id, fields, score) { StructuredFields = structuredFields });
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
                    var structuredFields = new Dictionary<string, RespireSearchValue>(StringComparer.Ordinal);
                    var reserved = hybrid && HasReservedHybridKey(item);
                    for (var k = 0; k + 1 < item.Count; k += 2)
                    {
                        var itemKey = item[k].AsString(); var itemValue = item[k + 1];
                        if (reserved ? itemKey == "__key" : itemKey is "id" or "key" or "keyid" or "__key") id = itemValue.AsString();
                        else if (itemKey == "extra_attributes")
                        {
                            var parsed = ParseTypedFields(itemValue);
                            fields = parsed.Fields;
                            structuredFields = parsed.Structured;
                        }
                        else if (reserved ? itemKey == "__score" : itemKey is "score" or "__score") score = itemValue.AsDouble();
                        else if (hybrid)
                        {
                            fields[itemKey] = itemValue.IsNull ? null : itemValue.AsString();
                            structuredFields[itemKey] = RespireSearchValue.From(itemValue);
                        }
                    }
                    if (id is not null) docs.Add(new(id, fields, score) { StructuredFields = structuredFields });
                }
            }
        }
        return new(total, docs, warnings);
    }

    private static (Dictionary<string, string?> Fields, Dictionary<string, RespireSearchValue> Structured) ParseTypedFields(RespireResult values)
    {
        var fields = new Dictionary<string, string?>(StringComparer.Ordinal);
        var structured = new Dictionary<string, RespireSearchValue>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < values.Count; i += 2)
        {
            var name = values[i].AsString();
            var value = values[i + 1];
            fields[name] = value.IsNull ? null : value.AsString();
            structured[name] = RespireSearchValue.From(value);
        }
        return (fields, structured);
    }
}

/// <summary>Aggregation response rows.</summary>
public sealed record RespireSearchAggregateResult(long Total, IReadOnlyList<IReadOnlyDictionary<string, string?>> Rows)
{
    /// <summary>Rows with nested RESP collections preserved for collection-valued reducers such as TOLIST.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, RespireSearchValue>> StructuredRows { get; init; } = [];

    internal static RespireSearchAggregateResult Parse(RespireResult result)
    {
        if (result.Count == 0) return new(0, []);
        if (result.Type == RespDataType.Map)
        {
            long total = 0;
            var mappedRows = new List<IReadOnlyDictionary<string, string?>>();
            var structuredRows = new List<IReadOnlyDictionary<string, RespireSearchValue>>();
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
        var structured = new List<IReadOnlyDictionary<string, RespireSearchValue>>();
        for (var i = 1; i < result.Count; i++)
        {
            var parsedFields = ParseFields(result[i]);
            rows.Add(parsedFields.Scalar);
            structured.Add(parsedFields.Structured);
        }
        return new(result[0].AsInteger(), rows) { StructuredRows = structured };
    }

    private static (IReadOnlyDictionary<string, string?> Scalar, IReadOnlyDictionary<string, RespireSearchValue> Structured) ParseFields(RespireResult values)
    {
        var scalar = new Dictionary<string, string?>(StringComparer.Ordinal);
        var structured = new Dictionary<string, RespireSearchValue>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < values.Count; i += 2)
        {
            var value = values[i + 1];
            var name = values[i].AsString();
            scalar[name] = value.IsNull ? null : value.AsString();
            structured[name] = RespireSearchValue.From(value);
        }
        return (scalar, structured);
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
        if (value.Type is RespDataType.Array or RespDataType.Map or RespDataType.Set or RespDataType.Push or RespDataType.Attribute)
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
