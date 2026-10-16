namespace Respire;

/// <summary>Limits and idle retention for each dedicated connection pool.</summary>
/// <remarks>Pools create connections on demand; the minimum is a retention floor, not a prewarming target.
/// Limits apply independently to each endpoint and routing generation, across ordinary and streaming leases.</remarks>
public sealed record RespireDedicatedPoolOptions
{
    /// <summary>Minimum live connections protected from timed idle retirement. Defaults to zero.</summary>
    public int MinimumSize { get; init; }

    /// <summary>Maximum total connecting, rented, idle and closing connections; null (the default) is unlimited.</summary>
    /// <remarks>Acquisitions wait with caller cancellation when all capacity is occupied. A closing connection
    /// releases capacity only after cleanup finishes. The pool has no independent acquisition timeout or FIFO
    /// guarantee; pass a cancellation deadline when leases can block indefinitely. Avoid operations that hold
    /// all capacity while awaiting another lease from the same pool. Replacement routing generations have independent limits.</remarks>
    public int? MaximumSize { get; init; }

    /// <summary>Maximum retained idle connections, shared by both lease kinds. Defaults to four; zero disables reuse.</summary>
    public int MaximumIdle { get; init; } = 4;

    /// <summary>Idle age before retirement down to <see cref="MinimumSize"/>; null disables timed retirement.</summary>
    /// <remarks>Must be between one millisecond and one day. A periodic sweep runs at this interval,
    /// so retirement normally occurs between one and two intervals after return.</remarks>
    public TimeSpan? IdleTimeout { get; init; }

    internal TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    internal void Validate()
    {
        if (MinimumSize < 0 || MaximumIdle < MinimumSize || MaximumSize is <= 0
            || MaximumSize is { } maximum && maximum < MinimumSize
            || IdleTimeout is { } timeout && (timeout < TimeSpan.FromMilliseconds(1) || timeout > TimeSpan.FromDays(1)))
            throw new RespireConfigurationException("DedicatedPool requires 0 <= MinimumSize <= MaximumIdle, a positive or null MaximumSize >= MinimumSize, and an IdleTimeout between one millisecond and one day or null.");
    }
}
