using System.Globalization;

namespace Respire.TimeSeries;

/// <summary>Label name and value attached to a time series.</summary>
public readonly record struct RespireTimeSeriesLabel(string Name, string Value);

/// <summary>A sample timestamp: Unix milliseconds or a RedisTimeSeries timestamp marker.</summary>
/// <remarks>
/// Timestamps are validated before any I/O. Writes accept a non-negative millisecond timestamp or
/// <see cref="Now"/>; ranges accept a non-negative millisecond timestamp, <see cref="Minimum"/>, or
/// <see cref="Maximum"/>.
/// </remarks>
public readonly record struct RespireTimeSeriesTimestamp(string Value)
{
    /// <summary>Earliest timestamp marker (<c>-</c>). Valid for ranges only.</summary>
    public static RespireTimeSeriesTimestamp Minimum => new("-");
    /// <summary>Latest timestamp marker (<c>+</c>). Valid for ranges only.</summary>
    public static RespireTimeSeriesTimestamp Maximum => new("+");
    /// <summary>Server clock marker (<c>*</c>). Valid for writes only; range commands reject it.</summary>
    public static RespireTimeSeriesTimestamp Now => new("*");
    /// <summary>Only samples newer than the latest existing sample (<c>$</c>). Valid for TS.READ only.</summary>
    public static RespireTimeSeriesTimestamp New => new("$");
    /// <summary>Creates a millisecond timestamp. Negative values are rejected when the timestamp is used.</summary>
    public static implicit operator RespireTimeSeriesTimestamp(long value) => new(value.ToString(CultureInfo.InvariantCulture));
    /// <summary>Converts to the Redis timestamp token.</summary>
    public override string ToString() => Value;

    internal string RequireWrite(string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Value, parameterName);
        if (Value == "*") return Value;
        if (Value is "-" or "+")
        {
            throw new ArgumentException(
                $"The '{Value}' marker is only valid for ranges. Writes take a millisecond timestamp or RespireTimeSeriesTimestamp.Now.",
                parameterName);
        }
        return RequireMilliseconds(parameterName);
    }

    internal string RequireRange(string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Value, parameterName);
        if (Value is "-" or "+") return Value;
        if (Value == "*")
        {
            throw new ArgumentException(
                "RespireTimeSeriesTimestamp.Now is only valid for writes. Ranges take a millisecond timestamp, Minimum, or Maximum.",
                parameterName);
        }
        return RequireMilliseconds(parameterName);
    }

    internal string RequireRead(string parameterName) => Value == "$" ? Value : RequireRange(parameterName);

    private string RequireMilliseconds(string parameterName)
    {
        // NumberStyles.None rejects signs, so negative timestamps fail here rather than on the server.
        if (!long.TryParse(Value, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            throw new ArgumentOutOfRangeException(
                parameterName, Value, "A time-series timestamp must be a non-negative number of milliseconds.");
        }
        return Value;
    }
}

/// <summary>Duplicate sample handling policy.</summary>
public enum RespireTimeSeriesDuplicatePolicy
{
    /// <summary>Reject duplicate timestamps.</summary>
    Block,
    /// <summary>Keep the first value for duplicate timestamps.</summary>
    First,
    /// <summary>Keep the last value for duplicate timestamps.</summary>
    Last,
    /// <summary>Keep the smaller value for duplicate timestamps.</summary>
    Min,
    /// <summary>Keep the larger value for duplicate timestamps.</summary>
    Max,
    /// <summary>Sum duplicate values.</summary>
    Sum,
}

/// <summary>Sample encoding format.</summary>
public enum RespireTimeSeriesEncoding
{
    /// <summary>Use compressed chunks.</summary>
    Compressed,
    /// <summary>Use uncompressed chunks.</summary>
    Uncompressed,
}

