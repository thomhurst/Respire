namespace Respire;

/// <summary>Bounds optimistic transaction retries after Redis reports a WATCH conflict.</summary>
public sealed record RespireTransactionRetryOptions
{
    /// <summary>Maximum number of attempts, including the initial attempt. Must be positive. Defaults to five.</summary>
    public int MaxAttempts { get; init; } = 5;

    /// <summary>Optional delay after a conflict, receiving its one-based attempt number. Null means no delay.</summary>
    /// <remarks>Return a nonnegative delay no greater than 2,147,483,647 milliseconds. Exceptions stop the operation.</remarks>
    public Func<int, TimeSpan>? Backoff { get; init; }

    /// <summary>Creates a capped exponential backoff with a uniformly random delay from zero to each attempt's ceiling.</summary>
    /// <remarks>The positive attempt number starts at one. The ceiling starts at baseDelay and doubles up to maxDelay.</remarks>
    public static Func<int, TimeSpan> ExponentialBackoff(TimeSpan baseDelay, TimeSpan maxDelay)
    {
        if (baseDelay < TimeSpan.Zero || baseDelay.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(baseDelay));
        if (maxDelay < baseDelay || maxDelay.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maxDelay));

        return attempt =>
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attempt);
            // All supported delays fit in 55 tick bits. Capping the exponent avoids infinity
            // and keeps a zero base delay at zero even for int.MaxValue attempts.
            var ceiling = Math.Min(maxDelay.Ticks, baseDelay.Ticks * Math.Pow(2, Math.Min(attempt - 1, 55)));
            return TimeSpan.FromTicks((long)(ceiling * Random.Shared.NextDouble()));
        };
    }

    internal TimeSpan GetDelay(int attempt)
    {
        var delay = Backoff?.Invoke(attempt) ?? TimeSpan.Zero;
        if (delay < TimeSpan.Zero || delay.TotalMilliseconds > int.MaxValue)
            throw new InvalidOperationException("The Backoff callback returned a delay outside zero to 2,147,483,647 milliseconds.");
        return delay;
    }
}

/// <summary>Every allowed transaction attempt was discarded because a watched key changed.</summary>
public sealed class RespireTransactionConflictException : RespireException
{
    internal RespireTransactionConflictException(int attempts)
        : base($"WATCH conflicts exhausted all {attempts} transaction attempts.") => Attempts = attempts;

    /// <summary>Number of transaction attempts discarded by Redis.</summary>
    public int Attempts { get; }
}
