namespace Respire.Extensions.Probabilistic;

/// <summary>Options accepted by BF.RESERVE.</summary>
public sealed record RespireBloomReserveOptions
{
    /// <summary>Filter expansion factor.</summary>
    public int? Expansion { get; init; }
    /// <summary>Prevent automatic filter expansion.</summary>
    public bool NonScaling { get; init; }

    internal RespireValue[] ToArguments()
    {
        var args = new List<RespireValue>();
        if (Expansion is { } expansion) { if (expansion <= 0) throw new ArgumentOutOfRangeException(nameof(Expansion)); args.Add("EXPANSION"); args.Add(expansion); }
        if (NonScaling) args.Add("NONSCALING");
        return [.. args];
    }
}

/// <summary>Options accepted by BF.INSERT.</summary>
public sealed record RespireBloomInsertOptions
{
    /// <summary>Capacity when creating a filter.</summary>
    public long? Capacity { get; init; }
    /// <summary>Error rate when creating a filter.</summary>
    public double? ErrorRate { get; init; }
    /// <summary>Expansion factor when creating a filter.</summary>
    public int? Expansion { get; init; }
    /// <summary>Prevent automatic filter expansion.</summary>
    public bool NonScaling { get; init; }
    /// <summary>Do not create the filter when it does not exist.</summary>
    public bool NoCreate { get; init; }

    internal RespireValue[] ToArguments(IReadOnlyList<RespireValue> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0) throw new ArgumentException("At least one item is required.", nameof(items));
        foreach (var item in items) RespireValue.ThrowIfNull(item, nameof(items));
        if (Capacity.HasValue != ErrorRate.HasValue) throw new ArgumentException("CAPACITY and ERROR must be specified together.");
        var args = new List<RespireValue>();
        if (Capacity is { } capacity) { if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(Capacity)); if (ErrorRate is <= 0 or >= 1) throw new ArgumentOutOfRangeException(nameof(ErrorRate)); args.Add("CAPACITY"); args.Add(capacity); args.Add("ERROR"); args.Add(ErrorRate!.Value); }
        if (Expansion is { } expansion) { if (expansion <= 0) throw new ArgumentOutOfRangeException(nameof(Expansion)); args.Add("EXPANSION"); args.Add(expansion); }
        if (NonScaling) args.Add("NONSCALING");
        if (NoCreate) args.Add("NOCREATE");
        args.Add("ITEMS"); args.AddRange(items);
        return [.. args];
    }
}

/// <summary>Options accepted by CF.RESERVE.</summary>
public sealed record RespireCuckooReserveOptions
{
    /// <summary>Entries per bucket.</summary>
    public int? BucketSize { get; init; }
    /// <summary>Maximum relocation attempts per insertion.</summary>
    public int? MaximumIterations { get; init; }
    /// <summary>Filter expansion factor.</summary>
    public int? Expansion { get; init; }

    internal RespireValue[] ToArguments()
    {
        var args = new List<RespireValue>();
        Add("BUCKETSIZE", BucketSize);
        Add("MAXITERATIONS", MaximumIterations);
        Add("EXPANSION", Expansion);
        return [.. args];

        void Add(string name, int? value)
        {
            if (value is not { } selected) return;
            if (selected <= 0) throw new ArgumentOutOfRangeException(name);
            args.Add(name); args.Add(selected);
        }
    }
}

/// <summary>Options accepted by CF.INSERT and CF.INSERTNX.</summary>
public sealed record RespireCuckooInsertOptions
{
    /// <summary>Capacity when creating a filter.</summary>
    public long? Capacity { get; init; }
    /// <summary>Do not create the filter when it does not exist.</summary>
    public bool NoCreate { get; init; }

