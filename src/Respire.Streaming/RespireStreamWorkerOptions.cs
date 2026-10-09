namespace Respire.Streaming;

/// <summary>Immutable settings captured when registering a hosted stream consumer.</summary>
public sealed record RespireStreamWorkerOptions
{
    /// <summary>Stable consumer-name base, suffixed with each reader's index. Null creates unique names.</summary>
    /// <remarks>Reuse the same name and consumer count to replay owned pending entries once at startup.
    /// Active registrations and hosts sharing a group must use different names.</remarks>
    public string? ConsumerName { get; init; }

    /// <summary>Maximum simultaneous handlers and blocking readers. Defaults to one.</summary>
    public int ConsumerCount { get; init; } = 1;

    /// <summary>Maximum entries fetched per reader. Each reader processes its batch sequentially.</summary>
    public int BatchSize { get; init; } = 1;

    /// <summary>Positive blocking wait for new entries. Defaults to five seconds.</summary>
    public TimeSpan ReadWait { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Positive visibility timeout before pending entries can be recovered. Defaults to one minute.</summary>
    public TimeSpan MinimumIdleTime { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Positive interval between bounded recovery scans per reader. Defaults to five seconds.</summary>
    public TimeSpan RecoveryPollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Whether startup creates the stream and group. Existing groups are retained.</summary>
    public bool CreateGroup { get; init; } = true;

    /// <summary>Initial position for a newly created group. Defaults to the beginning.</summary>
    public RespireStreamId GroupStart { get; init; } = RespireStreamId.Beginning;

    internal void Validate()
    {
        if (ConsumerName is not null) ArgumentException.ThrowIfNullOrWhiteSpace(ConsumerName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ConsumerCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(BatchSize);
        if (ReadWait <= TimeSpan.Zero || ReadWait > TimeSpan.FromMilliseconds(int.MaxValue))
            throw new ArgumentOutOfRangeException(nameof(ReadWait), "Read wait must be positive and at most Int32.MaxValue milliseconds.");
        if (MinimumIdleTime <= TimeSpan.Zero || MinimumIdleTime > TimeSpan.FromMilliseconds(int.MaxValue))
            throw new ArgumentOutOfRangeException(nameof(MinimumIdleTime), "Minimum idle time must be positive and at most Int32.MaxValue milliseconds.");
        if (RecoveryPollInterval <= TimeSpan.Zero || RecoveryPollInterval > TimeSpan.FromMilliseconds(int.MaxValue))
            throw new ArgumentOutOfRangeException(nameof(RecoveryPollInterval), "Recovery polling must be positive and at most Int32.MaxValue milliseconds.");
    }
}
