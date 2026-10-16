using System.Text;

namespace Respire.Streaming;

/// <summary>Scheduling settings for one enqueue. Durations use the Redis server clock.</summary>
public sealed record RespireJobEnqueueOptions
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Optional application group identity. Null means ungrouped; non-null values must be nonblank valid Unicode.</summary>
    /// <remarks>Eligible groups rotate in Redis when scheduling-enabled workers read. Does not impose concurrency or rate limits.</remarks>
    public string? Group { get; init; }

    /// <summary>Delay before the job becomes eligible. Defaults to zero.</summary>
    public TimeSpan Delay { get; init; }
    /// <summary>Higher values run first within each bounded candidate batch of due jobs, within a group when specified. Defaults to zero.</summary>
    public int Priority { get; init; }
    /// <summary>Lifetime from enqueue, including delay and retries. Null means no expiry.</summary>
    public TimeSpan? TimeToLive { get; init; }
    /// <summary>Deduplication window from enqueue. Defaults to one day.</summary>
    public TimeSpan DeduplicationWindow { get; init; } = TimeSpan.FromDays(1);

    internal void Validate()
    {
        if (Group is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(Group);
            _ = StrictUtf8.GetByteCount(Group);
        }
        JobQueueScheduling.ValidateDuration(Delay, nameof(Delay), allowZero: true);
        if (TimeToLive is { } ttl) JobQueueScheduling.ValidateDuration(ttl, nameof(TimeToLive));
        JobQueueScheduling.ValidateDuration(DeduplicationWindow, nameof(DeduplicationWindow));
    }
}

/// <summary>Queue scheduling, retry and log retention policy, shared by all workers for the queue.</summary>
public sealed record RespireJobQueueOptions
{
    /// <summary>Retained one-minute completion/failure buckets, from 1 to 1440. Zero disables metrics (the default).</summary>
    /// <remarks>All workers must use the same retention. To change it, stop workers and delete the queue's metrics key or let it expire.</remarks>
    public int MetricsRetentionMinutes { get; init; }

    /// <summary>Initial shared rate policy. Null uses the existing Redis policy (unlimited on a new queue).</summary>
    /// <remarks>Non-null policies must match existing configuration. Use SetRateLimitAsync to change it.</remarks>
    public RespireJobRateLimit? RateLimit { get; init; }
    /// <summary>Maximum retained log lines per application job ID, from 1 to 1000. Defaults to 100.</summary>
    public int MaximumLogLines { get; init; } = 100;
    /// <summary>Maximum UTF-8 bytes in one log line, from 1 to 16384. Defaults to 4096. Oversized lines are rejected.</summary>
    public int MaximumLogLineBytes { get; init; } = 4096;
    /// <summary>Log list lifetime from its first append. Defaults to one day; at most Int32.MaxValue milliseconds.</summary>
    /// <remarks>Later writes never extend this lifetime. An explicit job expiry shortens it. After expiry, a live job may create a new list.</remarks>
    public TimeSpan LogRetention { get; init; } = TimeSpan.FromDays(1);

    /// <summary>Maximum due candidates considered per group visit or ungrouped promotion. Between 1 and 256.</summary>
    public int SchedulingBatchSize { get; init; } = 64;
    /// <summary>Minimum interval between scheduling passes and maximum idle/pause poll interval. Defaults to one second.</summary>
    public TimeSpan SchedulingPollInterval { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>Initial delay after Nack or a handler exception. Null retains visibility-timeout recovery.</summary>
    public TimeSpan? RetryBackoff { get; init; }
    /// <summary>Upper bound for exponential retry delay. Defaults to one minute.</summary>
    public TimeSpan MaximumRetryBackoff { get; init; } = TimeSpan.FromMinutes(1);

    internal void Validate()
    {
        if (MetricsRetentionMinutes is < 0 or > 1440) throw new ArgumentOutOfRangeException(nameof(MetricsRetentionMinutes));
        RateLimit?.Validate();
        if (MaximumLogLines is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(MaximumLogLines));
        if (MaximumLogLineBytes is < 1 or > 16384) throw new ArgumentOutOfRangeException(nameof(MaximumLogLineBytes));
        JobQueueScheduling.ValidateDuration(LogRetention, nameof(LogRetention));
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
