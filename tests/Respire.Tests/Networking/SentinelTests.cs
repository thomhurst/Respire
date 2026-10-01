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
            SentinelPrimaryName = "mymaster",
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
            Endpoints = [new("127.0.0.1", seed.Port)], SentinelPrimaryName = "mymaster",
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
            Endpoints = [new("127.0.0.1", sentinel.Port)], SentinelPrimaryName = "mymaster",
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
            Endpoints = [new("127.0.0.1", sentinel.Port)], SentinelPrimaryName = "mymaster",
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
            Endpoints = [new("127.0.0.1", sentinel.Port)], SentinelPrimaryName = "mymaster",
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
            Endpoints = [new("127.0.0.1", sentinel.Port)], SentinelPrimaryName = "mymaster",
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
            SentinelPrimaryName = "mymaster", CommandTimeout = null, ConnectTimeout = TimeSpan.FromSeconds(2),
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
            SentinelPrimaryName = "mymaster",
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
            SentinelPrimaryName = "mymaster",
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
            SentinelPrimaryName = "mymaster",
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
            SentinelPrimaryName = "mymaster",
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
            SentinelPrimaryName = "mymaster",
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
            SentinelPrimaryName = "mymaster",
        });

        await Assert.That(client.IsConnected).IsFalse();
    }

    private static byte[] PrimaryReply(int port)
        => Encoding.ASCII.GetBytes($"*2\r\n$9\r\n127.0.0.1\r\n${port.ToString().Length}\r\n{port}\r\n");
}
