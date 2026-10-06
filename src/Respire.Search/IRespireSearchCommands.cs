using Respire;

namespace Respire.Search;

/// <summary>Generated low-level Redis Search commands used by <see cref="RespireSearchClient"/>.</summary>
[RespireCommands]
internal interface IRespireSearchCommands
{
    [RespireCommand("FT.DICTADD")]
    ValueTask<RespireResult> DictionaryAddAsync(string dictionary, string[] terms, CancellationToken cancellationToken = default);

    [RespireCommand("FT.DICTDEL")]
    ValueTask<RespireResult> DictionaryDeleteAsync(string dictionary, string[] terms, CancellationToken cancellationToken = default);

    [RespireCommand("FT.DICTDUMP", Mutation = RespireCacheMutation.ReadOnly)]
    ValueTask<RespireResult> DictionaryDumpAsync(string dictionary, CancellationToken cancellationToken = default);

    [RespireCommand("FT.SPELLCHECK", Mutation = RespireCacheMutation.ReadOnly)]
    ValueTask<RespireResult> SpellCheckAsync(string index, string query, RespireValue[] options, CancellationToken cancellationToken = default);

    [RespireCommand("FT.SUGADD")]
    ValueTask<RespireResult> AddSuggestionAsync(RespireKey key, string suggestion, double score, RespireValue[] options, CancellationToken cancellationToken = default);

    [RespireCommand("FT.SUGDEL")]
    ValueTask<RespireResult> DeleteSuggestionAsync(RespireKey key, string suggestion, CancellationToken cancellationToken = default);

    [RespireCommand("FT.SUGGET", Mutation = RespireCacheMutation.ReadOnly)]
    ValueTask<RespireResult> GetSuggestionsAsync(RespireKey key, string prefix, RespireValue[] options, CancellationToken cancellationToken = default);

    [RespireCommand("FT.SUGLEN", Mutation = RespireCacheMutation.ReadOnly)]
    ValueTask<RespireResult> GetSuggestionCountAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>Reads node-local Search configuration.</summary>
    [RespireCommand("FT.CONFIG GET")]
    ValueTask<RespireResult> ConfigGetAsync(string option, CancellationToken cancellationToken = default);

    /// <summary>Writes node-local Search configuration.</summary>
    [RespireCommand("FT.CONFIG SET")]
    ValueTask<RespireResult> ConfigSetAsync(string option, string value, CancellationToken cancellationToken = default);

    /// <summary>Creates or extends an index's synonym group.</summary>
    [RespireCommand("FT.SYNUPDATE")]
    ValueTask<RespireResult> UpdateSynonymsAsync(string index, string groupId, string[] termsAndOptions, CancellationToken cancellationToken = default);

    /// <summary>Reads synonym terms and their group memberships.</summary>
    [RespireCommand("FT.SYNDUMP", Mutation = RespireCacheMutation.ReadOnly)]
    ValueTask<RespireResult> GetSynonymsAsync(string index, CancellationToken cancellationToken = default);

    /// <summary>Reads the distinct values indexed in a TAG field.</summary>
    [RespireCommand("FT.TAGVALS", Mutation = RespireCacheMutation.ReadOnly)]
    ValueTask<RespireResult> GetTagValuesAsync(string index, string field, CancellationToken cancellationToken = default);

    /// <summary>Lists indexes on the selected server.</summary>
    [RespireCommand("FT._LIST", Mutation = RespireCacheMutation.ReadOnly)]
    ValueTask<RespireResult> ListIndexesAsync(CancellationToken cancellationToken = default);

    /// <summary>Adds an alias for an existing index.</summary>
    [RespireCommand("FT.ALIASADD")]
    ValueTask<RespireResult> AddAliasAsync(string alias, string index, CancellationToken cancellationToken = default);

    /// <summary>Deletes an alias without deleting its index.</summary>
    [RespireCommand("FT.ALIASDEL")]
    ValueTask<RespireResult> DeleteAliasAsync(string alias, CancellationToken cancellationToken = default);

    /// <summary>Creates or atomically redirects an alias to an existing index.</summary>
    [RespireCommand("FT.ALIASUPDATE")]
    ValueTask<RespireResult> UpdateAliasAsync(string alias, string index, CancellationToken cancellationToken = default);

    /// <summary>Lists aliases for an index.</summary>
    [RespireCommand("FT.ALIASLIST", Mutation = RespireCacheMutation.ReadOnly)]
    ValueTask<RespireResult> ListAliasesAsync(string index, CancellationToken cancellationToken = default);

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

    /// <summary>Profiles a Search, Aggregate, or Hybrid query.</summary>
    [RespireCommand("FT.PROFILE", Mutation = RespireCacheMutation.ReadOnly)]
    ValueTask<RespireResult> ProfileAsync(string index, string mode, string[] profileOptions, RespireValue[] queryArguments, CancellationToken cancellationToken = default);

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
