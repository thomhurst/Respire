using System.Net.Sockets;
using System.Reflection;
using Respire.Networking;

namespace Respire.Tests.Networking;

internal static class TestSocketBackpressure
{
    public static Socket LimitSendBuffer(RespireConnection connection)
    {
        var socket = (Socket)typeof(RespireConnection)
            .GetField("_socket", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection)!;
        // macOS rejects zero. A 16 KiB buffer keeps a multi-megabyte send pending against a parked peer.
        // Keep zero elsewhere, including Winsock where it disables provider send buffering.
        socket.SendBufferSize = OperatingSystem.IsMacOS() ? 16 * 1024 : 0;
        return socket;
    }
}
