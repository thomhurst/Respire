namespace Respire.TimeSeries;

/// <summary>Generated RedisTimeSeries command bindings used by <see cref="RespireTimeSeriesClient"/>.</summary>
[RespireCommands]
internal interface IRespireTimeSeriesCommands
{
    /// <summary>Creates a time series.</summary>
    [RespireCommand("TS.CREATE", Mutation = RespireCacheMutation.SingleKey)]
    ValueTask<RespireResult> CreateAsync(RespireKey key, RespireValue[] options, CancellationToken cancellationToken = default);

    /// <summary>Changes time-series metadata.</summary>
    [RespireCommand("TS.ALTER", Mutation = RespireCacheMutation.SingleKey)]
    ValueTask<RespireResult> AlterAsync(RespireKey key, RespireValue[] options, CancellationToken cancellationToken = default);

    /// <summary>Adds one sample.</summary>
    [RespireCommand("TS.ADD", Mutation = RespireCacheMutation.Unknown)]
    ValueTask<RespireResult> AddAsync(RespireKey key, string timestamp, double value, RespireValue[] options, CancellationToken cancellationToken = default);

    /// <summary>Adds samples to one or more series.</summary>
    [RespireCommand("TS.MADD", Mutation = RespireCacheMutation.Unknown)]
    ValueTask<RespireResult> MultiAddAsync(RespireValue[] keyTimestampValueTriples, CancellationToken cancellationToken = default);

    /// <summary>Increments a sample.</summary>
    [RespireCommand("TS.INCRBY", Mutation = RespireCacheMutation.Unknown)]
    ValueTask<RespireResult> IncrementByAsync(RespireKey key, double increment, RespireValue[] options, CancellationToken cancellationToken = default);

    /// <summary>Decrements a sample.</summary>
    [RespireCommand("TS.DECRBY", Mutation = RespireCacheMutation.Unknown)]
    ValueTask<RespireResult> DecrementByAsync(RespireKey key, double decrement, RespireValue[] options, CancellationToken cancellationToken = default);

    /// <summary>Gets the latest sample.</summary>
    [RespireCommand("TS.GET", Mutation = RespireCacheMutation.ReadOnly)]
    ValueTask<RespireResult> GetAsync(RespireKey key, string[] options, CancellationToken cancellationToken = default);

    /// <summary>Gets latest samples for label filters.</summary>
    [RespireCommand("TS.MGET", Mutation = RespireCacheMutation.ReadOnly)]
    ValueTask<RespireResult> MultiGetAsync(RespireValue[] optionsAndFilters, CancellationToken cancellationToken = default);

    /// <summary>Reads samples in ascending timestamp order.</summary>
    [RespireCommand("TS.RANGE", Mutation = RespireCacheMutation.ReadOnly)]
    ValueTask<RespireResult> RangeAsync(RespireKey key, string fromTimestamp, string toTimestamp, RespireValue[] options, CancellationToken cancellationToken = default);

    /// <summary>Reads samples in descending timestamp order.</summary>
    [RespireCommand("TS.REVRANGE", Mutation = RespireCacheMutation.ReadOnly)]
    ValueTask<RespireResult> ReverseRangeAsync(RespireKey key, string fromTimestamp, string toTimestamp, RespireValue[] options, CancellationToken cancellationToken = default);

    /// <summary>Reads matching series in ascending timestamp order.</summary>
    [RespireCommand("TS.MRANGE", Mutation = RespireCacheMutation.ReadOnly)]
    ValueTask<RespireResult> MultiRangeAsync(string fromTimestamp, string toTimestamp, RespireValue[] optionsAndFilters, CancellationToken cancellationToken = default);

    /// <summary>Reads matching series in descending timestamp order.</summary>
    [RespireCommand("TS.MREVRANGE", Mutation = RespireCacheMutation.ReadOnly)]
    ValueTask<RespireResult> MultiReverseRangeAsync(string fromTimestamp, string toTimestamp, RespireValue[] optionsAndFilters, CancellationToken cancellationToken = default);

    /// <summary>Deletes samples from one series over an inclusive timestamp range.</summary>
    [RespireCommand("TS.DEL", Mutation = RespireCacheMutation.Unknown)]
    ValueTask<RespireResult> DeleteRangeAsync(RespireKey key, string fromTimestamp, string toTimestamp, CancellationToken cancellationToken = default);

    /// <summary>Creates a compaction rule.</summary>
    [RespireCommand("TS.CREATERULE", Mutation = RespireCacheMutation.MultiKey)]
    ValueTask<RespireResult> CreateRuleAsync(RespireKey source, RespireKey destination, RespireValue[] aggregation, CancellationToken cancellationToken = default);

    /// <summary>Deletes a compaction rule.</summary>
    [RespireCommand("TS.DELETERULE", Mutation = RespireCacheMutation.MultiKey)]
    ValueTask<RespireResult> DeleteRuleAsync(RespireKey source, RespireKey destination, CancellationToken cancellationToken = default);

    /// <summary>Returns time-series metadata.</summary>
    [RespireCommand("TS.INFO", Mutation = RespireCacheMutation.ReadOnly)]
    ValueTask<RespireResult> InfoAsync(RespireKey key, string[] options, CancellationToken cancellationToken = default);

    /// <summary>Finds series matching label filters.</summary>
    [RespireCommand("TS.QUERYINDEX", Mutation = RespireCacheMutation.ReadOnly)]
    ValueTask<RespireResult> QueryIndexAsync(string[] filters, CancellationToken cancellationToken = default);
}
