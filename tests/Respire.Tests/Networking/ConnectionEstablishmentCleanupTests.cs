using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using OS = TUnit.Core.Enums.OS;

namespace Respire.Tests.Networking;

public class ConnectionEstablishmentCleanupTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task TcpEstablishmentFailureClosesTransport(bool useTls, bool constructionFails)
    {
        using var deadline = new CancellationTokenSource(Limit);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var certificate = useTls ? TestTlsCertificate.Create() : null;
        SslStream? clientTls = null;
        var options = FailureOptions(constructionFails) with
        {
            UseTls = useTls,
            TlsOptions = new()
            {
                RemoteCertificateValidationCallback = (sender, _, _, _) =>
                {
                    clientTls = (SslStream)sender;
                    return true;
                },
            },
        };
        var expected = new InvalidOperationException("Connected logging failed.");
        var connecting = RespireConnection.ConnectAsync(
            "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, options,
            constructionFails ? null : new ConnectedFailureLogger(expected), deadline.Token);
        using var peer = await listener.AcceptSocketAsync(deadline.Token);
        await using var network = new NetworkStream(peer, ownsSocket: false);
        await using var tls = useTls ? new SslStream(network, leaveInnerStreamOpen: true) : null;
        if (tls is not null)
        {
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
            }, deadline.Token);
        }

        await AssertFailureAsync(connecting, constructionFails, expected);
        if (useTls)
        {
            await Assert.That(clientTls).IsNotNull();
            await Assert.That(clientTls!.CanRead).IsFalse();
        }
        // A TLS close_notify alone does not prove the separately owned socket closed.
        await AssertPeerClosedAsync(peer, deadline.Token);
    }

    [Test]
    [RunOn(OS.Linux | OS.MacOs)]
    [Arguments(false)]
    [Arguments(true)]
    public async Task UnixEstablishmentFailureClosesSocket(bool constructionFails)
    {
        // macOS's per-user temporary directory can exceed Unix socket path limits.
        var path = $"/tmp/respire-{Guid.NewGuid():N}.sock";
        using var deadline = new CancellationTokenSource(Limit);
        try
        {
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(1);
            var expected = new InvalidOperationException("Connected logging failed.");
            var connecting = RespireConnection.ConnectAsync(path, 0, FailureOptions(constructionFails),
                constructionFails ? null : new ConnectedFailureLogger(expected), deadline.Token);
            using var peer = await listener.AcceptAsync(deadline.Token);

            await AssertFailureAsync(connecting, constructionFails, expected);
            await AssertPeerClosedAsync(peer, deadline.Token);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static RespireConnectionOptions FailureOptions(bool constructionFails) => new()
    {
        // The internal options let this fail in InflightRing's constructor, after connect.
        MaxInflightCommands = constructionFails ? 0 : 16,
    };

    private static async Task AssertPeerClosedAsync(Socket peer, CancellationToken cancellationToken)
    {
        try
        {
            // Drain any remaining TLS records until the underlying transport closes.
            var buffer = new byte[1024];
            while (await peer.ReceiveAsync(buffer, SocketFlags.None, cancellationToken) != 0) { }
        }
        catch (SocketException error) when (error.SocketErrorCode == SocketError.ConnectionReset)
        {
            // Disposing with unread TLS records can reset the peer instead of sending FIN.
        }
    }

    private static async Task AssertFailureAsync(
        Task<RespireConnection> connecting, bool constructionFails, Exception expected)
    {
        if (constructionFails)
        {
            var error = await Assert.That(async () => await connecting).Throws<ArgumentOutOfRangeException>();
            await Assert.That(error!.ParamName).IsEqualTo("capacity");
        }
        else
        {
            var error = await Assert.That(async () => await connecting).Throws<InvalidOperationException>();
            await Assert.That(error).IsSameReferenceAs(expected);
        }
    }

    private sealed class ConnectedFailureLogger(Exception error) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Name == "ConnectionConnected") throw error;
        }
    }
}
