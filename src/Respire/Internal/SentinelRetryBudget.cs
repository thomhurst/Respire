namespace Respire.Internal;

/// <summary>Counts replacement attempts after the initial Sentinel operation.</summary>
/// <remarks>
/// Keep this mutable budget in its owner's field or local; never pass it by value or
/// copy it while an episode is active. Copies have independent attempt counts.
/// The owning loop is the only writer. A monitor's transport-close callback can read
/// <see cref="Attempts"/> to capture the publication signal for a new retry episode.
/// Reset only after successful discovery/subscription or an observed monitor rearm signal;
/// a pending notification is another use of the current budget, not a reset.
/// </remarks>
internal struct SentinelRetryBudget(RespireReconnectPolicy? policy)
{
    private int _attempts;

    internal int Attempts => Volatile.Read(ref _attempts);
    internal bool IsExhausted => policy?.IsExhausted(Attempts) == true;

    internal void Reset() => Volatile.Write(ref _attempts, 0);

    internal void MarkSubscriptionExhausted()
    {
        if (policy?.MaxAttempts is { } maximum) Volatile.Write(ref _attempts, maximum);
    }

    internal bool TryStartRetry()
    {
        if (IsExhausted) return false;
        // Unlimited episodes must not wrap to a negative attempt or backoff exponent.
        if (Attempts < int.MaxValue) Volatile.Write(ref _attempts, Attempts + 1);
        return true;
    }

    internal TimeSpan GetDelay()
    {
        var attempt = Attempts;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attempt);
        return policy?.GetDelay(attempt)
            ?? TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(attempt - 1, 5)));
    }
}
