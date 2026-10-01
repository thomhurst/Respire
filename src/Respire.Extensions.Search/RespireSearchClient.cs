namespace Respire.Extensions.Search;

/// <summary>Typed Redis Search index, query, aggregation, vector, and hybrid operations.</summary>
/// <remarks>
/// <para>
/// FT.* commands carry an index name rather than keys. Respire routes them like other module
/// commands: on a cluster, a command goes to the node that owns the index name's hash slot, and
/// cursor reads follow the same index name. Respire does not fan out queries or merge shard
/// results; cross-shard search relies on the server's search coordinator.
/// </para>
/// <para>
/// With client-side caching, read-only Search commands leave the local cache intact; FT.CREATE,
/// FT.ALTER, and FT.DROPINDEX invalidate it conservatively. Key-prefixed views reject Search
/// commands, so include prefixes in index definitions. Each query method builds one argument list
/// per call; this package does not target the zero-allocation hot path.
/// </para>
/// </remarks>
public sealed class RespireSearchClient
{
    private static readonly string[] DeleteDocumentsOption = ["DD"];
    private static readonly string[] NoOptions = [];

    private readonly IRespireClient _client;
    private readonly IRespireSearchCommands _commands;

    /// <summary>Creates Search operations over a caller-owned Respire client.</summary>
    public RespireSearchClient(IRespireClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _commands = new IRespireSearchCommandsImplementation(client);
    }

