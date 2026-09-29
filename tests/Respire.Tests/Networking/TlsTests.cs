using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Respire.Commands;
using Respire.Infrastructure;
using Respire.Internal;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class TlsTests
{
    [Test]
    public async Task TlsConnection_PerformsHandshakeBeforeRespTraffic()
    {
        using var certificate = CreateCertificate();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = RunTlsServerAsync(listener, certificate);

        SslPolicyErrors? certificateErrors = null;
        var tlsOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, _, _, errors) =>
            {
                certificateErrors = errors;
                return true;
            },
        };
        RespireConnection connection;
        try
        {
            connection = await RespireConnection.ConnectAsync(
                "localhost",
                port,
                new RespireConnectionOptions
                {
                    UseTls = true,
                    TlsOptions = tlsOptions,
                });
        }
        catch
        {
            await server;
            throw;
        }

        await using (connection)
        {
            var response = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame));

            await Assert.That(response.AsString()).IsEqualTo("PONG");
            await Assert.That(certificateErrors!.Value.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)).IsFalse();
            await Assert.That(tlsOptions.TargetHost).IsNull();
            response.Dispose();
        }
        await server.WaitAsync(TimeSpan.FromSeconds(5));
        listener.Stop();
    }

    [Test]
    [Arguments(null)]
    [Arguments("explicit.example")]
    public async Task ClusterIpRedirectKeepsDiscoveredTlsIdentity(string? explicitHost)
    {
        using var certificate = CreateCertificate(explicitHost ?? "localhost");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string? observedSni = null;
        var server = RunClusterTlsServerAsync(listener, certificate, port,
            name => observedSni = name, deadline.Token);
        try
        {
            await using var seed = new FakeRespServer(ClusterNodeIdentityTests.Topology("localhost", port, "tls-node"));
            var tlsOptions = new SslClientAuthenticationOptions
            {
                TargetHost = explicitHost,
                // Trust the test's self-signed certificate, but never ignore a name mismatch.
                RemoteCertificateValidationCallback = (_, _, _, errors) =>
                    (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None,
            };
            var options = new RespireOptions
            {
                UseCluster = true,
                UseTls = true,
                TlsOptions = tlsOptions,
                Connections = 1,
                Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
            };
            // Discovery is scripted in plaintext; discovered command transports use real TLS.
            await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", seed.Port);
            await using var router = new ClusterRouter(options, primary);
            await router.EnsureConnectedAsync(deadline.Token);
            var connection = await router.GetConnectionAsync(0, deadline.Token);
            using var moved = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame), deadline.Token);
            var redirect = new RespireServerException(moved.GetErrorMessage());
            var redirected = await router.GetRedirectConnectionAsync(redirect, connection, deadline.Token);
            using var pong = await redirected.SendAsync(new RawCommand(FakeRespServer.PingFrame), deadline.Token);

            await Assert.That(ReferenceEquals(connection, redirected)).IsTrue();
            await Assert.That(pong.AsString()).IsEqualTo("PONG");
            await Assert.That(tlsOptions.TargetHost).IsEqualTo(explicitHost);
            await server;
            await Assert.That(observedSni).IsEqualTo(explicitHost ?? "localhost");
        }
        finally
        {
            deadline.Cancel();
            listener.Stop();
            try { await server; }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        }
    }

    private static async Task RunClusterTlsServerAsync(
        TcpListener listener, X509Certificate2 certificate, int port,
        Action<string?> observeSni, CancellationToken cancellationToken)
    {
        using var socket = await listener.AcceptSocketAsync(cancellationToken);
        await using var network = new NetworkStream(socket, ownsSocket: false);
        await using var tls = new SslStream(network);
        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificateSelectionCallback = (_, name) => { observeSni(name); return certificate; },
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        }, cancellationToken);
        var request = new byte[FakeRespServer.PingFrame.Length];
        await tls.ReadExactlyAsync(request, cancellationToken);
        await tls.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"-MOVED 0 127.0.0.1:{port}\r\n"), cancellationToken);
        await tls.ReadExactlyAsync(request, cancellationToken);
        await tls.WriteAsync(FakeRespServer.PongReply, cancellationToken);
    }

    [Test]
    public async Task RedissConnectionString_EnablesTlsAndUsesRedisDefaultPort()
    {
        var options = RespireOptions.Parse("rediss://cache.example");

        await Assert.That(options.UseTls).IsTrue();
        await Assert.That(options.PrimaryEndpoint).IsEqualTo(new RespireEndpoint("cache.example", 6379));
    }

    private static async Task RunTlsServerAsync(TcpListener listener, X509Certificate2 certificate)
    {
        using var socket = await listener.AcceptSocketAsync();
        await using var network = new NetworkStream(socket, ownsSocket: false);
        await using var tls = new SslStream(network);
        await tls.AuthenticateAsServerAsync(
            certificate,
            clientCertificateRequired: false,
            enabledSslProtocols: SslProtocols.Tls12 | SslProtocols.Tls13,
            checkCertificateRevocation: false);

        var request = new byte[FakeRespServer.PingFrame.Length];
        var received = 0;
        while (received < request.Length)
        {
            received += await tls.ReadAsync(request.AsMemory(received));
        }

        await tls.WriteAsync(FakeRespServer.PongReply);
    }

    private static X509Certificate2 CreateCertificate(string hostname = "localhost")
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={hostname}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
        subjectAlternativeNames.AddDnsName(hostname);
        request.CertificateExtensions.Add(subjectAlternativeNames.Build());
        using var ephemeral = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1));
#if NET10_0_OR_GREATER
        return X509CertificateLoader.LoadPkcs12(
            ephemeral.Export(X509ContentType.Pfx, "test-password"),
            "test-password",
            X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable);
#else
        return new X509Certificate2(
            ephemeral.Export(X509ContentType.Pfx, "test-password"),
            "test-password",
            X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable);
#endif
    }
}
