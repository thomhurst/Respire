namespace Respire.Extensions.Probabilistic;

internal static class ProbabilisticValueValidation
{
    internal const int MaximumExpansion = 32768;

    internal static void ThrowIfOutOfRange(int value, string parameterName, int maximum)
    {
        if (value <= 0 || value > maximum)
            throw new ArgumentOutOfRangeException(parameterName);
    }

    internal static void ThrowIfNull(RespireValue value, string parameterName)
    {
        if (value.IsNull)
            throw new ArgumentNullException(parameterName, "A null value cannot be sent as a Redis argument; use an empty string or delete the key.");
    }
}

/// <summary>Options accepted by BF.RESERVE.</summary>
public sealed record RespireBloomReserveOptions
{
    /// <summary>Filter expansion factor.</summary>
    public int? Expansion { get; init; }
    /// <summary>Prevent automatic filter expansion.</summary>
    public bool NonScaling { get; init; }

    internal RespireValue[] ToArguments()
    {
        if (NonScaling && Expansion is not null)
            throw new ArgumentException("Expansion and NonScaling are mutually exclusive.");
        var args = new List<RespireValue>();
        if (Expansion is { } expansion)
        {
            ProbabilisticValueValidation.ThrowIfOutOfRange(expansion, nameof(Expansion), ProbabilisticValueValidation.MaximumExpansion);
            args.Add("EXPANSION");
            args.Add(expansion);
        }
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
        foreach (var item in items) ProbabilisticValueValidation.ThrowIfNull(item, nameof(items));
        if (NonScaling && Expansion is not null)
            throw new ArgumentException("Expansion and NonScaling are mutually exclusive.");
        var args = new List<RespireValue>();
        if (Capacity is { } capacity) { if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(Capacity)); args.Add("CAPACITY"); args.Add(capacity); }
        if (ErrorRate is { } errorRate) { if (!double.IsFinite(errorRate) || errorRate <= 0 || errorRate >= 1) throw new ArgumentOutOfRangeException(nameof(ErrorRate)); args.Add("ERROR"); args.Add(errorRate); }
        if (Expansion is { } expansion)
        {
            ProbabilisticValueValidation.ThrowIfOutOfRange(expansion, nameof(Expansion), ProbabilisticValueValidation.MaximumExpansion);
            args.Add("EXPANSION");
            args.Add(expansion);
        }
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
        Add("BUCKETSIZE", BucketSize, 255);
        Add("MAXITERATIONS", MaximumIterations, 65535);
        Add("EXPANSION", Expansion, ProbabilisticValueValidation.MaximumExpansion);
        return [.. args];

        void Add(string name, int? value, int maximum)
        {
            if (value is not { } selected) return;
            ProbabilisticValueValidation.ThrowIfOutOfRange(selected, name, maximum);
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
        foreach (var item in items) ProbabilisticValueValidation.ThrowIfNull(item, nameof(items));
        var args = new List<RespireValue>();
        if (Capacity is { } capacity) { if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(Capacity)); args.Add("CAPACITY"); args.Add(capacity); }
        if (NoCreate) args.Add("NOCREATE");
        args.Add("ITEMS"); args.AddRange(items);
        return [.. args];
    }
}

/// <summary>Per-item outcome of CF.INSERT and CF.INSERTNX.</summary>
public enum RespireCuckooInsertResult
{
    /// <summary>The filter was full, so the item was not inserted.</summary>
    FilterFull = -1,
    /// <summary>The item may already exist, so CF.INSERTNX did not insert it.</summary>
    AlreadyExists = 0,
    /// <summary>The item was inserted.</summary>
    Inserted = 1,
}

/// <summary>Options accepted by CMS.MERGE.</summary>
public sealed record RespireCountMinMergeOptions
{
    /// <summary>Weights for each source sketch.</summary>
    public IReadOnlyList<long>? Weights { get; init; }

    internal RespireValue[] ToArguments(int sourceCount)
    {
        var args = new List<RespireValue>();
        if (Weights is not null)
        {
            if (Weights.Count != sourceCount) throw new ArgumentException("Weights must match source count.", nameof(Weights));
            foreach (var weight in Weights) if (weight <= 0) throw new ArgumentOutOfRangeException(nameof(Weights));
            args.Add("WEIGHTS"); args.AddRange(Weights.Select(static weight => (RespireValue)weight));
        }
        return [.. args];
    }
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
        if (!double.IsFinite(decay) || decay <= 0 || decay >= 1) throw new ArgumentOutOfRangeException(nameof(Decay));
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
        if (Compression is { } compression) { if (compression is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(Compression)); args.Add("COMPRESSION"); args.Add(compression); }
        if (Override) { if (!allowOverride) throw new ArgumentException("OVERRIDE is only valid for TDIGEST.MERGE.", nameof(Override)); args.Add("OVERRIDE"); }
        return [.. args];
    }
}

/// <summary>A chunk produced by BF.SCANDUMP or CF.SCANDUMP.</summary>
public sealed record RespireProbabilisticDumpChunk(long Iterator, byte[]? Data);