/// <summary>Creates or alters a series using RedisTimeSeries options.</summary>
public sealed record RespireTimeSeriesOptions
{
    /// <summary>Retention period in milliseconds. Zero means no retention limit.</summary>
    public long? RetentionMilliseconds { get; init; }
    /// <summary>Chunk size in bytes.</summary>
    public int? ChunkSizeBytes { get; init; }
    /// <summary>Chunk encoding.</summary>
    public RespireTimeSeriesEncoding? Encoding { get; init; }
    /// <summary>Duplicate timestamp policy.</summary>
    public RespireTimeSeriesDuplicatePolicy? DuplicatePolicy { get; init; }
    /// <summary>Ignore a new sample when both differences from the latest sample are within these limits.</summary>
    public (long MaxTimeDifference, double MaxValueDifference)? Ignore { get; init; }
    /// <summary>Label metadata. TS.ALTER replaces every existing label when any label is supplied.</summary>
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();
    /// <summary>Remove every existing label. Use with <see cref="RespireTimeSeriesClient.AlterAsync"/> only.</summary>
    public bool ClearLabels { get; init; }

    internal RespireValue[] ToArguments(bool allowEncoding)
    {
        if (ClearLabels && allowEncoding)
            throw new ArgumentException("ClearLabels can only be used while altering a series.", nameof(ClearLabels));
        if (ClearLabels && Labels is { Count: > 0 })
            throw new ArgumentException("ClearLabels cannot be combined with label values.", nameof(ClearLabels));
        if (Encoding is not null && !allowEncoding)
            throw new ArgumentException("Encoding can only be set while creating a series.", nameof(Encoding));

        var args = new List<RespireValue>();
        AppendSeriesSettings(args, RetentionMilliseconds, Encoding, ChunkSizeBytes, DuplicatePolicy);
        AppendIgnore(args, Ignore);
        AppendLabels(args, Labels, includeWhenEmpty: ClearLabels);
        return ToArray(args);
    }

    // Shared by TS.CREATE, TS.ALTER, TS.ADD, TS.INCRBY, and TS.DECRBY, which list these options in this order.
    internal static void AppendSeriesSettings(
        List<RespireValue> args,
        long? retentionMilliseconds,
        RespireTimeSeriesEncoding? encoding,
        int? chunkSizeBytes,
        RespireTimeSeriesDuplicatePolicy? duplicatePolicy)
    {
        if (retentionMilliseconds is { } retention)
        {
            if (retention < 0) throw new ArgumentOutOfRangeException(nameof(RetentionMilliseconds));
            args.Add("RETENTION");
            args.Add(retention);
        }
        if (encoding is { } chunkEncoding)
        {
            args.Add("ENCODING");
            args.Add(chunkEncoding switch
            {
                RespireTimeSeriesEncoding.Compressed => "COMPRESSED",
                RespireTimeSeriesEncoding.Uncompressed => "UNCOMPRESSED",
                _ => throw new ArgumentOutOfRangeException(nameof(Encoding)),
            });
        }
        if (chunkSizeBytes is { } chunkSize)
        {
            if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(ChunkSizeBytes));
            args.Add("CHUNK_SIZE");
            args.Add(chunkSize);
        }
        if (duplicatePolicy is { } policy)
        {
            args.Add("DUPLICATE_POLICY");
            args.Add(ToPolicy(policy));
        }
    }

    internal static void AppendIgnore(List<RespireValue> args, (long MaxTimeDifference, double MaxValueDifference)? ignore)
    {
        if (ignore is not { } limits) return;
        if (limits.MaxTimeDifference < 0 || !(limits.MaxValueDifference >= 0))
            throw new ArgumentOutOfRangeException(nameof(Ignore), "IGNORE thresholds must be non-negative.");
        args.Add("IGNORE");
        args.Add(limits.MaxTimeDifference);
        args.Add(limits.MaxValueDifference);
    }

    // LABELS consumes every remaining argument, so callers must append it last.
    internal static void AppendLabels(List<RespireValue> args, IReadOnlyDictionary<string, string> labels,
        bool includeWhenEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(labels, nameof(Labels));
        if (labels.Count == 0 && !includeWhenEmpty) return;
        args.Add("LABELS");
        foreach (var label in labels)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(label.Key, nameof(Labels));
            ArgumentNullException.ThrowIfNull(label.Value, nameof(Labels));
            args.Add(label.Key);
            args.Add(label.Value);
        }
    }

    internal static string ToPolicy(RespireTimeSeriesDuplicatePolicy policy) => policy switch
    {
        RespireTimeSeriesDuplicatePolicy.Block => "BLOCK",
        RespireTimeSeriesDuplicatePolicy.First => "FIRST",
        RespireTimeSeriesDuplicatePolicy.Last => "LAST",
        RespireTimeSeriesDuplicatePolicy.Min => "MIN",
        RespireTimeSeriesDuplicatePolicy.Max => "MAX",
        RespireTimeSeriesDuplicatePolicy.Sum => "SUM",
        _ => throw new ArgumentOutOfRangeException(nameof(policy)),
    };

    internal static RespireTimeSeriesDuplicatePolicy? FromPolicy(string? policy) => policy?.ToUpperInvariant() switch
    {
        "BLOCK" => RespireTimeSeriesDuplicatePolicy.Block,
        "FIRST" => RespireTimeSeriesDuplicatePolicy.First,
        "LAST" => RespireTimeSeriesDuplicatePolicy.Last,
        "MIN" => RespireTimeSeriesDuplicatePolicy.Min,
        "MAX" => RespireTimeSeriesDuplicatePolicy.Max,
        "SUM" => RespireTimeSeriesDuplicatePolicy.Sum,
        _ => null,
    };

    // A collection expression over an empty list still allocates; options that add nothing share the empty array.
    internal static RespireValue[] ToArray(List<RespireValue> args) => args.Count == 0 ? [] : [.. args];
}

