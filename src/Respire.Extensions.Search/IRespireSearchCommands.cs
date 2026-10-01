namespace Respire.Search;

/// <summary>Generated Redis Search command methods.</summary>
[RespireCommands]
public interface IRespireSearchCommands
{
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
    [RespireCommand("FT.INFO")]
    ValueTask<RespireResult> InfoAsync(string index, CancellationToken cancellationToken = default);

    /// <summary>Performs a full-text search.</summary>
    [RespireCommand("FT.SEARCH")]
    ValueTask<RespireResult> SearchAsync(string index, string query, RespireValue[] options, CancellationToken cancellationToken = default);

    /// <summary>Runs an aggregation pipeline.</summary>
    [RespireCommand("FT.AGGREGATE")]
    ValueTask<RespireResult> AggregateAsync(string index, string query, RespireValue[] options, CancellationToken cancellationToken = default);

    /// <summary>Runs a hybrid text and vector query. Redis Open Source 8.4.0 or later required.</summary>
    [RespireCommand("FT.HYBRID")]
    ValueTask<RespireResult> HybridAsync(string index, RespireValue[] queryArguments, CancellationToken cancellationToken = default);

    /// <summary>Explains a search query.</summary>
    [RespireCommand("FT.EXPLAIN")]
    ValueTask<RespireResult> ExplainAsync(string index, string query, string[] options, CancellationToken cancellationToken = default);

    /// <summary>Explains a search query with command-style output.</summary>
    [RespireCommand("FT.EXPLAINCLI")]
    ValueTask<RespireResult> ExplainCliAsync(string index, string query, string[] options, CancellationToken cancellationToken = default);
}
