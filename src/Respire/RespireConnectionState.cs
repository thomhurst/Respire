namespace Respire;

/// <summary>The coarse health of a client's connections, surfaced via <see cref="RespireClient.ConnectionStateChanged"/>.</summary>
public enum RespireConnectionState
{
    Connected,
    Reconnecting,
}
