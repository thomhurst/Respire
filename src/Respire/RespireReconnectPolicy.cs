namespace Respire;

/// <summary>Backoff and attempt limits for replacing failed command connections.</summary>
/// <remarks>Attempts are counted per connection slot and reset after a successful replacement.
/// Recovery remains demand-driven. This policy does not replay commands or retry initial connection setup.</remarks>
public sealed record RespireReconnectPolicy
{
    /// <summary>Delay before the first replacement attempt. Defaults to 250 milliseconds.</summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMilliseconds(250);
    /// <summary>Exponential delay multiplier. Must be finite and at least one.</summary>
    public double BackoffMultiplier { get; init; } = 2;
    /// <summary>Maximum actual delay, including jitter. Defaults to five seconds; at most one day.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Symmetric random variation as a fraction of the exponential delay, from zero to one.</summary>
    public double JitterRatio { get; init; } = 0.2;
    /// <summary>Maximum replacement attempts per failed slot; null permits unlimited attempts.</summary>
    /// <remarks>Exhaustion persists until the client is recreated. Successful replacement resets the count.</remarks>
    public int? MaxAttempts { get; init; }

    internal bool IsExhausted(int attempts) => MaxAttempts is { } maximum && attempts >= maximum;

    internal void Validate()
    {
        if (InitialDelay < TimeSpan.Zero || MaxDelay < InitialDelay || MaxDelay > TimeSpan.FromDays(1)
            || !double.IsFinite(BackoffMultiplier) || BackoffMultiplier < 1
            || !double.IsFinite(JitterRatio) || JitterRatio is < 0 or > 1 || MaxAttempts is <= 0)
            throw new RespireConfigurationException("ReconnectPolicy requires 0 <= InitialDelay <= MaxDelay <= one day, a finite BackoffMultiplier >= 1, JitterRatio in [0, 1], and a positive or null MaxAttempts.");
    }

    internal TimeSpan GetDelay(int attempt, double randomUnit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attempt);
        // Clamp before jitter and again afterward; even overflowed exponential growth stays bounded.
        var ticks = InitialDelay.Ticks == 0 ? 0 : Math.Min(MaxDelay.Ticks,
            InitialDelay.Ticks * Math.Pow(BackoffMultiplier, attempt - 1));
        ticks = Math.Clamp(ticks * (1 + JitterRatio * (2 * randomUnit - 1)), 0, MaxDelay.Ticks);
        return TimeSpan.FromTicks((long)ticks);
    }
}