/// <summary>Options for one sample write.</summary>
public sealed record RespireTimeSeriesAddOptions
{
    /// <summary>Overrides retention for this write-created series.</summary>
    public long? RetentionMilliseconds { get; init; }
    /// <summary>Encoding used when this write creates a series.</summary>
    public RespireTimeSeriesEncoding? Encoding { get; init; }
    /// <summary>Chunk size used when this write creates a series.</summary>
    public int? ChunkSizeBytes { get; init; }
    /// <summary>Duplicate policy used when this write creates a series.</summary>
    public RespireTimeSeriesDuplicatePolicy? DuplicatePolicy { get; init; }
    /// <summary>Labels used when this write creates a series.</summary>
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();
    /// <summary>Policy for duplicate timestamps in this write, overriding the series policy.</summary>
    public RespireTimeSeriesDuplicatePolicy? OnDuplicate { get; init; }
    /// <summary>Ignore this sample when both differences from the latest sample are within these limits.</summary>
    public (long MaxTimeDifference, double MaxValueDifference)? Ignore { get; init; }

    internal RespireValue[] ToArguments()
    {
        var args = new List<RespireValue>();
        RespireTimeSeriesOptions.AppendSeriesSettings(args, RetentionMilliseconds, Encoding, ChunkSizeBytes, DuplicatePolicy);
        if (OnDuplicate is { } onDuplicate)
        {
            args.Add("ON_DUPLICATE");
            args.Add(RespireTimeSeriesOptions.ToPolicy(onDuplicate));
        }
        RespireTimeSeriesOptions.AppendIgnore(args, Ignore);
        RespireTimeSeriesOptions.AppendLabels(args, Labels);
        return RespireTimeSeriesOptions.ToArray(args);
    }
}

/// <summary>Options for TS.INCRBY and TS.DECRBY.</summary>
public sealed record RespireTimeSeriesIncrementOptions
{
    /// <summary>
    /// Timestamp of the updated sample. Defaults to the server clock. <see cref="RespireTimeSeriesTimestamp.Now"/>
    /// also selects the server clock. An explicit timestamp must not be earlier than the latest sample.
    /// </summary>
    public RespireTimeSeriesTimestamp? Timestamp { get; init; }
    /// <summary>Retention used when this write creates a series.</summary>
    public long? RetentionMilliseconds { get; init; }
    /// <summary>Encoding used when this write creates a series.</summary>
    public RespireTimeSeriesEncoding? Encoding { get; init; }
    /// <summary>Chunk size used when this write creates a series.</summary>
    public int? ChunkSizeBytes { get; init; }
    /// <summary>Duplicate policy used when this write creates a series.</summary>
    public RespireTimeSeriesDuplicatePolicy? DuplicatePolicy { get; init; }
    /// <summary>Ignore this update when both differences from the latest sample are within these limits.</summary>
    public (long MaxTimeDifference, double MaxValueDifference)? Ignore { get; init; }
    /// <summary>Labels used when this write creates a series.</summary>
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();

