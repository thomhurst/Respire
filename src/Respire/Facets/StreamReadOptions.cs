using Respire.Internal;

namespace Respire;

/// <summary>Reply limits and blocking behavior for XREAD and XREADGROUP.</summary>
/// <remarks>This is a shared public API. Additional read options should use init-only properties,
/// preserving existing call shapes.</remarks>
public readonly record struct StreamReadOptions
{
    /// <summary>Maximum entries per stream. Must be positive when specified.</summary>
    public int? Count { get; init; }

    /// <summary>Maximum entries across all streams. Requires Redis 8.10 and must be positive and at least Count.</summary>
    public long? MaxCount { get; init; }

    /// <summary>Maximum reply bytes across all streams. Requires Redis 8.10 and must be positive.</summary>
    /// <remarks>The server always permits the first entry, even when it exceeds this budget.</remarks>
    public long? MaxSize { get; init; }

    /// <summary>Null is nonblocking; InfiniteTimeSpan waits until cancelled. Queued reads require null.</summary>
    public TimeSpan? WaitFor { get; init; }

    /// <summary>Do not add newly delivered entries to the pending list. Only valid for consumer group reads.</summary>
    /// <remarks>Redis ignores NOACK for pending entries, including those reclaimed with CLAIM.</remarks>
    public bool NoAck { get; init; }

    /// <summary>Claim pending entries idle for at least this duration before reading new entries. Requires Redis 8.4.</summary>
    /// <remarks>Only valid for consumer group reads. Redis ignores CLAIM for cursors other than &gt;.
    /// Fractional milliseconds round up so the encoded threshold is never shorter than requested.</remarks>
    public TimeSpan? ClaimMinIdle { get; init; }

    internal void Validate(bool queued = false, bool group = false)
    {
        if (!group && (NoAck || ClaimMinIdle.HasValue))
            throw new ArgumentException("NOACK and CLAIM require a consumer group read.");
        if (ClaimMinIdle is { } idle && idle < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ClaimMinIdle), "Minimum idle time must be non-negative.");
        if (Count is { } count) ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count, nameof(Count));
        if (MaxCount is { } maxCount) ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCount, nameof(MaxCount));
        if (MaxSize is { } maxSize) ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSize, nameof(MaxSize));
        if (MaxCount is { } total && Count is { } perStream && total < perStream)
            throw new ArgumentException("MAXCOUNT must be greater than or equal to COUNT.", nameof(MaxCount));
        if (WaitFor is not { } wait) return;
        if (queued) throw new ArgumentException("Queued stream reads cannot block.", nameof(WaitFor));
        MultiKeyPop.ValidateWait(wait);
    }

    internal long? GetClaimMinIdleMilliseconds()
    {
        if (ClaimMinIdle is not { } idle) return null;
        return idle.Ticks / TimeSpan.TicksPerMillisecond
            + (idle.Ticks % TimeSpan.TicksPerMillisecond == 0 ? 0 : 1);
    }

    internal long? GetBlockMilliseconds()
    {
        if (WaitFor is not { } wait) return null;
        return wait == Timeout.InfiniteTimeSpan ? 0
            : Math.Max(1, wait.Ticks / TimeSpan.TicksPerMillisecond + (wait.Ticks % TimeSpan.TicksPerMillisecond == 0 ? 0 : 1));
    }
}
