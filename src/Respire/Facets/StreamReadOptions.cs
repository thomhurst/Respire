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

    private readonly TimeSpan? _waitFor;

    /// <summary>Assign to request a blocking read: null waits forever; zero waits at least one millisecond.</summary>
    /// <remarks>Omit this property for a nonblocking read. Timeout.InfiniteTimeSpan remains a compatibility alias for null.
    /// Queued reads must leave this property unset.</remarks>
    public TimeSpan? WaitFor
    {
        get => _waitFor;
        init { _waitFor = value; IsBlocking = true; }
    }

    /// <summary>Whether WaitFor was explicitly assigned, including an explicit null.</summary>
    public bool IsBlocking { get; private init; }

    internal StreamReadOptions WithoutBlocking() => this with { IsBlocking = false };

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
        if (!IsBlocking) return;
        if (queued) throw new ArgumentException("Queued stream reads cannot block.", nameof(WaitFor));
        MultiKeyPop.ValidateWait(WaitFor);
    }

    internal long? GetClaimMinIdleMilliseconds()
    {
        if (ClaimMinIdle is not { } idle) return null;
        return StreamCommands.CeilingMilliseconds(idle);
    }

    internal long? GetBlockMilliseconds()
    {
        if (!IsBlocking) return null;
        return WaitFor is not { } wait || wait == Timeout.InfiniteTimeSpan ? 0
            : Math.Max(1, StreamCommands.CeilingMilliseconds(wait));
    }
}
