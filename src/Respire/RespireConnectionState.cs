namespace Respire;

/// <summary>The connection recovery path that supplied attempt metadata.</summary>
public enum RespireReconnectSource
{
    /// <summary>No recovery source was specified.</summary>
    Unspecified,
    /// <summary>A dedicated connection acquisition.</summary>
    Dedicated,
    /// <summary>A multiplexed command connection.</summary>
    Command,
}

/// <summary>The coarse health of a client's connections, surfaced via <see cref="RespireClient.ConnectionStateChanged"/>.</summary>
public enum RespireConnectionState
{
    /// <summary>All required connections are available.</summary>
    Connected,

    /// <summary>At least one required connection is being replaced.</summary>
    Reconnecting,

    /// <summary>A required connection could not be restored, or the client was disposed.</summary>
    Disconnected,
}

/// <summary>Describes a connection-state transition and its source.</summary>
/// <param name="Endpoint">The Redis endpoint whose connection triggered the transition.</param>
/// <param name="State">The resulting connection state.</param>
/// <param name="Error">The connection or recovery error, when available.</param>
public readonly record struct RespireConnectionStateChange(
    RespireEndpoint Endpoint,
    RespireConnectionState State,
    Exception? Error)
{
    /// <summary>The recovery path, when explicitly attributed.</summary>
    public RespireReconnectSource ReconnectSource { get; init; }
    /// <summary>The source's state before aggregation into endpoint health, when supplied.</summary>
    public RespireConnectionState? SourceState { get; init; }
    /// <summary>Process-local dedicated rent recovery identifier; null for other transitions.</summary>
    public long? ReconnectEpisodeId { get; init; }
    /// <summary>One-based configured replacement attempt; zero for transitions without policy metadata.</summary>
    public int ReconnectAttempt { get; init; }
    /// <summary>Zero-based source slot within this endpoint's multiplexer generation; null for dedicated or endpoint-wide transitions.</summary>
    public int? ConnectionSlot { get; init; }
    /// <summary>Delay reserved before this Reconnecting attempt; null when no attempt is scheduled.</summary>
    public TimeSpan? NextReconnectDelay { get; init; }
    /// <summary>Whether this connection slot or dedicated rent exhausted its configured attempt limit.</summary>
    public bool ReconnectExhausted { get; init; }
}
