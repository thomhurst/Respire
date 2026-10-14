namespace Respire.Search;

/// <summary>An immutable query over source-generated field selectors.</summary>
/// <remarks>Selectors are ordinary delegates invoked immediately. No expression trees or reflection are used.
/// Sorting and projection describe server fields; results retain the existing Search result types.</remarks>
public sealed class RespireTypedSearchQuery<TFields> where TFields : class
{
    private readonly TFields _fields;
    private readonly RespireSearchExpression? _filter;
    private readonly RespireSearchQueryOptions _options;

    /// <summary>Creates a query. Generated schemas supply their index and selectors.</summary>
    public RespireTypedSearchQuery(string indexName, TFields fields)
        : this(indexName, fields, null, new() { Dialect = 2 }) { }

    private RespireTypedSearchQuery(string indexName, TFields fields, RespireSearchExpression? filter, RespireSearchQueryOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        ArgumentNullException.ThrowIfNull(fields);
        IndexName = indexName;
        _fields = fields;
        _filter = filter;
        _options = options;
    }

    /// <summary>The index or deployment alias to query.</summary>
    public string IndexName { get; }

    /// <summary>The composed query expression.</summary>
    public RespireSearchExpression Expression => _filter ?? RespireSearchQueryBuilder.MatchAll();

    /// <summary>Adds a filter with AND. Use QueryBuilder.And/Or within the selector for groups.</summary>
    public RespireTypedSearchQuery<TFields> Where(Func<TFields, RespireSearchExpression> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var expression = predicate(_fields).RequireValid(nameof(predicate));
        return new(IndexName, _fields, _filter is { } previous ? RespireSearchQueryBuilder.And(previous, expression) : expression, _options);
    }

    /// <summary>Sets the single FT.SEARCH sort field, replacing any previous sort.</summary>
    public RespireTypedSearchQuery<TFields> OrderBy(Func<TFields, RespireSearchQueryField<TFields>> selector,
        RespireSearchSortDirection direction = RespireSearchSortDirection.Ascending)
    {
        var selected = Field(selector);
        if (!selected.Definition.Sortable) throw new InvalidOperationException($"Field '{selected.Name}' is not SORTABLE.");
        _ = RespireSearchSort.Token(direction);
        return WithOptions(_options with { SortBy = (selected.Name, direction) });
    }

    /// <summary>Sets descending order on a sortable field.</summary>
    public RespireTypedSearchQuery<TFields> OrderByDescending(Func<TFields, RespireSearchQueryField<TFields>> selector)
        => OrderBy(selector, RespireSearchSortDirection.Descending);

    /// <summary>Sets the zero-based server offset. Without Take, the page size is ten.</summary>
    public RespireTypedSearchQuery<TFields> Skip(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return WithOptions(_options with { Limit = (count, _options.Limit?.Count ?? 10) });
    }

    /// <summary>Sets the maximum page size, preserving any offset.</summary>
    public RespireTypedSearchQuery<TFields> Take(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return WithOptions(_options with { Limit = (_options.Limit?.Offset ?? 0, count) });
    }

    /// <summary>Selects returned fields. The selection is copied and replaces any prior projection.</summary>
    public RespireTypedSearchQuery<TFields> Select(Func<TFields, RespireSearchQueryField<TFields>[]> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        var selected = selector(_fields);
        ArgumentNullException.ThrowIfNull(selected);
        if (selected.Length == 0) throw new ArgumentException("Select at least one field.", nameof(selector));
        var names = new string[selected.Length];
        for (var i = 0; i < selected.Length; i++)
        {
            ArgumentNullException.ThrowIfNull(selected[i]);
            names[i] = selected[i].Name;
        }
        return WithOptions(_options with { ReturnFields = Array.AsReadOnly(names) });
    }

    /// <summary>Builds a request for the existing FT.SEARCH API.</summary>
    public RespireSearchQuery ToSearchQuery() => new(Expression, _options);

    /// <summary>Builds ordered LOAD, SORTBY and LIMIT stages for FT.AGGREGATE.</summary>
    /// <remarks>Selected fields and the sort field are loaded before sorting and paging.
    /// LOAD makes fields available to the pipeline; it does not remove other aggregate output fields.</remarks>
    public RespireSearchAggregateOptions ToAggregateOptions()
    {
        var stages = new List<RespireSearchAggregateStage>();
        var load = _options.ReturnFields.Select(name => "@" + name).ToList();
        if (_options.SortBy is { } sort && !load.Contains("@" + sort.Field)) load.Add("@" + sort.Field);
        if (load.Count > 0) stages.Add(RespireSearchAggregateStage.Load(load.ToArray()));
        if (_options.SortBy is { } order)
            stages.Add(RespireSearchAggregateStage.SortBy(new RespireSearchAggregateSort("@" + order.Field, order.Direction)));
        if (_options.Limit is { } limit) stages.Add(RespireSearchAggregateStage.Limit(limit.Offset, limit.Count));
        return new() { Stages = stages.AsReadOnly(), Dialect = _options.Dialect };
    }

    /// <summary>Builds a KNN request with the current filters as its hybrid pre-filter.</summary>
    /// <remarks>Supply bytes in the field's indexed element format, including for JSON vectors.
    /// Keep the underlying memory unchanged until execution completes.</remarks>
    public RespireVectorSearchRequest ToVectorRequest(Func<TFields, RespireSearchVectorField<TFields>> selector,
        ReadOnlyMemory<byte> vector, int count)
    {
        ArgumentNullException.ThrowIfNull(selector);
        var field = selector(_fields);
        ArgumentNullException.ThrowIfNull(field);
        return field.CreateRequest(vector, count, _filter);
    }

    /// <summary>Executes FT.SEARCH using this query's index and modifiers.</summary>
    public ValueTask<RespireSearchResult> SearchAsync(RespireSearchClient client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client.SearchAsync(IndexName, ToSearchQuery(), cancellationToken);
    }

    /// <summary>Executes FT.AGGREGATE using this query's filter and ordered stages.</summary>
    public ValueTask<RespireSearchAggregateResult> AggregateAsync(RespireSearchClient client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client.AggregateAsync(IndexName, Expression, ToAggregateOptions(), cancellationToken);
    }

    /// <summary>Executes filtered KNN with this query's sorting, paging and projection.</summary>
    public ValueTask<RespireSearchResult> VectorSearchAsync(RespireSearchClient client,
        Func<TFields, RespireSearchVectorField<TFields>> selector, ReadOnlyMemory<byte> vector, int count,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client.VectorSearchAsync(IndexName, ToVectorRequest(selector, vector, count), _options, cancellationToken);
    }

    private RespireSearchQueryField<TFields> Field(Func<TFields, RespireSearchQueryField<TFields>> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        var selected = selector(_fields);
        ArgumentNullException.ThrowIfNull(selected);
        return selected;
    }

    private RespireTypedSearchQuery<TFields> WithOptions(RespireSearchQueryOptions options) => new(IndexName, _fields, _filter, options);
}
