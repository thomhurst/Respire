using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class SentinelTests
{
    [Test]
    public async Task ConnectionString_ParsesSentinelOptions()
    {
        var options = RespireOptions.Parse(
            "redis://sentinel.example?serviceName=mymaster&sentinelUser=sentinel&sentinelPassword=secret&sentinelTls=false");

        await Assert.That(options.PrimaryEndpoint).IsEqualTo(new RespireEndpoint("sentinel.example", 26379));
        await Assert.That(options.SentinelPrimaryName).IsEqualTo("mymaster");
        await Assert.That(options.SentinelUsername).IsEqualTo("sentinel");
        await Assert.That(options.SentinelPassword).IsEqualTo("secret");
        await Assert.That(options.SentinelUseTls).IsFalse();
    }

    [Test]
    public async Task SentinelConnectionOptions_ForceResp2AndAllowPlaintextOverride()
    {
        var options = SentinelResolver.CreateSentinelConnectionOptions(new RespireOptions
        {
            Protocol = RespProtocol.Resp3,
            UseTls = true,
            SentinelUseTls = false,
            ClientName = "primary-client",
        });

        await Assert.That(options.UseResp3).IsFalse();
        await Assert.That(options.UseTls).IsFalse();
        await Assert.That(options.ClientName).IsNull();
    }

    [Test]
    public async Task SentinelConnectionOptions_UseDedicatedTlsOptions()
    {
        var primaryTlsOptions = new SslClientAuthenticationOptions { TargetHost = "primary.example" };
        var sentinelTlsOptions = new SslClientAuthenticationOptions { TargetHost = "sentinel.example" };

        var options = SentinelResolver.CreateSentinelConnectionOptions(new RespireOptions
        {
            UseTls = false,
            TlsOptions = primaryTlsOptions,
            SentinelUseTls = true,
            SentinelTlsOptions = sentinelTlsOptions,
        });

        await Assert.That(options.UseTls).IsTrue();
        await Assert.That(ReferenceEquals(options.TlsOptions, sentinelTlsOptions)).IsTrue();
    }

    [Test]
    public async Task SentinelConnectionString_InheritsPrimaryTlsHostUnlessOverridden()
    {
        var primary = RespireOptions.Parse(
            "sentinel,serviceName=primary,ssl=true,sslHost=shared.example,sslProtocols=Tls12");
        var sentinel = SentinelResolver.CreateSentinelConnectionOptions(primary);

        await Assert.That(sentinel.UseTls).IsTrue();
        await Assert.That(sentinel.TlsOptions!.TargetHost).IsEqualTo("shared.example");
        await Assert.That(sentinel.TlsOptions.EnabledSslProtocols).IsEqualTo(primary.TlsOptions!.EnabledSslProtocols);
    }

    [Test]
    public async Task SentinelConnectionString_SeparatesPrimaryAndSentinelTlsHostnames()
    {
        var primary = RespireOptions.Parse(
            "sentinel-a,sentinel-b,serviceName=primary,sslHost=primary.example," +
            "sentinelSslHost=sentinel.example,sslProtocols=Tls12|Tls13,checkCertificateRevocation=true");
        var sentinel = SentinelResolver.CreateSentinelConnectionOptions(primary);

        await Assert.That(primary.TlsOptions!.TargetHost).IsEqualTo("primary.example");
        await Assert.That(sentinel.TlsOptions!.TargetHost).IsEqualTo("sentinel.example");
        await Assert.That(sentinel.UseTls).IsTrue();
        await Assert.That(sentinel.TlsOptions.EnabledSslProtocols).IsEqualTo(primary.TlsOptions.EnabledSslProtocols);
        await Assert.That(sentinel.TlsOptions.CertificateRevocationCheckMode)
            .IsEqualTo(primary.TlsOptions.CertificateRevocationCheckMode);
    }

    [Test]
    [Arguments("sentinelTls=false,sentinelSslHost=sentinel.example")]
    [Arguments("sentinelSslHost=sentinel.example,sentinelTls=false")]
    public async Task SentinelConnectionString_ExplicitTlsDisableOverridesHost(string settings)
    {
        var primary = RespireOptions.Parse($"sentinel,serviceName=primary,ssl=true,{settings}");
        var sentinel = SentinelResolver.CreateSentinelConnectionOptions(primary);

        await Assert.That(primary.UseTls).IsTrue();
        await Assert.That(sentinel.UseTls).IsFalse();
        await Assert.That(sentinel.TlsOptions!.TargetHost).IsEqualTo("sentinel.example");
    }

    [Test]
    public async Task TlsHostOverride_PreservesConfiguredOptionsWithoutMutation()
    {
        var primary = new SslClientAuthenticationOptions
        {
            TargetHost = "primary.example",
            AllowRenegotiation = false,
            RemoteCertificateValidationCallback = (_, _, _, _) => true,
            ApplicationProtocols = [SslApplicationProtocol.Http2],
        };
        var sentinel = Respire.Networking.RespireConnection.CreateTlsOptions(
            primary, "sentinel.example", overrideTargetHost: true);

        await Assert.That(primary.TargetHost).IsEqualTo("primary.example");
        await Assert.That(sentinel.TargetHost).IsEqualTo("sentinel.example");
        await Assert.That(sentinel.AllowRenegotiation).IsFalse();
        await Assert.That(ReferenceEquals(sentinel.RemoteCertificateValidationCallback,
            primary.RemoteCertificateValidationCallback)).IsTrue();
        await Assert.That(sentinel.ApplicationProtocols).IsEquivalentTo(primary.ApplicationProtocols);
    }

    [Test]
    public async Task ConnectionString_RejectsEmptyServiceName()
    {
        var error = Assert.Throws<ArgumentException>(
            () => RespireOptions.Parse("redis://sentinel.example?serviceName=%20%20"));

        await Assert.That(error.Message).Contains("serviceName");
    }

    [Test]
    public async Task ConnectAsync_DiscoversPrimaryFromSentinel()
    {
        await using var primary = new FakeRespServer(FakeRespServer.PongReply);
        await using var sentinel = new FakeRespServer(PrimaryReply(primary.Port));

        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("127.0.0.1", sentinel.Port) },
            SentinelPrimaryName = "mymaster",
            ConnectTimeout = TimeSpan.FromSeconds(1),
        });

        _ = await client.PingAsync();

        await Assert.That(sentinel.ReceivedCommands).IsEquivalentTo(
            ["SENTINEL GET-MASTER-ADDR-BY-NAME mymaster"]);
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(["PING"]);
    }

    [Test]
    public async Task ConnectAsync_UsesSentinelCredentialsForDiscovery()
    {
        await using var primary = new FakeRespServer(
            FakeRespServer.OkReply,
            FakeRespServer.PongReply);
        await using var sentinel = new FakeRespServer(
            FakeRespServer.OkReply,
            PrimaryReply(primary.Port));

        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("127.0.0.1", sentinel.Port) },
            SentinelPrimaryName = "mymaster",
            Username = "redis-user",
            Password = "redis-secret",
            SentinelUsername = "sentinel-user",
            SentinelPassword = "sentinel-secret",
            ConnectTimeout = TimeSpan.FromSeconds(1),
        });

        _ = await client.PingAsync();

        await Assert.That(sentinel.ReceivedCommands).IsEquivalentTo(
        [
            "AUTH sentinel-user sentinel-secret",
            "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster",
        ], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(
        [
            "AUTH redis-user redis-secret",
            "PING",
        ], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task ConnectAsync_EmptySentinelPasswordDisablesInheritedAuthentication()
    {
        await using var primary = new FakeRespServer(
            FakeRespServer.OkReply,
            FakeRespServer.PongReply);
        await using var sentinel = new FakeRespServer(PrimaryReply(primary.Port));

        await using var client = await RespireClient.ConnectAsync(
            $"redis://:redis-secret@127.0.0.1:{sentinel.Port}?serviceName=mymaster&sentinelPassword=");

        _ = await client.PingAsync();

        await Assert.That(sentinel.ReceivedCommands).IsEquivalentTo(
            ["SENTINEL GET-MASTER-ADDR-BY-NAME mymaster"]);
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(
        [
            "AUTH redis-secret",
            "PING",
        ], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ConnectAsync_FallsBackWhenSentinelReturnsInvalidPort(bool useConnectionString)
    {
        await using var primary = new FakeRespServer(FakeRespServer.PongReply);
        await using var invalidSentinel = new FakeRespServer(PrimaryReply(65536));
        await using var validSentinel = new FakeRespServer(PrimaryReply(primary.Port));

        var options = new RespireOptions
        {
            Endpoints =
            {
                new RespireEndpoint("127.0.0.1", invalidSentinel.Port),
                new RespireEndpoint("127.0.0.1", validSentinel.Port),
            },
            SentinelPrimaryName = "mymaster",
            ConnectTimeout = TimeSpan.FromSeconds(1),
        };
        if (useConnectionString)
        {
            options = RespireOptions.Parse(
                $"127.0.0.1:{invalidSentinel.Port},127.0.0.1:{validSentinel.Port},serviceName=mymaster,connectTimeout=1000");
        }
        await using var client = await RespireClient.ConnectAsync(options);

        _ = await client.PingAsync();

        await Assert.That(invalidSentinel.ReceivedCommands).IsEquivalentTo(
            ["SENTINEL GET-MASTER-ADDR-BY-NAME mymaster"]);
        await Assert.That(validSentinel.ReceivedCommands).IsEquivalentTo(
            ["SENTINEL GET-MASTER-ADDR-BY-NAME mymaster"]);
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(["PING"]);
    }

    [Test]
    public async Task ConnectAsync_TimesOutUnresponsiveSentinelAndFallsBack()
    {
        await using var primary = new FakeRespServer(FakeRespServer.PongReply);
        await using var unresponsiveSentinel = new FakeRespServer(PrimaryReply(primary.Port));
        // Never race a delayed successful reply against the discovery deadline.
        unresponsiveSentinel.SuppressReply = static _ => true;
        await using var responsiveSentinel = new FakeRespServer(PrimaryReply(primary.Port));

        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints =
            {
                new RespireEndpoint("127.0.0.1", unresponsiveSentinel.Port),
                new RespireEndpoint("127.0.0.1", responsiveSentinel.Port),
            },
            SentinelPrimaryName = "mymaster",
            // Exercise the discovery timeout itself, without a competing command watchdog.
            // Healthy fallback connections need scheduling headroom on parallel CI runners.
            CommandTimeout = null,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        }).AsTask().WaitAsync(TimeSpan.FromSeconds(20));

        _ = await client.PingAsync();

        await Assert.That(unresponsiveSentinel.ReceivedCommands).IsEquivalentTo(
            ["SENTINEL GET-MASTER-ADDR-BY-NAME mymaster"]);
        await Assert.That(responsiveSentinel.ReceivedCommands).IsEquivalentTo(
            ["SENTINEL GET-MASTER-ADDR-BY-NAME mymaster"]);
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(["PING"]);
    }

    [Test]
    public async Task ConnectAsync_GivesPrimaryFreshConnectTimeoutAfterSlowDiscovery()
    {
        await using var primary = new FakeRespServer(
            FakeRespServer.OkReply,
            FakeRespServer.PongReply);
        primary.DelayReply(0, 1_200);
        await using var sentinel = new FakeRespServer(PrimaryReply(primary.Port));
        sentinel.DelayReply(0, 1_200);

        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("127.0.0.1", sentinel.Port) },
            SentinelPrimaryName = "mymaster",
            Password = "redis-secret",
            SentinelPassword = string.Empty,
            // Each phase fits comfortably, but their combined 2.4 seconds exceeds this timeout.
            // Reusing discovery's budget for the primary connection would therefore still fail.
            CommandTimeout = TimeSpan.FromSeconds(2),
            ConnectTimeout = TimeSpan.FromSeconds(3),
        });

        _ = await client.PingAsync();

        await Assert.That(sentinel.ReceivedCommands).IsEquivalentTo(
            ["SENTINEL GET-MASTER-ADDR-BY-NAME mymaster"]);
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(
        [
            "AUTH redis-secret",
            "PING",
        ], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task ConnectAsync_FallsBackWhenSentinelReportsUnreachablePrimary()
    {
        using var unavailablePrimary = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        unavailablePrimary.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var unavailablePrimaryPort = ((IPEndPoint)unavailablePrimary.LocalEndPoint!).Port;
        await using var primary = new FakeRespServer(FakeRespServer.PongReply);
        await using var staleSentinel = new FakeRespServer(PrimaryReply(unavailablePrimaryPort));
        await using var currentSentinel = new FakeRespServer(PrimaryReply(primary.Port));

        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints =
            {
                new RespireEndpoint("127.0.0.1", staleSentinel.Port),
                new RespireEndpoint("127.0.0.1", currentSentinel.Port),
            },
            SentinelPrimaryName = "mymaster",
            ConnectTimeout = TimeSpan.FromSeconds(1),
        });

        _ = await client.PingAsync();

        await Assert.That(staleSentinel.ReceivedCommands).IsEquivalentTo(
            ["SENTINEL GET-MASTER-ADDR-BY-NAME mymaster"]);
        await Assert.That(currentSentinel.ReceivedCommands).IsEquivalentTo(
            ["SENTINEL GET-MASTER-ADDR-BY-NAME mymaster"]);
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(["PING"]);
    }

    [Test]
    public async Task Create_RejectsSentinelBecauseDiscoveryIsNetworked()
    {
        var error = Assert.Throws<RespireConfigurationException>(() => RespireClient.Create(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("127.0.0.1", 26379) },
            SentinelPrimaryName = "mymaster",
        }));

        await Assert.That(error.Message).Contains("ConnectAsync");
    }

    private static byte[] PrimaryReply(int port)
        => Encoding.ASCII.GetBytes($"*2\r\n$9\r\n127.0.0.1\r\n${port.ToString().Length}\r\n{port}\r\n");
}
