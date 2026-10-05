namespace Respire;

/// <summary>Bounds optimistic transaction retries after Redis reports a WATCH conflict.</summary>
public sealed record RespireTransactionRetryOptions
{
    /// <summary>Maximum number of attempts, including the initial attempt. Must be positive. Defaults to five.</summary>
    public int MaxAttempts { get; init; } = 5;

    /// <summary>Optional delay after a conflict, receiving its one-based attempt number. Null means no delay.</summary>
    /// <remarks>Return a nonnegative delay no greater than 2,147,483,647 milliseconds. Exceptions stop the operation.</remarks>
    public Func<int, TimeSpan>? Backoff { get; init; }
}

/// <summary>Every allowed transaction attempt was discarded because a watched key changed.</summary>
public sealed class RespireTransactionConflictException : RespireException
{
    internal RespireTransactionConflictException(int attempts)
        : base($"WATCH conflicts exhausted all {attempts} transaction attempts.") => Attempts = attempts;

    /// <summary>Number of transaction attempts discarded by Redis.</summary>
    public int Attempts { get; }
}
