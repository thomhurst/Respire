namespace Respire;

/// <summary>Backoff and attempt limits for command, dedicated, and pub/sub recovery, Cluster discovery, and Sentinel fallback.</summary>
/// <remarks>Command attempts are counted per connection slot and reset after a successful replacement.
/// Dedicated acquisitions use a separate budget per rent, after an immediate initial attempt.
/// Command recovery remains demand-driven. Sentinel resolution applies one shared budget to candidates
/// after its first attempt. Pub/sub recovery runs automatically and resets only after all
/// live routes are resubscribed. Cluster discovery shares one fallback budget across nested
/// node, topology, and seed selection after a failure. A new discovery round starts fresh.
/// This policy does not replay commands or retry initial multiplexer setup.</remarks>
public sealed record RespireReconnectPolicy
{
    /// <summary>Delay before the first connection replacement or discovery fallback attempt. Defaults to 250 milliseconds.</summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMilliseconds(250);
    /// <summary>Exponential delay multiplier. Must be finite and at least one.</summary>
    public double BackoffMultiplier { get; init; } = 2;
    /// <summary>Maximum actual delay, including jitter. Defaults to five seconds; at most one day.</summary>
    /// <remarks>Sentinel fallback delays are bounded by caller cancellation, not the subsequent
    /// per-candidate ConnectTimeout or CommandTimeout. Supply a caller deadline to bound total resolution time.</remarks>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Symmetric random variation as a fraction of the exponential delay, from zero to one.</summary>
    public double JitterRatio { get; init; } = 0.2;
    /// <summary>Maximum replacement attempts per command slot, dedicated rent, pub/sub episode, Cluster discovery round, or Sentinel resolution; null applies no policy limit.</summary>
    /// <remarks>Defaults to null. A slot that exhausts this limit remains unavailable until the client
    /// is recreated; there is no automatic cooldown or reset. Successful replacement resets the count
    /// before exhaustion. Each dedicated rent starts a new budget, so exhaustion does not disable the pool.
    /// Cluster discovery counts fallbacks after a failed candidate or a rejected route. Successful
    /// required node connections do not consume fallback attempts. New rounds start fresh.
    /// Pub/sub exhaustion ends live subscriptions and prevents new subscriptions in that group.
    /// Cluster sharded subscriptions use a separate group from regular channel/pattern subscriptions.
    /// Leave null for long-lived clients that must keep trying after an outage.
    /// Sentinel resolution counts fallback candidates after the first; a new explicit resolution starts fresh.
    /// With null, all available Sentinel candidates may incur backoff and their own timeouts. Supply caller
    /// cancellation with a deadline to bound total resolution time; this setting does not provide one.</remarks>
    public int? MaxAttempts { get; init; }

    internal bool IsExhausted(int attempts) => MaxAttempts is { } maximum && attempts >= maximum;

    internal void Validate()
    {
        if (InitialDelay < TimeSpan.Zero || MaxDelay < InitialDelay || MaxDelay > TimeSpan.FromDays(1)
            || !double.IsFinite(BackoffMultiplier) || BackoffMultiplier < 1
            || !double.IsFinite(JitterRatio) || JitterRatio is < 0 or > 1 || MaxAttempts is <= 0)
            throw new RespireConfigurationException("ReconnectPolicy requires 0 <= InitialDelay <= MaxDelay <= one day, a finite BackoffMultiplier >= 1, JitterRatio in [0, 1], and a positive or null MaxAttempts.");
    }

    internal TimeSpan GetDelay(int attempt)
        => GetDelay(attempt, JitterRatio == 0 ? 0.5 : Random.Shared.NextDouble());

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
