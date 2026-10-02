using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Respire;

namespace Redis.Search;

/// <summary>Redis Search query syntax with an explicit trust boundary.</summary>
public readonly record struct RespireSearchExpression
{
    private readonly string? _value;

    private RespireSearchExpression(string value) => _value = value;

    /// <summary>Query syntax sent to Redis Search.</summary>
    public string Value => _value ?? throw new InvalidOperationException("A default search expression is invalid.");

    /// <summary>Creates an expression from trusted native Redis Search syntax.</summary>
    /// <remarks>Do not concatenate untrusted input into raw query syntax.</remarks>
    public static RespireSearchExpression FromRaw(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new(value);
    }

    internal static RespireSearchExpression FromBuilder(string value)
    {
        Debug.Assert(!string.IsNullOrWhiteSpace(value));
        return new(value);
    }

    internal RespireSearchExpression RequireValid(string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(_value, parameterName);
        return this;
    }

    /// <summary>Formats expression for diagnostics.</summary>
    /// <remarks>A default expression formats as an empty string; accessing <see cref="Value"/> still throws.</remarks>
    public override string ToString() => _value ?? string.Empty;
}

/// <summary>Search query with typed modifiers. The expression is validated when the query is created.</summary>
/// <param name="Expression">
/// Query expression. Use a <see cref="RespireSearchQueryBuilder"/> helper for escaped values, or
/// <see cref="RespireSearchExpression.FromRaw(string)"/> for trusted native syntax.
/// </param>
/// <param name="Options">Optional FT.SEARCH modifiers.</param>
public sealed record RespireSearchQuery(RespireSearchExpression Expression, RespireSearchQueryOptions? Options = null)
{
    /// <summary>Query expression.</summary>
    // Validate both initial construction and record-copy assignment; either can set this property.
    public RespireSearchExpression Expression { get; init => field = value.RequireValid(nameof(Expression)); } = Expression.RequireValid(nameof(Expression));

    internal RespireValue[] ToArguments() => (Options ?? RespireSearchQueryOptions.Default).ToArguments();

}

/// <summary>Composable helpers for common Redis Search query expressions.</summary>
/// <remarks>
/// <para>
/// Field names, tag values, and quoted text are escaped, and numeric bounds are formatted from typed
/// values, so values passed to these helpers cannot change the query structure.
/// The helpers build exact matches only. For prefix (<c>term*</c>), fuzzy (<c>%term%</c>), or
/// wildcard queries, wrap trusted native query syntax with <see cref="RespireSearchExpression.FromRaw(string)"/>.
/// </para>
/// <para>
/// <see cref="And"/> and <see cref="Or"/> accept only typed expressions. Use
/// <see cref="RespireSearchExpression.FromRaw(string)"/> to mark trusted native syntax explicitly.
/// </para>
/// </remarks>
public static class RespireSearchQueryBuilder
{
    /// <summary>Matches all indexed documents.</summary>
    public static RespireSearchExpression MatchAll() => RespireSearchExpression.FromBuilder("*");

    /// <summary>Matches an exact quoted term or phrase across indexed text fields.</summary>
    public static RespireSearchExpression Text(string term) => RespireSearchExpression.FromBuilder(Quote(term));

    /// <summary>Matches an exact quoted term or phrase in one text field.</summary>
    public static RespireSearchExpression TextField(string field, string term) => RespireSearchExpression.FromBuilder($"@{EscapeField(field)}:{Quote(term)}");

    /// <summary>Matches an exact tag value.</summary>
    public static RespireSearchExpression Tag(string field, string value) => RespireSearchExpression.FromBuilder($"@{EscapeField(field)}:{{{EscapeIdentifier(value)}}}");

    /// <summary>Matches a numeric range. Bounds are inclusive unless marked exclusive; infinities are allowed.</summary>
    public static RespireSearchExpression NumericRange(string field, double minimum, double maximum, bool exclusiveMinimum = false, bool exclusiveMaximum = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        if (double.IsNaN(minimum)) throw new ArgumentOutOfRangeException(nameof(minimum));
        if (double.IsNaN(maximum)) throw new ArgumentOutOfRangeException(nameof(maximum));
        if (minimum > maximum) throw new ArgumentOutOfRangeException(nameof(minimum));
        return FormatRange(field, FormatBound(minimum), FormatBound(maximum), exclusiveMinimum, exclusiveMaximum);
    }