    internal RespireValue[] ToArguments(IReadOnlyList<RespireValue> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0) throw new ArgumentException("At least one item is required.", nameof(items));
        foreach (var item in items) RespireValue.ThrowIfNull(item, nameof(items));
        var args = new List<RespireValue>();
        if (Capacity is { } capacity) { if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(Capacity)); args.Add("CAPACITY"); args.Add(capacity); }
        if (NoCreate) args.Add("NOCREATE");
        args.Add("ITEMS"); args.AddRange(items);
        return [.. args];
    }
}

/// <summary>Options accepted by CMS.MERGE.</summary>
public sealed record RespireCountMinMergeOptions
{
    /// <summary>Weights for each source sketch.</summary>
    public IReadOnlyList<double>? Weights { get; init; }
    /// <summary>Aggregation used to combine source counters.</summary>
    public RespireCountMinMergeAggregation? Aggregation { get; init; }

    internal RespireValue[] ToArguments(int sourceCount)
    {
        var args = new List<RespireValue>();
        if (Weights is not null)
        {
            if (Weights.Count != sourceCount) throw new ArgumentException("Weights must match source count.", nameof(Weights));
            foreach (var weight in Weights) if (!double.IsFinite(weight) || weight < 0) throw new ArgumentOutOfRangeException(nameof(Weights));
            args.Add("WEIGHTS"); args.AddRange(Weights.Select(static weight => (RespireValue)weight));
        }
        if (Aggregation is { } aggregation) { args.Add("AGGREGATE"); args.Add(aggregation switch { RespireCountMinMergeAggregation.Sum => "SUM", RespireCountMinMergeAggregation.Minimum => "MIN", RespireCountMinMergeAggregation.Maximum => "MAX", _ => throw new ArgumentOutOfRangeException(nameof(Aggregation)) }); }
        return [.. args];
    }
}

/// <summary>CMS.MERGE aggregation strategy.</summary>
public enum RespireCountMinMergeAggregation
{
    /// <summary>Sum counters from source sketches.</summary> Sum,
    /// <summary>Take the minimum counter from source sketches.</summary> Minimum,
    /// <summary>Take the maximum counter from source sketches.</summary> Maximum,
}

/// <summary>Options accepted by TOPK.RESERVE.</summary>
public sealed record RespireTopKReserveOptions
{
    /// <summary>Counter width. Redis default is 8.</summary>
    public int? Width { get; init; }
    /// <summary>Counter depth. Redis default is 7.</summary>
    public int? Depth { get; init; }
    /// <summary>Counter decay factor. Redis default is 0.9.</summary>
    public double? Decay { get; init; }
    internal RespireValue[] ToArguments()
    {
        if (Width is null && Depth is null && Decay is null) return [];
        var width = Width ?? 8;
        var depth = Depth ?? 7;
        var decay = Decay ?? 0.9;
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(Width));
        if (depth <= 0) throw new ArgumentOutOfRangeException(nameof(Depth));
        if (decay <= 0 || decay >= 1) throw new ArgumentOutOfRangeException(nameof(Decay));
        return [width, depth, decay];
    }
}

/// <summary>Options accepted by TDIGEST.CREATE and TDIGEST.MERGE.</summary>
public sealed record RespireTDigestOptions
{
    /// <summary>Compression and accuracy tradeoff.</summary>
    public int? Compression { get; init; }
    /// <summary>Replace existing destination during merge.</summary>
    public bool Override { get; init; }
    internal RespireValue[] ToArguments(bool allowOverride)
    {
        var args = new List<RespireValue>();
        if (Compression is { } compression) { if (compression <= 0) throw new ArgumentOutOfRangeException(nameof(Compression)); args.Add("COMPRESSION"); args.Add(compression); }
        if (Override) { if (!allowOverride) throw new ArgumentException("OVERRIDE is only valid for TDIGEST.MERGE.", nameof(Override)); args.Add("OVERRIDE"); }
        return [.. args];
    }
}

/// <summary>A chunk produced by BF.SCANDUMP or CF.SCANDUMP.</summary>
public sealed record RespireProbabilisticDumpChunk(long Iterator, byte[]? Data);
