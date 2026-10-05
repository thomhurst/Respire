namespace Respire.TimeSeries;

/// <summary>Controls TS.READ paging and optional blocking. Requires Redis 8.10.</summary>
public sealed record RespireTimeSeriesReadOptions
{
    /// <summary>Wait timeout in milliseconds, or null for a nonblocking read. Zero waits indefinitely.</summary>
    public long? BlockMilliseconds { get; init; }
    /// <summary>Samples required to unblock early. Requires BLOCK when different from one.</summary>
    public int MinimumCount { get; init; } = 1;
    /// <summary>Maximum samples returned, or null for an unbounded reply. Must be at least MinimumCount when blocking.</summary>
    public int? MaximumCount { get; init; }

    internal RespireValue[] ToArguments()
    {
        if (MinimumCount <= 0) throw new ArgumentOutOfRangeException(nameof(MinimumCount));
        if (MaximumCount is <= 0) throw new ArgumentOutOfRangeException(nameof(MaximumCount));
        var arguments = new List<RespireValue>(5);
        if (BlockMilliseconds is { } milliseconds)
        {
            if (milliseconds < 0) throw new ArgumentOutOfRangeException(nameof(BlockMilliseconds));
            if (MaximumCount is { } maximum && maximum < MinimumCount)
                throw new ArgumentException("MaximumCount must be at least MinimumCount for a blocking read.");
            arguments.Add("BLOCK");
            arguments.Add(milliseconds);
            arguments.Add(MinimumCount);
        }
        else if (MinimumCount != 1)
            throw new ArgumentException("MinimumCount requires BlockMilliseconds.");
        if (MaximumCount is { } count)
        {
            arguments.Add("MAX_COUNT");
            arguments.Add(count);
        }
        return RespireTimeSeriesOptions.ToArray(arguments);
    }
}

/// <summary>Controls continuous sample reads and optional completion after repeated empty server replies.</summary>
public sealed record RespireTimeSeriesFollowOptions
{
    /// <summary>Maximum samples per page. Must be positive.</summary>
    public int BatchSize { get; init; } = 256;

    /// <summary>Ends enumeration after this many consecutive empty replies. Null retries until cancellation.</summary>
    /// <remarks>Must be positive when set. Any sample resets the count. This does not impose an idle timeout on BLOCK 0.</remarks>
    public int? MaximumConsecutiveEmptyReads { get; init; }
}

/// <summary>A timestamp and flattened values from explicit series keys, in request order.</summary>
/// <remarks>
/// Each key contributes one value without aggregation, or one per requested aggregator.
/// NaN may represent missing data or a stored/aggregated NaN; the server does not distinguish them.
/// </remarks>
public sealed record RespireTimeSeriesRow(long Timestamp, IReadOnlyList<double> Values);

/// <summary>Options for TS.NRANGE and TS.NREVRANGE. Requires Redis 8.10.</summary>
public sealed record RespireTimeSeriesKeyRangeOptions
{
    /// <summary>Only return these exact timestamps.</summary>
    public IReadOnlyList<long> FilterByTimestamps { get; init; } = [];
    /// <summary>Inclusive sample value bounds, applied before timestamp alignment or aggregation.</summary>
    public (double Minimum, double Maximum)? FilterByValue { get; init; }
    /// <summary>Maximum timestamp rows, applied after combining series.</summary>
    public int? Count { get; init; }
    /// <summary>Include the latest partially compacted bucket.</summary>
    public bool Latest { get; init; }
    /// <summary>One nonempty aggregator list per key, in key order. Null disables aggregation.</summary>
    public IReadOnlyList<IReadOnlyList<RespireTimeSeriesAggregation>>? Aggregators { get; init; }
    /// <summary>Shared positive bucket duration. Required with Aggregators.</summary>
    public long? BucketMilliseconds { get; init; }
    /// <summary>Bucket alignment. Requires aggregation.</summary>
    public RespireTimeSeriesTimestamp? Align { get; init; }
    /// <summary>Position of each bucket's timestamp. Requires aggregation.</summary>
    public RespireTimeSeriesBucketTimestamp? BucketTimestamp { get; init; }
    /// <summary>Include empty aggregation buckets. Requires aggregation.</summary>
    public bool Empty { get; init; }

    internal (RespireValue[] Arguments, int Width) ToArguments(int keyCount)
    {
        string[]? tokens = null;
        var width = keyCount;
        if (Aggregators is { } perKey)
        {
            if (perKey.Count != keyCount) throw new ArgumentException("Provide one aggregator list per key.", nameof(Aggregators));
            if (BucketMilliseconds is not > 0) throw new ArgumentOutOfRangeException(nameof(BucketMilliseconds));
            tokens = new string[keyCount];
            width = 0;
            for (var index = 0; index < keyCount; index++)
            {
                var aggregators = perKey[index];
                if (aggregators is null || aggregators.Count == 0)
                    throw new ArgumentException("Each key requires at least one aggregator.", nameof(Aggregators));
                var names = new string[aggregators.Count];
                for (var item = 0; item < names.Length; item++)
                    names[item] = RespireTimeSeriesRangeOptions.ToAggregationName(aggregators[item]);
                tokens[index] = string.Join(',', names);
                width = checked(width + names.Length);
            }
        }
        else if (BucketMilliseconds is not null)
            throw new ArgumentException("BucketMilliseconds requires Aggregators.");

        // Reuse range validation and option ordering; only the aggregation tokens differ.
        var common = new RespireTimeSeriesRangeOptions
        {
            FilterByTimestamps = FilterByTimestamps, FilterByValue = FilterByValue, Count = Count, Latest = Latest,
            Align = Align, BucketTimestamp = BucketTimestamp, Empty = Empty,
            Aggregation = tokens is null ? null : (RespireTimeSeriesAggregation.Avg, BucketMilliseconds!.Value),
        };
        return (common.ToArguments(multiSeries: false, perKeyAggregators: tokens), width);
    }
}
