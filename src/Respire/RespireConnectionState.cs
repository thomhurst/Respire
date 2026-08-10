namespace Respire;

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
    Exception? Error);
