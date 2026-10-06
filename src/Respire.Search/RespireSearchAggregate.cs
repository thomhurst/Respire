using Respire;

namespace Respire.Search;

/// <summary>Typed FT.AGGREGATE pipeline options.</summary>
/// <remarks>
/// FT.AGGREGATE operations form an ordered pipeline: a FILTER can use an alias created by an
/// earlier APPLY, and an APPLY can use a reducer output from an earlier GROUPBY. <see cref="Stages"/>
/// is sent exactly in the order given.
/// </remarks>
public sealed record RespireSearchAggregateOptions
{
    internal static readonly RespireSearchAggregateOptions Default = new();

    /// <summary>Pipeline stages in execution order. Use the factory methods on <see cref="RespireSearchAggregateStage"/>.</summary>
    public IReadOnlyList<RespireSearchAggregateStage> Stages { get; init; } = [];

    /// <summary>Query dialect version.</summary>
    public int? Dialect { get; init; }

    /// <summary>Builds pipeline arguments in caller order.</summary>
    internal RespireValue[] ToArguments(RespireSearchCursorOptions? cursor = null)
    {
        ArgumentNullException.ThrowIfNull(Stages);
        RespireSearchDialect.Validate(Dialect, requiresParameters: false);
        var args = new List<RespireValue>();
        foreach (var stage in Stages)
        {
            ArgumentNullException.ThrowIfNull(stage, nameof(Stages));
            stage.AddArguments(args);
        }

        cursor?.AddArguments(args);
        RespireSearchDialect.Add(args, Dialect);
        return [.. args];
    }
}

/// <summary>Base type for ordered FT.AGGREGATE operations.</summary>
public abstract record RespireSearchAggregateStage
{
    private protected RespireSearchAggregateStage()
    {
    }

    /// <summary>Creates a LOAD stage.</summary>
    public static RespireSearchAggregateLoad Load(params string[] fields) => new(fields);

    /// <summary>Creates a FILTER stage using raw FT.AGGREGATE expression syntax.</summary>
    public static RespireSearchAggregateFilter Filter(string expression) => new(expression);

    /// <summary>Creates an APPLY stage using raw FT.AGGREGATE expression syntax.</summary>
    public static RespireSearchAggregateApply Apply(string expression, string alias) => new(expression, alias);

    /// <summary>Creates a GROUPBY stage. An empty property list groups every row together.</summary>
    public static RespireSearchAggregateGroupBy GroupBy(IReadOnlyList<string> properties, params RespireSearchReducer[] reducers) => new(properties, reducers);

    /// <summary>Creates a SORTBY stage over one or more keys.</summary>
    public static RespireSearchAggregateSortBy SortBy(params RespireSearchAggregateSort[] keys) => new(keys);

    /// <summary>Creates a LIMIT stage.</summary>
    public static RespireSearchAggregateLimit Limit(int offset, int count) => new(offset, count);

    internal abstract void AddArguments(List<RespireValue> args);
}

/// <summary>Loads fields before later stages.</summary>
public sealed record RespireSearchAggregateLoad(IReadOnlyList<string> Fields) : RespireSearchAggregateStage
{
    internal override void AddArguments(List<RespireValue> args)
    {
        ArgumentNullException.ThrowIfNull(Fields);
        if (Fields.Count == 0) throw new ArgumentException("LOAD requires at least one field.", nameof(Fields));
        args.Add("LOAD");
        args.Add(Fields.Count);
        foreach (var field in Fields)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(field);
            args.Add(field);
        }
    }
}

/// <summary>Filters rows at this point in the pipeline using raw FT.AGGREGATE expression syntax.</summary>
public sealed record RespireSearchAggregateFilter(string Expression) : RespireSearchAggregateStage
{
    internal override void AddArguments(List<RespireValue> args)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Expression);
        args.Add("FILTER");
        args.Add(Expression);
    }
}