    /// <summary>Matches an integer range without converting the bounds through <see cref="double"/> text.</summary>
    /// <remarks>Redis Search stores numeric fields as doubles, so values beyond 2^53 still lose precision on the server.</remarks>
    public static RespireSearchExpression NumericRange(string field, long minimum, long maximum, bool exclusiveMinimum = false, bool exclusiveMaximum = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        if (minimum > maximum) throw new ArgumentOutOfRangeException(nameof(minimum));
        return FormatRange(
            field,
            minimum.ToString(CultureInfo.InvariantCulture),
            maximum.ToString(CultureInfo.InvariantCulture),
            exclusiveMinimum,
            exclusiveMaximum);
    }

    /// <summary>Combines already-built expressions with AND. Multiple inputs are parenthesized; inputs are not escaped.</summary>
    public static RespireSearchExpression And(params RespireSearchExpression[] expressions) => Combine(" ", expressions);

    /// <summary>Combines already-built expressions with OR. Multiple inputs are parenthesized; inputs are not escaped.</summary>
    public static RespireSearchExpression Or(params RespireSearchExpression[] expressions) => Combine(" | ", expressions);

    internal static string EscapeField(string value) => EscapeIdentifier(value);

    private static RespireSearchExpression FormatRange(string field, string lower, string upper, bool exclusiveMinimum, bool exclusiveMaximum)
        => RespireSearchExpression.FromBuilder($"@{EscapeField(field)}:[{(exclusiveMinimum ? "(" : "")}{lower} {(exclusiveMaximum ? "(" : "")}{upper}]");

    private static string FormatBound(double value) => value switch
    {
        double.NegativeInfinity => "-inf",
        double.PositiveInfinity => "+inf",
        // The query lexer accepts 1E-07 and 1E20 but rejects an explicit exponent sign such as 1E+20.
        _ => value.ToString("R", CultureInfo.InvariantCulture).Replace("E+", "E", StringComparison.Ordinal),
    };

    private static RespireSearchExpression Combine(string separator, RespireSearchExpression[] expressions)
    {
        ArgumentNullException.ThrowIfNull(expressions);
        if (expressions.Length == 0) throw new ArgumentException("At least one expression is required.", nameof(expressions));
        foreach (var expression in expressions) expression.RequireValid(nameof(expressions));
        return RespireSearchExpression.FromBuilder(expressions.Length == 1
            ? expressions[0].Value
            : $"({string.Join(separator, expressions.Select(value => $"({value.Value})"))})");
    }

    private static string Quote(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var escaped = value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }

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
    internal static readonly RespireSearchQueryOptions Default = new();

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

    /// <summary>Query dialect version. Named parameters require dialect 2 or later.</summary>
    public int? Dialect { get; init; }

    /// <summary>Maximum server execution time in milliseconds.</summary>
    public int? TimeoutMilliseconds { get; init; }

    /// <summary>Named query parameters.</summary>
    public IReadOnlyDictionary<string, RespireValue> Parameters { get; init; } = RespireSearchEmpty.Values;

    internal RespireValue[] ToArguments(KeyValuePair<string, RespireValue>? reservedParameter = null)
    {
        ArgumentNullException.ThrowIfNull(ReturnFields);
        ArgumentNullException.ThrowIfNull(Parameters);
        if (reservedParameter is not null) RespireSearchParameters.RejectReserved(Parameters, nameof(Parameters));
        RespireSearchDialect.Validate(Dialect, requiresParameters: Parameters.Count > 0 || reservedParameter is not null);
        var args = new List<RespireValue>();
        if (NoContent) args.Add("NOCONTENT");
        if (WithScores) args.Add("WITHSCORES");
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
            args.Add(RespireSearchSort.Token(sort.Direction));
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

        AddParameters(args, Parameters, reservedParameter);
        RespireSearchDialect.Add(args, Dialect);
        return [.. args];
    }

    internal static void AddParameters(List<RespireValue> args, IReadOnlyDictionary<string, RespireValue> parameters, KeyValuePair<string, RespireValue>? reserved)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var count = parameters.Count + (reserved is null ? 0 : 1);
        if (count == 0) return;
        args.Add("PARAMS");
        args.Add(checked(count * 2));
        if (reserved is { } value)
        {
            args.Add(value.Key);
            args.Add(value.Value);
        }

        foreach (var parameter in parameters)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(parameter.Key);
            args.Add(parameter.Key);
            args.Add(parameter.Value);
        }
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

/// <summary>Typed KNN vector query for FT.SEARCH. Values are validated when the request is created.</summary>
/// <param name="Field">Vector field name.</param>
/// <param name="Vector">Query vector bytes in the index's element type.</param>
/// <param name="K">Number of nearest neighbours.</param>
public sealed record RespireVectorSearchRequest(string Field, ReadOnlyMemory<byte> Vector, int K)
{
    /// <summary>The query parameter name that carries <see cref="Vector"/>. Callers cannot reuse it.</summary>
    public const string VectorParameterName = "vector";

    /// <summary>Vector field name.</summary>
    public string Field { get; init => field = RequireText(value, nameof(Field)); } = RequireText(Field, nameof(Field));

    /// <summary>Query vector bytes.</summary>
    public ReadOnlyMemory<byte> Vector { get; init => field = RequireVector(value); } = RequireVector(Vector);

    /// <summary>Number of nearest neighbours.</summary>
    public int K { get; init => field = RequirePositive(value); } = RequirePositive(K);

    /// <summary>Alias used for distance score.</summary>
    public string ScoreField { get; init => field = RequireText(value, nameof(ScoreField)); } = "vector_score";

    /// <summary>
    /// Optional pre-filter. Build escaped values with <see cref="RespireSearchQueryBuilder"/> or mark
    /// trusted native syntax with <see cref="RespireSearchExpression.FromRaw(string)"/>. Null searches all documents.
    /// </summary>
    public RespireSearchExpression? Filter
    {
        get;
        init => field = value?.RequireValid(nameof(Filter));
    }

    /// <summary>Builds the dialect 2 query expression.</summary>
    public RespireSearchExpression Expression
        => RespireSearchExpression.FromBuilder($"{(Filter is null ? "*" : "(" + Filter.Value.Value + ")")}=>[KNN {K} @{RespireSearchQueryBuilder.EscapeField(Field)} ${VectorParameterName} AS {RespireSearchQueryBuilder.EscapeField(ScoreField)}]");

    private static string RequireText(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        return value;
    }

    private static ReadOnlyMemory<byte> RequireVector(ReadOnlyMemory<byte> value)
        => value.IsEmpty ? throw new ArgumentException("Vector bytes are required.", nameof(Vector)) : value;

    private static int RequirePositive(int value)
        => value <= 0 ? throw new ArgumentOutOfRangeException(nameof(K), value, "K must be positive.") : value;
}

/// <summary>Typed FT.HYBRID text and vector query.</summary>
/// <param name="TextExpression">
/// Typed expression for the text leg. Use builder helpers for escaped values or
/// <see cref="RespireSearchExpression.FromRaw(string)"/> for trusted native syntax.
/// </param>
/// <param name="VectorField">Vector field name; it is escaped.</param>
/// <param name="Vector">Query vector bytes in the index's element type.</param>
/// <param name="K">Number of nearest neighbours for the vector leg.</param>
/// <param name="Limit">Maximum number of fused results.</param>
public sealed record RespireHybridSearchQuery(RespireSearchExpression TextExpression, string VectorField, ReadOnlyMemory<byte> Vector, int K, int Limit = 10)
{
    /// <summary>Reciprocal-rank-fusion constant.</summary>
    public int RrfConstant { get; init; } = 60;

    /// <summary>Reciprocal-rank-fusion window size.</summary>
    public int? RrfWindow { get; init; }

    /// <summary>Field names to load from matched documents.</summary>
    public IReadOnlyList<string> LoadFields { get; init; } = [];

    /// <summary>
    /// Additional named parameters used by the text expression. The name
    /// <see cref="RespireVectorSearchRequest.VectorParameterName"/> is reserved for the vector.
    /// </summary>
    public IReadOnlyDictionary<string, RespireValue> Parameters { get; init; } = RespireSearchEmpty.Values;

    /// <summary>Maximum server execution time in milliseconds.</summary>
    public int? TimeoutMilliseconds { get; init; }

    internal RespireValue[] ToArguments()
    {
        TextExpression.RequireValid(nameof(TextExpression));
        ArgumentException.ThrowIfNullOrWhiteSpace(VectorField);
        if (Vector.IsEmpty) throw new ArgumentException("Vector bytes are required.", nameof(Vector));
        if (K <= 0) throw new ArgumentOutOfRangeException(nameof(K));
        if (Limit < 0) throw new ArgumentOutOfRangeException(nameof(Limit));
        if (RrfConstant <= 0) throw new ArgumentOutOfRangeException(nameof(RrfConstant));
        if (RrfWindow is <= 0) throw new ArgumentOutOfRangeException(nameof(RrfWindow));
        if (TimeoutMilliseconds is <= 0) throw new ArgumentOutOfRangeException(nameof(TimeoutMilliseconds));
        ArgumentNullException.ThrowIfNull(LoadFields);
        RespireSearchParameters.RejectReserved(Parameters, nameof(Parameters));

        var args = new List<RespireValue>
        {
            "SEARCH",
            TextExpression.Value,
            "VSIM",
            "@" + RespireSearchQueryBuilder.EscapeField(VectorField),
            "$" + RespireVectorSearchRequest.VectorParameterName,
            "KNN",
            2,
            "K",
            K,
            "COMBINE",
            "RRF",
            RrfWindow is null ? 2 : 4,
            "CONSTANT",
            RrfConstant,
        };
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
            // Load the reserved key and score so document identity survives an explicit projection.
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

        RespireSearchQueryOptions.AddParameters(
            args,
            Parameters,
            new KeyValuePair<string, RespireValue>(RespireVectorSearchRequest.VectorParameterName, Vector));
        if (TimeoutMilliseconds is { } timeout)
        {
            args.Add("TIMEOUT");
            args.Add(timeout);
        }

        return [.. args];
    }
}

/// <summary>Options for FT.EXPLAIN and FT.EXPLAINCLI.</summary>
public sealed record RespireSearchExplainOptions
{
    internal static readonly RespireSearchExplainOptions Default = new();

    /// <summary>Uses FT.EXPLAINCLI and joins its plan lines with <see cref="Environment.NewLine"/>.</summary>
    public bool Cli { get; init; }

    /// <summary>Query dialect used to parse the expression, such as 2 for vector KNN syntax.</summary>
    public int? Dialect { get; init; }
}

internal static class RespireSearchDialect
{
    internal const int ParameterDialect = 2;

    internal static void Validate(int? dialect, bool requiresParameters)
    {
        if (dialect is <= 0)
            throw new ArgumentOutOfRangeException("Dialect", dialect, "Dialect must be positive.");
        if (requiresParameters && dialect is null or < ParameterDialect)
            throw new ArgumentOutOfRangeException("Dialect", dialect, "Named parameters and vector queries require an explicit dialect 2 or later.");
    }

    internal static void Add(List<RespireValue> args, int? dialect)
    {
        if (dialect is not { } value) return;
        args.Add("DIALECT");
        args.Add(value);
    }
}

internal static class RespireSearchParameters
{
    internal static void RejectReserved(IReadOnlyDictionary<string, RespireValue> parameters, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(parameters, parameterName);
        if (parameters.ContainsKey(RespireVectorSearchRequest.VectorParameterName))
        {
            throw new ArgumentException(
                $"The '{RespireVectorSearchRequest.VectorParameterName}' parameter name is reserved for the query vector.",
                parameterName);
        }
    }
}

internal static class RespireSearchSort
{
    internal static string Token(RespireSearchSortDirection direction) => direction switch
    {
        RespireSearchSortDirection.Ascending => "ASC",
        RespireSearchSortDirection.Descending => "DESC",
        _ => throw new ArgumentOutOfRangeException(nameof(direction)),
    };
}

internal static class RespireSearchEmpty
{
    internal static readonly IReadOnlyDictionary<string, RespireValue> Values = ReadOnlyDictionary<string, RespireValue>.Empty;
    internal static readonly IReadOnlyDictionary<string, string> Strings = ReadOnlyDictionary<string, string>.Empty;
    internal static readonly IReadOnlyDictionary<string, string?> NullableStrings = ReadOnlyDictionary<string, string?>.Empty;
    internal static readonly IReadOnlyDictionary<string, RespireSearchValue> SearchValues = ReadOnlyDictionary<string, RespireSearchValue>.Empty;
}
