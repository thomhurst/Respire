namespace Respire.Extensions.TimeSeries;

/// <summary>Generated RedisTimeSeries command bindings used by <see cref="RespireTimeSeriesClient"/>.</summary>
[RespireCommands]
internal interface IRespireTimeSeriesCommands
{
    /// <summary>Creates a time series.</summary>
    [RespireCommand("TS.CREATE")]
    ValueTask<RespireResult> CreateAsync(RespireKey key, RespireValue[] options, CancellationToken cancellationToken = default);

    /// <summary>Changes time-series metadata.</summary>
    [RespireCommand("TS.ALTER")]
    ValueTask<RespireResult> AlterAsync(RespireKey key, RespireValue[] options, CancellationToken cancellationToken = default);

    /// <summary>Adds one sample.</summary>
    [RespireCommand("TS.ADD")]
    ValueTask<RespireResult> AddAsync(RespireKey key, string timestamp, double value, RespireValue[] options, CancellationToken cancellationToken = default);

    /// <summary>Adds samples to one or more series.</summary>
    [RespireCommand("TS.MADD")]
    ValueTask<RespireResult> MultiAddAsync(RespireValue[] keyTimestampValueTriples, CancellationToken cancellationToken = default);

    /// <summary>Increments a sample.</summary>
    [RespireCommand("TS.INCRBY")]
    ValueTask<RespireResult> IncrementByAsync(RespireKey key, double increment, RespireValue[] options, CancellationToken cancellationToken = default);

    /// <summary>Decrements a sample.</summary>
    [RespireCommand("TS.DECRBY")]
    ValueTask<RespireResult> DecrementByAsync(RespireKey key, double decrement, RespireValue[] options, CancellationToken cancellationToken = default);

    /// <summary>Gets the latest sample.</summary>
    [RespireCommand("TS.GET")]
    ValueTask<RespireResult> GetAsync(RespireKey key, string[] options, CancellationToken cancellationToken = default);

    /// <summary>Gets latest samples for label filters.</summary>
    [RespireCommand("TS.MGET")]
    ValueTask<RespireResult> MultiGetAsync(RespireValue[] optionsAndFilters, CancellationToken cancellationToken = default);

    /// <summary>Reads samples in ascending timestamp order.</summary>
    [RespireCommand("TS.RANGE")]
    ValueTask<RespireResult> RangeAsync(RespireKey key, string fromTimestamp, string toTimestamp, RespireValue[] options, CancellationToken cancellationToken = default);

    /// <summary>Reads samples in descending timestamp order.</summary>
    [RespireCommand("TS.REVRANGE")]
    ValueTask<RespireResult> ReverseRangeAsync(RespireKey key, string fromTimestamp, string toTimestamp, RespireValue[] options, CancellationToken cancellationToken = default);

    /// <summary>Reads matching series in ascending timestamp order.</summary>
    [RespireCommand("TS.MRANGE")]
    ValueTask<RespireResult> MultiRangeAsync(string fromTimestamp, string toTimestamp, RespireValue[] optionsAndFilters, CancellationToken cancellationToken = default);

    /// <summary>Reads matching series in descending timestamp order.</summary>
    [RespireCommand("TS.MREVRANGE")]
    ValueTask<RespireResult> MultiReverseRangeAsync(string fromTimestamp, string toTimestamp, RespireValue[] optionsAndFilters, CancellationToken cancellationToken = default);

    /// <summary>Deletes samples from one series over an inclusive timestamp range.</summary>
    [RespireCommand("TS.DEL")]
    ValueTask<RespireResult> DeleteRangeAsync(RespireKey key, string fromTimestamp, string toTimestamp, CancellationToken cancellationToken = default);

    /// <summary>Creates a compaction rule.</summary>
    [RespireCommand("TS.CREATERULE")]
    ValueTask<RespireResult> CreateRuleAsync(RespireKey source, RespireKey destination, RespireValue[] aggregation, CancellationToken cancellationToken = default);

    /// <summary>Deletes a compaction rule.</summary>
    [RespireCommand("TS.DELETERULE")]
    ValueTask<RespireResult> DeleteRuleAsync(RespireKey source, RespireKey destination, CancellationToken cancellationToken = default);

    /// <summary>Returns time-series metadata.</summary>
    [RespireCommand("TS.INFO")]
    ValueTask<RespireResult> InfoAsync(RespireKey key, string[] options, CancellationToken cancellationToken = default);

    /// <summary>Finds series matching label filters.</summary>
    [RespireCommand("TS.QUERYINDEX")]
    ValueTask<RespireResult> QueryIndexAsync(string[] filters, CancellationToken cancellationToken = default);
}
