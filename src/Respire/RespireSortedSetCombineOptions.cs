namespace Respire;

/// <summary>How to combine scores in ZUNION, ZINTER, ZUNIONSTORE, and ZINTERSTORE.</summary>
public enum RespireSortedSetAggregate
{
    /// <summary>Sum the weighted scores (the default).</summary>
    Sum = 0,
    /// <summary>Use the minimum weighted score.</summary>
    Min = 1,
    /// <summary>Use the maximum weighted score.</summary>
    Max = 2,
    /// <summary>Sum membership weights, ignoring scores. Requires Redis 8.8 or later.</summary>
    Count = 3,
}

/// <summary>Weights and score aggregation for sorted-set unions and intersections.</summary>
public sealed class RespireSortedSetCombineOptions
{
    /// <summary>One weight per input key, in key order. Empty means a weight of one for every key.</summary>
    /// <remarks>Weights are copied into command arguments when the command is called or queued.</remarks>
    public ReadOnlyMemory<double> Weights { get; init; }

    /// <summary>How to combine weighted scores. COUNT requires Redis 8.8 or later.</summary>
    public RespireSortedSetAggregate Aggregate { get; init; }
}
