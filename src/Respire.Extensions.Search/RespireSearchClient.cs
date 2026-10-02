using Respire;
using Respire.Protocol;

namespace Redis.Search;

/// <summary>Typed Redis Search index, query, aggregation, vector, and hybrid operations.</summary>
/// <remarks>
/// <para>
/// FT.* commands carry an index name rather than keys. Respire routes them like other module
/// commands: on a cluster, a command goes to the node that owns the index name's hash slot, and
/// cursor reads follow the same index name. Respire does not fan out queries or merge shard
/// results; cross-shard search relies on the server's search coordinator. On a cluster without
/// one (for example, plain Redis Open Source cluster mode), each command sees only the documents
/// on the node that receives it, and Respire cannot detect the partial result.
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
    // Set once a COMMAND INFO probe has shown that the server knows FT.HYBRID, so later
    // FT.HYBRID errors are passed through without probing again.
    private volatile bool _hybridConfirmed;

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
        var options = query.Options ?? RespireSearchQueryOptions.Default;
        using var result = await _commands.SearchAsync(RequireName(index), query.Expression.Value, options.ToArguments(), cancellationToken).ConfigureAwait(false);
        return RespireSearchResult.Parse(result, options.NoContent, options.WithScores);
    }

    /// <summary>Runs an ordered aggregation pipeline. Each row contains named values.</summary>
    public async ValueTask<RespireSearchAggregateResult> AggregateAsync(string index, RespireSearchExpression expression, RespireSearchAggregateOptions? options = null, CancellationToken cancellationToken = default)
    {
        expression.RequireValid(nameof(expression));
        var arguments = (options ?? RespireSearchAggregateOptions.Default).ToArguments();
        using var result = await _commands.AggregateAsync(RequireName(index), expression.Value, arguments, cancellationToken).ConfigureAwait(false);
        return RespireSearchAggregateResult.Parse(result);
    }

    /// <summary>
    /// Runs an aggregation with <c>WITHCURSOR</c> and returns its first page. Read later pages with
    /// <see cref="ReadCursorAsync(RespireSearchAggregateCursorPage, int?, CancellationToken)"/> until
    /// <see cref="RespireSearchAggregateCursorPage.IsComplete"/> is true, or release the cursor early with
    /// <see cref="DeleteCursorAsync(RespireSearchAggregateCursorPage, CancellationToken)"/>.
    /// <see cref="AggregatePagesAsync"/> does both for you.
    /// </summary>
    public async ValueTask<RespireSearchAggregateCursorPage> AggregateWithCursorAsync(string index, RespireSearchExpression expression, RespireSearchAggregateOptions? options = null, RespireSearchCursorOptions? cursor = null, CancellationToken cancellationToken = default)
    {
        expression.RequireValid(nameof(expression));
        var name = RequireName(index);
        var arguments = (options ?? RespireSearchAggregateOptions.Default).ToArguments(cursor ?? new RespireSearchCursorOptions());
        using var result = await _commands.AggregateAsync(name, expression.Value, arguments, cancellationToken).ConfigureAwait(false);
        try
        {
            return RespireSearchAggregateCursorPage.Parse(result, "FT.AGGREGATE", name);
        }
        catch
        {
            // The caller never receives the cursor ID when the first page cannot be parsed, so
            // release it here instead of leaving it to MAXIDLE, then report the parse failure.
            if (RespireSearchAggregateCursorPage.ReadCursorId(result) is var orphan and > 0)
                await TryDeleteCursorAsync(name, orphan).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Runs a cursor aggregation and yields every page, reading the next page only when the caller asks
    /// for it. When enumeration stops before the last page (a <c>break</c>, an exception, or
    /// cancellation), the cursor is deleted on a best-effort basis so it does not wait for
    /// <see cref="RespireSearchCursorOptions.MaxIdleMilliseconds"/> on the server.
    /// </summary>
    public async IAsyncEnumerable<RespireSearchAggregateResult> AggregatePagesAsync(
        string index,
        RespireSearchExpression expression,
        RespireSearchAggregateOptions? options = null,
        RespireSearchCursorOptions? cursor = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var page = await AggregateWithCursorAsync(index, expression, options, cursor, cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                yield return page.Result;
                if (page.IsComplete) yield break;
                page = await ReadCursorAsync(page, count: null, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            // A failed cursor read leaves page at the last known cursor ID. Best-effort cleanup
            // may then target a cursor Redis already advanced or expired.
            if (!page.IsComplete) await TryDeleteCursorAsync(RequirePageIndex(page), page.CursorId).ConfigureAwait(false);
        }
    }

    /// <summary>Reads the page after <paramref name="page"/> with <c>FT.CURSOR READ</c>, using the page's index.</summary>
    /// <param name="page">A page returned by this client that is not yet complete.</param>
    /// <param name="count">Rows to read, or null for the cursor's configured count.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    public ValueTask<RespireSearchAggregateCursorPage> ReadCursorAsync(RespireSearchAggregateCursorPage page, int? count = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        return ReadCursorAsync(RequirePageIndex(page), page.CursorId, count, cancellationToken);
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
        var name = RequireName(index);
        RespireValue[] args = count is { } value
            ? [name, cursorId, "COUNT", value]
            : [name, cursorId];
        // FT.CURSOR READ/DEL use the catalog command rather than IRespireSearchCommands. The generator
        // accepts one command token, so a generated FT.CURSOR method would send READ as its first
        // argument, and cluster routing would hash "READ" instead of the index name and could send
        // the read to a node that does not own the cursor. The catalog entry carries the subcommand,
        // so routing starts at the index name, the same slot FT.AGGREGATE used.
        using var result = await _client.ExecuteAsync(RespireCommands.Search.FT_CURSOR_READ, args, cancellationToken: cancellationToken).ConfigureAwait(false);
        return RespireSearchAggregateCursorPage.Parse(result, "FT.CURSOR READ", name);
    }

    /// <summary>Deletes the cursor behind <paramref name="page"/> with <c>FT.CURSOR DEL</c>. A complete page has no cursor left to delete.</summary>
    public ValueTask DeleteCursorAsync(RespireSearchAggregateCursorPage page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        return page.IsComplete ? default : DeleteCursorAsync(RequirePageIndex(page), page.CursorId, cancellationToken);
    }

    /// <summary>Deletes an aggregation cursor with <c>FT.CURSOR DEL</c>.</summary>
    public async ValueTask DeleteCursorAsync(string index, long cursorId, CancellationToken cancellationToken = default)
    {
        if (cursorId <= 0) throw new ArgumentOutOfRangeException(nameof(cursorId));
        // See ReadCursorAsync for why this uses the catalog command.
        using var result = await _client.ExecuteAsync(RespireCommands.Search.FT_CURSOR_DEL, [RequireName(index), cursorId], cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask TryDeleteCursorAsync(string index, long cursorId)
    {
        try
        {
            // Cleanup runs during unwinding, possibly after the caller's token was cancelled.
            await DeleteCursorAsync(index, cursorId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RespireException or ObjectDisposedException or OperationCanceledException)
        {
            // Best effort: the cursor may already have expired, or the connection may be gone. The
            // server still frees it after MAXIDLE, and a cleanup failure must not hide the error or
            // early exit that ended the enumeration.
        }
    }

    /// <summary>
    /// Decides whether an FT.HYBRID error means the server lacks the command. A <c>COMMAND INFO</c>
    /// probe answers this from the server's command table instead of the error text. The error text
    /// ("ERR unknown command ...") is used only when the probe itself is unavailable, for example
    /// when an ACL denies <c>COMMAND</c> or a proxy does not implement it.
    /// </summary>
    private async ValueTask<bool> IsHybridMissingAsync(RespireServerException error, CancellationToken cancellationToken)
    {
        if (_hybridConfirmed || error.Code != RespireErrorCodes.Err) return false;
        try
        {
            using var info = await _client.ExecuteAsync(RespireCommands.Server.COMMAND_INFO, ["FT.HYBRID"], cancellationToken: cancellationToken).ConfigureAwait(false);
            if (info.Type is RespDataType.Array or RespDataType.Set && info.Count == 1)
            {
                if (info[0].IsNull) return true;
                _hybridConfirmed = true;
                return false;
            }
        }
        catch (RespireException)
        {
            // The probe was denied or failed; fall through to the error text rather than hide the FT.HYBRID error.
        }

        // Redis replies "ERR unknown command 'FT.HYBRID', with args beginning with: ..." when the command does not exist.
        return IsUnknownHybridCommandMessage(error.Message);
    }

    private static bool IsUnknownHybridCommandMessage(string message)
    {
        const string marker = "unknown command";
        var markerIndex = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0) return false;

        var token = message.AsSpan(markerIndex + marker.Length).TrimStart();
        if (token.IsEmpty) return false;
        if (token[0] is '\'' or '"')
        {
            var quote = token[0];
            token = token[1..];
            var end = token.IndexOf(quote);
            if (end >= 0) token = token[..end];
        }
        else
        {
            var end = 0;
            while (end < token.Length && !char.IsWhiteSpace(token[end]) && token[end] is not ',' and not ':') end++;
            token = token[..end];
        }

        return token.Equals("FT.HYBRID", StringComparison.OrdinalIgnoreCase);
    }

    private static string RequirePageIndex(RespireSearchAggregateCursorPage page)
        => string.IsNullOrWhiteSpace(page.Index)
            ? throw new ArgumentException("The page has no index. Use a page returned by RespireSearchClient, or pass the index explicitly.", nameof(page))
            : page.Index;

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
        using var result = await _commands.SearchAsync(RequireName(index), vector.Expression.Value, arguments, cancellationToken).ConfigureAwait(false);
        return RespireSearchResult.Parse(result, selected.NoContent, selected.WithScores);
    }

    /// <summary>Runs an FT.HYBRID query. Redis Open Source 8.4.0 or later is required.</summary>
    /// <exception cref="NotSupportedException">The server does not recognize FT.HYBRID (Redis earlier than 8.4.0).</exception>
    public async ValueTask<RespireSearchResult> HybridSearchAsync(string index, RespireHybridSearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var name = RequireName(index);
        var arguments = query.ToArguments();
        RespireResult result;
        try
        {
            result = await _commands.HybridAsync(name, arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (RespireServerException ex)
        {
            if (await IsHybridMissingAsync(ex, cancellationToken).ConfigureAwait(false))
            {
                throw new NotSupportedException(
                    "The server does not support FT.HYBRID, which requires Redis Open Source 8.4.0 or later with Redis Search. Server error: " + ex.Message,
                    ex);
            }

            throw;
        }

        using (result)
        {
            return RespireSearchResult.ParseHybrid(result);
        }
    }

    /// <summary>Returns the server query plan text.</summary>
    public async ValueTask<string> ExplainAsync(string index, RespireSearchExpression expression, RespireSearchExplainOptions? options = null, CancellationToken cancellationToken = default)
    {
        expression.RequireValid(nameof(expression));
        var selected = options ?? RespireSearchExplainOptions.Default;
        RespireSearchDialect.Validate(selected.Dialect, requiresParameters: false);
        string[] arguments = selected.Dialect is { } dialect
            ? ["DIALECT", dialect.ToString(System.Globalization.CultureInfo.InvariantCulture)]
            : NoOptions;
        using var result = selected.Cli
            ? await _commands.ExplainCliAsync(RequireName(index), expression.Value, arguments, cancellationToken).ConfigureAwait(false)
            : await _commands.ExplainAsync(RequireName(index), expression.Value, arguments, cancellationToken).ConfigureAwait(false);
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
