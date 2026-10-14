namespace Respire;

/// <summary>A map mutation sent to an external store. A missing entry represents a deletion.</summary>
public readonly record struct RespireMapChange<TKey, TValue>(TKey Key, RespireGet<TValue> Entry);

/// <summary>Read-through and external persistence settings for a near-cache map.</summary>
public sealed record RespireMapOptions<TKey, TValue>
{
    /// <summary>Loads missing entries. Returning Found=false leaves the field absent.</summary>
    public Func<TKey, CancellationToken, ValueTask<RespireGet<TValue>>>? Loader { get; init; }

    /// <summary>
    /// Persists ordered mutations after Redis accepts them. Must be idempotent: a failed batch
    /// can be delivered again. Callbacks must not call back into this map.
    /// </summary>
    public Func<IReadOnlyList<RespireMapChange<TKey, TValue>>, CancellationToken, ValueTask>? Writer { get; init; }

    /// <summary>Queues writer calls for periodic batches instead of awaiting each write-through call.</summary>
    public bool WriteBehind { get; init; }

    /// <summary>Maximum changes per writer call. Defaults to 100.</summary>
    public int BatchSize { get; init; } = 100;

    /// <summary>Maximum queued changes. At capacity, new mutations first flush a batch.</summary>
    public int MaxPendingWrites { get; init; } = 1_000;

    /// <summary>Interval between background flushes (and retries). Defaults to one second.</summary>
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Default field lifetime on writes and loader fills. Requires Redis 8+ when set.</summary>
    public TimeSpan? EntryExpiration { get; init; }

    internal void Validate()
    {
        if (WriteBehind && Writer is null)
            throw new ArgumentException("WriteBehind requires a Writer.");
        ArgumentOutOfRangeException.ThrowIfLessThan(BatchSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxPendingWrites, BatchSize);
        if (FlushInterval < TimeSpan.FromMilliseconds(1) || FlushInterval.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(FlushInterval));
        ValidateExpiration(EntryExpiration);
    }

    internal static void ValidateExpiration(TimeSpan? expiration)
    {
        if (expiration is { } value && value < TimeSpan.FromMilliseconds(1))
            throw new ArgumentOutOfRangeException(nameof(expiration), "Field lifetime must be at least one millisecond.");
    }
}
