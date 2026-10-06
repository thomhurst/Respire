using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Text;
using Respire.Internal;
using Respire.Networking;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

[NotInParallel]
public class ConnectionMetricTests
{
    [Test]
    public async Task LateListenersSeeLiveBaselinesWithoutInventingCreationEvents()
    {
        using var configuration = new MetricConfigurationScope();
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply);
        await using var client = await RespireClient.ConnectAsync(Options(server));
        using var first = new Capture(server.Port);
        using var second = new Capture(server.Port);
        first.Observe();
        second.Observe();
        await Assert.That(first.Current("db.client.connection.count")).IsEqualTo(1d);
        await Assert.That(second.Current("db.client.connection.count")).IsEqualTo(1d);
        await Assert.That(first.Events).IsEmpty();
        await Assert.That(second.Events).IsEmpty();
        await Assert.That(first.Observations.First(item => item.Name == "db.client.connection.count").Unit).IsEqualTo("{connection}");
        await Assert.That(first.Observations.First(item => item.Name == "db.client.connection.pending_requests").Unit).IsEqualTo("{request}");
        await Assert.That(first.Observations.First(item => item.Name == "redis.client.connection.relaxed_timeout").Unit).IsEqualTo("{relaxation}");

        await client.DisposeAsync();
        await client.DisposeAsync();
        first.Observe();
        second.Observe();
        await Assert.That(first.Current("db.client.connection.count")).IsEqualTo(0d);
        await Assert.That(second.Current("db.client.connection.count")).IsEqualTo(0d);
        var closed = await first.WaitForCloseAsync();
        await Assert.That(closed.Value).IsEqualTo(1d);
        await Assert.That(closed.Unit).IsEqualTo("{connection}");
        await Assert.That(closed.Tags["redis.client.connection.close.reason"]).IsEqualTo("application_close");
        await Assert.That(closed.Tags["db.system.name"]).IsEqualTo("redis");
        await Assert.That(closed.Tags["redis.client.library"] as string).StartsWith("Respire:");
    }

    [Test]
    public async Task GroupChangesKeepTheCurrentConnectionBaseline()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.None });
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply);
        await using var client = await RespireClient.ConnectAsync(Options(server));
        using var capture = new Capture(server.Port);
        capture.Observe();
        await Assert.That(capture.Observations).IsEmpty();
        RespireMetrics.Configure(new() { Groups = RespireMetricGroups.ConnectionBasic });
        capture.Observe();
        await Assert.That(capture.Current("db.client.connection.count")).IsEqualTo(1d);
        RespireMetrics.Configure(new() { Groups = RespireMetricGroups.None });
        capture.Observe();
        await Assert.That(capture.Observations).IsEmpty();
        RespireMetrics.Configure(new() { Groups = RespireMetricGroups.ConnectionBasic });
        capture.Observe();
        await Assert.That(capture.Current("db.client.connection.count")).IsEqualTo(1d);
        await Assert.That(capture.Events).IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PendingRepliesChangeMultiplexedStateUntilTheFrameDrains(bool cancel)
    {
        using var configuration = new MetricConfigurationScope();
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply)
        { SuppressReply = command => command.StartsWith("GET ", StringComparison.Ordinal) };
        await using var client = await RespireClient.ConnectAsync(Options(server));
        using var capture = new Capture(server.Port);
        using var cancellation = new CancellationTokenSource();
        var pending = client.GetStringAsync("held", cancellation.Token).AsTask();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!server.ReceivedCommands.Contains("GET held"))
                await Task.Delay(1, deadline.Token);
            if (cancel)
            {
                cancellation.Cancel();
                await Assert.That(async () => await pending).Throws<OperationCanceledException>();
            }
            capture.Observe();
            await Assert.That(capture.Current("db.client.connection.count", "used")).IsEqualTo(1d);
            await Assert.That(capture.Current("db.client.connection.count", "idle")).IsEqualTo(0d);
            await Assert.That(capture.Current("db.client.connection.pending_requests")).IsEqualTo(1d);
            await server.SendRawAsync("$4\r\nheld\r\n"u8.ToArray());
            if (!cancel) await Assert.That(await pending.WaitAsync(TimeSpan.FromSeconds(5))).IsEqualTo("held");
            await WaitUntil(() =>
            {
                capture.Observe();
                return capture.Current("db.client.connection.pending_requests") == 0;
            });
            capture.Observe();
            await Assert.That(capture.Current("db.client.connection.count", "idle")).IsEqualTo(1d);
            await Assert.That(capture.Current("db.client.connection.pending_requests")).IsEqualTo(0d);
        }
        finally
        {
            await client.DisposeAsync();
            if (!pending.IsCompletedSuccessfully)
            {
                try { await pending; }
                catch (Exception) { }
            }
        }
    }

    [Test]
    public async Task CreationTimeIncludesHandshakeAndUsesSeconds()
    {
        using var configuration = new MetricConfigurationScope();
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command == "HELLO 3"
                ? "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray() : null,
        };
        server.DelayCommand("HELLO 3", 100);
        using var capture = new Capture(server.Port);
        await using var client = await RespireClient.ConnectAsync(Options(server) with { Protocol = RespProtocol.Resp3 });
        var created = capture.Events.Single(item => item.Name == "db.client.connection.create_time");
        await Assert.That(created.Value).IsGreaterThanOrEqualTo(0.09);
        await Assert.That(created.Unit).IsEqualTo("s");
        await Assert.That(created.Tags["db.client.connection.pool.name"] as string).IsNotNull();
    }

    [Test]
    public async Task ReenabledListenerReadsCurrentValuesWithoutReplayingEvents()
    {
        using var configuration = new MetricConfigurationScope();
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply);
        using var capture = new Capture(server.Port);
        capture.SetEnabled(false);
        await using var client = await RespireClient.ConnectAsync(Options(server));
        capture.SetEnabled(true);
        capture.Observe();
        await Assert.That(capture.Current("db.client.connection.count")).IsEqualTo(1d);
        await Assert.That(capture.Events).IsEmpty();
    }

    [Test]
    public async Task DedicatedLeasesBecomeIdleAndReuseWithoutAnotherWait()
    {
        using var configuration = new MetricConfigurationScope();
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply);
        using var capture = new Capture(server.Port);
        await using var pool = new DedicatedConnectionPool("127.0.0.1", server.Port,
            new() { Protocol = RespProtocol.Resp2 }, null);
        var connection = await pool.RentAsync(default);
        capture.Observe();
        await Assert.That(capture.Current("db.client.connection.count", "used")).IsEqualTo(1d);
        pool.Return(connection);
        capture.Observe();
        await Assert.That(capture.Current("db.client.connection.count", "idle")).IsEqualTo(1d);
        var reused = await pool.RentAsync(default);
        await Assert.That(ReferenceEquals(connection, reused)).IsTrue();
        capture.Observe();
        await Assert.That(capture.Current("db.client.connection.count", "used")).IsEqualTo(1d);
        var waits = capture.Events.Where(item => item.Name == "db.client.connection.wait_time").ToArray();
        await Assert.That(waits.Length).IsEqualTo(1);
        await Assert.That(waits[0].Unit).IsEqualTo("s");
        await Assert.That(waits[0].Value).IsGreaterThanOrEqualTo(0d);
        pool.Return(reused);
    }

    [Test]
    public async Task DedicatedIdleCapacityClosesOverflowOnce()
    {
        using var configuration = new MetricConfigurationScope();
        await using var server = new FakeRespServer(5, FakeRespServer.PongReply);
        using var capture = new Capture(server.Port);
        await using var pool = new DedicatedConnectionPool("127.0.0.1", server.Port,
            new() { Protocol = RespProtocol.Resp2 }, null);
        var connections = new RespireConnection[5];
        for (var i = 0; i < connections.Length; i++) connections[i] = await pool.RentAsync(default);
        foreach (var connection in connections) pool.Return(connection);
        await WaitUntil(() => capture.Events.Any(item => item.Name == "redis.client.connection.closed"));
        capture.Observe();
        await Assert.That(capture.Current("db.client.connection.count", "idle")).IsEqualTo(4d);
        var closed = await capture.WaitForCloseAsync();
        await Assert.That(closed.Tags["redis.client.connection.close.reason"]).IsEqualTo("pool_eviction_idle");
    }

    [Test]
    public async Task EofAndReplacementKeepCountsAndCloseReasonsTruthful()
    {
        using var configuration = new MetricConfigurationScope();
        await using var server = new FakeRespServer(2, FakeRespServer.PongReply) { CloseConnectionAfterCommand = 1 };
        using var capture = new Capture(server.Port);
        await using var first = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new() { Protocol = RespProtocol.Resp2 });
        await Assert.That(async () => { using var reply = await first.SendAsync(new Commands.RawCommand(FakeRespServer.PingFrame)); })
            .Throws<RespireConnectionException>();
        await first.DisposeAsync();
        capture.Observe();
        await Assert.That(capture.Current("db.client.connection.count")).IsEqualTo(0d);
        server.CloseConnectionAfterCommand = null;
        await using var replacement = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new() { Protocol = RespProtocol.Resp2 });
        capture.Observe();
        await Assert.That(capture.Current("db.client.connection.count")).IsEqualTo(1d);
        var closed = await capture.WaitForCloseAsync();
        await Assert.That(closed.Tags["redis.client.connection.close.reason"]).IsEqualTo("server_close");
        await Assert.That(closed.Tags.ContainsKey("error.type")).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PeerResetIsAServerClose(bool tls)
    {
        using var configuration = new MetricConfigurationScope();
        using var certificate = TestTlsCertificate.Create();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var capture = new Capture(port);
        var connect = RespireConnection.ConnectAsync("127.0.0.1", port,
            new() { Protocol = RespProtocol.Resp2, UseTls = tls,
                TlsOptions = new() { RemoteCertificateValidationCallback = static (_, _, _, _) => true } },
            cancellationToken: deadline.Token);
        using var peer = await listener.AcceptSocketAsync(deadline.Token);
        using var network = new NetworkStream(peer, ownsSocket: false);
        using var secure = tls ? new SslStream(network, leaveInnerStreamOpen: true) : null;
        if (secure is not null)
            await secure.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, deadline.Token);
        await using var connection = await connect.WaitAsync(deadline.Token);
        var pending = connection.SendAsync(new Commands.RawCommand(FakeRespServer.PingFrame)).AsTask();
        await Assert.That(await (secure as Stream ?? network).ReadAsync(new byte[1], deadline.Token)).IsEqualTo(1);
        peer.LingerState = new LingerOption(true, 0);
        peer.Dispose();
        await Assert.That(async () => { using var reply = await pending.WaitAsync(deadline.Token); })
            .Throws<RespireConnectionException>();
        await connection.DisposeAsync();
        var closed = await capture.WaitForCloseAsync();
        await Assert.That(closed.Tags["redis.client.connection.close.reason"]).IsEqualTo("server_close");
        await Assert.That(closed.Tags.ContainsKey("error.type")).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PeerDisconnectDuringTlsSetupIsAServerClose(bool reset)
    {
        using var configuration = new MetricConfigurationScope();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var capture = new Capture(port);
        var connect = RespireConnection.ConnectAsync("127.0.0.1", port,
            new() { Protocol = RespProtocol.Resp2, UseTls = true }, cancellationToken: deadline.Token);
        using var peer = await listener.AcceptSocketAsync(deadline.Token);
        using var stream = new NetworkStream(peer, ownsSocket: false);
        await Assert.That(await stream.ReadAsync(new byte[4096], deadline.Token)).IsGreaterThan(0);
        if (reset) peer.LingerState = new LingerOption(true, 0);
        else peer.Shutdown(SocketShutdown.Send);
        if (reset) peer.Dispose();
        await Assert.That(async () => await connect.WaitAsync(deadline.Token)).Throws<IOException>();
        var closed = await capture.WaitForCloseAsync();
        await Assert.That(closed.Tags["redis.client.connection.close.reason"]).IsEqualTo("server_close");
        await Assert.That(closed.Tags.ContainsKey("error.type")).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task StreamedReplyStaysPendingUntilItsFrameDrains(bool discard)
    {
        using var configuration = new MetricConfigurationScope();
        await using var server = new FakeRespServer("$6\r\nabc"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(Options(server));
        using var capture = new Capture(server.Port);
        await using var stream = await client.Strings.GetStreamAsync("key");
        await Assert.That(await stream!.ReadAtLeastAsync(new byte[3], 3)).IsEqualTo(3);
        if (discard) await stream.DisposeAsync();
        capture.Observe();
        await Assert.That(capture.Current("db.client.connection.pending_requests")).IsEqualTo(1d);
        await Assert.That(capture.Current("db.client.connection.count", "used")).IsEqualTo(1d);
        await server.SendRawAsync("def\r\n"u8.ToArray());
        if (!discard) await stream.CopyToAsync(Stream.Null);
        await WaitUntil(() =>
        {
            // Observable instruments collect separately while receive completion can advance.
            // Wait for both final observations rather than treating them as one atomic scrape.
            capture.Observe();
            return capture.Current("db.client.connection.pending_requests") == 0
                && capture.Current("db.client.connection.count", "idle") == 1;
        });
        await Assert.That(capture.Current("db.client.connection.count", "idle")).IsEqualTo(1d);
    }

    [Test]
    public async Task FailedHandshakeClosesTheSocketWithoutPublishingAReadyConnection()
    {
        using var configuration = new MetricConfigurationScope();
        await using var server = new FakeRespServer(1, "-WRONGPASS rejected\r\n"u8.ToArray());
        using var capture = new Capture(server.Port);
        await Assert.That(async () => await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new() { Protocol = RespProtocol.Resp3 })).Throws<RespireConnectionException>();
        capture.Observe();
        await Assert.That(capture.Current("db.client.connection.count")).IsEqualTo(0d);
        await Assert.That(capture.Events.Count(item => item.Name == "db.client.connection.create_time")).IsEqualTo(0);
        var closed = await capture.WaitForCloseAsync();
        await Assert.That(closed.Tags["redis.client.connection.close.reason"]).IsEqualTo("error");
        await Assert.That(closed.Tags["redis.client.errors.category"]).IsEqualTo("auth");
        await Assert.That(closed.Tags["error.type"]).IsEqualTo(typeof(RespireServerException).FullName);
    }

    [Test]
    public async Task FailedTlsHandshakeCountsTheConnectedSocketAsAnErrorClose()
    {
        using var configuration = new MetricConfigurationScope();
        using var certificate = TestTlsCertificate.Create();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var capture = new Capture(port);
        var server = ServeAsync();
        try
        {
            await Assert.That(async () => await RespireConnection.ConnectAsync("127.0.0.1", port,
                new() { UseTls = true, TlsOptions = new() { RemoteCertificateValidationCallback = static (_, _, _, _) => false } },
                cancellationToken: deadline.Token)).Throws<AuthenticationException>();
            capture.Observe();
            await Assert.That(capture.Current("db.client.connection.count")).IsEqualTo(0d);
            await Assert.That(capture.Events.Count(item => item.Name == "db.client.connection.create_time")).IsEqualTo(0);
            var closed = await capture.WaitForCloseAsync();
            await Assert.That(closed.Tags["redis.client.connection.close.reason"]).IsEqualTo("error");
            await Assert.That(closed.Tags["redis.client.errors.category"]).IsEqualTo("tls");
        }
        finally
        {
            deadline.Cancel();
            listener.Stop();
            await server;
        }

        async Task ServeAsync()
        {
            try
            {
                using var socket = await listener.AcceptSocketAsync(deadline.Token);
                await using var tls = new SslStream(new NetworkStream(socket, ownsSocket: false));
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, deadline.Token);
            }
            catch (Exception error) when (error is AuthenticationException or IOException or OperationCanceledException) { }
        }
    }

    [Test]
    [Arguments(false, true)]
    [Arguments(true, true)]
    [Arguments(true, false)]
    public async Task HandshakeCancellationIsClassifiedByItsSource(bool tls, bool callerCancels)
    {
        using var configuration = new MetricConfigurationScope();
        using var caller = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var capture = new Capture(port);
        var connect = RespireConnection.ConnectAsync("127.0.0.1", port,
            new() { Protocol = RespProtocol.Resp3, UseTls = tls,
                ConnectTimeout = TimeSpan.FromSeconds(callerCancels ? 5 : 1) }, cancellationToken: caller.Token);
        using var peer = await listener.AcceptSocketAsync(deadline.Token);
        using var stream = new NetworkStream(peer, ownsSocket: false);
        // Receiving a handshake byte proves TCP connected before cancellation.
        await Assert.That(await stream.ReadAsync(new byte[1], deadline.Token)).IsEqualTo(1);
        if (callerCancels) caller.Cancel();
        var error = await Assert.That(async () => await connect.WaitAsync(deadline.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken == caller.Token).IsEqualTo(callerCancels);
        var closed = await capture.WaitForCloseAsync();
        await Assert.That(closed.Tags["redis.client.connection.close.reason"])
            .IsEqualTo(callerCancels ? "application_close" : "error");
        await Assert.That(closed.Tags.ContainsKey("error.type")).IsEqualTo(!callerCancels);
        await Assert.That(closed.Tags.ContainsKey("redis.client.errors.category")).IsEqualTo(!callerCancels);
    }

    [Test]
    public async Task PubSubConnectionsStayUsedWithoutPendingCommandReplies()
    {
        using var configuration = new MetricConfigurationScope();
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply);
        using var capture = new Capture(server.Port);
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new() { Protocol = RespProtocol.Resp2, SubscriptionConfirmationHandler = static (in global::Respire.Protocol.RespValue _) => { } });
        capture.Observe();
        await Assert.That(capture.Current("db.client.connection.count", "used")).IsEqualTo(1d);
        await Assert.That(capture.Current("db.client.connection.pending_requests")).IsEqualTo(0d);
        var used = capture.Observations.Single(item => item.Name == "db.client.connection.count"
            && item.Value == 1 && Equals(item.Tags["db.client.connection.state"], "used"));
        await Assert.That((bool)used.Tags["redis.client.connection.pubsub"]!).IsTrue();
    }

    [Test]
    public async Task RenewalFailureRetainsItsAuthenticationCategoryAndExceptionType()
    {
        using var configuration = new MetricConfigurationScope();
        await using var server = new FakeRespServer(1, FakeRespServer.OkReply);
        using var capture = new Capture(server.Port);
        var renewal = new TaskCompletionSource<RespireCredentials>(TaskCreationOptions.RunContinuationsAsynchronously);
        var expiry = DateTimeOffset.UtcNow.AddHours(1);
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port, new()
        {
            Protocol = RespProtocol.Resp2,
            CredentialProvider = new PendingRenewalProvider(new(null, "first", expiry), renewal.Task),
            CredentialRefreshBeforeExpiry = TimeSpan.FromHours(2),
            CredentialCacheInvalidation = static () => throw new InvalidOperationException("cache fence"),
        });
        renewal.SetResult(new(null, "second", expiry));
        await WaitUntil(() => capture.Events.Any(item => item.Name == "redis.client.connection.closed"));
        var closed = await capture.WaitForCloseAsync();
        await Assert.That(closed.Tags["redis.client.connection.close.reason"]).IsEqualTo("error");
        await Assert.That(closed.Tags["redis.client.errors.category"]).IsEqualTo("auth");
        await Assert.That(closed.Tags["error.type"]).IsEqualTo(typeof(RespireAuthenticationException).FullName);
    }

    private sealed class PendingRenewalProvider(RespireCredentials initial, Task<RespireCredentials> renewal) : IRespireCredentialProvider
    {
        private int _calls;
        public ValueTask<RespireCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default)
            => Interlocked.Increment(ref _calls) == 1 ? ValueTask.FromResult(initial) : new(renewal.WaitAsync(cancellationToken));
    }

    [Test]
    public async Task MaintenanceCompletionAndExpiryClearTheRelaxedAllowance()
    {
        using var configuration = new MetricConfigurationScope();
        await using var server = MaintenanceServer();
        using var capture = new Capture(server.Port);
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port, new()
        {
            Protocol = RespProtocol.Resp3, MaintenanceNotifications = RespireMaintenanceNotificationMode.Enabled,
            CommandTimeout = TimeSpan.FromSeconds(1), MaintenanceRelaxedTimeout = TimeSpan.FromSeconds(5),
            MaintenanceWindowTimeout = TimeSpan.FromMilliseconds(100),
        });
        // Observe on the receive-side state transition. An asynchronously scheduled poll
        // can miss the entire 100 ms window on a constrained runner.
        capture.Observe();
        var snapshots = new double[3];
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var index = 0;
        Action onChanged = () =>
        {
            capture.Observe();
            snapshots[index++] = capture.Current("redis.client.connection.relaxed_timeout");
            if (index == snapshots.Length) observed.TrySetResult();
        };
        typeof(RespireConnection).GetEvent("MaintenanceStateChanged",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetAddMethod(nonPublic: true)!.Invoke(connection, [onChanged]);
        await server.SendRawAsync(
            ">3\r\n+MIGRATING\r\n:1\r\n:1\r\n>2\r\n+MIGRATED\r\n:1\r\n>3\r\n+MIGRATING\r\n:2\r\n:1\r\n"u8.ToArray());
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(snapshots[0]).IsEqualTo(1d);
        await Assert.That(snapshots[1]).IsEqualTo(0d);
        await Assert.That(snapshots[2]).IsEqualTo(1d);
        await WaitUntil(() => !connection.HasMaintenanceWindow);
        capture.Observe();
        await Assert.That(capture.Current("redis.client.connection.relaxed_timeout")).IsEqualTo(0d);
    }

    [Test]
    public async Task MovingCountsPublishedReplacementsRatherThanDuplicateNotifications()
    {
        using var configuration = new MetricConfigurationScope();
        await using var source = MaintenanceServer();
        await using var target = MaintenanceServer();
        using var capture = new Capture(source.Port);
        await using var client = await RespireClient.ConnectAsync(Options(source) with
        { Protocol = RespProtocol.Resp3, MaintenanceNotifications = RespireMaintenanceNotificationMode.Enabled });
        var moving = Encoding.UTF8.GetBytes($">4\r\n+MOVING\r\n:1\r\n:5\r\n+127.0.0.1:{target.Port}\r\n");
        await source.SendRawAsync(moving.Concat(moving).ToArray());
        await WaitUntil(() => capture.Events.Any(item => item.Name == "redis.client.connection.handoff"));
        await client.PingAsync();
        await Assert.That(target.ReceivedCommands.Contains("PING")).IsTrue();
        var handoff = capture.Events.Single(item => item.Name == "redis.client.connection.handoff");
        await Assert.That(handoff.Value).IsEqualTo(1d);
        await Assert.That(handoff.Unit).IsEqualTo("1");
    }

    [Test]
    public async Task MovingDoesNotCountSocketsClosedBeforePublication()
    {
        using var configuration = new MetricConfigurationScope();
        await using var source = MaintenanceServer();
        await using var target = MaintenanceServer();
        using var capture = new Capture(source.Port);
        await using var client = await RespireClient.ConnectAsync(Options(source) with
        { Protocol = RespProtocol.Resp3, MaintenanceNotifications = RespireMaintenanceNotificationMode.Enabled });
        var old = client.Core.Multiplexer.GetConnection();
        var handshake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Core.Multiplexer.MovingHandoffPublished += () => published.TrySetResult();
        target.SuppressReply = command =>
        {
            if (command != "HELLO 3") return false;
            handshake.TrySetResult();
            return true;
        };
        await source.SendRawAsync(Encoding.UTF8.GetBytes($">4\r\n+MOVING\r\n:1\r\n:5\r\n+127.0.0.1:{target.Port}\r\n"));
        await handshake.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // The source has no second accepted socket: reconnect cannot replace this slot.
        // Finish closing it while the MOVING target is still in its handshake.
        source.CloseConnection(source.ReceivedConnectionIds[0]);
        await WaitUntil(() => !old.IsConnected);
        await old.DisposeAsync();
        await target.SendRawAsync("%1\r\n+proto\r\n:3\r\n"u8.ToArray());
        await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(capture.Events.Any(item => item.Name == "redis.client.connection.handoff")).IsFalse();
        await client.PingAsync();
        await Assert.That(target.ReceivedCommands.Contains("PING")).IsTrue();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task WriteFailureClassifiesResetWithoutAReceiveFlag(bool wrapped, bool unrelatedError)
    {
        using var configuration = new MetricConfigurationScope();
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new() { Protocol = RespProtocol.Resp2 });
        using var capture = new Capture(server.Port);
        var pool = new ConnectionTelemetry.Pool($"127.0.0.1:{server.Port}/0/shared", false);
        var state = new ConnectionTelemetry.State(connection, pool, false);
        // Match FlushLoopAsync's wrapped failure before the receive loop publishes its flag.
        // TLS adds an IOException around the underlying socket reset.
        Exception cause = unrelatedError ? new IOException("Local write failure.")
            : new SocketException((int)SocketError.ConnectionReset);
        var error = wrapped
            ? new RespireConnectionException("Send failed.", new IOException("TLS write failed.", cause))
            : cause;
        state.Closed(error, peerClosed: false);
        state.Closed(error, peerClosed: false);
        var closed = await capture.WaitForCloseAsync();
        await Assert.That(closed.Tags["redis.client.connection.close.reason"])
            .IsEqualTo(unrelatedError ? "error" : "server_close");
        await Assert.That(closed.Tags.ContainsKey("error.type")).IsEqualTo(unrelatedError);
        await Assert.That(closed.Value).IsEqualTo(1d);
    }

    [Test]
    [Arguments("standalone")]
    [Arguments("sentinel")]
    [Arguments("cluster")]
    public async Task MovingCountsIdleAndBorrowedDedicatedConnectionsOnce(string mode)
    {
        using var configuration = new MetricConfigurationScope();
        await using var source = RoutingMaintenanceServer();
        await using var target = RoutingMaintenanceServer();
        await using var sentinel = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("SENTINEL GET-MASTER-ADDR", StringComparison.Ordinal)
                ? Encoding.ASCII.GetBytes("*2\r\n$9\r\n127.0.0.1\r\n" + "$"
                    + $"{source.Port.ToString().Length}\r\n{source.Port}\r\n")
                : "*0\r\n"u8.ToArray(),
        };
        var options = Options(source) with
        {
            Protocol = RespProtocol.Resp3, MaintenanceNotifications = RespireMaintenanceNotificationMode.Enabled,
            UseCluster = mode == "cluster", SentinelPrimaryName = mode == "sentinel" ? "mymaster" : null,
        };
        if (mode == "sentinel")
        {
            options.Endpoints.Clear();
            options.Endpoints.Add(new("127.0.0.1", sentinel.Port));
        }
        await using var client = await RespireClient.ConnectAsync(options);
        var pool = client.Core.Cluster is { } cluster
            ? await cluster.GetDedicatedPoolAsync(ClusterHash.GetSlot("lease"), default, discovery: null)
            : await client.Core.GetDedicatedPoolAsync(default);
        var idle = await pool.RentAsync(default);
        var borrowed = await pool.RentAsync(default, reuseIdle: false);
        var streaming = await pool.RentAsync(default, kind: DedicatedLeaseKind.Streaming);
        pool.Return(idle);
        using var capture = new Capture(source.Port);
        var expectedHandoffs = pool.MovingOwner!.ConnectionCount + 3;
        var moving = Encoding.UTF8.GetBytes($">4\r\n+MOVING\r\n:1\r\n:5\r\n+127.0.0.1:{target.Port}\r\n");
        try
        {
            await source.SendRawAsync(moving.Concat(moving).ToArray());
            await WaitUntil(() => pool.IsStopping);
            await WaitUntil(() => capture.Events.Count(item => item.Name == "redis.client.connection.handoff") == expectedHandoffs);
            await Assert.That(borrowed.IsConnected).IsTrue();
            await Assert.That(streaming.IsConnected).IsTrue();
            // Count all old sockets so this also works after the process-wide label budget
            // combines shared and dedicated identities in its ordinary overflow pool.
            var handoffs = capture.Events.Where(item => item.Name == "redis.client.connection.handoff").ToArray();
            await Assert.That(handoffs.Length).IsEqualTo(expectedHandoffs);
            await Assert.That(handoffs.Sum(item => item.Value)).IsEqualTo((double)expectedHandoffs);
            var repeated = pool.RetireAsync().AsTask();
            pool.Return(borrowed);
            pool.Return(streaming);
            await repeated.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(capture.Events.Count(item => item.Name == "redis.client.connection.handoff"))
                .IsEqualTo(expectedHandoffs);
        }
        finally
        {
            pool.Return(borrowed);
            pool.Return(streaming);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DedicatedRetirementReportsOnlyMovingAndExcludesClosedConnections(bool moving)
    {
        using var configuration = new MetricConfigurationScope();
        await using var server = new FakeRespServer(3, FakeRespServer.PongReply);
        await using var pool = new DedicatedConnectionPool("127.0.0.1", server.Port,
            new() { Protocol = RespProtocol.Resp2 }, null);
        var idle = await pool.RentAsync(default);
        var borrowed = await pool.RentAsync(default, reuseIdle: false);
        var dead = await pool.RentAsync(default, reuseIdle: false);
        await dead.DisposeAsync();
        pool.Return(idle);
        using var capture = new Capture(server.Port);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "redis.client.connection.handoff")
                current.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        });
        listener.Start();
        var retirement = Task.Run(async () => await pool.RetireAsync(moving));
        try
        {
            if (moving) await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            else await WaitUntil(() => pool.IsStopping);
            var gateCheck = Task.Run(() => pool.CaptureRetirementState());
            await gateCheck.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(pool.IsStopping).IsTrue();
            await WaitUntil(() => !idle.IsConnected);
            await Assert.That(idle.IsConnected).IsFalse();
            await Assert.That(borrowed.IsConnected).IsTrue();
            await Assert.That(retirement.IsCompleted).IsFalse();
            pool.Return(borrowed);
            pool.Return(dead);
            // Repeated calls cannot emit more handoffs, even while the first callback blocks.
            await pool.RetireAsync(moving).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.TrySetResult();
            pool.Return(borrowed);
            pool.Return(dead);
            await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        }
        var handoffs = capture.Events.Where(item => item.Name == "redis.client.connection.handoff").ToArray();
        await Assert.That(handoffs.Length).IsEqualTo(moving ? 2 : 0);
    }

    private static FakeRespServer RoutingMaintenanceServer()
    {
        var server = new FakeRespServer(8, FakeRespServer.PongReply);
        server.ReplyOverride = (_, command) => command switch
        {
            "HELLO 3" => "%1\r\n+proto\r\n:3\r\n"u8.ToArray(),
            "CLIENT MAINT_NOTIFICATIONS ON" => FakeRespServer.OkReply,
            "ROLE" => "*3\r\n$6\r\nmaster\r\n:0\r\n*0\r\n"u8.ToArray(),
            "CLUSTER SLOTS" => Encoding.ASCII.GetBytes(
                $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{server.Port}\r\n"),
            _ => FakeRespServer.OkReply,
        };
        return server;
    }

    [Test]
    public async Task BlockingHandoffListenerCannotDelayOldSocketRetirement()
    {
        using var configuration = new MetricConfigurationScope();
        await using var source = MaintenanceServer();
        await using var target = MaintenanceServer();
        await using var client = await RespireClient.ConnectAsync(Options(source) with
        { Protocol = RespProtocol.Resp3, MaintenanceNotifications = RespireMaintenanceNotificationMode.Enabled });
        var old = client.Core.Multiplexer.GetConnection();
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SuppressReply = command => { if (command != "PING") return false; sent.TrySetResult(); return true; };
        var pending = client.PingAsync().AsTask();
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "redis.client.connection.handoff")
                current.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        });
        listener.Start();
        try
        {
            await source.SendRawAsync(Encoding.UTF8.GetBytes($">4\r\n+MOVING\r\n:1\r\n:1\r\n+127.0.0.1:{target.Port}\r\n"));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(old.IsAcceptingCommands).IsFalse();
            await source.PeerClosed.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.TrySetResult();
            try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (RespireConnectionException) { }
        }
    }

    [Test]
    public async Task ListenerExceptionsDoNotChangeConnectionOrDisposalOutcomes()
    {
        using var configuration = new MetricConfigurationScope();
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument is Counter<long> or Histogram<double>)
                current.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>(static (_, _, _, _) => throw new InvalidOperationException("listener"));
        listener.SetMeasurementEventCallback<double>(static (_, _, _, _) => throw new InvalidOperationException("listener"));
        listener.Start();
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new() { Protocol = RespProtocol.Resp2 });
        using var reply = await connection.SendAsync(new Commands.RawCommand(FakeRespServer.PingFrame));
        await Assert.That(reply.ToString()).IsEqualTo("PONG");
        await connection.DisposeAsync();
        await Assert.That(connection.IsConnected).IsFalse();
    }

    [Test]
    public async Task PoolObservationsPruneCollectedTargetsAndKeepStableSnapshots()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new() { Protocol = RespProtocol.Resp2 });
        var pool = new ConnectionTelemetry.Pool("test", false);
        var state = new ConnectionTelemetry.State(connection, pool, false);
        pool.Add(state);
        var snapshot = pool.SnapshotForObservation();
        await Assert.That(snapshot.Length).IsEqualTo(1);
        pool.Remove(state);
        await Assert.That(pool.SnapshotForObservation()).IsEmpty();
        await Assert.That(snapshot[0]).IsSameReferenceAs(state);
        pool.Add(state);
        // Clear the weak target deterministically instead of relying on GC timing while
        // connection-owned tasks still run. Observation must remove the retained state.
        ConnectionTarget(state).SetTarget(null!);
        await Assert.That(pool.SnapshotForObservation()).IsEmpty();
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_connection")]
    private static extern ref WeakReference<RespireConnection> ConnectionTarget(ConnectionTelemetry.State state);

    [Test]
    public async Task EndpointLabelBudgetHasDistinctBoundedOverflowSeries()
    {
        var registry = new ConnectionTelemetry.Registry();
        for (var i = 0; i < 1000; i++)
        {
            registry.ForPool($"host:{i}/0/shared", false);
            registry.ForPool($"host:{i}/0/pubsub", true);
        }
        await Assert.That(registry.Snapshot.Length).IsEqualTo(66);
        await Assert.That(registry.Snapshot.Select(pool => pool.Tags.Last().Value).Distinct().Count()).IsEqualTo(66);
        await Assert.That(registry.ForPool(new string('x', 257), false).Tags.Last().Value).IsEqualTo("overflow/shared");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LifecycleCallbacksDoNotHoldTransportOrPoolGates(bool close)
    {
        using var configuration = new MetricConfigurationScope();
        await using var server = new FakeRespServer(2, FakeRespServer.PongReply);
        await using var pool = new DedicatedConnectionPool("127.0.0.1", server.Port,
            new() { Protocol = RespProtocol.Resp2 }, null);
        var connection = await pool.RentAsync(default);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new MeterListener();
        var name = close ? "redis.client.connection.closed" : "db.client.connection.wait_time";
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == name)
                current.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => Block());
        listener.SetMeasurementEventCallback<double>((_, _, _, _) => Block());
        listener.Start();
        RespireConnection? extra = null;
        var operation = Task.Run(async () =>
        {
            if (close) await connection.DisposeAsync();
            else extra = await pool.RentAsync(default, reuseIdle: false);
        });
        Task? gateCheck = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            gateCheck = Task.Run(() =>
            {
                if (close) connection.StopAcceptingCommands();
                else _ = pool.CaptureRetirementState();
            });
            await gateCheck.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.TrySetResult();
            await operation.WaitAsync(TimeSpan.FromSeconds(5));
            if (gateCheck is not null) await gateCheck.WaitAsync(TimeSpan.FromSeconds(5));
            if (extra is not null) pool.Return(extra);
        }

        void Block()
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        }
    }

    [Test]
    public async Task DisabledLifecycleMeasurementsAllocateNothingOnExistingConnections()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.None });
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply);
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new() { Protocol = RespProtocol.Resp2 });
        for (var i = 0; i < 20; i++) MeasureDisabled(connection);
        MeasurePositiveControl();
        var measured = AllocationMeasurement.WithoutConcurrentGc(() => (MeasureDisabled(connection), MeasurePositiveControl()));
        await Assert.That(measured.Item1).IsEqualTo(0L);
        await Assert.That(measured.Item2).IsGreaterThan(0L);
    }

    [Test]
    public async Task WatchdogCloseCallbackReleasesTheReceiveDeadlineGate()
    {
        using var configuration = new MetricConfigurationScope();
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply) { SuppressReply = _ => true };
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new() { Protocol = RespProtocol.Resp2, ResponseTimeout = TimeSpan.FromMilliseconds(100) });
        var gate = (Lock)typeof(RespireConnection).GetField("_receiveDeadlineGate",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(connection)!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "redis.client.connection.closed")
                current.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        });
        listener.Start();
        var pending = connection.SendAsync(new Commands.RawCommand(FakeRespServer.PingFrame)).AsTask();
        Task? gateCheck = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            gateCheck = Task.Run(() => { lock (gate) { } });
            await gateCheck.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.TrySetResult();
            if (gateCheck is not null) await gateCheck.WaitAsync(TimeSpan.FromSeconds(5));
            try { using var reply = await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (RespireConnectionException) { }
        }
        await Assert.That(connection.IsConnected).IsFalse();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureDisabled(RespireConnection connection)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            connection.SetLeaseRented(true);
            connection.SetLeaseRented(false);
            connection.RecordConnectionWait(1);
            connection.RecordConnectionHandoff();
            _ = connection.PendingResponseCount;
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static object? _allocationAnchor;
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasurePositiveControl()
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) _allocationAnchor = new byte[37];
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static FakeRespServer MaintenanceServer() => new(1, FakeRespServer.PongReply)
    {
        ReplyOverride = (_, command) => command == "HELLO 3" ? "%1\r\n+proto\r\n:3\r\n"u8.ToArray()
            : command == "CLIENT MAINT_NOTIFICATIONS ON" ? FakeRespServer.OkReply : null,
    };

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(1, deadline.Token);
    }

    private static RespireOptions Options(FakeRespServer server) => new()
    {
        Endpoints = [new("127.0.0.1", server.Port)], Connections = 1,
        Protocol = RespProtocol.Resp2, ThreadPoolMonitoring = false,
    };

    private sealed record Sample(string Name, string? Unit, double Value, Dictionary<string, object?> Tags);

    private sealed class Capture : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<Instrument> _instruments = [];
        internal readonly ConcurrentQueue<Sample> Observations = new();
        internal readonly ConcurrentQueue<Sample> Events = new();

        internal Capture(int port)
        {
            _ = RespireTelemetry.Meter;
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Respire" &&
                    (instrument.Name.StartsWith("db.client.connection.", StringComparison.Ordinal) ||
                     instrument.Name.StartsWith("redis.client.connection.", StringComparison.Ordinal)))
                {
                    _instruments.Add(instrument);
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
            _listener.Start();

            void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
            {
                var copied = tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value);
                if (!copied.TryGetValue("db.client.connection.pool.name", out var pool) ||
                    pool is not string name || !(name.StartsWith($"127.0.0.1:{port}/", StringComparison.Ordinal)
                        || name.StartsWith("overflow/", StringComparison.Ordinal))) return;
                var sample = new Sample(instrument.Name, instrument.Unit, value, copied);
                if (instrument is ObservableUpDownCounter<long>) Observations.Enqueue(sample);
                else Events.Enqueue(sample);
            }
        }

        internal async Task<Sample> WaitForCloseAsync()
        {
            await WaitUntil(() => Events.Any(item => item.Name == "redis.client.connection.closed"));
            return Events.Single(item => item.Name == "redis.client.connection.closed");
        }

        internal void Observe()
        {
            Observations.Clear();
            _listener.RecordObservableInstruments();
        }

        internal void SetEnabled(bool enabled)
        {
            foreach (var instrument in _instruments)
                if (enabled) _listener.EnableMeasurementEvents(instrument);
                else _listener.DisableMeasurementEvents(instrument);
        }

        internal double Current(string name, string? state = null)
            => Observations.Where(item => item.Name == name && (state is null ||
                item.Tags.TryGetValue("db.client.connection.state", out var value) && Equals(value, state)))
                .Sum(item => item.Value);

        public void Dispose() => _listener.Dispose();
    }
}
