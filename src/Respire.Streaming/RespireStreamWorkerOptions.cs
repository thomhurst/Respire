namespace Respire.Streaming;

/// <summary>Immutable settings captured when registering a hosted stream consumer.</summary>
public sealed record RespireStreamWorkerOptions
{
    /// <summary>Maximum simultaneous handlers and blocking readers. Defaults to one.</summary>
    public int ConsumerCount { get; init; } = 1;

    /// <summary>Maximum entries fetched per reader. Each reader processes its batch sequentially.</summary>
    public int BatchSize { get; init; } = 1;

    /// <summary>Positive blocking wait for new entries. Defaults to five seconds.</summary>
    public TimeSpan ReadWait { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Whether startup creates the stream and group. Existing groups are retained.</summary>
    public bool CreateGroup { get; init; } = true;

    /// <summary>Initial position for a newly created group. Defaults to the beginning.</summary>
    public RespireStreamId GroupStart { get; init; } = RespireStreamId.Beginning;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ConsumerCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(BatchSize);
        if (ReadWait <= TimeSpan.Zero || ReadWait > TimeSpan.FromMilliseconds(int.MaxValue))
            throw new ArgumentOutOfRangeException(nameof(ReadWait), "Read wait must be positive and at most Int32.MaxValue milliseconds.");
    }
}
