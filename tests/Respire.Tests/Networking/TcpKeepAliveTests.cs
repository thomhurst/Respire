using System.Net.Sockets;
using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

/// <summary>
/// TCP keepalive configuration: option defaults, application to the socket, validation, and
/// the client-level option mapping.
/// </summary>
public class TcpKeepAliveTests
{
    [Test]
    public async Task KeepAlive_DisabledByDefault()
    {
        var options = RespireConnectionOptions.Default;

        await Assert.That(options.TcpKeepAliveTime).IsNull();
        await Assert.That(options.TcpKeepAliveInterval).IsNull();
        await Assert.That(options.TcpKeepAliveRetryCount).IsNull();
    }

    [Test]
    public async Task ApplyTcpKeepAlive_WithoutTime_LeavesKeepAliveOff()
    {
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);

        RespireConnection.ApplyTcpKeepAlive(socket, RespireConnectionOptions.Default);

        var enabled = (int)socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive)!;
        await Assert.That(enabled).IsEqualTo(0);
    }

    [Test]
    public async Task ApplyTcpKeepAlive_EnablesKeepAliveAndSetsIdleTime()
    {
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        var options = new RespireConnectionOptions { TcpKeepAliveTime = TimeSpan.FromSeconds(45) };

        RespireConnection.ApplyTcpKeepAlive(socket, options);

        var enabled = (int)socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive)!;
        var time = (int)socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime)!;
        await Assert.That(enabled).IsNotEqualTo(0);
        await Assert.That(time).IsEqualTo(45);
    }

    [Test]
    public async Task ApplyTcpKeepAlive_SetsIntervalAndRetryCount()
    {
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        var options = new RespireConnectionOptions
        {
            TcpKeepAliveTime = TimeSpan.FromSeconds(60),
            TcpKeepAliveInterval = TimeSpan.FromSeconds(10),
            TcpKeepAliveRetryCount = 4,
        };

        RespireConnection.ApplyTcpKeepAlive(socket, options);

        var interval = (int)socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval)!;
        var retryCount = (int)socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount)!;
        await Assert.That(interval).IsEqualTo(10);
        await Assert.That(retryCount).IsEqualTo(4);
    }

    [Test]
    public async Task ConnectAsync_SubSecondKeepAliveTime_Throws()
    {
        var options = new RespireConnectionOptions { TcpKeepAliveTime = TimeSpan.FromMilliseconds(500) };

        await Assert.That(async () => await RespireConnection.ConnectAsync("127.0.0.1", 1, options))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task ConnectAsync_IntervalWithoutTime_Throws()
    {
        var options = new RespireConnectionOptions { TcpKeepAliveInterval = TimeSpan.FromSeconds(10) };

        await Assert.That(async () => await RespireConnection.ConnectAsync("127.0.0.1", 1, options))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task ConnectAsync_RetryCountWithoutTime_Throws()
    {
        var options = new RespireConnectionOptions { TcpKeepAliveRetryCount = 3 };

        await Assert.That(async () => await RespireConnection.ConnectAsync("127.0.0.1", 1, options))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task RespireOptions_MapKeepAliveToConnectionOptions()
    {
        var options = new RespireOptions
        {
            TcpKeepAliveTime = TimeSpan.FromSeconds(60),
            TcpKeepAliveInterval = TimeSpan.FromSeconds(10),
            TcpKeepAliveRetryCount = 4,
        };

        var connectionOptions = options.ToConnectionOptions();

        await Assert.That(connectionOptions.TcpKeepAliveTime).IsEqualTo(TimeSpan.FromSeconds(60));
        await Assert.That(connectionOptions.TcpKeepAliveInterval).IsEqualTo(TimeSpan.FromSeconds(10));
        await Assert.That(connectionOptions.TcpKeepAliveRetryCount).IsEqualTo(4);
    }

    [Test]
    public async Task Connection_WithKeepAlive_RoundTrips()
    {
        await using var server = new FakeRespServer("+PONG\r\n"u8.ToArray());
        await using var connection = await RespireConnection.ConnectAsync(
            "127.0.0.1", server.Port,
            new RespireConnectionOptions { TcpKeepAliveTime = TimeSpan.FromSeconds(60) });

        var response = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame));

        await Assert.That(response.AsString()).IsEqualTo("PONG");
        response.Dispose();
    }
}
