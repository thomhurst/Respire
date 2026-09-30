namespace Respire;

/// <summary>The kind of item yielded by a pub/sub subscription.</summary>
public enum RespireMessageKind
{
    /// <summary>A message published by Redis.</summary>
    Message,
    /// <summary>Delivery continuity was lost; reload dependent state from its source.</summary>
    Gap,
}

/// <summary>Reasons delivery continuity was lost. Adjacent gaps can combine reasons.</summary>
[Flags]
public enum RespireSubscriptionGapReason
{
    /// <summary>The subscriber connection was lost and the target was resubscribed.</summary>
    Reconnect = 1,
    /// <summary>The subscription buffer discarded one or more messages.</summary>
    BufferOverflow = 2,
}

/// <summary>An observed delivery gap. Reconnect loss counts are unknown.</summary>
/// <param name="Reason">The cause or combined causes of the gap.</param>
/// <param name="StartedAt">When the client first observed the interruption or discard.</param>
/// <param name="EndedAt">When resubscription was acknowledged or the last discard occurred.</param>
/// <param name="DroppedMessages">Known local buffer discards; excludes unknown server-side loss.</param>
public sealed record RespireSubscriptionGap(
    RespireSubscriptionGapReason Reason,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    long DroppedMessages = 0)
{
    /// <summary>The observed interval. Detection can occur after actual connectivity was lost.</summary>
    public TimeSpan Duration => EndedAt - StartedAt;

    internal static RespireSubscriptionGap? Merge(RespireSubscriptionGap? first, RespireSubscriptionGap? second)
        => first is null ? second : second is null ? first : new(
            first.Reason | second.Reason,
            first.StartedAt < second.StartedAt ? first.StartedAt : second.StartedAt,
            first.EndedAt > second.EndedAt ? first.EndedAt : second.EndedAt,
            first.DroppedMessages + second.DroppedMessages);
}
