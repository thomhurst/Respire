namespace Respire.Streaming;

/// <summary>Scheduling settings for one enqueue. Durations use the Redis server clock.</summary>
public sealed record RespireJobEnqueueOptions
{
    /// <summary>Delay before the job becomes eligible. Defaults to zero.</summary>
    public TimeSpan Delay { get; init; }
    /// <summary>Higher values run first within each bounded batch of due jobs. Defaults to zero.</summary>
    public int Priority { get; init; }
    /// <summary>Lifetime from enqueue, including delay and retries. Null means no expiry.</summary>
    public TimeSpan? TimeToLive { get; init; }
    /// <summary>Deduplication window from enqueue. Defaults to one day.</summary>
    public TimeSpan DeduplicationWindow { get; init; } = TimeSpan.FromDays(1);

    internal void Validate()
    {
        JobQueueScheduling.ValidateDuration(Delay, nameof(Delay), allowZero: true);
        if (TimeToLive is { } ttl) JobQueueScheduling.ValidateDuration(ttl, nameof(TimeToLive));
        JobQueueScheduling.ValidateDuration(DeduplicationWindow, nameof(DeduplicationWindow));
    }
}

/// <summary>Queue scheduling and retry policy, shared by all workers for the queue.</summary>
public sealed record RespireJobQueueOptions
{
    /// <summary>Maximum due jobs ordered by priority in one promotion. Between 1 and 256.</summary>
    public int SchedulingBatchSize { get; init; } = 64;
    /// <summary>Minimum interval between each reader's scheduling passes and maximum blocking read. Defaults to one second.</summary>
    public TimeSpan SchedulingPollInterval { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>Initial delay after Nack or a handler exception. Null retains visibility-timeout recovery.</summary>
    public TimeSpan? RetryBackoff { get; init; }
    /// <summary>Upper bound for exponential retry delay. Defaults to one minute.</summary>
    public TimeSpan MaximumRetryBackoff { get; init; } = TimeSpan.FromMinutes(1);

    internal void Validate()
    {
        if (SchedulingBatchSize is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(SchedulingBatchSize));
        JobQueueScheduling.ValidateDuration(SchedulingPollInterval, nameof(SchedulingPollInterval));
        JobQueueScheduling.ValidateDuration(MaximumRetryBackoff, nameof(MaximumRetryBackoff));
        if (RetryBackoff is { } delay)
        {
            JobQueueScheduling.ValidateDuration(delay, nameof(RetryBackoff), allowZero: true);
            if (delay > MaximumRetryBackoff) throw new ArgumentOutOfRangeException(nameof(RetryBackoff));
        }
    }

    internal TimeSpan? GetRetryDelay(long attempt) => RetryBackoff is { } delay
        ? TimeSpan.FromMilliseconds(Math.Min(MaximumRetryBackoff.TotalMilliseconds,
            delay.TotalMilliseconds * Math.Pow(2, Math.Min(63, attempt - 1))))
        : null;
}

internal interface IJobRetry
{
    TimeSpan? RetryDelay { get; }
}