    internal RespireValue[] ToArguments()
    {
        var args = new List<RespireValue>();
        if (Timestamp is { } timestamp && timestamp != RespireTimeSeriesTimestamp.Now)
        {
            args.Add("TIMESTAMP");
            args.Add(timestamp.RequireWrite(nameof(Timestamp)));
        }
        RespireTimeSeriesOptions.AppendSeriesSettings(args, RetentionMilliseconds, Encoding, ChunkSizeBytes, DuplicatePolicy);
        RespireTimeSeriesOptions.AppendIgnore(args, Ignore);
        RespireTimeSeriesOptions.AppendLabels(args, Labels);
        return RespireTimeSeriesOptions.ToArray(args);
    }
}

/// <summary>One time-series key and sample to write with TS.MADD.</summary>
public readonly record struct RespireTimeSeriesWrite(RespireKey Key, RespireTimeSeriesTimestamp Timestamp, double Value);

/// <summary>A time-series sample.</summary>
public readonly record struct RespireTimeSeriesSample(long Timestamp, double Value);

/// <summary>Inclusive range bounds.</summary>
public readonly record struct RespireTimeSeriesRange(RespireTimeSeriesTimestamp From, RespireTimeSeriesTimestamp To)
{
    internal (string From, string To) ToTokens(string parameterName)
        => (From.RequireRange(parameterName), To.RequireRange(parameterName));
}

/// <summary>TS.MADD rejected one or more samples. Accepted samples were still written.</summary>
public sealed class RespireTimeSeriesMultiAddException : RespireException
{
    internal RespireTimeSeriesMultiAddException(long?[] timestamps, string?[] errors)
        : base(CreateMessage(errors))
    {
        Timestamps = timestamps;
        Errors = errors;
    }

    /// <summary>The assigned timestamp of each sample, in request order, or null where the sample was rejected.</summary>
    public IReadOnlyList<long?> Timestamps { get; }

    /// <summary>The server error of each sample, in request order, or null where the sample was written.</summary>
    public IReadOnlyList<string?> Errors { get; }

    private static string CreateMessage(string?[] errors)
    {
        var failed = 0;
        var first = -1;
        for (var index = 0; index < errors.Length; index++)
        {
            if (errors[index] is null) continue;
            failed++;
            if (first < 0) first = index;
        }
        return $"TS.MADD rejected {failed} of {errors.Length} samples. Sample {first} failed: {errors[first]}";
    }
}

/// <summary>Time bucket aggregation function.</summary>
public enum RespireTimeSeriesAggregation
{
    /// <summary>Average sample value.</summary>
    Avg,
    /// <summary>Sum sample values.</summary>
    Sum,
    /// <summary>Minimum sample value.</summary>
    Min,
    /// <summary>Maximum sample value.</summary>
    Max,
    /// <summary>Difference between maximum and minimum values.</summary>
    Range,
    /// <summary>Sample count.</summary>
    Count,
    /// <summary>First sample value.</summary>
    First,
    /// <summary>Last sample value.</summary>
    Last,
    /// <summary>Population standard deviation.</summary>
    StdP,
    /// <summary>Sample standard deviation.</summary>
    StdS,
    /// <summary>Population variance.</summary>
    VarP,
    /// <summary>Sample variance.</summary>
    VarS,
    /// <summary>Time-weighted average. Requires RedisTimeSeries 1.8 or later.</summary>
    Twa,
    /// <summary>Number of NaN values. Requires Redis 8.10.</summary>
    CountNaN,
    /// <summary>Number of all values, including NaN. Requires Redis 8.10.</summary>
    CountAll,
}

/// <summary>Timestamp assigned to an aggregation bucket.</summary>
public enum RespireTimeSeriesBucketTimestamp
{
    /// <summary>Timestamp bucket at its start.</summary>
    Start,
    /// <summary>Timestamp bucket at its midpoint.</summary>
    Mid,
    /// <summary>Timestamp bucket at its end.</summary>
    End,
}

