using Cronos;

namespace Respire.Streaming;

/// <summary>Controls how a recurring schedule resumes after missed occurrences.</summary>
public enum RespireJobMissedTickPolicy
{
    /// <summary>Enqueue only the latest due occurrence, discarding older missed ticks.</summary>
    Skip,
    /// <summary>Enqueue every missed occurrence in order, bounded by each scheduling pass.</summary>
    CatchUp,
}

/// <summary>A fixed interval or five-field UTC cron schedule. Specify exactly one cadence.</summary>
public sealed record RespireJobSchedule
{
    /// <summary>Positive interval in whole milliseconds, anchored at upsert time.</summary>
    public TimeSpan? Interval { get; init; }
    /// <summary>Five-field Cronos expression evaluated in UTC, with minute resolution.</summary>
    public string? Cron { get; init; }
    /// <summary>Defaults to coalescing missed ticks into the latest due occurrence.</summary>
    public RespireJobMissedTickPolicy MissedTickPolicy { get; init; }

    internal CronExpression? Validate()
    {
        if ((Interval is null) == (Cron is null))
            throw new ArgumentException("Specify exactly one of Interval or Cron.");
        if (!Enum.IsDefined(MissedTickPolicy))
            throw new ArgumentOutOfRangeException(nameof(MissedTickPolicy));
        if (Interval is { } interval && (interval <= TimeSpan.Zero || interval.Ticks % TimeSpan.TicksPerMillisecond != 0))
            throw new ArgumentOutOfRangeException(nameof(Interval), "Intervals must be positive whole milliseconds.");
        return Cron is { } cron ? CronExpression.Parse(cron, CronFormat.Standard) : null;
    }
}