    /// <summary>Creates a search index.</summary>
    public async ValueTask CreateIndexAsync(string name, RespireSearchIndexDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(definition);
        using var result = await _commands.CreateAsync(name, definition.ToArguments(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Drops an index, optionally deleting indexed documents.</summary>
    public async ValueTask DropIndexAsync(string name, bool deleteDocuments = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        using var result = await _commands.DropAsync(name, deleteDocuments ? DeleteDocumentsOption : NoOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Adds a field to an existing index schema.</summary>
    public async ValueTask AlterIndexAsync(string name, RespireSearchField field, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(field);
        var args = new List<RespireValue> { "SCHEMA", "ADD" };
        field.AddArguments(args);
        using var result = await _commands.AlterAsync(name, [.. args], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns parsed FT.INFO details: name, document count, schema attributes, and every reported property.</summary>
    public async ValueTask<RespireSearchIndexInfo> GetIndexInfoAsync(string name, CancellationToken cancellationToken = default)
    {
        using var result = await _commands.InfoAsync(RequireName(name), cancellationToken).ConfigureAwait(false);
        return RespireSearchIndexInfo.Parse(result);
    }

    /// <summary>Searches an index with a typed query and response model.</summary>
    public async ValueTask<RespireSearchResult> SearchAsync(string index, RespireSearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.Expression);
        var options = query.Options ?? RespireSearchQueryOptions.Default;
        using var result = await _commands.SearchAsync(RequireName(index), query.Expression, options.ToArguments(), cancellationToken).ConfigureAwait(false);
        return RespireSearchResult.Parse(result, options.NoContent, options.WithScores);
    }

    /// <summary>Runs an ordered aggregation pipeline. Each row contains named values.</summary>
    public async ValueTask<RespireSearchAggregateResult> AggregateAsync(string index, string expression, RespireSearchAggregateOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        var arguments = (options ?? RespireSearchAggregateOptions.Default).ToArguments();
        using var result = await _commands.AggregateAsync(RequireName(index), expression, arguments, cancellationToken).ConfigureAwait(false);
        return RespireSearchAggregateResult.Parse(result);
    }

    /// <summary>
    /// Runs an aggregation with <c>WITHCURSOR</c> and returns its first page. Read later pages with
    /// <see cref="ReadCursorAsync"/> until <see cref="RespireSearchAggregateCursorPage.IsComplete"/> is true,
    /// or release the cursor early with <see cref="DeleteCursorAsync"/>.
    /// </summary>
    public async ValueTask<RespireSearchAggregateCursorPage> AggregateWithCursorAsync(string index, string expression, RespireSearchAggregateOptions? options = null, RespireSearchCursorOptions? cursor = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        var arguments = (options ?? RespireSearchAggregateOptions.Default).ToArguments(cursor ?? new RespireSearchCursorOptions());
        using var result = await _commands.AggregateAsync(RequireName(index), expression, arguments, cancellationToken).ConfigureAwait(false);
        return RespireSearchAggregateCursorPage.Parse(result, "FT.AGGREGATE");
    }

    /// <summary>Reads the next page of an aggregation cursor with <c>FT.CURSOR READ</c>.</summary>
    /// <param name="index">Index used by the aggregation.</param>
    /// <param name="cursorId">Cursor returned by the previous page.</param>
    /// <param name="count">Rows to read, or null for the cursor's configured count.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    public async ValueTask<RespireSearchAggregateCursorPage> ReadCursorAsync(string index, long cursorId, int? count = null, CancellationToken cancellationToken = default)
    {
        if (cursorId <= 0) throw new ArgumentOutOfRangeException(nameof(cursorId));
        if (count is <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        RespireValue[] args = count is { } value
            ? [RequireName(index), cursorId, "COUNT", value]
            : [RequireName(index), cursorId];
        using var result = await _client.ExecuteAsync(RespireCommands.Search.FT_CURSOR_READ, args, cancellationToken: cancellationToken).ConfigureAwait(false);
        return RespireSearchAggregateCursorPage.Parse(result, "FT.CURSOR READ");
    }

    /// <summary>Deletes an aggregation cursor with <c>FT.CURSOR DEL</c>.</summary>
    public async ValueTask DeleteCursorAsync(string index, long cursorId, CancellationToken cancellationToken = default)
    {
        if (cursorId <= 0) throw new ArgumentOutOfRangeException(nameof(cursorId));
        using var result = await _client.ExecuteAsync(RespireCommands.Search.FT_CURSOR_DEL, [RequireName(index), cursorId], cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs a typed vector similarity query through FT.SEARCH KNN syntax.</summary>
    /// <remarks>
    /// Dialect 2 is used unless the options select a later one. <c>Limit</c> defaults to
    /// <c>(0, K)</c> and <c>SortBy</c> to the score ascending; explicit values are kept. The vector
    /// travels in the reserved <see cref="RespireVectorSearchRequest.VectorParameterName"/> parameter.
    /// </remarks>
    public async ValueTask<RespireSearchResult> VectorSearchAsync(string index, RespireVectorSearchRequest vector, RespireSearchQueryOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vector);
        var selected = options ?? RespireSearchQueryOptions.Default;
        selected = selected with
        {
            Dialect = selected.Dialect ?? RespireSearchDialect.ParameterDialect,
            SortBy = selected.SortBy ?? (vector.ScoreField, RespireSearchSortDirection.Ascending),
            Limit = selected.Limit ?? (0, vector.K),
        };
        var arguments = selected.ToArguments(
            new KeyValuePair<string, RespireValue>(RespireVectorSearchRequest.VectorParameterName, vector.Vector));
        using var result = await _commands.SearchAsync(RequireName(index), vector.Expression, arguments, cancellationToken).ConfigureAwait(false);
        return RespireSearchResult.Parse(result, selected.NoContent, selected.WithScores);
    }

    /// <summary>Runs an FT.HYBRID query. Redis Open Source 8.4.0 or later is required.</summary>
    public async ValueTask<RespireSearchResult> HybridSearchAsync(string index, RespireHybridSearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        using var result = await _commands.HybridAsync(RequireName(index), query.ToArguments(), cancellationToken).ConfigureAwait(false);
        return RespireSearchResult.ParseHybrid(result);
    }

    /// <summary>Returns the server query plan text.</summary>
    public async ValueTask<string> ExplainAsync(string index, string expression, RespireSearchExplainOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        var selected = options ?? RespireSearchExplainOptions.Default;
        RespireSearchDialect.Validate(selected.Dialect, requiresParameters: false);
        string[] arguments = selected.Dialect is { } dialect
            ? ["DIALECT", dialect.ToString(System.Globalization.CultureInfo.InvariantCulture)]
            : NoOptions;
        using var result = selected.Cli
            ? await _commands.ExplainCliAsync(RequireName(index), expression, arguments, cancellationToken).ConfigureAwait(false)
            : await _commands.ExplainAsync(RequireName(index), expression, arguments, cancellationToken).ConfigureAwait(false);
        if (!selected.Cli) return result.AsString();
        var lines = new string[result.Count];
        for (var i = 0; i < result.Count; i++) lines[i] = result[i].AsString();
        return string.Join(Environment.NewLine, lines);
    }

    private static string RequireName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name;
    }
}
