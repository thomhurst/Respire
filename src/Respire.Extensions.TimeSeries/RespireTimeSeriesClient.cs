namespace Respire.Extensions.TimeSeries;

/// <summary>Typed RedisTimeSeries operations over a caller-owned Respire client.</summary>
/// <remarks>
/// Key-prefixed views prefix every series key. Label-filter queries (<see cref="MultiGetAsync"/>,
/// <see cref="MultiRangeAsync"/>, <see cref="MultiReverseRangeAsync"/>, and <see cref="QueryIndexAsync"/>)
/// name no keys and would return series outside the prefix, so prefixed views reject them with
/// <see cref="NotSupportedException"/>; run them through an unprefixed client. In Redis Cluster those
/// queries are sent to one node; whether they cover every shard depends on the server's RedisTimeSeries
/// cluster support.
/// </remarks>
public sealed class RespireTimeSeriesClient
{
    private static readonly string[] LatestOption = ["LATEST"];
    private static readonly string[] DebugOption = ["DEBUG"];

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
        var arguments = options?.ToArguments(allowEncoding: true) ?? [];
        using var result = await _commands.CreateAsync(key, arguments, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Updates retention, chunk size, duplicate policy, IGNORE thresholds, or labels for an existing series.</summary>
    /// <exception cref="ArgumentException"><see cref="RespireTimeSeriesOptions.Encoding"/> is set; TS.ALTER cannot change it.</exception>
    public async ValueTask AlterAsync(RespireKey key, RespireTimeSeriesOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        using var result = await _commands.AlterAsync(key, options.ToArguments(allowEncoding: false), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Adds a sample, creating the series if needed, and returns its timestamp.</summary>
    /// <param name="key">The series key.</param>
    /// <param name="timestamp">A non-negative millisecond timestamp, or <see cref="RespireTimeSeriesTimestamp.Now"/>.</param>
    /// <param name="value">The sample value.</param>
    /// <param name="options">Options for this write and for a series it creates.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async ValueTask<long> AddAsync(RespireKey key, RespireTimeSeriesTimestamp timestamp, double value, RespireTimeSeriesAddOptions? options = null, CancellationToken cancellationToken = default)
    {
        var token = timestamp.RequireWrite(nameof(timestamp));
        var arguments = options?.ToArguments() ?? [];
        using var result = await _commands.AddAsync(key, token, value, arguments, cancellationToken).ConfigureAwait(false);
        return result.AsInteger();
    }

    /// <summary>Adds samples to existing series and returns each assigned timestamp, in request order.</summary>
    /// <remarks>
    /// The whole batch is sent as one TS.MADD command, and this overload applies no batch size limit. Redis runs the
    /// command to completion before it serves other clients, and the command and reply are buffered in full,
    /// so pass a maximum batch size to
    /// <see cref="MultiAddAsync(IReadOnlyList{RespireTimeSeriesWrite}, int, CancellationToken)"/> for very large
    /// batches to keep latency and buffer use bounded.
    /// </remarks>
    /// <exception cref="RespireTimeSeriesMultiAddException">
    /// The server rejected one or more samples. Accepted samples were written; the exception reports the
    /// timestamp or error of every sample.
    /// </exception>
    public ValueTask<long[]> MultiAddAsync(IReadOnlyList<RespireTimeSeriesWrite> samples, CancellationToken cancellationToken = default)
        => MultiAddAsync(samples, int.MaxValue, cancellationToken);

    /// <summary>
    /// Adds samples to existing series in TS.MADD commands of at most <paramref name="maxBatchSize"/> samples,
    /// and returns each assigned timestamp, in request order.
    /// </summary>
    /// <remarks>
    /// Chunks are sent one after another, so other clients can run commands between them, and the batch as a whole
    /// is not atomic. Every timestamp is validated before the first chunk is sent. If a chunk fails to send, or the
    /// operation is cancelled, the chunks before it stay written. Samples rejected by the server do not stop later
    /// chunks: every chunk is sent, and the rejections are reported together at the end.
    /// </remarks>
    /// <param name="samples">The key, timestamp, and value of each sample.</param>
    /// <param name="maxBatchSize">The largest number of samples sent in one TS.MADD command. It must be positive.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <exception cref="RespireTimeSeriesMultiAddException">
    /// The server rejected one or more samples. Accepted samples were written; the exception reports the
    /// timestamp or error of every sample, across all chunks.
    /// </exception>
    public async ValueTask<long[]> MultiAddAsync(IReadOnlyList<RespireTimeSeriesWrite> samples, int maxBatchSize, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0) throw new ArgumentException("At least one sample is required.", nameof(samples));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBatchSize);
        // Validate and normalize every timestamp first, so invalid input never leaves earlier chunks written.
        var sampleCount = samples.Count;
        var stableSamples = new RespireTimeSeriesWrite[sampleCount];
        var validatedTimestamps = new string[sampleCount];
        for (var index = 0; index < sampleCount; index++)
        {
            var sample = samples[index];
            stableSamples[index] = sample;
            validatedTimestamps[index] = sample.Timestamp.RequireWrite(nameof(samples));
        }

        var timestamps = new long[sampleCount];
        string?[]? errors = null;
        for (var start = 0; start < sampleCount;)
        {
            var count = Math.Min(maxBatchSize, sampleCount - start);
            var arguments = new RespireValue[checked(count * 3)];
            for (var index = 0; index < count; index++)
            {
                var sample = stableSamples[start + index];
                arguments[index * 3] = sample.Key;
                arguments[index * 3 + 1] = validatedTimestamps[start + index];
                arguments[index * 3 + 2] = sample.Value;
            }
            using var result = await _commands.MultiAddAsync(arguments, cancellationToken).ConfigureAwait(false);
            if (result.Count != count) throw TimeSeriesReplyParser.UnexpectedReply();

            for (var index = 0; index < count; index++)
            {
                var reply = result[index];
                if (reply.IsError)
                {
                    errors ??= new string?[timestamps.Length];
                    errors[start + index] = reply.ErrorMessage;
                    continue;
                }
                timestamps[start + index] = reply.AsInteger();
            }

            start += count;
        }
        if (errors is null) return timestamps;

        var partial = new long?[timestamps.Length];
        for (var index = 0; index < partial.Length; index++)
            partial[index] = errors[index] is null ? timestamps[index] : null;
        throw new RespireTimeSeriesMultiAddException(partial, errors);
    }

    /// <summary>Increments the latest sample, or creates a series with the increment value, and returns its timestamp.</summary>
    /// <param name="key">The series key.</param>
    /// <param name="increment">The amount added to the latest value.</param>
    /// <param name="options">The sample timestamp, and settings for a series this call creates.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async ValueTask<long> IncrementByAsync(RespireKey key, double increment, RespireTimeSeriesIncrementOptions? options = null, CancellationToken cancellationToken = default)
    {
        var arguments = options?.ToArguments() ?? [];
        using var result = await _commands.IncrementByAsync(key, increment, arguments, cancellationToken).ConfigureAwait(false);
        return result.AsInteger();
    }

    /// <summary>Decrements the latest sample, or creates a series with the decremented value, and returns its timestamp.</summary>
    /// <param name="key">The series key.</param>
    /// <param name="decrement">The amount subtracted from the latest value.</param>
    /// <param name="options">The sample timestamp, and settings for a series this call creates.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async ValueTask<long> DecrementByAsync(RespireKey key, double decrement, RespireTimeSeriesIncrementOptions? options = null, CancellationToken cancellationToken = default)
    {
        var arguments = options?.ToArguments() ?? [];
        using var result = await _commands.DecrementByAsync(key, decrement, arguments, cancellationToken).ConfigureAwait(false);
        return result.AsInteger();
    }

    /// <summary>Gets the latest sample, or null when the series has no samples.</summary>
    public async ValueTask<RespireTimeSeriesSample?> GetAsync(RespireKey key, bool latestPartialBucket = false, CancellationToken cancellationToken = default)
    {
        using var result = await _commands.GetAsync(key, latestPartialBucket ? LatestOption : [], cancellationToken).ConfigureAwait(false);
        return TimeSeriesReplyParser.ParseLatest(result);
    }

    /// <summary>Gets the latest sample of every series matching the label filters.</summary>
    /// <param name="filters">Label filter expressions, such as <c>sensor=temperature</c>.</param>
    /// <param name="withLabels">Returns every label of each series.</param>
    /// <param name="selectedLabels">Returns only these labels; cannot be combined with <paramref name="withLabels"/>.</param>
    /// <param name="latestPartialBucket">Includes the latest partially compacted bucket of compaction series.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <exception cref="NotSupportedException">The client is a key-prefixed view.</exception>
    public async ValueTask<IReadOnlyList<RespireTimeSeriesSeries>> MultiGetAsync(IReadOnlyList<string> filters, bool withLabels = false, IReadOnlyList<string>? selectedLabels = null, bool latestPartialBucket = false, CancellationToken cancellationToken = default)
    {
        RespireTimeSeriesRangeOptions.ValidateFilters(filters, nameof(filters));
        var arguments = new List<RespireValue>(filters.Count + (selectedLabels?.Count ?? 0) + 3);
        if (latestPartialBucket) arguments.Add("LATEST");
        RespireTimeSeriesRangeOptions.AppendLabelSelection(arguments, withLabels, selectedLabels);
        RespireTimeSeriesRangeOptions.AppendFilters(arguments, filters, nameof(filters));
        using var result = await _commands.MultiGetAsync([.. arguments], cancellationToken).ConfigureAwait(false);
        return TimeSeriesReplyParser.ParseSeries(result, latestSample: true);
    }

    /// <summary>Reads one series in ascending timestamp order.</summary>
    public async ValueTask<RespireTimeSeriesRangeResult> RangeAsync(RespireKey key, RespireTimeSeriesRange range, RespireTimeSeriesRangeOptions? options = null, CancellationToken cancellationToken = default)
    {
        var (from, to) = range.ToTokens(nameof(range));
        var arguments = options?.ToArguments(multiSeries: false) ?? [];
        using var result = await _commands.RangeAsync(key, from, to, arguments, cancellationToken).ConfigureAwait(false);
        return TimeSeriesReplyParser.ParseRange(result);
    }

    /// <summary>Reads one series in descending timestamp order.</summary>
    public async ValueTask<RespireTimeSeriesRangeResult> ReverseRangeAsync(RespireKey key, RespireTimeSeriesRange range, RespireTimeSeriesRangeOptions? options = null, CancellationToken cancellationToken = default)
    {
        var (from, to) = range.ToTokens(nameof(range));
        var arguments = options?.ToArguments(multiSeries: false) ?? [];
        using var result = await _commands.ReverseRangeAsync(key, from, to, arguments, cancellationToken).ConfigureAwait(false);
        return TimeSeriesReplyParser.ParseRange(result);
    }

    /// <summary>Reads series matching <see cref="RespireTimeSeriesRangeOptions.Filters"/> in ascending timestamp order.</summary>
    /// <exception cref="NotSupportedException">The client is a key-prefixed view.</exception>
    public async ValueTask<IReadOnlyList<RespireTimeSeriesSeries>> MultiRangeAsync(RespireTimeSeriesRange range, RespireTimeSeriesRangeOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var (from, to) = range.ToTokens(nameof(range));
        using var result = await _commands.MultiRangeAsync(from, to, options.ToArguments(multiSeries: true), cancellationToken).ConfigureAwait(false);
        return TimeSeriesReplyParser.ParseSeries(result, latestSample: false);
    }

    /// <summary>Reads series matching <see cref="RespireTimeSeriesRangeOptions.Filters"/> in descending timestamp order.</summary>
    /// <exception cref="NotSupportedException">The client is a key-prefixed view.</exception>
    public async ValueTask<IReadOnlyList<RespireTimeSeriesSeries>> MultiReverseRangeAsync(RespireTimeSeriesRange range, RespireTimeSeriesRangeOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var (from, to) = range.ToTokens(nameof(range));
        using var result = await _commands.MultiReverseRangeAsync(from, to, options.ToArguments(multiSeries: true), cancellationToken).ConfigureAwait(false);
        return TimeSeriesReplyParser.ParseSeries(result, latestSample: false);
    }

    /// <summary>Deletes samples in an inclusive timestamp range and returns the deleted count.</summary>
    public async ValueTask<long> DeleteRangeAsync(RespireKey key, RespireTimeSeriesRange range, CancellationToken cancellationToken = default)
    {
        var (from, to) = range.ToTokens(nameof(range));
        using var result = await _commands.DeleteRangeAsync(key, from, to, cancellationToken).ConfigureAwait(false);
        return result.AsInteger();
    }

    /// <summary>Creates a compaction rule that aggregates a source series into an existing destination series.</summary>
    /// <param name="source">Series that receives raw samples.</param>
    /// <param name="destination">Existing series that receives aggregated buckets. In Redis Cluster it must share the source's hash slot.</param>
    /// <param name="aggregation">Bucket aggregation function.</param>
    /// <param name="bucketDurationMilliseconds">
    /// Bucket duration in milliseconds. An integer argument, such as <c>5</c>, binds to this overload and means
    /// milliseconds; pass a <see cref="TimeSpan"/> to the other overload to give the duration in other units.
    /// </param>
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

    /// <summary>Creates a compaction rule that aggregates a source series into an existing destination series.</summary>
    /// <param name="source">Series that receives raw samples.</param>
    /// <param name="destination">Existing series that receives aggregated buckets. In Redis Cluster it must share the source's hash slot.</param>
    /// <param name="aggregation">Bucket aggregation function.</param>
    /// <param name="bucketDuration">Bucket duration. It must be positive and a whole number of milliseconds.</param>
    /// <param name="alignTimestamp">Aligns buckets so one starts at this millisecond timestamp; defaults to the epoch.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public ValueTask CreateRuleAsync(RespireKey source, RespireKey destination, RespireTimeSeriesAggregation aggregation, TimeSpan bucketDuration, long? alignTimestamp = null, CancellationToken cancellationToken = default)
    {
        if (bucketDuration <= TimeSpan.Zero || bucketDuration.Ticks % TimeSpan.TicksPerMillisecond != 0)
            throw new ArgumentOutOfRangeException(nameof(bucketDuration), bucketDuration, "The bucket duration must be a positive whole number of milliseconds.");
        return CreateRuleAsync(source, destination, aggregation, bucketDuration.Ticks / TimeSpan.TicksPerMillisecond, alignTimestamp, cancellationToken);
    }

    /// <summary>Deletes the compaction rule from a source series to a destination series.</summary>
    public async ValueTask DeleteRuleAsync(RespireKey source, RespireKey destination, CancellationToken cancellationToken = default)
    {
        using var result = await _commands.DeleteRuleAsync(source, destination, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns series metadata from TS.INFO: sample counts, retention, chunks, labels, and compaction rules.</summary>
    public async ValueTask<RespireTimeSeriesInfo> GetInfoAsync(RespireKey key, CancellationToken cancellationToken = default)
    {
        using var result = await _commands.InfoAsync(key, [], cancellationToken).ConfigureAwait(false);
        return TimeSeriesReplyParser.ParseInfo(result);
    }

    /// <summary>
    /// Reads the raw TS.INFO, or TS.INFO DEBUG, response with <paramref name="read"/> and disposes it afterwards,
    /// so pooled response buffers cannot leak. Use it for fields <see cref="RespireTimeSeriesInfo"/> does not model.
    /// </summary>
    /// <param name="key">Series key.</param>
    /// <param name="read">Projects the response. The result is disposed when it returns, so it must not escape.</param>
    /// <param name="debug">Sends TS.INFO DEBUG, which adds per-chunk details.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async ValueTask<T> GetRawInfoAsync<T>(RespireKey key, Func<RespireResult, T> read, bool debug = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(read);
        using var result = await _commands.InfoAsync(key, debug ? DebugOption : [], cancellationToken).ConfigureAwait(false);
        return read(result);
    }

    /// <summary>Finds binary-safe keys of series matching label filters.</summary>
    /// <exception cref="NotSupportedException">The client is a key-prefixed view.</exception>
    public async ValueTask<RespireKey[]> QueryIndexAsync(IReadOnlyList<string> filters, CancellationToken cancellationToken = default)
    {
        RespireTimeSeriesRangeOptions.ValidateFilters(filters, nameof(filters));
        // TS.QUERYINDEX takes bare filter expressions, without the FILTER token.
        using var result = await _commands.QueryIndexAsync([.. filters], cancellationToken).ConfigureAwait(false);
        var keys = new RespireKey[result.Count];
        for (var index = 0; index < keys.Length; index++) keys[index] = new RespireKey(result[index].AsBytes());
        return keys;
    }
}