/// <summary>Options shared by TS.RANGE, TS.REVRANGE, TS.MRANGE, and TS.MREVRANGE.</summary>
/// <remarks>
/// <see cref="WithLabels"/>, <see cref="SelectedLabels"/>, <see cref="Filters"/>, and <see cref="GroupBy"/>
/// apply only to the multi-series range commands; single-series ranges reject them.
/// </remarks>
public sealed record RespireTimeSeriesRangeOptions
{
    /// <summary>Only return these exact timestamps.</summary>
    public IReadOnlyList<long> FilterByTimestamps { get; init; } = [];
    /// <summary>Inclusive minimum and maximum sample values.</summary>
    public (double Minimum, double Maximum)? FilterByValue { get; init; }
    /// <summary>Maximum number of samples, or of buckets when aggregating, per series.</summary>
    public int? Count { get; init; }
    /// <summary>Includes the latest partially compacted bucket of a compaction series.</summary>
    public bool Latest { get; init; }
    /// <summary>Aggregation function and bucket duration in milliseconds.</summary>
    public (RespireTimeSeriesAggregation Aggregation, long BucketMilliseconds)? Aggregation { get; init; }
    /// <summary>
    /// Bucket alignment: <see cref="RespireTimeSeriesTimestamp.Minimum"/> aligns to the range start,
    /// <see cref="RespireTimeSeriesTimestamp.Maximum"/> to the range end, or a millisecond timestamp. Requires <see cref="Aggregation"/>.
    /// </summary>
    public RespireTimeSeriesTimestamp? Align { get; init; }
    /// <summary>Where to timestamp aggregate buckets. Requires <see cref="Aggregation"/>.</summary>
    public RespireTimeSeriesBucketTimestamp? BucketTimestamp { get; init; }
    /// <summary>Return empty aggregation buckets. Requires <see cref="Aggregation"/>.</summary>
    public bool Empty { get; init; }
    /// <summary>Returns every label of each series. Multi-series ranges only.</summary>
    public bool WithLabels { get; init; }
    /// <summary>Returns only these labels of each series. Multi-series ranges only.</summary>
    public IReadOnlyList<string> SelectedLabels { get; init; } = [];
    /// <summary>Label filter expressions that select series. Required by multi-series ranges.</summary>
    public IReadOnlyList<string> Filters { get; init; } = [];
    /// <summary>Group matching series by this label and reduce each group with the reducer, such as <c>max</c>. Multi-series ranges only.</summary>
    public (string Label, string Reducer)? GroupBy { get; init; }

