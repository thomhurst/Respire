using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Respire.Commands;
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

        RespireConnection connection;
        try
        {
            connection = await RespireConnection.ConnectAsync(
                "localhost",
                port,
                new RespireConnectionOptions
                {
                    UseTls = true,
                    TlsOptions = new SslClientAuthenticationOptions
                    {
                        TargetHost = "localhost",
                        RemoteCertificateValidationCallback = static (_, _, _, _) => true,
                    },
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
            response.Dispose();
        }
        await server.WaitAsync(TimeSpan.FromSeconds(5));
        listener.Stop();
    }

    [Test]
    public async Task RedissConnectionString_EnablesTlsAndUsesTlsDefaultPort()
    {
        var options = RespireOptions.Parse("rediss://cache.example");

        await Assert.That(options.UseTls).IsTrue();
        await Assert.That(options.PrimaryEndpoint).IsEqualTo(new RespireEndpoint("cache.example", 6380));
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

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
        subjectAlternativeNames.AddDnsName("localhost");
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
