namespace Respire;

/// <summary>Opt-in duplication of slow, idempotent buffered reads onto another eligible server.</summary>
/// <remarks>
/// Hedging respects the read policy: Primary never hedges, Replica only uses replicas, and
/// preferred-role and Nearest policies can use either role. Replica replies can be stale.
/// Writes, scripts, random selections, cursors, blocking commands, batches, transactions,
/// streamed replies, and commands without audited idempotent-read metadata are not hedged.
/// </remarks>
public sealed record RespireHedgedReadOptions
{
    /// <summary>Delay after the first read is dispatched before trying one additional server. Defaults to 10 ms.</summary>
    public TimeSpan Delay { get; init; } = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// Maximum percentage of eligible reads that can start a hedge, from 1 through 100.
    /// Defaults to 5. The budget is shared by all views of one client, starts empty, and saves
    /// at most one hedge, so a long run of fast reads cannot fund an unbounded later burst.
    /// </summary>
    public int MaximumExtraLoadPercent { get; init; } = 5;

    internal void Validate()
    {
        if (Delay < TimeSpan.FromMilliseconds(1) || Delay > TimeSpan.FromMinutes(1))
            throw new RespireConfigurationException("RespireOptions.HedgedReads.Delay must be between one millisecond and one minute.");
        if (MaximumExtraLoadPercent is < 1 or > 100)
            throw new RespireConfigurationException("RespireOptions.HedgedReads.MaximumExtraLoadPercent must be between 1 and 100.");
    }
}
