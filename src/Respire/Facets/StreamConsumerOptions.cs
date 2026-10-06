namespace Respire;

/// <summary>Filters for an extended XPENDING query.</summary>
public readonly record struct StreamPendingOptions
{
    internal const int DefaultCount = 10;

    /// <summary>Inclusive lower ID bound; null means the beginning. Prefix an ID with ( for an exclusive bound.</summary>
    public RespireStreamId? Start { get; init; }
    /// <summary>Inclusive upper ID bound; null means the end. Prefix an ID with ( for an exclusive bound.</summary>
    public RespireStreamId? End { get; init; }
    /// <summary>Maximum entries to return; null uses 10. Must be positive.</summary>
    public int? Count { get; init; }
    /// <summary>Restrict results to this consumer; null includes all consumers.</summary>
    public string? Consumer { get; init; }
    /// <summary>Minimum idle duration. Requires Redis 6.2 and must be nonnegative.</summary>
    /// <remarks>Fractional milliseconds round up so the filter never includes entries idle for less than requested.</remarks>
    public TimeSpan? MinIdle { get; init; }

    internal long? GetMinIdleMilliseconds()
    {
        if (MinIdle is not { } idle) return null;
        if (idle < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(MinIdle), "Minimum idle time must be non-negative.");
        return StreamCommands.CeilingMilliseconds(idle);
    }
}

/// <summary>Optional pending-entry changes applied by XCLAIM.</summary>
public readonly record struct StreamClaimOptions
{
    /// <summary>Set the entry's idle duration instead of resetting it to zero. Cannot be combined with DeliveryTime.</summary>
    public TimeSpan? IdleTime { get; init; }
    /// <summary>Set the last delivery timestamp. Cannot be combined with IdleTime.</summary>
    public DateTimeOffset? DeliveryTime { get; init; }
    /// <summary>Set the delivery counter. Must be nonnegative.</summary>
    public long? RetryCount { get; init; }
    /// <summary>Create a pending entry when the ID exists in the stream but is not pending.</summary>
    public bool Force { get; init; }
    /// <summary>Advance the group's last delivered ID if this ID is greater.</summary>
    public RespireStreamId? LastId { get; init; }
}

/// <summary>An XAUTOCLAIM JUSTID page. Continue from NextStart until it is 0-0.</summary>
/// <param name="NextStart">Cursor for the next scan.</param>
/// <param name="Ids">IDs transferred to the consumer without incrementing their delivery counters.</param>
/// <param name="DeletedIds">IDs removed from the pending list because their entries no longer exist. Empty before Redis 7.</param>
public readonly record struct RespireStreamClaimIdsResult(
    RespireStreamId NextStart, RespireStreamId[] Ids, RespireStreamId[] DeletedIds);
