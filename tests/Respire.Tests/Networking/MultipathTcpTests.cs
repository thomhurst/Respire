using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Respire.Commands;
using Respire.Internal;
using Respire.Networking;

namespace Respire.Tests.Networking;

public class MultipathTcpTests
{
    [Test]
    public async Task OptionDefaultsOffAndPropagatesToTransportAndSentinel()
    {
        await Assert.That(new RespireOptions().EnableMultipathTcp).IsFalse();
        var options = new RespireOptions { Endpoints = [new("localhost")], EnableMultipathTcp = true }.ValidateAndSnapshot();
        await Assert.That(options.ToConnectionOptions().EnableMultipathTcp).IsTrue();
        await Assert.That(options.ToControlConnectionOptions().EnableMultipathTcp).IsTrue();
        await Assert.That(SentinelMonitoring.CreateOptions(options, new("sentinel", 26379))
            .ToConnectionOptions().EnableMultipathTcp).IsTrue();
    }

    [Test]
    [Arguments(22)]
    [Arguments(92)]
    [Arguments(93)]
    public async Task OnlyUnavailableNativeErrorsFallBackWithLog(int error)
    {
        var logger = new CaptureLogger();
        (int Domain, int Type, int Protocol) request = default;
        using var socket = MultipathTcpSocket.CreateLinux(AddressFamily.InterNetwork, logger, (domain, type, protocol) =>
        {
            request = (domain, type, protocol);
            Marshal.SetLastPInvokeError(error);
            return -1;
        });
        await Assert.That(request).IsEqualTo((2, 1 | 0x80000, 262));
        await Assert.That(socket.ProtocolType).IsEqualTo(ProtocolType.Tcp);
        await Assert.That(logger.Messages.Single()).Contains($"errno {error}");
    }

    [Test]
    [Arguments(1)]
    [Arguments(13)]
    [Arguments(24)]
    [Arguments(97)]
    public async Task UnrelatedNativeErrorsAreNotHidden(int error)
    {
        var logger = new CaptureLogger();
        var exception = await Assert.That(() => MultipathTcpSocket.CreateLinux(AddressFamily.InterNetwork, logger, (_, _, _) =>
        {
            Marshal.SetLastPInvokeError(error);
            return -1;
        })).Throws<SocketException>();
        await Assert.That(exception!.NativeErrorCode).IsEqualTo(error);
        await Assert.That(logger.Messages).IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OptInConnectsAndPreservesSocketOptions(bool hostname)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var logger = new CaptureLogger();
        using var socket = await MultipathTcpSocket.ConnectAsync(
            new(hostname ? "localhost" : "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port),
            new RespireConnectionOptions { TcpKeepAliveTime = TimeSpan.FromSeconds(20), SocketReceiveBufferSize = 8192 },
            logger, timeout.Token);
        using var accepted = await listener.AcceptSocketAsync(timeout.Token);
        await Assert.That(socket.NoDelay).IsTrue();
        await Assert.That(socket.ReceiveBufferSize).IsGreaterThanOrEqualTo(8192);
        await Assert.That((int)socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive)!).IsEqualTo(1);
        await socket.SendAsync(new byte[] { 42 }, SocketFlags.None, timeout.Token);
        var buffer = new byte[1];
        await accepted.ReceiveAsync(buffer, SocketFlags.None, timeout.Token);
        await Assert.That(buffer[0]).IsEqualTo((byte)42);
        if (!OperatingSystem.IsLinux())
            await Assert.That(logger.Messages.Single()).Contains("only supported on Linux");
    }

    [Test]
    [Arguments(AddressFamily.InterNetwork)]
    [Arguments(AddressFamily.InterNetworkV6)]
    public async Task LinuxSocketUsesNativeProtocolOrDocumentedFallback(AddressFamily family)
    {
        if (!OperatingSystem.IsLinux()) return;
        if (family == AddressFamily.InterNetworkV6 && !Socket.OSSupportsIPv6) return;
        var logger = new CaptureLogger();
        using var socket = MultipathTcpSocket.CreateLinux(family, logger);
        var protocol = new byte[4];
        socket.GetRawSocketOption(1, 38, protocol); // Linux SOL_SOCKET / SO_PROTOCOL.
        var enabled = File.Exists("/proc/sys/net/mptcp/enabled")
            && File.ReadAllText("/proc/sys/net/mptcp/enabled").Trim() == "1";
        await Assert.That(BitConverter.ToInt32(protocol)).IsEqualTo(enabled ? 262 : 6);
        await Assert.That(logger.Messages.Count).IsEqualTo(enabled ? 0 : 1);
    }

    [Test]
    public async Task LinuxRetriesNextAddressWithFreshNativeSocket()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = await MultipathTcpSocket.ConnectAddressesAsync(
            [IPAddress.Parse("127.0.0.2"), IPAddress.Loopback], ((IPEndPoint)listener.LocalEndpoint).Port,
            new RespireConnectionOptions(), null, timeout.Token);
        using var accepted = await listener.AcceptSocketAsync(timeout.Token);
        await Assert.That(socket.Connected).IsTrue();
    }

    [Test]
    public async Task CancellationIsPreserved()
    {
        using var timeout = new CancellationTokenSource();
        timeout.Cancel();
        var error = await Assert.That(async () => await MultipathTcpSocket.ConnectAsync(
            new("localhost", 6379), new RespireConnectionOptions(), null, timeout.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(timeout.Token);
    }

    [Test]
    public async Task UnixSocketTakesPrecedence()
    {
        if (!Socket.OSSupportsUnixDomainSockets) return;
        var logger = new CaptureLogger();
        var path = Path.Combine(Path.GetTempPath(), $"mptcp-{Guid.NewGuid():N}.sock");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // The absent Unix endpoint must fail as a Unix socket, without attempting TCP or logging fallback.
        await Assert.That(async () => await RespireConnection.ConnectAsync(path, 0,
            new RespireConnectionOptions { EnableMultipathTcp = true }, logger, timeout.Token))
            .Throws<SocketException>();
        await Assert.That(logger.Messages).IsEmpty();
    }

    [Test]
    public async Task RespConnectionUsesOptionAndFactoryTakesPrecedence()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var logger = new CaptureLogger();
        var options = new RespireOptions { EnableMultipathTcp = true, Protocol = RespireProtocol.Resp2, LibraryName = null };
        await using (var connection = await RespireConnection.ConnectAsync("localhost", server.Port,
            options.ToConnectionOptions(), logger, timeout.Token))
        {
            using var reply = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame), timeout.Token);
            await Assert.That(reply.AsString()).IsEqualTo("PONG");
        }
        logger.Messages.Clear();
        var calls = 0;
        options = options with
        {
            ConnectionFactory = async (_, token) =>
            {
                calls++;
                var client = new TcpClient();
                try
                {
                    await client.ConnectAsync(IPAddress.Loopback, server.Port, token);
                    return client.GetStream();
                }
                catch { client.Dispose(); throw; }
            },
        };
        await using var custom = await RespireConnection.ConnectAsync("factory.invalid", server.Port,
            options.ToConnectionOptions(), logger, timeout.Token);
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(logger.Messages).IsEmpty();
    }

    private sealed class CaptureLogger : ILogger
    {
        internal List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id == 143) Messages.Add(formatter(state, exception));
        }
    }
}
