namespace Respire;

/// <summary>Safety and delay settings for immediate-command retry dispatch.</summary>
/// <remarks>
/// Assign to RespireOptions.RetryPolicy to enable buffered immediate-command retries, or to
/// RespireFailoverGroupOptions.RetryPolicy to retry failover-group operations.
/// RunTransactionAsync and RunTransactionWithReadsAsync also use RespireOptions.RetryPolicy
/// to replay complete watched callbacks. It does not recover a lost reply or provide exactly-once
/// execution. Standalone and Sentinel batches retry as complete units: every attempt resends every
/// member, and a resend after any send started requires the policy to permit every member's category.
/// Cluster batches and direct transaction commits are not replayed.
/// </remarks>
public sealed record RespireRetryPolicy
{
    /// <summary>Maximum sends, including the initial send, from one through 100. Defaults to three.</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>Maximum permitted risk after possible execution. Defaults to read-only commands.</summary>
    /// <remarks>
    /// Never disables retries. Known commands that are definitely unwritten can be retried under any
    /// other selection. Unknown commands and scripts require an explicit category from their caller.
    /// Selecting WriteAccumulating or ServerAdmin explicitly accepts repeated effects and lost results.
    /// </remarks>
    public RespireCommandRetryCategory MaxCommandRetryCategory { get; init; } = RespireCommandRetryCategory.ReadOnly;

    /// <summary>Failures eligible for a retry, still subject to execution risk and the logical deadline.</summary>
    public RespireRetryableErrors RetryableErrors { get; init; } = RespireRetryableErrors.ConnectionLoss
        | RespireRetryableErrors.Loading | RespireRetryableErrors.TryAgain
        | RespireRetryableErrors.MasterDown | RespireRetryableErrors.ReadOnly;

    /// <summary>First retry's delay ceiling before jitter. Defaults to 10 milliseconds. Zero disables delay.</summary>
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromMilliseconds(10);

    /// <summary>Maximum delay ceiling, at most one minute and at least InitialBackoff. Defaults to one second.</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Fraction of the delay ceiling randomized downwards, from zero through one. Defaults to one (full jitter).</summary>
    public double Jitter { get; init; } = 1;

    internal void Validate()
    {
        if (MaxAttempts is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(MaxAttempts));
        if (!Enum.IsDefined(MaxCommandRetryCategory))
            throw new ArgumentOutOfRangeException(nameof(MaxCommandRetryCategory));
        const RespireRetryableErrors supported = RespireRetryableErrors.ConnectionLoss | RespireRetryableErrors.Loading
            | RespireRetryableErrors.TryAgain | RespireRetryableErrors.MasterDown | RespireRetryableErrors.ReadOnly;
        if ((RetryableErrors & ~supported) != 0) throw new ArgumentOutOfRangeException(nameof(RetryableErrors));
        if (InitialBackoff < TimeSpan.Zero || InitialBackoff > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(InitialBackoff));
        if (MaxBackoff < InitialBackoff || MaxBackoff > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(MaxBackoff));
        if (!double.IsFinite(Jitter) || Jitter is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(Jitter));
    }
}

/// <summary>Explicitly supported transient failure selections. Other server errors are not retryable.</summary>
[Flags]
public enum RespireRetryableErrors
{
    /// <summary>No failures are eligible.</summary>
    None = 0,
    /// <summary>A transport connection failure, excluding authentication and exhausted connection recovery.</summary>
    ConnectionLoss = 1,
    /// <summary>A LOADING server reply.</summary>
    Loading = 2,
    /// <summary>A TRYAGAIN server reply.</summary>
    TryAgain = 4,
    /// <summary>A MASTERDOWN server reply.</summary>
    MasterDown = 8,
    /// <summary>A READONLY server reply. Dispatch must refresh the primary route before another send.</summary>
    ReadOnly = 16,
}
