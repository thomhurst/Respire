using Respire.Internal;

namespace Respire;

/// <summary>Reply limits and blocking behavior for XREAD and XREADGROUP.</summary>
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

    internal long? Validate(bool queued = false)
    {
        if (Count is { } count) ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count, nameof(Count));
        if (MaxCount is { } maxCount) ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCount, nameof(MaxCount));
        if (MaxSize is { } maxSize) ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSize, nameof(MaxSize));
        if (MaxCount is { } total && Count is { } perStream && total < perStream)
            throw new ArgumentException("MAXCOUNT must be greater than or equal to COUNT.", nameof(MaxCount));
        if (WaitFor is not { } wait) return null;
        if (queued) throw new ArgumentException("Queued stream reads cannot block.", nameof(WaitFor));
        MultiKeyPop.ValidateWait(wait);
        return wait == Timeout.InfiniteTimeSpan ? 0
            : Math.Max(1, wait.Ticks / TimeSpan.TicksPerMillisecond + (wait.Ticks % TimeSpan.TicksPerMillisecond == 0 ? 0 : 1));
    }
}
