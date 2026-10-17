namespace Respire.AI;

/// <summary>Limits and retention for Redis conversation history.</summary>
public sealed class RespireChatHistoryOptions
{
    /// <summary>Application/tenant namespace. Defaults to "default".</summary>
    public string StoreName { get; init; } = "default";

    /// <summary>Maximum stored messages per conversation. Defaults to 1000.</summary>
    public int MaximumMessages { get; init; } = 1000;

    /// <summary>Idle lifetime renewed by nonempty appends, not reads. Defaults to one day.</summary>
    public TimeSpan TimeToLive { get; init; } = TimeSpan.FromDays(1);

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(StoreName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumMessages);
        if (TimeToLive.Ticks < TimeSpan.TicksPerMillisecond)
            throw new ArgumentOutOfRangeException(nameof(TimeToLive));
    }
}
