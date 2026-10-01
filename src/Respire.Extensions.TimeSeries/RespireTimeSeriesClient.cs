namespace Respire.Extensions.TimeSeries;

/// <summary>Typed RedisTimeSeries operations over a caller-owned Respire client.</summary>
/// <remarks>
/// Key-prefixed views prefix every series key. Label-filter queries (<see cref="MultiGetAsync"/>,
/// <see cref="MultiRangeAsync"/>, <see cref="MultiReverseRangeAsync"/>, and <see cref="QueryIndexAsync"/>)
/// name no keys and would return series outside the prefix, so prefixed views reject them with
/// <see cref="NotSupportedException"/>. In Redis Cluster those queries are sent to one node; whether they
/// cover every shard depends on the server's RedisTimeSeries cluster support.
/// </remarks>
public sealed class RespireTimeSeriesClient
{
    private readonly IRespireTimeSeriesCommands _commands;

    /// <summary>Creates TimeSeries operations over an existing client.</summary>
    public RespireTimeSeriesClient(IRespireClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _commands = new IRespireTimeSeriesCommandsImplementation(client);
    }

    /// <summary>Creates an empty time series.</summary>
    public async ValueTask CreateAsync(RespireKey key, RespireTimeSeriesOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var result = await _commands.CreateAsync(key, (options ?? new()).ToArguments(allowEncoding: true), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Updates retention, chunk size, duplicate policy, IGNORE thresholds, or labels for an existing series.</summary>
    /// <exception cref="ArgumentException"><see cref="RespireTimeSeriesOptions.Encoding"/> is set; TS.ALTER cannot change it.</exception>
    public async ValueTask AlterAsync(RespireKey key, RespireTimeSeriesOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        using var result = await _commands.AlterAsync(key, options.ToArguments(allowEncoding: false), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Adds a sample, creating the series if needed, and returns its timestamp.</summary>
    public async ValueTask<long> AddAsync(RespireKey key, RespireTimeSeriesTimestamp timestamp, double value, RespireTimeSeriesAddOptions? options = null, CancellationToken cancellationToken = default)
    {
        var token = RequireTimestamp(timestamp, nameof(timestamp));
        using var result = await _commands.AddAsync(key, token, value, (options ?? new()).ToArguments(), cancellationToken).ConfigureAwait(false);
        return result.AsInteger();
    }

    /// <summary>Adds samples to existing series and returns each assigned timestamp.</summary>
    public async ValueTask<long[]> MultiAddAsync(IReadOnlyList<RespireTimeSeriesWrite> samples, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0) throw new ArgumentException("At least one sample is required.", nameof(samples));
        var arguments = new RespireValue[checked(samples.Count * 3)];
        for (var index = 0; index < samples.Count; index++)
        {
            var sample = samples[index];
            arguments[index * 3] = sample.Key;
            arguments[index * 3 + 1] = RequireTimestamp(sample.Timestamp, nameof(samples));
            arguments[index * 3 + 2] = sample.Value;
        }
        using var result = await _commands.MultiAddAsync(arguments, cancellationToken).ConfigureAwait(false);
        var timestamps = new long[result.Count];
        for (var index = 0; index < timestamps.Length; index++) timestamps[index] = result[index].AsInteger();
        return timestamps;
    }

    /// <summary>Increments the latest sample, or creates a series with the increment value, and returns its timestamp.</summary>
    public async ValueTask<long> IncrementByAsync(RespireKey key, double increment, CancellationToken cancellationToken = default)
    {
        using var result = await _commands.IncrementByAsync(key, increment, [], cancellationToken).ConfigureAwait(false);
        return result.AsInteger();
    }

    /// <summary>Decrements the latest sample, or creates a series with the decremented value, and returns its timestamp.</summary>
    public async ValueTask<long> DecrementByAsync(RespireKey key, double decrement, CancellationToken cancellationToken = default)
    {
        using var result = await _commands.DecrementByAsync(key, decrement, [], cancellationToken).ConfigureAwait(false);
        return result.AsInteger();
    }

    /// <summary>Gets the latest sample, or null when the series has no samples.</summary>
    public async ValueTask<RespireTimeSeriesSample?> GetAsync(RespireKey key, bool latestPartialBucket = false, CancellationToken cancellationToken = default)
    {
        using var result = await _commands.GetAsync(key, latestPartialBucket ? ["LATEST"] : [], cancellationToken).ConfigureAwait(false);
        return result.IsNull || result.Count == 0 ? null : RespireTimeSeriesSeries.ParseSample(result);
    }

    /// <summary>Gets the latest sample of every series matching the label filters.</summary>
    /// <param name="filters">Label filter expressions, such as <c>sensor=temperature</c>.</param>
    /// <param name="withLabels">Returns every label of each series.</param>
    /// <param name="selectedLabels">Returns only these labels; cannot be combined with <paramref name="withLabels"/>.</param>
    /// <param name="latestPartialBucket">Includes the latest partially compacted bucket of compaction series.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async ValueTask<IReadOnlyList<RespireTimeSeriesSeries>> MultiGetAsync(IReadOnlyList<string> filters, bool withLabels = false, IReadOnlyList<string>? selectedLabels = null, bool latestPartialBucket = false, CancellationToken cancellationToken = default)
    {
        var arguments = new List<RespireValue>();
        if (latestPartialBucket) arguments.Add("LATEST");
        RespireTimeSeriesRangeOptions.AppendLabelSelection(arguments, withLabels, selectedLabels);
        RespireTimeSeriesRangeOptions.AppendFilters(arguments, filters, nameof(filters));
        using var result = await _commands.MultiGetAsync([.. arguments], cancellationToken).ConfigureAwait(false);
        return RespireTimeSeriesSeries.ParseMany(result);
    }

    /// <summary>Reads one series in ascending timestamp order.</summary>
    public async ValueTask<RespireTimeSeriesRangeResult> RangeAsync(RespireKey key, RespireTimeSeriesRange range, RespireTimeSeriesRangeOptions? options = null, CancellationToken cancellationToken = default)
    {
        var arguments = (options ?? new()).ToArguments(multiSeries: false);
        using var result = await _commands.RangeAsync(key, RequireTimestamp(range.From, nameof(range)), RequireTimestamp(range.To, nameof(range)), arguments, cancellationToken).ConfigureAwait(false);
        return RespireTimeSeriesRangeResult.Parse(result);
    }

    /// <summary>Reads one series in descending timestamp order.</summary>
    public async ValueTask<RespireTimeSeriesRangeResult> ReverseRangeAsync(RespireKey key, RespireTimeSeriesRange range, RespireTimeSeriesRangeOptions? options = null, CancellationToken cancellationToken = default)
    {
        var arguments = (options ?? new()).ToArguments(multiSeries: false);
        using var result = await _commands.ReverseRangeAsync(key, RequireTimestamp(range.From, nameof(range)), RequireTimestamp(range.To, nameof(range)), arguments, cancellationToken).ConfigureAwait(false);
        return RespireTimeSeriesRangeResult.Parse(result);
    }

    /// <summary>Reads series matching <see cref="RespireTimeSeriesRangeOptions.Filters"/> in ascending timestamp order.</summary>
    public async ValueTask<IReadOnlyList<RespireTimeSeriesSeries>> MultiRangeAsync(RespireTimeSeriesRange range, RespireTimeSeriesRangeOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var arguments = options.ToArguments(multiSeries: true);
        using var result = await _commands.MultiRangeAsync(RequireTimestamp(range.From, nameof(range)), RequireTimestamp(range.To, nameof(range)), arguments, cancellationToken).ConfigureAwait(false);
        return RespireTimeSeriesSeries.ParseMany(result);
    }

    /// <summary>Reads series matching <see cref="RespireTimeSeriesRangeOptions.Filters"/> in descending timestamp order.</summary>
    public async ValueTask<IReadOnlyList<RespireTimeSeriesSeries>> MultiReverseRangeAsync(RespireTimeSeriesRange range, RespireTimeSeriesRangeOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var arguments = options.ToArguments(multiSeries: true);
        using var result = await _commands.MultiReverseRangeAsync(RequireTimestamp(range.From, nameof(range)), RequireTimestamp(range.To, nameof(range)), arguments, cancellationToken).ConfigureAwait(false);
        return RespireTimeSeriesSeries.ParseMany(result);
    }

    /// <summary>Deletes samples in an inclusive timestamp range and returns the deleted count.</summary>
    public async ValueTask<long> DeleteRangeAsync(RespireKey key, RespireTimeSeriesRange range, CancellationToken cancellationToken = default)
    {
        using var result = await _commands.DeleteRangeAsync(key, RequireTimestamp(range.From, nameof(range)), RequireTimestamp(range.To, nameof(range)), cancellationToken).ConfigureAwait(false);
        return result.AsInteger();
    }

    /// <summary>Creates a compaction rule that aggregates a source series into an existing destination series.</summary>
    /// <param name="source">Series that receives raw samples.</param>
    /// <param name="destination">Existing series that receives aggregated buckets. In Redis Cluster it must share the source's hash slot.</param>
    /// <param name="aggregation">Bucket aggregation function.</param>
    /// <param name="bucketDurationMilliseconds">Bucket duration in milliseconds.</param>
    /// <param name="alignTimestamp">Aligns buckets so one starts at this millisecond timestamp; defaults to the epoch.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async ValueTask CreateRuleAsync(RespireKey source, RespireKey destination, RespireTimeSeriesAggregation aggregation, long bucketDurationMilliseconds, long? alignTimestamp = null, CancellationToken cancellationToken = default)
    {
        if (bucketDurationMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(bucketDurationMilliseconds));
        if (alignTimestamp < 0) throw new ArgumentOutOfRangeException(nameof(alignTimestamp));
        var aggregationName = RespireTimeSeriesRangeOptions.ToAggregationName(aggregation);
        RespireValue[] arguments = alignTimestamp is { } align
            ? ["AGGREGATION", aggregationName, bucketDurationMilliseconds, align]
            : ["AGGREGATION", aggregationName, bucketDurationMilliseconds];
        using var result = await _commands.CreateRuleAsync(source, destination, arguments, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes the compaction rule from a source series to a destination series.</summary>
    public async ValueTask DeleteRuleAsync(RespireKey source, RespireKey destination, CancellationToken cancellationToken = default)
    {
        using var result = await _commands.DeleteRuleAsync(source, destination, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns the raw TS.INFO, or TS.INFO DEBUG, response.</summary>
    /// <remarks>The caller owns the returned result and must dispose it.</remarks>
    public ValueTask<RespireResult> GetInfoAsync(RespireKey key, bool debug = false, CancellationToken cancellationToken = default)
        => _commands.InfoAsync(key, debug ? ["DEBUG"] : [], cancellationToken);

    /// <summary>Finds binary-safe keys of series matching label filters.</summary>
    public async ValueTask<RespireKey[]> QueryIndexAsync(IReadOnlyList<string> filters, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filters);
        if (filters.Count == 0) throw new ArgumentException("At least one label filter is required.", nameof(filters));
        var arguments = new string[filters.Count];
        for (var index = 0; index < filters.Count; index++)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filters[index], nameof(filters));
            arguments[index] = filters[index];
        }
        using var result = await _commands.QueryIndexAsync(arguments, cancellationToken).ConfigureAwait(false);
        var keys = new RespireKey[result.Count];
        for (var index = 0; index < keys.Length; index++) keys[index] = new RespireKey(result[index].AsBytes());
        return keys;
    }

    private static string RequireTimestamp(RespireTimeSeriesTimestamp timestamp, string parameterName)
        => RespireTimeSeriesRangeOptions.RequireTimestamp(timestamp, parameterName);
}
