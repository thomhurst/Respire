using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DotNet.Testcontainers.Builders;
using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
[NotInParallel]
public class ProxyIntegrationTests
{
    [Test]
    [Arguments(false, false, false)]
    [Arguments(false, false, true)]
    [Arguments(false, true, false)]
    [Arguments(false, true, true)]
    [Arguments(true, false, false)]
    [Arguments(true, false, true)]
    [Arguments(true, true, false)]
    [Arguments(true, true, true)]
    public async Task RedisCommandsThroughContainerProxy(bool http, bool tls, bool authenticate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = deadline.Token;
        const string redisHost = "redis-proxy-target";
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={redisHost}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(redisHost);
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));

        await using var network = new NetworkBuilder().Build();
        await network.CreateAsync(token);
        // Redis has no published host port: only the proxy can resolve and reach this endpoint.
        await using var redis = new ContainerBuilder("redis:8.4-alpine")
            .WithNetwork(network).WithNetworkAliases(redisHost)
            .WithResourceMapping(Encoding.ASCII.GetBytes(certificate.ExportCertificatePem()), "/tls/server.crt")
            .WithResourceMapping(Encoding.ASCII.GetBytes(key.ExportPkcs8PrivateKeyPem()), "/tls/server.key")
            .WithCommand("redis-server", "--protected-mode", "no", "--save", "", "--port", "6379",
                "--tls-port", "6380", "--tls-cert-file", "/tls/server.crt", "--tls-key-file", "/tls/server.key",
                "--tls-ca-cert-file", "/tls/server.crt", "--tls-auth-clients", "no")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379))
            .Build();
        await redis.StartAsync(token);
        var scheme = http ? "http" : "socks5";
        var credentials = authenticate ? "proxy-user:proxy-password@" : "";
        await using var proxy = await ContainerStartRetry.StartAsync(() => new ContainerBuilder("gogost/gost:3.0.0")
            .WithNetwork(network).WithPortBinding(1080, true)
            .WithCommand("-L", $"{scheme}://{credentials}:1080")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(1080))
            .Build(), [1080], token);

        var proxyEndpoint = new RespireEndpoint(proxy.Hostname, proxy.GetMappedPublicPort(1080));
        var username = authenticate ? "proxy-user" : null;
        var password = authenticate ? "proxy-password" : null;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new(redisHost, tls ? 6380 : 6379)], Connections = 1,
            Protocol = RespireProtocol.Resp3, UseTls = tls,
            TlsOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, received, _, errors) =>
                    (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0
                    && received?.GetCertHashString() == certificate.GetCertHashString(),
            },
            ConnectionFactory = http ? RespireProxy.HttpConnect(proxyEndpoint, username, password)
                : RespireProxy.Socks5(proxyEndpoint, username, password),
        }, token);
        await client.PingAsync(token);
        await client.SetAsync("proxy:key", "through the tunnel", cancellationToken: token);
        (await client.GetStringAsync("proxy:key", token)).Should().Be("through the tunnel");

        // A dedicated connection must negotiate its own tunnel as well.
        await using var subscription = await client.SubscribeAsync("proxy:channel", cancellationToken: token);
        await client.PublishAsync("proxy:channel", "message", token);
        await using var messages = subscription.GetAsyncEnumerator(token);
        (await messages.MoveNextAsync()).Should().BeTrue();
    }
}