    internal RespireValue[] ToArguments(bool multiSeries, string[]? perKeyAggregators = null)
    {
        ArgumentNullException.ThrowIfNull(FilterByTimestamps, nameof(FilterByTimestamps));
        ArgumentNullException.ThrowIfNull(SelectedLabels, nameof(SelectedLabels));
        ArgumentNullException.ThrowIfNull(Filters, nameof(Filters));
        if (!multiSeries && (WithLabels || SelectedLabels.Count > 0 || Filters.Count > 0 || GroupBy is not null))
        {
            throw new ArgumentException(
                "WITHLABELS, SELECTED_LABELS, FILTER, and GROUPBY are only supported by multi-series range commands.");
        }

        var args = new List<RespireValue>();
        if (Latest) args.Add("LATEST");
        if (FilterByTimestamps.Count > 0)
        {
            args.Add("FILTER_BY_TS");
            foreach (var timestamp in FilterByTimestamps)
            {
                if (timestamp < 0) throw new ArgumentOutOfRangeException(nameof(FilterByTimestamps), "Timestamps must be non-negative.");
                args.Add(timestamp);
            }
        }
        if (FilterByValue is { } valueRange)
        {
            if (!(valueRange.Minimum <= valueRange.Maximum)) throw new ArgumentOutOfRangeException(nameof(FilterByValue));
            args.Add("FILTER_BY_VALUE");
            args.Add(valueRange.Minimum);
            args.Add(valueRange.Maximum);
        }
        if (multiSeries) AppendLabelSelection(args, WithLabels, SelectedLabels);
        if (Count is { } count)
        {
            if (count <= 0) throw new ArgumentOutOfRangeException(nameof(Count));
            args.Add("COUNT");
            args.Add(count);
        }
        if (Aggregation is { } aggregation)
        {
            if (aggregation.BucketMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(Aggregation));
            if (Align is { } align)
            {
                args.Add("ALIGN");
                args.Add(align.RequireRange(nameof(Align)));
            }
            args.Add("AGGREGATION");
            if (perKeyAggregators is null) args.Add(ToAggregationName(aggregation.Aggregation));
            else foreach (var aggregators in perKeyAggregators) args.Add(aggregators);
            args.Add(aggregation.BucketMilliseconds);
            if (BucketTimestamp is { } bucketTimestamp)
            {
                args.Add("BUCKETTIMESTAMP");
                args.Add(bucketTimestamp switch
                {
                    RespireTimeSeriesBucketTimestamp.Start => "-",
                    RespireTimeSeriesBucketTimestamp.Mid => "~",
                    RespireTimeSeriesBucketTimestamp.End => "+",
                    _ => throw new ArgumentOutOfRangeException(nameof(BucketTimestamp)),
                });
            }
            if (Empty) args.Add("EMPTY");
        }
        else if (Align is not null || BucketTimestamp is not null || Empty)
        {
            throw new ArgumentException("ALIGN, BUCKETTIMESTAMP, and EMPTY require AGGREGATION.");
        }
        if (multiSeries)
        {
            AppendFilters(args, Filters, nameof(Filters));
            if (GroupBy is { } group)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(group.Label, nameof(GroupBy));
                ArgumentException.ThrowIfNullOrWhiteSpace(group.Reducer, nameof(GroupBy));
                args.Add("GROUPBY");
                args.Add(group.Label);
                args.Add("REDUCE");
                args.Add(group.Reducer);
            }
        }
        return RespireTimeSeriesOptions.ToArray(args);
    }

    internal static void AppendLabelSelection(List<RespireValue> args, bool withLabels, IReadOnlyList<string>? selectedLabels)
    {
        if (withLabels)
        {
            if (selectedLabels is { Count: > 0 })
                throw new ArgumentException("WITHLABELS and SELECTED_LABELS cannot be combined.", nameof(selectedLabels));
            args.Add("WITHLABELS");
            return;
        }
        if (selectedLabels is not { Count: > 0 }) return;
        args.Add("SELECTED_LABELS");
        foreach (var label in selectedLabels)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(label, nameof(selectedLabels));
            args.Add(label);
        }
    }

    /// <summary>Appends <c>FILTER</c> and the validated label filter expressions.</summary>
    internal static void AppendFilters(List<RespireValue> args, IReadOnlyList<string> filters, string parameterName)
    {
        ValidateFilters(filters, parameterName);
        args.Add("FILTER");
        foreach (var filter in filters) args.Add(filter);
    }

    /// <summary>Requires at least one label filter and rejects blank expressions.</summary>
    internal static void ValidateFilters(IReadOnlyList<string> filters, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(filters, parameterName);
        if (filters.Count == 0) throw new ArgumentException("At least one label filter is required.", parameterName);
        foreach (var filter in filters) ArgumentException.ThrowIfNullOrWhiteSpace(filter, parameterName);
    }

    /// <exception cref="ArgumentOutOfRangeException"><paramref name="aggregation"/> is not a defined value.</exception>
    internal static string ToAggregationName(RespireTimeSeriesAggregation aggregation) => aggregation switch
    {
        RespireTimeSeriesAggregation.Avg => "AVG",
        RespireTimeSeriesAggregation.Sum => "SUM",
        RespireTimeSeriesAggregation.Min => "MIN",
        RespireTimeSeriesAggregation.Max => "MAX",
        RespireTimeSeriesAggregation.Range => "RANGE",
        RespireTimeSeriesAggregation.Count => "COUNT",
        RespireTimeSeriesAggregation.First => "FIRST",
        RespireTimeSeriesAggregation.Last => "LAST",
        RespireTimeSeriesAggregation.StdP => "STD.P",
        RespireTimeSeriesAggregation.StdS => "STD.S",
        RespireTimeSeriesAggregation.VarP => "VAR.P",
        RespireTimeSeriesAggregation.VarS => "VAR.S",
        RespireTimeSeriesAggregation.Twa => "TWA",
        RespireTimeSeriesAggregation.CountNaN => "COUNTNAN",
        RespireTimeSeriesAggregation.CountAll => "COUNTALL",
        _ => throw new ArgumentOutOfRangeException(nameof(aggregation)),
    };
}