/// <summary>Adds an expression result at this point in the pipeline using raw FT.AGGREGATE expression syntax.</summary>
public sealed record RespireSearchAggregateApply(string Expression, string Alias) : RespireSearchAggregateStage
{
    internal override void AddArguments(List<RespireValue> args)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Expression);
        ArgumentException.ThrowIfNullOrWhiteSpace(Alias);
        args.Add("APPLY");
        args.Add(Expression);
        args.Add("AS");
        args.Add(Alias);
    }
}

/// <summary>Groups rows at this point in the pipeline.</summary>
/// <param name="Properties">Group-by properties. An empty list groups every row together (<c>GROUPBY 0</c>).</param>
/// <param name="Reducers">Reducers applied to each group.</param>
public sealed record RespireSearchAggregateGroupBy(IReadOnlyList<string> Properties, IReadOnlyList<RespireSearchReducer> Reducers) : RespireSearchAggregateStage
{
    internal override void AddArguments(List<RespireValue> args)
    {
        ArgumentNullException.ThrowIfNull(Properties);
        ArgumentNullException.ThrowIfNull(Reducers);
        args.Add("GROUPBY");
        args.Add(Properties.Count);
        foreach (var property in Properties)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(property);
            args.Add(property);
        }

        foreach (var reducer in Reducers)
        {
            ArgumentNullException.ThrowIfNull(reducer, nameof(Reducers));
            reducer.AddArguments(args);
        }
    }
}

/// <summary>Sorts rows at this point in the pipeline.</summary>
/// <param name="Keys">Sort keys, applied in order.</param>
public sealed record RespireSearchAggregateSortBy(IReadOnlyList<RespireSearchAggregateSort> Keys) : RespireSearchAggregateStage
{
    /// <summary>Optional <c>MAX</c> number of sorted rows to keep.</summary>
    public int? Max { get; init; }

    internal override void AddArguments(List<RespireValue> args)
    {
        ArgumentNullException.ThrowIfNull(Keys);
        if (Keys.Count == 0) throw new ArgumentException("SORTBY requires at least one key.", nameof(Keys));
        if (Max is <= 0) throw new ArgumentOutOfRangeException(nameof(Max));
        args.Add("SORTBY");
        args.Add(checked(Keys.Count * 2));
        foreach (var key in Keys)
        {
            ArgumentNullException.ThrowIfNull(key, nameof(Keys));
            ArgumentException.ThrowIfNullOrWhiteSpace(key.Field);
            args.Add(key.Field);
            args.Add(RespireSearchSort.Token(key.Direction));
        }

        if (Max is { } max)
        {
            args.Add("MAX");
            args.Add(max);
        }
    }
}

/// <summary>One aggregation sort key. The field is sent as one argument, so aliases may contain spaces.</summary>
/// <param name="Field">Property or alias, such as <c>@count</c>.</param>
/// <param name="Direction">Sort direction.</param>
public sealed record RespireSearchAggregateSort(string Field, RespireSearchSortDirection Direction = RespireSearchSortDirection.Ascending);

/// <summary>Limits rows at this point in the pipeline.</summary>
/// <param name="Offset">Number of rows to skip.</param>
/// <param name="Count">Maximum number of rows to keep.</param>
public sealed record RespireSearchAggregateLimit(int Offset, int Count) : RespireSearchAggregateStage
{
    internal override void AddArguments(List<RespireValue> args)
    {
        if (Offset < 0) throw new ArgumentOutOfRangeException(nameof(Offset));
        if (Count < 0) throw new ArgumentOutOfRangeException(nameof(Count));
        args.Add("LIMIT");
        args.Add(Offset);
        args.Add(Count);
    }
}

