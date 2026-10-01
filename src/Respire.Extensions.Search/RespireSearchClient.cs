namespace Respire.Search;

/// <summary>Typed Redis Search index, query, aggregation, vector, and hybrid operations.</summary>
/// <remarks>Search commands are raw module commands. Keys and prefixes must be supplied in index definitions; generated module commands use conservative routing and cache invalidation.</remarks>
public sealed class RespireSearchClient
{
    private readonly IRespireSearchCommands _commands;

    /// <summary>Creates Search operations over a caller-owned Respire client.</summary>
    public RespireSearchClient(IRespireClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
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
        using var result = await _commands.DropAsync(name, deleteDocuments ? ["DD"] : [], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Adds fields to an existing index schema.</summary>
    public async ValueTask AlterIndexAsync(string name, RespireSearchField field, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(field);
        using var result = await _commands.AlterAsync(name, ["SCHEMA", "ADD", .. field.ToArguments()], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns the raw index information response.</summary>
    /// <remarks>The caller owns the result and must dispose it.</remarks>
    public ValueTask<RespireResult> GetIndexInfoAsync(string name, CancellationToken cancellationToken = default)
        => _commands.InfoAsync(RequireName(name), cancellationToken);

    /// <summary>Searches an index with a fluent query and typed response model.</summary>
    public async ValueTask<RespireSearchResult> SearchAsync(string index, RespireSearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        using var result = await _commands.SearchAsync(RequireName(index), query.Expression, query.ToArguments(), cancellationToken).ConfigureAwait(false);
        return RespireSearchResult.Parse(result, query.Options?.NoContent ?? false, query.Options?.WithScores ?? false);
    }

    /// <summary>Runs a typed aggregation pipeline. Each row contains named values.</summary>
    public async ValueTask<RespireSearchAggregateResult> AggregateAsync(string index, string expression, RespireSearchAggregateOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        using var result = await _commands.AggregateAsync(RequireName(index), expression, (options ?? new()).ToArguments(), cancellationToken).ConfigureAwait(false);
        return RespireSearchAggregateResult.Parse(result);
    }

    /// <summary>Runs a typed vector similarity query through FT.SEARCH KNN syntax.</summary>
    public ValueTask<RespireSearchResult> VectorSearchAsync(string index, RespireVectorSearchRequest vector, RespireSearchQueryOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vector);
        var selected = options ?? new RespireSearchQueryOptions();
        if (selected.Dialect is < 2)
            throw new ArgumentOutOfRangeException(nameof(options), selected.Dialect, "Vector KNN queries require dialect 2 or later.");
        var parameters = new Dictionary<string, RespireValue>(StringComparer.Ordinal);
        foreach (var parameter in selected.Parameters) parameters[parameter.Key] = parameter.Value;
        parameters["vector"] = vector.Vector;
        selected = selected with
        {
            Parameters = parameters,
            Dialect = selected.Dialect ?? 2,
            SortBy = selected.SortBy ?? (vector.ScoreField, RespireSearchSortDirection.Ascending),
            Limit = selected.Limit ?? (0, vector.K)
        };
        return SearchAsync(index, new RespireSearchQuery(vector.Expression, selected), cancellationToken);
    }

    /// <summary>Runs an FT.HYBRID query. Redis Open Source 8.4.0 or later is required.</summary>
    public async ValueTask<RespireSearchResult> HybridSearchAsync(string index, RespireHybridSearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        using var result = await _commands.HybridAsync(RequireName(index), query.ToArguments(), cancellationToken).ConfigureAwait(false);
        return RespireSearchResult.Parse(result, hybrid: true);
    }

    /// <summary>Returns the server query plan text.</summary>
    public async ValueTask<string> ExplainAsync(string index, string expression, bool explainCli = false, CancellationToken cancellationToken = default, int? dialect = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        if (dialect is <= 0) throw new ArgumentOutOfRangeException(nameof(dialect));
        string[] options = dialect is { } value
            ? ["DIALECT", value.ToString(System.Globalization.CultureInfo.InvariantCulture)]
            : [];
        using var result = explainCli
            ? await _commands.ExplainCliAsync(RequireName(index), expression, options, cancellationToken).ConfigureAwait(false)
            : await _commands.ExplainAsync(RequireName(index), expression, options, cancellationToken).ConfigureAwait(false);
        if (!explainCli) return result.AsString();
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
