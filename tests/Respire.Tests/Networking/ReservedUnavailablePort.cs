using System.Net;
using System.Net.Sockets;

namespace Respire.Tests.Networking;

/// <summary>Reserves a loopback TCP port that refuses connections until disposed.</summary>
internal sealed class ReservedUnavailablePort : IDisposable
{
    private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
    {
        ExclusiveAddressUse = true,
    };

    public ReservedUnavailablePort()
    {
        try
        {
            // Bind without Listen: keep parallel fixtures from claiming this port while
            // preserving connection refusal instead of an accepted, stalled connection.
            _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            // .NET enables SO_REUSEADDR inside Unix Bind; clear it after binding too.
            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
            Port = ((IPEndPoint)_socket.LocalEndPoint!).Port;
        }
        catch
        {
            _socket.Dispose();
            throw;
        }
    }

    public int Port { get; }
    public void Dispose() => _socket.Dispose();
}
