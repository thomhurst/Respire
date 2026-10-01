using System.Diagnostics;
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
    private static readonly byte[] PrimaryRole = "*3\r\n$6\r\nmaster\r\n:0\r\n*0\r\n"u8.ToArray();

    [Test]
    [Arguments("*1\r\n$5\r\nslave\r\n")]
    [Arguments("*2\r\n$8\r\nsentinel\r\n*0\r\n")]
    [Arguments("+master\r\n")]
    [Arguments("*1\r\n$6\r\nmaster\r\n")]
    [Arguments("-NOPERM ROLE is denied\r\n")]
    public async Task ConnectAsync_RejectsStaleOrInvalidPrimaryAndTriesDiscoveredPeer(string invalidRole)
    {
        await using var stale = new FakeRespServer(Encoding.ASCII.GetBytes(invalidRole));
        await using var primary = new FakeRespServer(PrimaryRole, FakeRespServer.PongReply);
        await using var peer = new FakeRespServer(PrimaryReply(primary.Port));
        await using var seed = new FakeRespServer(PrimaryReply(stale.Port), PeersReply(peer.Port));
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = [new("127.0.0.1", seed.Port)],
            SentinelPrimaryName = "mymaster", DisableSentinelEventMonitoring = true,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        });
        await client.PingAsync();
        await stale.PeerClosed.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(stale.ReceivedCommands).IsEquivalentTo(["ROLE"]);
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(["ROLE", "PING"]);
        await Assert.That(seed.ReceivedCommands).IsEquivalentTo(
            ["SENTINEL GET-MASTER-ADDR-BY-NAME mymaster", "SENTINEL SENTINELS mymaster"]);
        // A newly learned peer is tried, but is not recursively expanded in this attempt.
        await Assert.That(peer.ReceivedCommands).IsEquivalentTo(["SENTINEL GET-MASTER-ADDR-BY-NAME mymaster"]);
    }

    [Test]
    public async Task ConnectAsync_PeerDiscoveryAclFailureDoesNotRejectValidatedPrimary()
    {
        await using var primary = new FakeRespServer(PrimaryRole, FakeRespServer.PongReply);
        await using var seed = new FakeRespServer(PrimaryReply(primary.Port), "-NOPERM SENTINELS is denied\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = [new("127.0.0.1", seed.Port)], SentinelPrimaryName = "mymaster", DisableSentinelEventMonitoring = true,
        });
        await client.PingAsync();
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(["ROLE", "PING"]);
    }

    [Test]
    [Arguments("timeout")]
    [Arguments("disconnect")]
    [Arguments("protocol")]
    public async Task ConnectAsync_OptionalPeerFailurePreservesTheCompletedPrimaryReply(string failure)
    {
        await using var primary = new FakeRespServer(PrimaryRole, FakeRespServer.PongReply);
        await using var sentinel = new FakeRespServer(PrimaryReply(primary.Port), "?invalid RESP\r\n"u8.ToArray())
        {
            CloseConnectionAfterCommand = failure == "disconnect" ? 2 : null,
            SuppressReply = command => failure == "timeout" && command == "SENTINEL SENTINELS mymaster",
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = [new("127.0.0.1", sentinel.Port)], SentinelPrimaryName = "mymaster", DisableSentinelEventMonitoring = true,
            CommandTimeout = null, ConnectTimeout = TimeSpan.FromSeconds(2),
        }).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await client.PingAsync();
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(["ROLE", "PING"]);
    }

    [Test]
    public async Task ConnectAsync_CallerCancellationDuringOptionalPeersIsNotSuppressed()
    {
        var peersRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var primary = new FakeRespServer(PrimaryRole);
        await using var sentinel = new FakeRespServer(PrimaryReply(primary.Port))
        {
            SuppressReply = command =>
            {
                if (command != "SENTINEL SENTINELS mymaster") return false;
                peersRequested.TrySetResult();
                return true;
            },
        };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = [new("127.0.0.1", sentinel.Port)], SentinelPrimaryName = "mymaster", DisableSentinelEventMonitoring = true,
            CommandTimeout = null, ConnectTimeout = TimeSpan.FromSeconds(10),
        }, cancellation.Token).AsTask();
        await peersRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var error = await Assert.That(async () => await pending).Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        await sentinel.PeerClosed.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(primary.ReceivedCommands).IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Discovery_CallerCancellationWinsOverConcurrentConnectionFailure(bool protocolFailure)
    {
        await using var sentinel = new FakeRespServer(PrimaryReply(6379), "*0\r\n"u8.ToArray());
        using var cancellation = new CancellationTokenSource();
        var options = new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = [new("127.0.0.1", sentinel.Port)], SentinelPrimaryName = "mymaster", DisableSentinelEventMonitoring = true,
        };
        var pending = SentinelResolver.ResolveAndConnectPrimaryAsync<int>(options, (_, _) =>
        {
            cancellation.Cancel();
            return ValueTask.FromException<int>(protocolFailure
                ? new RespireProtocolException("Malformed response during cancellation.")
                : new RespireConnectionException("Connection lost during cancellation."));
        }, cancellation.Token).AsTask();
        var error = await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)))
            .Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
    }

    [Test]
    public async Task Discovery_PrimaryConnectDeadlineReportsConnectTimeout()
    {
        await using var sentinel = new FakeRespServer(PrimaryReply(6379), "*0\r\n"u8.ToArray());
        var options = new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = [new("127.0.0.1", sentinel.Port)], SentinelPrimaryName = "mymaster",
            CommandTimeout = TimeSpan.FromSeconds(5), ConnectTimeout = TimeSpan.FromMilliseconds(200),
        };
        var pending = SentinelResolver.ResolveAndConnectPrimaryAsync<int>(options, async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 0;
        }, CancellationToken.None).AsTask();
        var error = await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(10)))
            .Throws<RespireConnectionException>();
        await Assert.That(error!.InnerException is RespireTimeoutException).IsTrue();
        var timeout = (RespireTimeoutException)error.InnerException!;
        await Assert.That(timeout.CommandName).IsEqualTo("CONNECT");
        await Assert.That(timeout.Timeout).IsEqualTo(TimeSpan.FromMilliseconds(200));
    }

    [Test]
    public async Task Discovery_PeerDiscoveryTimeoutDoesNotReclassifyPrimaryConnectTimeout()
    {
        // Optional peer discovery hits the discovery deadline after the primary reply arrived.
        // The later primary connection deadline must still surface as CONNECT.
        await using var sentinel = new FakeRespServer(PrimaryReply(6379), "*0\r\n"u8.ToArray())
        {
            SuppressReply = command => command == "SENTINEL SENTINELS mymaster",
        };
        var options = new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = [new("127.0.0.1", sentinel.Port)], SentinelPrimaryName = "mymaster",
            CommandTimeout = TimeSpan.FromMilliseconds(200), ConnectTimeout = TimeSpan.FromMilliseconds(300),
        };
        var pending = SentinelResolver.ResolveAndConnectPrimaryAsync<int>(options, async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 0;
        }, CancellationToken.None).AsTask();
        var error = await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(10)))
            .Throws<RespireConnectionException>();
        await Assert.That(error!.InnerException is RespireTimeoutException).IsTrue();
        var timeout = (RespireTimeoutException)error.InnerException!;
        await Assert.That(timeout.CommandName).IsEqualTo("CONNECT");
        await Assert.That(timeout.Timeout).IsEqualTo(TimeSpan.FromMilliseconds(300));
    }

    private static byte[] PeersReply(int port)
        => Encoding.ASCII.GetBytes($"*1\r\n*4\r\n$2\r\nip\r\n$9\r\n127.0.0.1\r\n$4\r\nport\r\n${port.ToString().Length}\r\n{port}\r\n");

    [Test]
    public async Task DiscoveryStateRetainsSeedsBoundsPeersAndReturnsOwnedSnapshots()
    {
        var state = new SentinelDiscoveryState([new("seed.example", 26379), new("SEED.example", 26379)]);
        await Assert.That(state.TryAdd(new("Seed.Example", 26379))).IsFalse();
        for (var index = 0; index < SentinelDiscoveryState.MaximumDiscoveredEndpoints; index++)
            await Assert.That(state.TryAdd(new("peer.example", 10000 + index))).IsTrue();
        await Assert.That(state.TryAdd(new("overflow.example", 26379))).IsFalse();
        var snapshot = state.Snapshot();
        await Assert.That(snapshot.Length).IsEqualTo(1 + SentinelDiscoveryState.MaximumDiscoveredEndpoints);
        snapshot[0] = new("changed.example", 1);
        await Assert.That(state.Snapshot()[0]).IsEqualTo(new RespireEndpoint("seed.example", 26379));
    }

    [Test]
    public async Task DiscoveryIgnoresMalformedAndDuplicatePeerRecords()
    {
        var records = "*5\r\n" + Record("", "26379") + Record("bad host", "26379")
            + Record("peer.example", "65536") + Record("peer.example", "26379") + Record("PEER.example", "26379");
        await using var seed = new FakeRespServer(PrimaryReply(6379), Encoding.ASCII.GetBytes(records));
        var endpoint = new RespireEndpoint("127.0.0.1", seed.Port);
        var state = new SentinelDiscoveryState([endpoint]);
        var options = new RespireOptions { Protocol = RespProtocol.Resp2, Endpoints = [endpoint], SentinelPrimaryName = "mymaster" };
        var result = await SentinelResolver.ResolveAndConnectPrimaryAsync(options,
            static (primaryOptions, _) => ValueTask.FromResult(primaryOptions.PrimaryEndpoint), default, state);
        await Assert.That(result).IsEqualTo(new RespireEndpoint("127.0.0.1", 6379));
        await Assert.That(state.Snapshot()).IsEquivalentTo([endpoint, new RespireEndpoint("peer.example", 26379)]);

        static string Record(string host, string port)
            => $"*4\r\n$2\r\nip\r\n${host.Length}\r\n{host}\r\n$4\r\nport\r\n${port.Length}\r\n{port}\r\n";
    }

    [Test]
    public async Task ConnectAsync_CancellationDuringRolePreservesCallerTokenAndDisposesCandidate()
    {
        var roleRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var primary = new FakeRespServer(PrimaryRole)
        {
            SuppressReply = command => { if (command == "ROLE") roleRequested.TrySetResult(); return true; },
        };
        await using var sentinel = new FakeRespServer(PrimaryReply(primary.Port), "*0\r\n"u8.ToArray());
        using var cancellation = new CancellationTokenSource();
        var pending = RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = [new("127.0.0.1", sentinel.Port)], SentinelPrimaryName = "mymaster", DisableSentinelEventMonitoring = true,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        }, cancellation.Token).AsTask();
        await roleRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var failure = await Assert.That(async () => await pending).Throws<OperationCanceledException>();
        await Assert.That(failure!.CancellationToken).IsEqualTo(cancellation.Token);
        await primary.PeerClosed.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task ConnectAsync_RoleTimeoutTriesNextConfiguredSentinel()
    {
        await using var stalled = new FakeRespServer(PrimaryRole) { SuppressReply = _ => true };
        await using var primary = new FakeRespServer(PrimaryRole, FakeRespServer.PongReply);
        await using var first = new FakeRespServer(PrimaryReply(stalled.Port), "*0\r\n"u8.ToArray());
        await using var second = new FakeRespServer(PrimaryReply(primary.Port), "*0\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = [new("127.0.0.1", first.Port), new("127.0.0.1", second.Port)],
            SentinelPrimaryName = "mymaster", DisableSentinelEventMonitoring = true, CommandTimeout = null, ConnectTimeout = TimeSpan.FromSeconds(2),
        }).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await client.PingAsync();
        await stalled.PeerClosed.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(["ROLE", "PING"]);
    }

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

        await Assert.That(options.Protocol).IsEqualTo(RespProtocol.Resp2);
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
            Protocol = RespProtocol.Resp2,
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
        await using var primary = new FakeRespServer(PrimaryRole, FakeRespServer.PongReply);
        await using var sentinel = new FakeRespServer(PrimaryReply(primary.Port));

        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = { new RespireEndpoint("127.0.0.1", sentinel.Port) },
            SentinelPrimaryName = "mymaster", DisableSentinelEventMonitoring = true,
            ConnectTimeout = TimeSpan.FromSeconds(1),
        });

        _ = await client.PingAsync();

        await Assert.That(sentinel.ReceivedCommands).IsEquivalentTo(
            ["SENTINEL GET-MASTER-ADDR-BY-NAME mymaster", "SENTINEL SENTINELS mymaster"]);
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(["ROLE", "PING"]);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ConnectAsync_UsesSentinelCredentialsForDiscovery(bool useProviders)
    {
        await using var primary = new FakeRespServer(
            FakeRespServer.OkReply,
            PrimaryRole,
            FakeRespServer.PongReply);
        await using var sentinel = new FakeRespServer(
            FakeRespServer.OkReply,
            PrimaryReply(primary.Port));

        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = { new RespireEndpoint("127.0.0.1", sentinel.Port) },
            SentinelPrimaryName = "mymaster", DisableSentinelEventMonitoring = true,
            Username = "redis-user",
            Password = "redis-secret",
            SentinelUsername = "sentinel-user",
            SentinelPassword = "sentinel-secret",
            CredentialProvider = useProviders ? new FixedCredentials("redis-user", "redis-secret") : null,
            SentinelCredentialProvider = useProviders ? new FixedCredentials("sentinel-user", "sentinel-secret") : null,
            ConnectTimeout = TimeSpan.FromSeconds(1),
        });

        _ = await client.PingAsync();

        await Assert.That(sentinel.ReceivedCommands).IsEquivalentTo(
        [
            "AUTH sentinel-user sentinel-secret",
            "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster",
            "SENTINEL SENTINELS mymaster",
        ], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(
        [
            "AUTH redis-user redis-secret",
            "ROLE",
            "PING",
        ], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    private sealed class FixedCredentials(string username, string password) : IRespireCredentialProvider
    {
        public ValueTask<RespireCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new RespireCredentials(username, password));
    }

    [Test]
    public async Task ConnectAsync_EmptySentinelPasswordDisablesInheritedAuthentication()
    {
        await using var primary = new FakeRespServer(
            FakeRespServer.OkReply,
            PrimaryRole,
            FakeRespServer.PongReply);
        await using var sentinel = new FakeRespServer(PrimaryReply(primary.Port));

        await using var client = await RespireClient.ConnectAsync(
            $"redis://:redis-secret@127.0.0.1:{sentinel.Port}?serviceName=mymaster&sentinelPassword=&protocol=2");

        _ = await client.PingAsync();

        await Assert.That(sentinel.ReceivedCommands).IsEquivalentTo(
            ["SENTINEL GET-MASTER-ADDR-BY-NAME mymaster", "SENTINEL SENTINELS mymaster"]);
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(
        [
            "AUTH redis-secret",
            "ROLE",
            "PING",
        ], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ConnectAsync_FallsBackWhenSentinelReturnsInvalidPort(bool useConnectionString)
    {
        await using var primary = new FakeRespServer(PrimaryRole, FakeRespServer.PongReply);
        await using var invalidSentinel = new FakeRespServer(PrimaryReply(65536));
        await using var validSentinel = new FakeRespServer(PrimaryReply(primary.Port));

        var options = new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints =
            {
                new RespireEndpoint("127.0.0.1", invalidSentinel.Port),
                new RespireEndpoint("127.0.0.1", validSentinel.Port),
            },
            SentinelPrimaryName = "mymaster", DisableSentinelEventMonitoring = true,
            ConnectTimeout = TimeSpan.FromSeconds(1),
        };
        if (useConnectionString)
        {
            options = RespireOptions.Parse(
                $"127.0.0.1:{invalidSentinel.Port},127.0.0.1:{validSentinel.Port},serviceName=mymaster,connectTimeout=1000,protocol=2");
        }
        await using var client = await RespireClient.ConnectAsync(options);

        _ = await client.PingAsync();

        await Assert.That(invalidSentinel.ReceivedCommands).IsEquivalentTo(
            ["SENTINEL GET-MASTER-ADDR-BY-NAME mymaster", "SENTINEL SENTINELS mymaster"]);
        await Assert.That(validSentinel.ReceivedCommands).IsEquivalentTo(
            ["SENTINEL GET-MASTER-ADDR-BY-NAME mymaster", "SENTINEL SENTINELS mymaster"]);
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(["ROLE", "PING"]);
    }

    [Test]
    public async Task ConnectAsync_TimesOutUnresponsiveSentinelAndFallsBack()
    {
        await using var primary = new FakeRespServer(PrimaryRole, FakeRespServer.PongReply);
        await using var unresponsiveSentinel = new FakeRespServer(PrimaryReply(primary.Port));
        // Never race a delayed successful reply against the discovery deadline.
        unresponsiveSentinel.SuppressReply = static _ => true;
        await using var responsiveSentinel = new FakeRespServer(PrimaryReply(primary.Port));

        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints =
            {
                new RespireEndpoint("127.0.0.1", unresponsiveSentinel.Port),
                new RespireEndpoint("127.0.0.1", responsiveSentinel.Port),
            },
            SentinelPrimaryName = "mymaster", DisableSentinelEventMonitoring = true,
            // Exercise the discovery timeout itself, without a competing command watchdog.
            // Healthy fallback connections need scheduling headroom on parallel CI runners.
            CommandTimeout = null,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        }).AsTask().WaitAsync(TimeSpan.FromSeconds(20));

        _ = await client.PingAsync();

        await Assert.That(unresponsiveSentinel.ReceivedCommands).IsEquivalentTo(
            ["SENTINEL GET-MASTER-ADDR-BY-NAME mymaster"]);
        await Assert.That(responsiveSentinel.ReceivedCommands).IsEquivalentTo(
            ["SENTINEL GET-MASTER-ADDR-BY-NAME mymaster", "SENTINEL SENTINELS mymaster"]);
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(["ROLE", "PING"]);
    }

    [Test]
    [NotInParallel]
    public async Task ConnectAsync_GivesPrimaryFreshConnectTimeoutAfterSlowDiscovery()
    {
        await using var primary = new FakeRespServer(
            FakeRespServer.OkReply,
            PrimaryRole,
            FakeRespServer.PongReply);
        primary.DelayReply(0, 1_200);
        await using var sentinel = new FakeRespServer(PrimaryReply(primary.Port));
        sentinel.DelayReply(0, 1_200);

        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = { new RespireEndpoint("127.0.0.1", sentinel.Port) },
            SentinelPrimaryName = "mymaster", DisableSentinelEventMonitoring = true,
            Password = "redis-secret",
            SentinelPassword = string.Empty,
            // Each phase fits, but their combined 2.4 seconds exceeds the 2-second discovery budget.
            // Run alone so parallel test load cannot consume a phase's scheduling headroom.
            // Reusing discovery's token for the primary connection would therefore still fail.
            CommandTimeout = TimeSpan.FromSeconds(2),
            ConnectTimeout = TimeSpan.FromSeconds(3),
        });

        _ = await client.PingAsync();

        await Assert.That(sentinel.ReceivedCommands).IsEquivalentTo(
            ["SENTINEL GET-MASTER-ADDR-BY-NAME mymaster", "SENTINEL SENTINELS mymaster"]);
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(
        [
            "AUTH redis-secret",
            "ROLE",
            "PING",
        ], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    [NotInParallel]
    public async Task ConnectAsync_DoesNotLabelPrimaryConnectTimeoutAsDiscoveryTimeout()
    {
        await using var primary = new FakeRespServer(PrimaryRole, FakeRespServer.PongReply);
        primary.DelayReply(0, 4_000);
        await using var sentinel = new FakeRespServer(PrimaryReply(primary.Port));

        var error = await Assert.That(async () => await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = { new RespireEndpoint("127.0.0.1", sentinel.Port) },
            SentinelPrimaryName = "mymaster",
            CommandTimeout = TimeSpan.FromSeconds(2),
            ConnectTimeout = TimeSpan.FromSeconds(3),
        })).ThrowsExactly<RespireTimeoutException>();

        await Assert.That(error!.CommandName).IsNotEqualTo("SENTINEL GET-MASTER-ADDR-BY-NAME");
    }

    [Test]
    public async Task ConnectAsync_FallsBackWhenSentinelReportsUnreachablePrimary()
    {
        using var unavailablePrimary = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        unavailablePrimary.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var unavailablePrimaryPort = ((IPEndPoint)unavailablePrimary.LocalEndPoint!).Port;
        await using var primary = new FakeRespServer(PrimaryRole, FakeRespServer.PongReply);
        await using var staleSentinel = new FakeRespServer(PrimaryReply(unavailablePrimaryPort));
        await using var currentSentinel = new FakeRespServer(PrimaryReply(primary.Port));

        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints =
            {
                new RespireEndpoint("127.0.0.1", staleSentinel.Port),
                new RespireEndpoint("127.0.0.1", currentSentinel.Port),
            },
            SentinelPrimaryName = "mymaster", DisableSentinelEventMonitoring = true,
            ConnectTimeout = TimeSpan.FromSeconds(1),
        });

        _ = await client.PingAsync();

        await Assert.That(staleSentinel.ReceivedCommands).IsEquivalentTo(
            ["SENTINEL GET-MASTER-ADDR-BY-NAME mymaster", "SENTINEL SENTINELS mymaster"]);
        await Assert.That(currentSentinel.ReceivedCommands).IsEquivalentTo(
            ["SENTINEL GET-MASTER-ADDR-BY-NAME mymaster", "SENTINEL SENTINELS mymaster"]);
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(["ROLE", "PING"]);
    }

    [Test]
    public async Task Create_DoesNotContactSentinelBeforeFirstOperation()
    {
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = { new RespireEndpoint("127.0.0.1", 26379) },
            SentinelPrimaryName = "mymaster", DisableSentinelEventMonitoring = true,
        });

        await Assert.That(client.IsConnected).IsFalse();
    }

    [Test]
    public async Task SwitchMasterEventDiscoversAndPublishesValidatedPrimary()
    {
        await using var first = new FakeRespServer(PrimaryRole, FakeRespServer.PongReply);
        await using var replacement = new FakeRespServer(PrimaryRole, FakeRespServer.PongReply);
        var switched = 0;
        await using var sentinel = new FakeRespServer(16, PrimaryReply(first.Port), "*0\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) =>
            {
                if (command.StartsWith("SENTINEL GET-MASTER-ADDR-BY-NAME ", StringComparison.Ordinal))
                    return PrimaryReply(Volatile.Read(ref switched) == 0 ? first.Port : replacement.Port);
                if (command == "SENTINEL SENTINELS mymaster") return "*0\r\n"u8.ToArray();
                if (command.StartsWith("SUBSCRIBE ", StringComparison.Ordinal))
                {
                    var channel = command["SUBSCRIBE ".Length..];
                    return Encoding.ASCII.GetBytes($"*3\r\n$9\r\nsubscribe\r\n${channel.Length}\r\n{channel}\r\n:1\r\n");
                }
                return null;
            },
        };

        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = [new("127.0.0.1", sentinel.Port)],
            SentinelPrimaryName = "mymaster",
            ConnectTimeout = TimeSpan.FromSeconds(5),
            DisableSentinelEventMonitoring = false,
        });
        await client.PingAsync();
        await WaitUntilAsync(() => sentinel.ReceivedCommands.Count(command => command.StartsWith("SUBSCRIBE ", StringComparison.Ordinal)) == 3);
        await Task.Delay(50);
        var subscribeIndex = sentinel.ReceivedCommands.ToList().FindIndex(command => command.StartsWith("SUBSCRIBE ", StringComparison.Ordinal));
        var monitorConnection = sentinel.ReceivedConnectionIds[subscribeIndex];
        await sentinel.SendRawAsync(SwitchMasterMessage("othermaster", first.Port, replacement.Port), monitorConnection);
        await Task.Delay(50);
        await Assert.That(first.ReceivedCommands.Count(command => command == "ROLE")).IsEqualTo(1);
        Volatile.Write(ref switched, 1);
        await sentinel.SendRawAsync(SwitchMasterMessage("mymaster", first.Port, replacement.Port), monitorConnection);

        await WaitUntilAsync(() => replacement.ReceivedCommands.Contains("ROLE"));
        await client.PingAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(replacement.ReceivedCommands).IsEquivalentTo(["ROLE", "PING"]);
        await Assert.That(first.ReceivedCommands).IsEquivalentTo(["ROLE", "PING"]);
        await sentinel.SendRawAsync(SwitchMasterMessage("mymaster", replacement.Port, replacement.Port), monitorConnection);
        await Task.Delay(50);
        await Assert.That(replacement.ReceivedCommands.Count(command => command == "ROLE")).IsEqualTo(1);
    }

    [Test]
    [NotInParallel]
    public async Task MonitorSubscriptionReconcilesPrimaryAndIgnoresStaleSwitchEvent()
    {
        await using var previous = new FakeRespServer(PrimaryRole, FakeRespServer.PongReply);
        await using var replacement = new FakeRespServer(PrimaryRole, FakeRespServer.PongReply);
        await using var staleSentinel = CreateSentinel(() => previous.Port);
        await using var reportingSentinel = CreateSentinel(() => replacement.Port);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = [new("127.0.0.1", staleSentinel.Port), new("127.0.0.1", reportingSentinel.Port)],
            SentinelPrimaryName = "mymaster",
            ConnectTimeout = TimeSpan.FromSeconds(5),
        });
        await client.PingAsync();
        await WaitUntilAsync(() => staleSentinel.ReceivedCommands.Count(command => command == "SUBSCRIBE +switch-master") == 1
            && reportingSentinel.ReceivedCommands.Count(command => command == "SUBSCRIBE +switch-master") == 1);
        await WaitUntilAsync(() => replacement.ReceivedCommands.Contains("ROLE"));
        await client.PingAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var reportingLookups = reportingSentinel.ReceivedCommands.Count(command =>
            command == "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster");
        var staleLookups = staleSentinel.ReceivedCommands.Count(command =>
            command == "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster");
        var commandIndex = reportingSentinel.ReceivedCommands.ToList()
            .FindIndex(command => command == "SUBSCRIBE +switch-master");
        await reportingSentinel.SendRawAsync(SwitchMasterMessage("mymaster", previous.Port, 6390),
            reportingSentinel.ReceivedConnectionIds[commandIndex]);
        await Task.Delay(50);
        await Assert.That(client.Core.Sentinel!.Current!.Endpoint).IsEqualTo(new RespireEndpoint("127.0.0.1", replacement.Port));
        await Assert.That(reportingSentinel.ReceivedCommands.Count(command =>
            command == "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster")).IsEqualTo(reportingLookups);
        await Assert.That(staleSentinel.ReceivedCommands.Count(command =>
            command == "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster")).IsEqualTo(staleLookups);
    }

    [Test]
    [NotInParallel]
    public async Task DuplicateSwitchEventsFromSeveralSentinelsReplacePrimaryOnce()
    {
        await using var first = CreatePrimary();
        await using var replacement = CreatePrimary();
        var switched = 0;
        Func<int> primaryPort = () => Volatile.Read(ref switched) == 0 ? first.Port : replacement.Port;
        await using var sentinelA = CreateSentinel(primaryPort);
        await using var sentinelB = CreateSentinel(primaryPort);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = [new("127.0.0.1", sentinelA.Port), new("127.0.0.1", sentinelB.Port)],
            SentinelPrimaryName = "mymaster",
            ConnectTimeout = TimeSpan.FromSeconds(5),
        });
        await client.PingAsync();
        await WaitUntilAsync(() => sentinelA.ReceivedCommands.Count(command => command == "SUBSCRIBE +switch-master") == 1
            && sentinelB.ReceivedCommands.Count(command => command == "SUBSCRIBE +switch-master") == 1);
        // Subscription reconciliations must finish before the failover so only switch
        // refreshes connect to the replacement.
        await WaitUntilQuietAsync(() => sentinelA.ReceivedCommands.Count + sentinelB.ReceivedCommands.Count + first.ReceivedCommands.Count);
        var monitorA = sentinelA.ReceivedConnectionIds[sentinelA.ReceivedCommands.ToList()
            .FindIndex(command => command == "SUBSCRIBE +switch-master")];
        var monitorB = sentinelB.ReceivedConnectionIds[sentinelB.ReceivedCommands.ToList()
            .FindIndex(command => command == "SUBSCRIBE +switch-master")];

        Volatile.Write(ref switched, 1);
        var message = SwitchMasterMessage("mymaster", first.Port, replacement.Port);
        await Task.WhenAll(sentinelA.SendRawAsync(message, monitorA), sentinelB.SendRawAsync(message, monitorB));

        await WaitUntilAsync(() => client.Core.Sentinel!.Current is { IsRetired: false } current
            && current.Endpoint.Port == replacement.Port);
        await client.PingAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilQuietAsync(() => sentinelA.ReceivedCommands.Count + sentinelB.ReceivedCommands.Count + replacement.ReceivedCommands.Count);
        await Assert.That(replacement.ReceivedCommands.Count(command => command == "ROLE")).IsEqualTo(1);
        await Assert.That(client.Core.Sentinel!.Current!.IsRetired).IsFalse();
    }

    [Test]
    public async Task MonitorDisposalDoesNotWaitForCommandTimeoutOnUnsubscribe()
    {
        await using var primary = CreatePrimary();
        await using var sentinel = CreateSentinel(() => primary.Port);
        var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = [new("127.0.0.1", sentinel.Port)],
            SentinelPrimaryName = "mymaster",
            CommandTimeout = TimeSpan.FromMinutes(1),
            ConnectTimeout = TimeSpan.FromSeconds(2),
        });
        await client.PingAsync();
        await WaitUntilAsync(() => sentinel.ReceivedCommands.Count(command => command == "SUBSCRIBE +switch-master") == 1);
        sentinel.SuppressReply = command => command.StartsWith("UNSUBSCRIBE ", StringComparison.Ordinal);

        await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task MonitorDisposalIsBoundedWhenCommandTimeoutIsDisabled()
    {
        await using var primary = new FakeRespServer(PrimaryRole, FakeRespServer.PongReply);
        await using var sentinel = CreateSentinel(() => primary.Port);
        var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = [new("127.0.0.1", sentinel.Port)],
            SentinelPrimaryName = "mymaster",
            CommandTimeout = null,
            ConnectTimeout = TimeSpan.FromSeconds(2),
        });
        await client.PingAsync();
        await WaitUntilAsync(() => sentinel.ReceivedCommands.Count(command => command == "SUBSCRIBE +switch-master") == 1);
        sentinel.SuppressReply = command => command.StartsWith("UNSUBSCRIBE ", StringComparison.Ordinal);

        var started = Stopwatch.GetTimestamp();
        await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5)).IsTrue();
    }

    private static FakeRespServer CreateSentinel(Func<int> primaryPort)
        => new FakeRespServer(16, "*0\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) =>
            {
                if (command == "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster") return PrimaryReply(primaryPort());
                if (command == "SENTINEL SENTINELS mymaster") return "*0\r\n"u8.ToArray();
                if (command.StartsWith("SUBSCRIBE ", StringComparison.Ordinal))
                {
                    var channel = command["SUBSCRIBE ".Length..];
                    return Encoding.ASCII.GetBytes($"*3\r\n$9\r\nsubscribe\r\n${channel.Length}\r\n{channel}\r\n:1\r\n");
                }
                return null;
            },
        };

    private static FakeRespServer CreatePrimary()
        => new FakeRespServer(16, PrimaryRole, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "ROLE" => PrimaryRole,
                "PING" => FakeRespServer.PongReply,
                _ => null,
            },
        };

    private static async Task WaitUntilQuietAsync(Func<int> activity)
    {
        var last = activity();
        var quietSince = Stopwatch.GetTimestamp();
        for (var attempt = 0; attempt < 400; attempt++)
        {
            await Task.Delay(25);
            var current = activity();
            if (current != last)
            {
                last = current;
                quietSince = Stopwatch.GetTimestamp();
            }
            else if (Stopwatch.GetElapsedTime(quietSince) >= TimeSpan.FromMilliseconds(500)) return;
        }
        throw new TimeoutException("Sentinel activity did not settle.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        throw new TimeoutException("The expected Sentinel monitor state was not reached.");
    }

    private static byte[] PrimaryReply(int port)
        => Encoding.ASCII.GetBytes($"*2\r\n$9\r\n127.0.0.1\r\n${port.ToString().Length}\r\n{port}\r\n");

    private static byte[] SwitchMasterMessage(string service, int oldPort, int newPort)
    {
        var details = $"{service} 127.0.0.1 {oldPort} 127.0.0.1 {newPort}";
        return Encoding.ASCII.GetBytes(
            $"*3\r\n$7\r\nmessage\r\n$14\r\n+switch-master\r\n${details.Length}\r\n{details}\r\n");
    }
}
