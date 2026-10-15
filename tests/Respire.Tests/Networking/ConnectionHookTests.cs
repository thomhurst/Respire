using System.Collections.Concurrent;
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

public class ConnectionHookTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Test]
    public async Task SuccessSurroundsAuthenticationAndSetup()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        var hook = new Hook
        {
            Before = (_, _) =>
            {
                if (server.ReceivedCommands.Count != 0) throw new InvalidOperationException("Transport started before hook.");
                return ValueTask.CompletedTask;
            },
            After = (_, outcome, _) =>
            {
                if (outcome != RespireConnectionOutcome.Succeeded
                    || !server.ReceivedCommands.Contains("SELECT 2"))
                    throw new InvalidOperationException("Completion preceded handshake.");
                return ValueTask.CompletedTask;
            },
        };
        using var cancellation = new CancellationTokenSource(Limit);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)], Connections = 1,
            Protocol = RespireProtocol.Resp2, Password = "secret", Database = 2, ConnectionHook = hook,
        }, cancellation.Token);

        await Assert.That(server.ReceivedCommands.Contains("AUTH secret")).IsTrue();
        var attempt = hook.Completed.Single();
        await Assert.That(ReferenceEquals(hook.Started.Single(), attempt.Context)).IsTrue();
        await Assert.That(attempt.Context.Endpoint).IsEqualTo(new RespireEndpoint("127.0.0.1", server.Port));
        await Assert.That(attempt.Context.UseTls).IsFalse();
        await Assert.That(attempt.Elapsed > TimeSpan.Zero).IsTrue();
    }

    [Test]
    public async Task RejectionPreventsCredentialAndTransportAcquisition()
    {
        var rejection = new InvalidOperationException("Policy rejected endpoint.");
        var provider = new CountingProvider();
        var transports = 0;
        var hook = new Hook { Before = (_, _) => throw rejection };
        var options = new RespireConnectionOptions
        {
            ConnectionHook = hook, CredentialProvider = provider,
            TestingStreamFactory = (_, _, _) => { transports++; throw new InvalidOperationException(); },
        };
        var error = await CaptureAsync(() => RespireConnection.ConnectAsync("blocked", 6379, options));
        await Assert.That(ReferenceEquals(error, rejection)).IsTrue();
        await Assert.That(provider.Calls).IsEqualTo(0);
        await Assert.That(transports).IsEqualTo(0);
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireConnectionOutcome.Failed);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FailureIsReportedWithoutExposingErrorTextOrMaskingOriginal(bool handshake)
    {
        await using var server = new FakeRespServer("-WRONGPASS private-secret\r\n"u8.ToArray());
        var failure = new IOException("private-transport-detail");
        var hook = new Hook { After = (_, _, _) => throw new InvalidOperationException("audit failed") };
        var options = new RespireConnectionOptions
        {
            ConnectionHook = hook, Protocol = RespireProtocol.Resp2,
            CredentialProvider = handshake ? new CountingProvider() : null,
            TestingStreamFactory = handshake ? null : (_, _, _) => throw failure,
        };
        var error = await CaptureAsync(() => RespireConnection.ConnectAsync("127.0.0.1", server.Port, options));
        await Assert.That(handshake ? error is RespireAuthenticationException : ReferenceEquals(error, failure)).IsTrue();
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireConnectionOutcome.Failed);
        if (handshake) await server.PeerClosed.WaitAsync(Limit);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CompletionRejectionOrCancellationDisposesReadyConnection(bool cancel)
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        using var cancellation = new CancellationTokenSource(Limit);
        var rejection = new InvalidOperationException("Rejected ready connection.");
        var hook = new Hook
        {
            After = (_, _, token) =>
            {
                if (token != cancellation.Token) throw new InvalidOperationException("Wrong token.");
                if (!cancel) throw rejection;
                cancellation.Cancel();
                return ValueTask.CompletedTask; // An uncooperative hook cannot publish after cancellation.
            },
        };
        var options = new RespireConnectionOptions { ConnectionHook = hook, ClientName = "hook-test" };
        var error = await CaptureAsync(() => RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            options, cancellationToken: cancellation.Token));
        await Assert.That(cancel ? error is OperationCanceledException canceled && canceled.CancellationToken == cancellation.Token
            : ReferenceEquals(error, rejection)).IsTrue();
        await server.PeerClosed.WaitAsync(Limit);
        await Assert.That(hook.Completed.Count).IsEqualTo(1);
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireConnectionOutcome.Succeeded);
    }

    [Test]
    public async Task CancellationDuringBeforeCallbackDoesNotCreateTransport()
    {
        using var cancellation = new CancellationTokenSource(Limit);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hook = new Hook
        {
            Before = async (_, token) =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
        };
        var connecting = RespireConnection.ConnectAsync("unused", 6379,
            new RespireConnectionOptions { ConnectionHook = hook }, cancellationToken: cancellation.Token);
        await entered.Task.WaitAsync(Limit);
        cancellation.Cancel();
        var error = await CaptureAsync(() => connecting);
        await Assert.That(error is OperationCanceledException canceled && canceled.CancellationToken == cancellation.Token).IsTrue();
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireConnectionOutcome.Canceled);
    }

    [Test]
    public async Task ReconnectInvokesHookAgainAndRecoversAfterHookFailure()
    {
        await using var server = new FakeRespServer(4, FakeRespServer.PongReply);
        var count = 0;
        var hook = new Hook
        {
            Before = (_, _) => Interlocked.Increment(ref count) == 2
                ? ValueTask.FromException(new IOException("Transient policy failure.")) : ValueTask.CompletedTask,
        };
        await using var multiplexer = RespireConnectionMultiplexer.Create("127.0.0.1", server.Port, 1,
            new RespireConnectionOptions
            {
                ConnectionHook = hook,
                ReconnectPolicy = new() { InitialDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero, JitterRatio = 0 },
            });
        using var cancellation = new CancellationTokenSource(Limit);
        await multiplexer.EnsureConnectedAsync(cancellation.Token);
        var original = multiplexer.GetConnection();
        using (var initial = await original.SendAsync(new RawCommand(FakeRespServer.PingFrame), cancellation.Token))
            await Assert.That(initial.AsString()).IsEqualTo("PONG");
        server.CloseConnections();
        await original.Closed.WaitAsync(Limit);
        // Recovery is demand-driven and must remain usable after the hook rejects a replacement.
        var replacement = await multiplexer.GetHealthyConnectionAsync(cancellation.Token);
        using var reply = await replacement.SendAsync(new RawCommand(FakeRespServer.PingFrame), cancellation.Token);
        await Assert.That(reply.AsString()).IsEqualTo("PONG");
        await Assert.That(hook.Completed.Select(attempt => attempt.Outcome).ToArray()).IsEquivalentTo(new[]
        {
            RespireConnectionOutcome.Succeeded, RespireConnectionOutcome.Failed, RespireConnectionOutcome.Succeeded,
        });
        await Assert.That(ReferenceEquals(hook.Started.First(), hook.Started.Last())).IsFalse();
    }

    [Test]
    public async Task DedicatedPoolInvokesHookOnlyForNewConnections()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        var hook = new Hook();
        await using var pool = new DedicatedConnectionPool("127.0.0.1", server.Port,
            new RespireOptions { ConnectionHook = hook, Protocol = RespireProtocol.Resp2 }.ToConnectionOptions(), null);
        var first = await pool.RentAsync(default);
        pool.Return(first);
        var reused = await pool.RentAsync(default);
        await Assert.That(ReferenceEquals(first, reused)).IsTrue();
        pool.Return(reused);
        await Assert.That(hook.Completed.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TopologyConnectionsInheritHook(bool cluster)
    {
        var role = "*5\r\n$5\r\nslave\r\n$9\r\n127.0.0.1\r\n:6379\r\n$9\r\nconnected\r\n:0\r\n"u8.ToArray();
        await using var replica = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "ROLE" => role,
                "GET key" => "$5\r\nvalue\r\n"u8.ToArray(),
                _ => FakeRespServer.OkReply,
            },
        };
        await using var primary = new FakeRespServer(8, FakeRespServer.OkReply);
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*4\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{primary.Port}\r\n" +
            $"*2\r\n$9\r\n127.0.0.1\r\n:{replica.Port}\r\n");
        primary.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? topology : FakeRespServer.OkReply;
        var hook = new Hook();
        using var cancellation = new CancellationTokenSource(Limit);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", primary.Port)], Connections = 1, Protocol = RespireProtocol.Resp2,
            UseCluster = cluster, ClusterTopologyRefreshInterval = null, ConnectionHook = hook,
            ReplicaEndpoints = cluster ? [] : [new("127.0.0.1", replica.Port)],
        }, cancellation.Token);
        var value = await client.WithReadFrom(RespireReadFrom.Replica).GetStringAsync("key", cancellation.Token);
        await Assert.That(value).IsEqualTo("value");
        await Assert.That(hook.Completed.Any(attempt => attempt.Context.Endpoint.Port == primary.Port)).IsTrue();
        await Assert.That(hook.Completed.Any(attempt => attempt.Context.Endpoint.Port == replica.Port)).IsTrue();
        await Assert.That(hook.Completed.All(attempt => attempt.Outcome == RespireConnectionOutcome.Succeeded)).IsTrue();
        if (cluster) await Assert.That(replica.ReceivedCommands.Contains("READONLY")).IsTrue();
    }

    [Test]
    public async Task SuccessIncludesTlsAndEncryptedHandshake()
    {
        using var certificate = TestTlsCertificate.Create();
        using var cancellation = new CancellationTokenSource(Limit);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var handshake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peerClosed = ServeAsync();
        var hook = new Hook
        {
            After = (context, outcome, _) =>
            {
                if (!context.UseTls || outcome != RespireConnectionOutcome.Succeeded || !handshake.Task.IsCompletedSuccessfully)
                    throw new InvalidOperationException("Hook completed before encrypted setup.");
                return ValueTask.CompletedTask;
            },
        };
        await using (var connection = await RespireConnection.ConnectAsync("127.0.0.1", port,
            new RespireConnectionOptions
            {
                ConnectionHook = hook, UseTls = true, ClientName = "tls-hook", Protocol = RespireProtocol.Resp2,
                TlsOptions = new() { RemoteCertificateValidationCallback = (_, _, _, _) => true },
            }, cancellationToken: cancellation.Token)) { }
        await peerClosed.WaitAsync(cancellation.Token);
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireConnectionOutcome.Succeeded);

        async Task ServeAsync()
        {
            using var socket = await listener.AcceptSocketAsync(cancellation.Token);
            await using var tls = new SslStream(new NetworkStream(socket, ownsSocket: false));
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, cancellation.Token);
            var expected = "*3\r\n$6\r\nCLIENT\r\n$7\r\nSETNAME\r\n$8\r\ntls-hook\r\n"u8.ToArray();
            var actual = new byte[expected.Length];
            await tls.ReadExactlyAsync(actual, cancellation.Token);
            await Assert.That(actual).IsEquivalentTo(expected);
            handshake.SetResult();
            await tls.WriteAsync(FakeRespServer.OkReply, cancellation.Token);
            try { await Assert.That(await tls.ReadAsync(new byte[1], cancellation.Token)).IsEqualTo(0); }
            catch (IOException) { /* Abortive client disposal may reset the peer. */ }
        }
    }

    [Test]
    public async Task ControlAndSentinelFactoriesPreserveHookAndEffectiveTls()
    {
        var hook = new Hook();
        var options = new RespireOptions { ConnectionHook = hook, UseTls = true, SentinelUseTls = false };
        await Assert.That(ReferenceEquals(options.ToControlConnectionOptions().ConnectionHook, hook)).IsTrue();
        var discovery = SentinelResolver.CreateSentinelConnectionOptions(options);
        var monitor = SentinelMonitoring.CreateOptions(options, new("sentinel", 26379)).ToConnectionOptions();
        foreach (var connectionOptions in new[] { discovery, monitor })
        {
            await Assert.That(ReferenceEquals(connectionOptions.ConnectionHook, hook)).IsTrue();
            await Assert.That(connectionOptions.UseTls).IsFalse();
            var failure = new IOException("No test transport.");
            await CaptureAsync(() => RespireConnection.ConnectAsync("sentinel", 26379,
                connectionOptions with { TestingStreamFactory = (_, _, _) => throw failure }));
        }
        await Assert.That(hook.Completed.All(attempt => !attempt.Context.UseTls
            && attempt.Context.Endpoint == new RespireEndpoint("sentinel", 26379))).IsTrue();
    }

    private static async Task<Exception> CaptureAsync(Func<Task> action)
    {
        try { await action().WaitAsync(Limit); }
        catch (Exception error) when (error is not TimeoutException) { return error; }
        throw new InvalidOperationException("Expected failure.");
    }

    private sealed class CountingProvider : IRespireCredentialProvider
    {
        internal int Calls;
        public ValueTask<RespireCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(new RespireCredentials("user", "secret"));
        }
    }

    private sealed class Hook : RespireConnectionHook
    {
        internal Func<RespireConnectionContext, CancellationToken, ValueTask>? Before;
        internal Func<RespireConnectionContext, RespireConnectionOutcome, CancellationToken, ValueTask>? After;
        internal ConcurrentQueue<RespireConnectionContext> Started { get; } = new();
        internal ConcurrentQueue<(RespireConnectionContext Context, RespireConnectionOutcome Outcome, TimeSpan Elapsed)> Completed { get; } = new();
        public override ValueTask OnConnectingAsync(RespireConnectionContext context, CancellationToken cancellationToken)
        {
            Started.Enqueue(context);
            return Before?.Invoke(context, cancellationToken) ?? ValueTask.CompletedTask;
        }
        public override ValueTask OnCompletedAsync(RespireConnectionContext context, RespireConnectionOutcome outcome,
            TimeSpan elapsed, CancellationToken cancellationToken)
        {
            Completed.Enqueue((context, outcome, elapsed));
            return After?.Invoke(context, outcome, cancellationToken) ?? ValueTask.CompletedTask;
        }
    }
}