/// <summary>One Redis Search aggregation reducer.</summary>
public sealed record RespireSearchReducer(string Function, IReadOnlyList<string> Arguments, string? Alias = null)
{
    /// <summary>Creates a Redis 8.10 COLLECT reducer. Fields must already be loaded or generated in the pipeline.</summary>
    public static RespireSearchReducer Collect(RespireSearchCollectOptions options, string? alias = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new("COLLECT", options.ToArguments(), alias);
    }

    internal void AddArguments(List<RespireValue> args)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Function);
        ArgumentNullException.ThrowIfNull(Arguments);
        args.Add("REDUCE");
        args.Add(Function);
        args.Add(Arguments.Count);
        foreach (var argument in Arguments) args.Add(argument);
        if (!string.IsNullOrWhiteSpace(Alias))
        {
            args.Add("AS");
            args.Add(Alias);
        }
    }
}

/// <summary>Projects and optionally sorts and limits documents within each aggregate group.</summary>
public sealed record RespireSearchCollectOptions
{
    /// <summary>Fields to collect. Names are normalized to an @ prefix.</summary>
    public IReadOnlyList<string> Fields { get; init; } = [];

    /// <summary>Collects all fields materialized in the pipeline. Cannot be combined with <see cref="Fields"/>.</summary>
    public bool AllFields { get; init; }

    /// <summary>Deduplicates collected entries.</summary>
    public bool Distinct { get; init; }

    /// <summary>Sort keys within each group.</summary>
    public IReadOnlyList<RespireSearchAggregateSort> SortBy { get; init; } = [];

    /// <summary>Offset and count within each group, applied after sorting.</summary>
    public (int Offset, int Count)? Limit { get; init; }

    internal string[] ToArguments()
    {
        ArgumentNullException.ThrowIfNull(Fields);
        ArgumentNullException.ThrowIfNull(SortBy);
        if (AllFields == (Fields.Count > 0))
            throw new ArgumentException("Choose AllFields or a nonempty Fields list.", nameof(Fields));
        var args = new List<string> { "FIELDS" };
        if (AllFields) args.Add("*");
        else
        {
            args.Add(Fields.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var field in Fields) args.Add(Property(field));
        }
        if (Distinct) args.Add("DISTINCT");
        if (SortBy.Count > 0)
        {
            args.Add("SORTBY");
            args.Add(checked(SortBy.Count * 2).ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var sort in SortBy)
            {
                ArgumentNullException.ThrowIfNull(sort);
                args.Add(Property(sort.Field));
                args.Add(RespireSearchSort.Token(sort.Direction));
            }
        }
        if (Limit is { } limit)
        {
            if (limit.Offset < 0 || limit.Count < 0) throw new ArgumentOutOfRangeException(nameof(Limit));
            args.Add("LIMIT");
            args.Add(limit.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture));
            args.Add(limit.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return [.. args];
    }

    private static string Property(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var field = name.TrimStart('@');
        ArgumentException.ThrowIfNullOrWhiteSpace(field, nameof(name));
        return "@" + field;
    }
}

/// <summary>Cursor settings for paging large FT.AGGREGATE results.</summary>
public sealed record RespireSearchCursorOptions
{
    /// <summary>Rows per page (<c>COUNT</c>). Null uses the server default.</summary>
    public int? Count { get; init; }

    /// <summary>Idle time in milliseconds before the server deletes the cursor (<c>MAXIDLE</c>).</summary>
    public int? MaxIdleMilliseconds { get; init; }

    internal void AddArguments(List<RespireValue> args)
    {
        if (Count is <= 0) throw new ArgumentOutOfRangeException(nameof(Count));
        if (MaxIdleMilliseconds is <= 0) throw new ArgumentOutOfRangeException(nameof(MaxIdleMilliseconds));
        args.Add("WITHCURSOR");
        if (Count is { } count)
        {
            args.Add("COUNT");
            args.Add(count);
        }

        if (MaxIdleMilliseconds is { } maxIdle)
        {
            args.Add("MAXIDLE");
            args.Add(maxIdle);
        }
    }
}
