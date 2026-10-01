using System.Globalization;
using Respire.Protocol;

namespace Respire.Extensions.TimeSeries;

/// <summary>Label name and value attached to a time series.</summary>
public readonly record struct RespireTimeSeriesLabel(string Name, string Value);

/// <summary>A sample timestamp: Unix milliseconds or a RedisTimeSeries timestamp marker.</summary>
public readonly record struct RespireTimeSeriesTimestamp(string Value)
{
    /// <summary>Earliest timestamp marker.</summary>
    public static RespireTimeSeriesTimestamp Minimum => new("-");
    /// <summary>Latest timestamp marker.</summary>
    public static RespireTimeSeriesTimestamp Maximum => new("+");
    /// <summary>Server clock marker for writes (<c>*</c>); range commands do not accept it.</summary>
    public static RespireTimeSeriesTimestamp Now => new("*");
    /// <summary>Creates a millisecond timestamp.</summary>
    public static implicit operator RespireTimeSeriesTimestamp(long value) => new(value.ToString(CultureInfo.InvariantCulture));
    /// <summary>Converts to the Redis timestamp token.</summary>
    public override string ToString() => Value;
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

        var args = new List<RespireValue>();
        AppendRetention(args, RetentionMilliseconds);
        if (Encoding is { } encoding)
        {
            if (!allowEncoding) throw new ArgumentException("Encoding can only be set while creating a series.", nameof(Encoding));
            AppendEncoding(args, encoding);
        }
        AppendChunkSize(args, ChunkSizeBytes);
        if (DuplicatePolicy is { } duplicatePolicy)
        {
            args.Add("DUPLICATE_POLICY");
            args.Add(ToPolicy(duplicatePolicy));
        }
        AppendIgnore(args, Ignore);
        AppendLabels(args, Labels, includeWhenEmpty: ClearLabels);
        return [.. args];
    }

    internal static void AppendRetention(List<RespireValue> args, long? retentionMilliseconds)
    {
        if (retentionMilliseconds is not { } retention) return;
        if (retention < 0) throw new ArgumentOutOfRangeException(nameof(RetentionMilliseconds));
        args.Add("RETENTION");
        args.Add(retention);
    }

    internal static void AppendChunkSize(List<RespireValue> args, int? chunkSizeBytes)
    {
        if (chunkSizeBytes is not { } chunkSize) return;
        if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(ChunkSizeBytes));
        args.Add("CHUNK_SIZE");
        args.Add(chunkSize);
    }

    internal static void AppendEncoding(List<RespireValue> args, RespireTimeSeriesEncoding encoding)
    {
        args.Add("ENCODING");
        args.Add(encoding switch
        {
            RespireTimeSeriesEncoding.Compressed => "COMPRESSED",
            RespireTimeSeriesEncoding.Uncompressed => "UNCOMPRESSED",
            _ => throw new ArgumentOutOfRangeException(nameof(Encoding)),
        });
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
        RespireTimeSeriesOptions.AppendRetention(args, RetentionMilliseconds);
        if (Encoding is { } encoding) RespireTimeSeriesOptions.AppendEncoding(args, encoding);
        RespireTimeSeriesOptions.AppendChunkSize(args, ChunkSizeBytes);
        if (DuplicatePolicy is { } duplicatePolicy)
        {
            args.Add("DUPLICATE_POLICY");
            args.Add(RespireTimeSeriesOptions.ToPolicy(duplicatePolicy));
        }
        if (OnDuplicate is { } onDuplicate)
        {
            args.Add("ON_DUPLICATE");
            args.Add(RespireTimeSeriesOptions.ToPolicy(onDuplicate));
        }
        RespireTimeSeriesOptions.AppendIgnore(args, Ignore);
        RespireTimeSeriesOptions.AppendLabels(args, Labels);
        return [.. args];
    }
}

/// <summary>One time-series key and sample to write with TS.MADD.</summary>
public readonly record struct RespireTimeSeriesWrite(RespireKey Key, RespireTimeSeriesTimestamp Timestamp, double Value);

/// <summary>A time-series sample.</summary>
public readonly record struct RespireTimeSeriesSample(long Timestamp, double Value);