/// <summary>Samples returned by a single-series range.</summary>
public sealed record RespireTimeSeriesRangeResult(IReadOnlyList<RespireTimeSeriesSample> Samples);

/// <summary>Series labels and samples returned by multi-series commands.</summary>
/// <param name="KeyValue">The original series key bytes, preserved for binary-safe follow-up commands.</param>
/// <param name="Labels">Labels returned for the series; values are null for selected labels the series lacks.</param>
/// <param name="Samples">Samples in the order the server returned them.</param>
/// <remarks>
/// <see cref="Key"/> is derived from <see cref="KeyValue"/>, so the two always agree.
/// For GROUPBY results the key is the group name, such as <c>label=value</c>, and RESP2 replies list the reducer and
/// sources as the <c>__reducer__</c> and <c>__source__</c> labels.
/// </remarks>
public sealed record RespireTimeSeriesSeries(RespireKey KeyValue, IReadOnlyDictionary<string, string?> Labels, IReadOnlyList<RespireTimeSeriesSample> Samples)
{
    /// <summary>The display form of the server-side key. Invalid UTF-8 bytes are replacement-decoded; use <see cref="KeyValue"/> to reuse the key.</summary>
    public string Key => KeyValue.ToString();
}

/// <summary>A compaction rule reported by TS.INFO.</summary>
/// <param name="DestinationKey">The series that receives the aggregated buckets.</param>
/// <param name="BucketDurationMilliseconds">Bucket duration in milliseconds.</param>
/// <param name="Aggregation">The aggregator name as the server reports it, such as <c>avg</c> or <c>std.p</c>.</param>
/// <param name="AlignTimestamp">The bucket alignment timestamp.</param>
public readonly record struct RespireTimeSeriesRule(RespireKey DestinationKey, long BucketDurationMilliseconds, string Aggregation, long AlignTimestamp);

/// <summary>Series metadata returned by TS.INFO.</summary>
/// <remarks>Fields the server does not report keep their defaults. Unrecognized fields are ignored.</remarks>
public sealed record RespireTimeSeriesInfo
{
    internal static IReadOnlyDictionary<string, string?> EmptyLabels { get; } = new Dictionary<string, string?>();

    /// <summary>Number of samples in the series.</summary>
    public long TotalSamples { get; init; }
    /// <summary>Memory used by the series, in bytes.</summary>
    public long MemoryUsageBytes { get; init; }
    /// <summary>Timestamp of the first sample, or zero for an empty series.</summary>
    public long FirstTimestamp { get; init; }
    /// <summary>Timestamp of the last sample, or zero for an empty series.</summary>
    public long LastTimestamp { get; init; }
    /// <summary>Retention period in milliseconds. Zero means no retention limit.</summary>
    public long RetentionMilliseconds { get; init; }
    /// <summary>Number of memory chunks.</summary>
    public long ChunkCount { get; init; }
    /// <summary>Chunk size in bytes.</summary>
    public long ChunkSizeBytes { get; init; }
    /// <summary>Chunk encoding as the server reports it, such as <c>compressed</c>.</summary>
    public string? ChunkType { get; init; }
    /// <summary>Duplicate policy of the series, or null when the server reports none or an unknown policy.</summary>
    public RespireTimeSeriesDuplicatePolicy? DuplicatePolicy { get; init; }
    /// <summary>The IGNORE maximum time difference, in milliseconds.</summary>
    public long IgnoreMaxTimeDifference { get; init; }
    /// <summary>The IGNORE maximum value difference.</summary>
    public double IgnoreMaxValueDifference { get; init; }
    /// <summary>Labels of the series.</summary>
    public IReadOnlyDictionary<string, string?> Labels { get; init; } = EmptyLabels;
    /// <summary>The source series when this series is a compaction destination; otherwise null.</summary>
    public RespireKey? SourceKey { get; init; }
    /// <summary>Compaction rules whose source is this series.</summary>
    public IReadOnlyList<RespireTimeSeriesRule> Rules { get; init; } = [];
}
