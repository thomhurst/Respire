namespace Respire.Networking;

/// <summary>The transport retired before accepting this command. No bytes were enqueued.</summary>
/// <remarks>Routers may retry this failure on a current generation. It never represents an
/// accepted command, a server error, or an ambiguous connection failure.</remarks>
internal sealed class RespireConnectionRetiredException(string host, int port)
    : RespireException($"Connection to {host}:{port} is retired; the command was not accepted.");
