using Respire;

namespace Respire.Search;

/// <summary>Generated low-level Redis Search commands used by <see cref="RespireSearchClient"/>.</summary>
[RespireCommands]
internal interface IRespireSearchCommands
{
    [RespireCommand("FT.SUGADD")]
    ValueTask<RespireResult> AddSuggestionAsync(RespireKey key, string suggestion, double score, RespireValue[] options, CancellationToken cancellationToken = default);

    [RespireCommand("FT.SUGDEL")]
    ValueTask<RespireResult> DeleteSuggestionAsync(RespireKey key, string suggestion, CancellationToken cancellationToken = default);

    [RespireCommand("FT.SUGGET", Mutation = RespireCacheMutation.ReadOnly)]
    ValueTask<RespireResult> GetSuggestionsAsync(RespireKey key, string prefix, RespireValue[] options, CancellationToken cancellationToken = default);

    [RespireCommand("FT.SUGLEN", Mutation = RespireCacheMutation.ReadOnly)]
    ValueTask<RespireResult> GetSuggestionCountAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>Creates or extends an index's synonym group.</summary>
    [RespireCommand("FT.SYNUPDATE")]
    ValueTask<RespireResult> UpdateSynonymsAsync(string index, string groupId, string[] termsAndOptions, CancellationToken cancellationToken = default);

    /// <summary>Reads synonym terms and their group memberships.</summary>
    [RespireCommand("FT.SYNDUMP", Mutation = RespireCacheMutation.ReadOnly)]
    ValueTask<RespireResult> GetSynonymsAsync(string index, CancellationToken cancellationToken = default);

    /// <summary>Reads the distinct values indexed in a TAG field.</summary>
    [RespireCommand("FT.TAGVALS", Mutation = RespireCacheMutation.ReadOnly)]
    ValueTask<RespireResult> GetTagValuesAsync(string index, string field, CancellationToken cancellationToken = default);

    /// <summary>Creates an index from command arguments.</summary>
    [RespireCommand("FT.CREATE")]
    ValueTask<RespireResult> CreateAsync(string index, RespireValue[] arguments, CancellationToken cancellationToken = default);

    /// <summary>Drops an index.</summary>
    [RespireCommand("FT.DROPINDEX")]
    ValueTask<RespireResult> DropAsync(string index, string[] options, CancellationToken cancellationToken = default);

    /// <summary>Alters an index schema.</summary>
    [RespireCommand("FT.ALTER")]
    ValueTask<RespireResult> AlterAsync(string index, RespireValue[] arguments, CancellationToken cancellationToken = default);

    /// <summary>Returns index information.</summary>
    [RespireCommand("FT.INFO", Mutation = RespireCacheMutation.ReadOnly)]
    ValueTask<RespireResult> InfoAsync(string index, CancellationToken cancellationToken = default);

    /// <summary>Performs a full-text search.</summary>
    [RespireCommand("FT.SEARCH", Mutation = RespireCacheMutation.ReadOnly)]
    ValueTask<RespireResult> SearchAsync(string index, string query, RespireValue[] options, CancellationToken cancellationToken = default);

    /// <summary>Runs an aggregation pipeline.</summary>
    [RespireCommand("FT.AGGREGATE", Mutation = RespireCacheMutation.ReadOnly)]
    ValueTask<RespireResult> AggregateAsync(string index, string query, RespireValue[] options, CancellationToken cancellationToken = default);

    /// <summary>Runs a hybrid text and vector query. Redis Open Source 8.4.0 or later required.</summary>
    [RespireCommand("FT.HYBRID", Mutation = RespireCacheMutation.ReadOnly)]
    ValueTask<RespireResult> HybridAsync(string index, RespireValue[] queryArguments, CancellationToken cancellationToken = default);

    /// <summary>Explains a search query.</summary>
    [RespireCommand("FT.EXPLAIN", Mutation = RespireCacheMutation.ReadOnly)]
    ValueTask<RespireResult> ExplainAsync(string index, string query, string[] options, CancellationToken cancellationToken = default);

    /// <summary>Explains a search query with command-style output.</summary>
    [RespireCommand("FT.EXPLAINCLI", Mutation = RespireCacheMutation.ReadOnly)]
    ValueTask<RespireResult> ExplainCliAsync(string index, string query, string[] options, CancellationToken cancellationToken = default);

    /// <summary>Reads a page from an aggregation cursor.</summary>
    [RespireCommand("FT.CURSOR READ")]
    ValueTask<RespireResult> CursorReadAsync(string index, long cursorId, RespireValue[] options, CancellationToken cancellationToken = default);

    /// <summary>Deletes an aggregation cursor.</summary>
    [RespireCommand("FT.CURSOR DEL")]
    ValueTask<RespireResult> CursorDeleteAsync(string index, long cursorId, CancellationToken cancellationToken = default);
}
