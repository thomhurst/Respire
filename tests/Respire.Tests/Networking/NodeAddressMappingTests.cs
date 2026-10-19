using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using Respire.Commands;
using Respire.Infrastructure;
using Respire.Internal;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class NodeAddressMappingTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task ConnectionHookSeesMappedDestinationOnce(bool reject, bool factory)
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        var source = new RespireEndpoint("private.internal", 6379);
        var target = new RespireEndpoint("127.0.0.1", server.Port);
        var calls = 0;
        var hook = new MappingHook(target, reject);
        var options = new RespireConnectionOptions
        {
            Protocol = RespireProtocol.Resp2, ConnectionHook = hook,
            ConnectionFactory = factory ? async (endpoint, token) =>
            {
                if (endpoint != target) throw new InvalidOperationException("Factory destination differs from hook destination.");
                return await OpenStreamAsync(endpoint, token);
            } : null,
            NodeAddressResolver = endpoint =>
            {
                Interlocked.Increment(ref calls);
                if (endpoint != source) throw new InvalidOperationException("Destination resolved twice.");
                return target;
            },
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        if (reject)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await using var connection = await RespireConnection.ConnectAsync(source.Host, source.Port, options,
                    cancellationToken: deadline.Token);
            });
            await Assert.That(server.ConnectionAccepted.IsCompleted).IsFalse();
        }
        else
        {
            await using var connection = await RespireConnection.ConnectAsync(source.Host, source.Port,
                options, cancellationToken: deadline.Token);
            using var reply = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame), deadline.Token);
            await Assert.That(reply.AsString()).IsEqualTo("PONG");
            await Assert.That(connection.Host).IsEqualTo(source.Host);
        }
        await Assert.That(hook.Started!.Endpoint).IsEqualTo(target);
        await Assert.That(hook.Completed).IsSameReferenceAs(hook.Started);
        await Assert.That(calls).IsEqualTo(1);
    }

    private static async ValueTask<Stream> OpenStreamAsync(RespireEndpoint endpoint, CancellationToken token)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(endpoint.Host, endpoint.Port, token);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SentinelClientsKeepMappedTransport(bool callback)
    {
        await using var sentinel = new FakeRespServer(4, "*0\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command switch
            {
                "INFO SERVER" => "$0\r\n\r\n"u8.ToArray(),
                "PING" => FakeRespServer.PongReply,
                _ => null,
            },
        };
        var source = new RespireEndpoint("sentinel.internal", 26379);
        var target = new RespireEndpoint("127.0.0.1", sentinel.Port);
        var options = new RespireOptions
        {
            NodeAddressMap = callback ? null : new Dictionary<RespireEndpoint, RespireEndpoint> { [source] = target },
            NodeAddressResolver = callback ? endpoint => endpoint == source ? target : endpoint : null,
            UseMappedHostForTls = true,
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var monitorOptions = SentinelMonitoring.CreateOptions(options, source);
        await Assert.That(monitorOptions.UseMappedHostForTls).IsTrue();
        await using var monitor = await RespireClient.ConnectAsync(monitorOptions, deadline.Token);
        await monitor.PingAsync(deadline.Token);
        await using var admin = await RespireSentinelClient.ConnectAsync(source, options, deadline.Token);
        await Assert.That(admin.Endpoint).IsEqualTo(source);
        await Assert.That(await admin.PrimariesAsync(deadline.Token)).IsEmpty();
    }

    private sealed class MappingHook(RespireEndpoint expected, bool reject) : RespireConnectionHook
    {
        internal RespireConnectionContext? Started;
        internal RespireConnectionContext? Completed;
        public override ValueTask OnConnectingAsync(RespireConnectionContext context, CancellationToken cancellationToken)
        {
            Started = context;
            if (context.Endpoint != expected || reject) throw new InvalidOperationException("Endpoint rejected.");
            return default;
        }
        public override ValueTask OnCompletedAsync(RespireConnectionContext context, RespireConnectionOutcome outcome,
            TimeSpan elapsed, CancellationToken cancellationToken)
        {
            Completed = context;
            return default;
        }
    }

    [Test]
    [Arguments(false, false, false, false)]
    [Arguments(true, false, false, false)]
    [Arguments(true, true, false, false)]
    [Arguments(false, false, true, false)]
    [Arguments(true, false, true, false)]
    [Arguments(true, true, true, false)]
    [Arguments(false, false, false, true)]
    [Arguments(true, false, false, true)]
    [Arguments(true, true, false, true)]
    [Arguments(false, false, true, true)]
    [Arguments(true, false, true, true)]
    [Arguments(true, true, true, true)]
    public async Task SentinelRefreshReconnectsMappedAnnouncement(bool callback, bool identityResolver, bool factory, bool epoch)
    {
        static FakeRespServer Primary() => new(16, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "ROLE" ? "*3\r\n+master\r\n:0\r\n*0\r\n"u8.ToArray() : null,
        };
        await using var first = Primary();
        await using var second = Primary();
        var port = first.Port;
        var announced = identityResolver ? new RespireEndpoint("localhost", first.Port) : new("private.internal", 6379);
        await using var sentinel = new FakeRespServer(32, "*0\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command switch
            {
                "SENTINEL MASTER mymaster" when epoch => Encoding.ASCII.GetBytes($"*6\r\n+ip\r\n+{announced.Host}\r\n+port\r\n+{announced.Port}\r\n+config-epoch\r\n+42\r\n"),
                "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster" => Encoding.ASCII.GetBytes($"*2\r\n+{announced.Host}\r\n+{announced.Port}\r\n"),
                "SUBSCRIBE +switch-master +sdown +odown" => "*3\r\n$9\r\nsubscribe\r\n$14\r\n+switch-master\r\n:1\r\n*3\r\n$9\r\nsubscribe\r\n$6\r\n+sdown\r\n:2\r\n*3\r\n$9\r\nsubscribe\r\n$6\r\n+odown\r\n:3\r\n"u8.ToArray(),
                _ => null,
            },
        };
        var options = new RespireOptions
        {
            Endpoints = [new("127.0.0.1", sentinel.Port)], SentinelPrimaryName = "mymaster",
            Protocol = RespireProtocol.Resp2,
            ConnectionFactory = factory ? OpenStreamAsync : null,
            NodeAddressResolver = callback ? endpoint => endpoint.Host == "private.internal"
                ? new("127.0.0.1", Volatile.Read(ref port)) : endpoint : null,
            NodeAddressMap = callback ? null : new Dictionary<RespireEndpoint, RespireEndpoint>
                { ["private.internal:6379"] = new("127.0.0.1", first.Port) },
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var client = await RespireClient.ConnectAsync(options, deadline.Token);
        await SentinelTestSetup.WaitForStartupAsync(client);
        var router = client.Core.Sentinel!;
        var original = router.Current!;
        var connectionsBefore = first.ReceivedConnectionIds.Distinct().Count();
        if (callback && !identityResolver) Volatile.Write(ref port, second.Port);
        // A static map may target a hostname whose DNS changes. Even a healthy old peer
        // cannot prove that a new connection would still reach that same destination.
        var replacement = await router.GetGenerationAsync(deadline.Token, forceDiscovery: true);
        if (callback && !identityResolver)
        {
            await Assert.That(replacement).IsNotSameReferenceAs(original);
            await Assert.That(original.IsRetired).IsTrue();
        }
        else
        {
            await Assert.That(first.ReceivedConnectionIds.Distinct().Count() > connectionsBefore).IsTrue();
            await Assert.That(replacement).IsSameReferenceAs(original);
        }
        await Assert.That(replacement.ValidatedPeer!.Value.Port).IsEqualTo(callback && !identityResolver ? second.Port : first.Port);
    }

    [Test]
    public async Task MapHostMatchingIgnoresCaseAndKeepsPortsDistinct()
    {
        var map = NodeAddressMapping.Parse("Redis.Internal:6379=localhost:7001;redis.internal:6380=localhost:7002");
        var snapshot = NodeAddressMapping.Snapshot(map)!;
        await Assert.That(snapshot[new("REDIS.INTERNAL", 6379)]).IsEqualTo(new RespireEndpoint("localhost", 7001));
        await Assert.That(snapshot[new("REDIS.INTERNAL", 6380)]).IsEqualTo(new RespireEndpoint("localhost", 7002));
        await Assert.That(() => NodeAddressMapping.Parse("Redis:6379=localhost:7001;redis:6379=localhost:7002"))
            .ThrowsExactly<RespireConfigurationException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SentinelCloneAndFactoryKeepMappedTlsIdentity(bool useMappedHost)
    {
        var expectedHost = useMappedHost ? "localhost" : "sentinel.internal";
        using var certificate = TestTlsCertificate.Create(expectedHost);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = FakeRespServer.StartListener();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string? serverName = null;
        var errors = SslPolicyErrors.None;
        var server = ServeAsync();
        try
        {
            var source = new RespireEndpoint("sentinel.internal", 26379);
            var options = SentinelMonitoring.CreateOptions(new RespireOptions
            {
                ConnectionFactory = OpenStreamAsync,
                NodeAddressMap = new Dictionary<RespireEndpoint, RespireEndpoint> { [source] = new("localhost", port) },
                SentinelUseTls = true,
                UseMappedHostForTls = useMappedHost,
                SentinelTlsOptions = new SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, _, _, observed) => { errors = observed; return true; },
                },
            }, source).ValidateAndSnapshot();
            await using var connection = await RespireConnection.ConnectAsync(source.Host, source.Port,
                options.ToConnectionOptions() with { LibraryName = null }, cancellationToken: deadline.Token);
            using var reply = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame), deadline.Token);
            await Assert.That(reply.AsString()).IsEqualTo("PONG");
            await server.WaitAsync(deadline.Token);
            await Assert.That(serverName).IsEqualTo(expectedHost);
            await Assert.That(connection.TlsHost).IsEqualTo(expectedHost);
            await Assert.That(errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)).IsFalse();
        }
        finally
        {
            await deadline.CancelAsync();
            listener.Stop();
            try { await server; }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        }

        async Task ServeAsync()
        {
            using var socket = await listener.AcceptSocketAsync(deadline.Token);
            await using var stream = new SslStream(new NetworkStream(socket, ownsSocket: false));
            await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificateSelectionCallback = (_, name) => { serverName = name; return certificate; },
            }, deadline.Token);
            await stream.ReadExactlyAsync(new byte[FakeRespServer.PingFrame.Length], deadline.Token);
            await stream.WriteAsync(FakeRespServer.PongReply, deadline.Token);
        }
    }

    [Test]
    [Arguments(false, null, false)]
    [Arguments(true, null, false)]
    [Arguments(false, "explicit.example", false)]
    [Arguments(true, "explicit.example", false)]
    [Arguments(false, null, true)]
    [Arguments(true, null, true)]
    [Arguments(false, "explicit.example", true)]
    [Arguments(true, "explicit.example", true)]
    public async Task TlsKeepsAnnouncedHostnameUnlessMappedHostnameIsSelected(bool useMappedHost, string? explicitHost, bool factory)
    {
        var expectedHost = explicitHost ?? (useMappedHost ? "localhost" : "redis.internal");
        using var certificate = TestTlsCertificate.Create(expectedHost);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = FakeRespServer.StartListener();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverNames = new List<string?>();
        var certificateErrors = new List<SslPolicyErrors>();
        var server = ServeBothAsync();
        var tlsOptions = new SslClientAuthenticationOptions
        {
            TargetHost = explicitHost!,
            RemoteCertificateValidationCallback = (_, _, _, errors) =>
            {
                certificateErrors.Add(errors);
                return true; // The test certificate is self-signed; hostname errors are asserted below.
            },
        };
        try
        {
            var options = new RespireOptions
            {
                Endpoints = [new("redis.internal", 6380)],
                LibraryName = null,
                Protocol = RespireProtocol.Resp2, UseTls = true, TlsOptions = tlsOptions,
                ConnectionFactory = factory ? OpenStreamAsync : null,
                UseMappedHostForTls = useMappedHost,
                NodeAddressMap = new Dictionary<RespireEndpoint, RespireEndpoint> { ["redis.internal:6380"] = new("localhost", port) },
            }.ValidateAndSnapshot();
            await using var primary = await RespireConnectionMultiplexer.CreateAsync("redis.internal", 6380,
                options: options.ToConnectionOptions(), cancellationToken: deadline.Token);
            var connection = primary.GetConnection();
            using var reply = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame), deadline.Token);
            await Assert.That(reply.AsString()).IsEqualTo("PONG");
            await using var router = new ClusterRouter(options with { UseCluster = true }, primary);
            await using var correction = router.GetCorrectionLease(connection);
            var control = await correction.Pool.RentAsync(deadline.Token);
            try
            {
                using var pong = await control.SendAsync(new RawCommand(FakeRespServer.PingFrame), deadline.Token);
                await Assert.That(pong.AsString()).IsEqualTo("PONG");
            }
            finally { correction.Pool.Return(control); }
            await server.WaitAsync(deadline.Token);
            await Assert.That(serverNames).IsEquivalentTo(new string?[] { expectedHost, expectedHost });
            await Assert.That(certificateErrors.Count).IsEqualTo(2);
            foreach (var errors in certificateErrors)
                await Assert.That(errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)).IsFalse();
            await Assert.That(tlsOptions.TargetHost).IsEqualTo(explicitHost);
        }
        finally
        {
            await deadline.CancelAsync();
            listener.Stop();
            try { await server; }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        }

        async Task ServeBothAsync()
        {
            await ServeAsync();
            await ServeAsync();
        }

        async Task ServeAsync()
        {
            using var socket = await listener.AcceptSocketAsync(deadline.Token);
            await using var stream = new SslStream(new NetworkStream(socket, ownsSocket: false));
            await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificateSelectionCallback = (_, name) => { serverNames.Add(name); return certificate; },
            }, deadline.Token);
            var request = new byte[FakeRespServer.PingFrame.Length];
            await stream.ReadExactlyAsync(request, deadline.Token);
            await stream.WriteAsync(FakeRespServer.PongReply, deadline.Token);
        }
    }

    [Test]
    [Arguments("standalone", false)]
    [Arguments("sentinel", false)]
    [Arguments("cluster", false)]
    [Arguments("standalone", true)]
    [Arguments("sentinel", true)]
    [Arguments("cluster", true)]
    public async Task CorrectionConnectionsKeepCapturedPeerWithoutResolvingAgain(string mode, bool factory)
    {
        await using var server = new FakeRespServer(2, FakeRespServer.PongReply);
        var calls = 0;
        var options = new RespireOptions
        {
            Protocol = RespireProtocol.Resp2, Endpoints = [new("internal")],
            ConnectionFactory = factory ? OpenStreamAsync : null,
            NodeAddressResolver = endpoint =>
            {
                Interlocked.Increment(ref calls);
                if (endpoint.Host != "internal") throw new InvalidOperationException("Physical peer was mapped twice.");
                return new("127.0.0.1", server.Port);
            },
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var client = await RespireClient.ConnectAsync(options, deadline.Token);
        var original = client.Core.Multiplexer.GetConnection();
        if (mode == "cluster")
        {
            await using var router = new ClusterRouter(options with { UseCluster = true }, client.Core.Multiplexer);
            await using var lease = router.GetCorrectionLease(original);
            await VerifyAsync(lease.Pool);
        }
        else if (mode == "sentinel")
        {
            await using var router = new SentinelRouter(client.Core);
            await using var lease = router.GetCorrectionLease(original);
            await VerifyAsync(lease.Pool);
        }
        else
        {
            await using var lease = client.Core.GetCorrectionLease(new("internal"), original);
            await VerifyAsync(lease.Pool);
        }
        await Assert.That(calls).IsEqualTo(1);

        async Task VerifyAsync(DedicatedConnectionPool pool)
        {
            var connection = await pool.RentAsync(deadline.Token);
            try
            {
                using var pong = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame), deadline.Token);
                await Assert.That(pong.AsString()).IsEqualTo("PONG");
            }
            finally { pool.Return(connection); }
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FactoryRetirementFenceKeepsCapturedDestination(bool callback)
    {
        await using var originalServer = new FakeRespServer(2, ":42\r\n"u8.ToArray());
        await using var otherServer = new FakeRespServer(2, ":42\r\n"u8.ToArray());
        var source = new RespireEndpoint("private.internal", 6379);
        var target = new RespireEndpoint("127.0.0.1", originalServer.Port);
        var destinations = new List<RespireEndpoint>();
        var calls = 0;
        var options = new RespireConnectionOptions
        {
            Protocol = RespireProtocol.Resp2,
            ConnectionFactory = async (endpoint, token) =>
            {
                destinations.Add(endpoint);
                return await OpenStreamAsync(endpoint, token);
            },
            NodeAddressMap = callback ? null : new Dictionary<RespireEndpoint, RespireEndpoint> { [source] = target },
            NodeAddressResolver = callback ? _ =>
            {
                Interlocked.Increment(ref calls);
                return target;
            } : null,
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var node = await RespireConnectionMultiplexer.CreateAsync(source.Host, source.Port,
            options: options, cancellationToken: deadline.Token);
        var connection = node.GetConnection();
        await connection.EnsureServerClientIdAsync(deadline.Token);
        await Assert.That(connection.Host).IsEqualTo(source.Host);
        await Assert.That(connection.Port).IsEqualTo(source.Port);
        await Assert.That(connection.NetworkPeerAddress).IsNull();
        await Assert.That(connection.NetworkPeerPort).IsNull();
        target = new("127.0.0.1", otherServer.Port);
        await connection.DisposeAsync();
        await node.RetireAsync().WaitAsync(deadline.Token);
        await Assert.That(destinations).IsEquivalentTo(new RespireEndpoint[]
            { new("127.0.0.1", originalServer.Port), new("127.0.0.1", originalServer.Port) });
        await Assert.That(originalServer.ReceivedCommands).Contains("CLIENT KILL ID 42");
        await Assert.That(otherServer.ReceivedCommands).DoesNotContain("CLIENT KILL ID 42");
        await Assert.That(node.HasPendingCorrectionFences).IsFalse();
        await Assert.That(calls).IsEqualTo(callback ? 1 : 0);
    }

    [Test]
    public async Task MovingHandoffMapsAdvertisedTarget()
    {
        static FakeRespServer Server() => new(2, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "HELLO 3" => "%1\r\n+proto\r\n:3\r\n"u8.ToArray(),
                "CLIENT MAINT_NOTIFICATIONS ON" => FakeRespServer.OkReply,
                _ => null,
            },
        };
        await using var source = Server();
        await using var target = Server();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var options = new RespireOptions
        {
            Endpoints = [new("127.0.0.1", source.Port)],
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Enabled,
            NodeAddressMap = new Dictionary<RespireEndpoint, RespireEndpoint> { ["internal:6379"] = new("127.0.0.1", target.Port) },
        }.ValidateAndSnapshot();
        await using var primary = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", source.Port,
            options: options.ToConnectionOptions(enableMaintenanceNotifications: true), cancellationToken: deadline.Token);
        var original = primary.GetConnection();
        await source.SendRawAsync(">4\r\n+MOVING\r\n:1\r\n:10\r\n+internal:6379\r\n"u8.ToArray());
        while (original.IsAcceptingCommands) await Task.Delay(5, deadline.Token);
        using var reply = await original.SendAsync(new RawCommand(FakeRespServer.PingFrame), deadline.Token);
        await Assert.That(reply.AsString()).IsEqualTo("PONG");
        await Assert.That(primary.GetConnection().Host).IsEqualTo("internal");
        await Assert.That(primary.GetConnection().Port).IsEqualTo(6379);
        await Assert.That(target.ReceivedCommands).Contains("PING");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReplicaConnectionsUseMappedAddresses(bool cluster)
    {
        await using var replica = new FakeRespServer(4, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "ROLE" => "*5\r\n+slave\r\n+primary.internal\r\n:6379\r\n+connected\r\n:0\r\n"u8.ToArray(),
                "GET key" => "$7\r\nreplica\r\n"u8.ToArray(),
                _ => null,
            },
        };
        var topology = "*1\r\n*4\r\n:0\r\n:16383\r\n*2\r\n+primary.internal\r\n:6379\r\n*2\r\n+replica.internal\r\n:6380\r\n"u8.ToArray();
        await using var primary = new FakeRespServer(4, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? topology : null,
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespireProtocol.Resp2, UseCluster = cluster,
            Endpoints = [new("primary.internal")],
            ReplicaEndpoints = cluster ? [] : [new("replica.internal", 6380)],
            NodeAddressMap = new Dictionary<RespireEndpoint, RespireEndpoint>
            {
                ["primary.internal:6379"] = new("127.0.0.1", primary.Port),
                ["replica.internal:6380"] = new("127.0.0.1", replica.Port),
            },
        }, deadline.Token);
        var reader = client.WithReadFrom(RespireReadFrom.Replica);
        await Assert.That(await reader.GetStringAsync("key", deadline.Token)).IsEqualTo("replica");
        if (cluster) await Assert.That(replica.ReceivedCommands).Contains("READONLY");
    }

    [Test]
    [Arguments("MOVED")]
    [Arguments("ASK")]
    public async Task ClusterRedirectConnectsAdvertisedAddressThroughMap(string code)
    {
        var slot = Respire.Internal.ClusterHash.GetSlot("mapped-key");
        await using var target = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("GET ", StringComparison.Ordinal)
                ? "$6\r\nmapped\r\n"u8.ToArray() : FakeRespServer.OkReply,
        };
        await using var seed = new FakeRespServer("*0\r\n"u8.ToArray(),
            Encoding.ASCII.GetBytes($"-{code} {slot} internal:6379\r\n"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespireProtocol.Resp2, UseCluster = true,
            Endpoints = [new("127.0.0.1", seed.Port)], ClusterTopologyRefreshInterval = null,
            NodeAddressMap = new Dictionary<RespireEndpoint, RespireEndpoint> { ["internal:6379"] = new("127.0.0.1", target.Port) },
        }, deadline.Token);
        await Assert.That(await client.GetStringAsync("mapped-key", deadline.Token)).IsEqualTo("mapped");
        if (code == "ASK") await Assert.That(target.ReceivedCommands).Contains("ASKING");
    }

    [Test]
    public async Task InvalidCallbackResultFailsBeforeOpeningSocket()
    {
        var options = new RespireOptions
        {
            Endpoints = [new("internal")], NodeAddressResolver = _ => new("localhost", 0),
        }.ValidateAndSnapshot();
        await Assert.That(async () => await RespireConnection.ConnectAsync("internal", 6379, options.ToConnectionOptions()))
            .ThrowsExactly<RespireConfigurationException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SentinelDoesNotResolveMappedPrivateDnsBeforeConnecting(bool callback)
    {
        await using var sentinel = new FakeRespServer(4, "-ERR unsupported\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command.StartsWith("SENTINEL GET-MASTER-ADDR", StringComparison.Ordinal)
                ? "*2\r\n+private.internal\r\n+6379\r\n"u8.ToArray() : null,
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var options = new RespireOptions
        {
            Endpoints = [new("127.0.0.1", sentinel.Port)], SentinelPrimaryName = "mymaster",
            NodeAddressMap = callback ? null : new Dictionary<RespireEndpoint, RespireEndpoint> { ["PRIVATE.INTERNAL:6379"] = "localhost:7001" },
            NodeAddressResolver = callback ? endpoint => endpoint : null,
        }.ValidateAndSnapshot();
        var host = await SentinelResolver.ResolveAndConnectPrimaryAsync(options,
            (resolved, _, _) => ValueTask.FromResult(resolved.PrimaryEndpoint.Host), deadline.Token,
            hostResolver: (_, _) => throw new InvalidOperationException("Private DNS lookup must not delay mapped connections."));
        await Assert.That(host).IsEqualTo("private.internal");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ConnectsMappedSocketAndKeepsLogicalEndpoint(bool callback)
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        var source = new RespireEndpoint("unreachable.internal", 6379);
        var target = new RespireEndpoint("127.0.0.1", server.Port);
        var calls = 0;
        var options = new RespireOptions
        {
            Endpoints = [source],
            Protocol = RespireProtocol.Resp2,
            NodeAddressMap = callback ? null : new Dictionary<RespireEndpoint, RespireEndpoint> { [source] = target },
            NodeAddressResolver = callback ? endpoint =>
            {
                Interlocked.Increment(ref calls);
                return endpoint == source ? target : endpoint;
            } : null,
        }.ValidateAndSnapshot();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var connection = await RespireConnection.ConnectAsync(source.Host, source.Port,
            options.ToConnectionOptions(), cancellationToken: deadline.Token);
        using var reply = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame), deadline.Token);
        await Assert.That(reply.AsString()).IsEqualTo("PONG");
        await Assert.That(connection.Host).IsEqualTo(source.Host);
        await Assert.That(connection.Port).IsEqualTo(source.Port);
        await Assert.That(calls).IsEqualTo(callback ? 1 : 0);
    }

    [Test]
    public async Task SnapshotCopiesMapAndRejectsConflictingResolvers()
    {
        var map = new Dictionary<RespireEndpoint, RespireEndpoint> { ["internal:6379"] = "localhost:7001" };
        var options = new RespireOptions { Endpoints = [new("internal")], NodeAddressMap = map }.ValidateAndSnapshot();
        map.Clear();
        await Assert.That(options.NodeAddressMap!["internal:6379"]).IsEqualTo(new RespireEndpoint("localhost", 7001));
        await Assert.That(() => (options with { NodeAddressResolver = endpoint => endpoint }).ValidateAndSnapshot())
            .ThrowsExactly<RespireConfigurationException>();
    }

    [Test]
    [Arguments("localhost,nodeAddressMap=internal:6379=localhost:7001;[::1]:6380=localhost:7002,useMappedHostForTls=true")]
    [Arguments("redis://localhost?nodeAddressMap=internal%3A6379%3Dlocalhost%3A7001%3B%5B%3A%3A1%5D%3A6380%3Dlocalhost%3A7002&useMappedHostForTls=true")]
    public async Task ParsesStaticMapInBothConnectionStringForms(string text)
    {
        var options = RespireOptions.Parse(text);
        await Assert.That(options.NodeAddressMap!["internal:6379"]).IsEqualTo(new RespireEndpoint("localhost", 7001));
        await Assert.That(options.NodeAddressMap["[::1]:6380"]).IsEqualTo(new RespireEndpoint("localhost", 7002));
        await Assert.That(options.UseMappedHostForTls).IsTrue();
    }

    [Test]
    [Arguments("")]
    [Arguments("internal:6379")]
    [Arguments("internal:6379=")]
    [Arguments("internal:6379=localhost:7001;")]
    [Arguments("internal:6379=localhost:7001;internal:6379=localhost:7002")]
    [Arguments("internal:6379=!/tmp/redis.sock")]
    public async Task RejectsMalformedStaticMap(string value)
        => await Assert.That(() => RespireOptions.Parse("localhost,nodeAddressMap=" + value))
            .ThrowsExactly<RespireConfigurationException>();
}