/// <summary>Inclusive range bounds.</summary>
public readonly record struct RespireTimeSeriesRange(RespireTimeSeriesTimestamp From, RespireTimeSeriesTimestamp To);

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

    internal RespireValue[] ToArguments(bool multiSeries)
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
            foreach (var timestamp in FilterByTimestamps) args.Add(timestamp);
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
                args.Add(RequireTimestamp(align, nameof(Align)));
            }
            args.Add("AGGREGATION");
            args.Add(ToAggregationName(aggregation.Aggregation));
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
        return [.. args];
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

    internal static void AppendFilters(List<RespireValue> args, IReadOnlyList<string> filters, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(filters, parameterName);
        if (filters.Count == 0) throw new ArgumentException("At least one label filter is required.", parameterName);
        args.Add("FILTER");
        foreach (var filter in filters)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filter, parameterName);
            args.Add(filter);
        }
    }

    internal static string RequireTimestamp(RespireTimeSeriesTimestamp timestamp, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timestamp.Value, parameterName);
        return timestamp.Value;
    }

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
        _ => throw new ArgumentOutOfRangeException(nameof(aggregation)),
    };
}

/// <summary>Samples returned by a single-series range.</summary>
public sealed record RespireTimeSeriesRangeResult(IReadOnlyList<RespireTimeSeriesSample> Samples)
{
    internal static RespireTimeSeriesRangeResult Parse(RespireResult result)
        => new(RespireTimeSeriesSeries.ParseSamples(result));
}

/// <summary>Series labels and samples returned by multi-series commands.</summary>
/// <remarks>
/// <see cref="Key"/> is the display form of the server-side key; <see cref="KeyValue"/> preserves the original bytes.
/// For GROUPBY results the key is the group name, such as <c>label=value</c>, and RESP2 replies list the reducer and
/// sources as the <c>__reducer__</c> and <c>__source__</c> labels.
/// </remarks>
public sealed record RespireTimeSeriesSeries(string Key, IReadOnlyDictionary<string, string?> Labels, IReadOnlyList<RespireTimeSeriesSample> Samples)
{
    /// <summary>The original series key bytes, preserved for binary-safe follow-up commands.</summary>
    public RespireKey KeyValue { get; init; } = new(Key);

    internal static IReadOnlyList<RespireTimeSeriesSeries> ParseMany(RespireResult result)
    {
        if (result.Type == RespDataType.Map)
        {
            // RESP3: key => [labels, ...metadata maps..., samples]. MGET has no metadata and one sample.
            var mapped = new List<RespireTimeSeriesSeries>(result.Count / 2);
            for (var index = 0; index + 1 < result.Count; index += 2)
            {
                var data = result[index + 1];
                if (data.Count < 2) throw UnexpectedReply();
                mapped.Add(new(result[index].AsString(), ParseLabels(data[0]), ParseSamples(data[data.Count - 1]))
                {
                    KeyValue = new RespireKey(result[index].AsBytes()),
                });
            }
            return mapped;
        }

        // RESP2: [key, labels, samples] per series.
        var series = new List<RespireTimeSeriesSeries>(result.Count);
        foreach (var item in result)
        {
            if (item.Count != 3) throw UnexpectedReply();
            series.Add(new(item[0].AsString(), ParseLabels(item[1]), ParseSamples(item[2]))
            {
                KeyValue = new RespireKey(item[0].AsBytes()),
            });
        }
        return series;
    }

    private static Dictionary<string, string?> ParseLabels(RespireResult labels)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (labels.Type == RespDataType.Map)
        {
            for (var index = 0; index + 1 < labels.Count; index += 2)
                result[labels[index].AsString()] = ReadLabelValue(labels[index + 1]);
            return result;
        }
        foreach (var pair in labels)
        {
            if (pair.Count != 2) throw UnexpectedReply();
            result[pair[0].AsString()] = ReadLabelValue(pair[1]);
        }
        return result;
    }

    private static string? ReadLabelValue(RespireResult value) => value.IsNull ? null : value.AsString();

    // A single sample is [timestamp, value]; a sample list is [[timestamp, value], ...].
    internal static IReadOnlyList<RespireTimeSeriesSample> ParseSamples(RespireResult samples)
    {
        if (samples.Count == 2 && samples[0].Type == RespDataType.Integer)
            return [ParseSample(samples)];
        var result = new List<RespireTimeSeriesSample>(samples.Count);
        foreach (var sample in samples) result.Add(ParseSample(sample));
        return result;
    }

    internal static RespireTimeSeriesSample ParseSample(RespireResult sample)
    {
        if (sample.Count != 2) throw UnexpectedReply();
        return new(sample[0].AsInteger(), sample[1].AsDouble());
    }

    private static InvalidOperationException UnexpectedReply()
        => new("Unexpected RedisTimeSeries reply shape.");
}
