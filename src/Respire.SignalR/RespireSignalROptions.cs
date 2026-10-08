namespace Respire.SignalR;

/// <summary>Settings for the SignalR backplane. The registered Respire client is shared and remains owned by DI.</summary>
public sealed class RespireSignalROptions
{
    /// <summary>Literal bytes prepended to Microsoft's hub channel names. Match Redis Configuration.ChannelPrefix during migration.</summary>
    public string ChannelPrefix { get; set; } = "";

    /// <summary>Uses SSUBSCRIBE/SPUBLISH. All participating servers must use this mode; Microsoft Redis servers cannot participate.</summary>
    public bool UseShardedPubSub { get; set; }

    /// <summary>Maximum buffered messages per channel. Overflow drops the oldest message and reports a delivery gap.</summary>
    public int SubscriptionBufferSize { get; set; } = 1024;

    /// <summary>Maximum time to await a remote group acknowledgement.</summary>
    public TimeSpan GroupAckTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
