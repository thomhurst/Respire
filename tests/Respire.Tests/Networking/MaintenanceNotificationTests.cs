using System.Diagnostics;
using System.Text;
using Respire.Commands;
using Respire.Internal;
using Respire.Infrastructure;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class MaintenanceNotificationTests
{
    private static readonly byte[] Hello = "%1\r\n+proto\r\n:3\r\n"u8.ToArray();

    [Test]
    [Arguments(RespireMaintenanceNotificationMode.Disabled, RespProtocol.Auto, 2)]
    [Arguments(RespireMaintenanceNotificationMode.Auto, RespProtocol.Auto, 3)]
    [Arguments(RespireMaintenanceNotificationMode.Enabled, RespProtocol.Resp3, 3)]
    [Arguments(RespireMaintenanceNotificationMode.Auto, RespProtocol.Resp2, 1)]
    public async Task NegotiatesOnlyWhenRequestedOnResp3(RespireMaintenanceNotificationMode mode, RespProtocol protocol, int count)
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server) with { MaintenanceNotifications = mode, Protocol = protocol });
        await client.PingAsync();
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(count);
        await Assert.That(server.ReceivedCommands.Contains("CLIENT MAINT_NOTIFICATIONS ON")).IsEqualTo(count == 3);
    }

    [Test]
    [Arguments(RespireMaintenanceNotificationMode.Auto, "-ERR unknown subcommand 'MAINT_NOTIFICATIONS'\r\n", false)]
    [Arguments(RespireMaintenanceNotificationMode.Enabled, "-ERR unknown subcommand 'MAINT_NOTIFICATIONS'\r\n", true)]
    [Arguments(RespireMaintenanceNotificationMode.Auto, "-NOPERM denied\r\n", true)]
    [Arguments(RespireMaintenanceNotificationMode.Auto, "-ERR maintenance subsystem unavailable\r\n", true)]
    [Arguments(RespireMaintenanceNotificationMode.Auto, "+unexpected\r\n", true)]
    public async Task OnlyAutoUnsupportedCapabilityFallsBack(RespireMaintenanceNotificationMode mode, string reply, bool fails)
    {
        await using var server = Server(Encoding.UTF8.GetBytes(reply));
        var options = Options(server) with { MaintenanceNotifications = mode };
        if (fails)
            await Assert.That(async () => await RespireClient.ConnectAsync(options)).Throws<RespireException>();
        else
        {
            await using var client = await RespireClient.ConnectAsync(options);
            await client.PingAsync();
        }
    }

    [Test]
    [Arguments(RespireMaintenanceNotificationMode.Auto, false)]
    [Arguments(RespireMaintenanceNotificationMode.Enabled, true)]
    public async Task Resp2FallbackDisablesAutoAndRejectsEnabled(RespireMaintenanceNotificationMode mode, bool fails)
    {
        await using var server = new FakeRespServer("-NOPROTO unsupported\r\n"u8.ToArray(), FakeRespServer.PongReply);
        var options = Options(server) with { MaintenanceNotifications = mode };
        if (fails)
            await Assert.That(async () => await RespireClient.ConnectAsync(options)).Throws<RespireException>();
        else
        {
            await using var client = await RespireClient.ConnectAsync(options);
            await client.PingAsync();
        }
        await Assert.That(server.ReceivedCommands.Contains("CLIENT MAINT_NOTIFICATIONS ON")).IsFalse();
    }

    [Test]
    public async Task BlockingAndPubSubConnectionsDoNotNegotiateMaintenance()
    {
        await using var server = Server(maxConnections: 3);
        var ordinaryReply = server.ReplyOverride!;
        server.ReplyOverride = (id, command) => command switch
        {
            "BLPOP queue 1" => "_\r\n"u8.ToArray(),
            "SUBSCRIBE events" => ">3\r\n+subscribe\r\n+events\r\n:1\r\n"u8.ToArray(),
            "UNSUBSCRIBE events" => ">3\r\n+unsubscribe\r\n+events\r\n:0\r\n"u8.ToArray(),
            _ => ordinaryReply(id, command),
        };
        await using var client = await RespireClient.ConnectAsync(Options(server));
        _ = await client.Lists.LeftPopAsync("queue", waitFor: TimeSpan.FromSeconds(1));
        await using var subscription = await client.SubscribeAsync("events");
        await Assert.That(server.ReceivedCommands.Count(command => command == "HELLO 3")).IsEqualTo(3);
        await Assert.That(server.ReceivedCommands.Count(command => command == "CLIENT MAINT_NOTIFICATIONS ON")).IsEqualTo(1);
    }

    [Test]
    public async Task CorrectionConnectionsDoNotInheritMaintenanceNegotiation()
    {
        await using var server = Server(maxConnections: 2);
        var options = Options(server) with { UseCluster = true };
        await using var primary = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", server.Port,
            options: options.ToConnectionOptions(enableMaintenanceNotifications: true));
        await using var router = new ClusterRouter(options, primary);
        await using var lease = router.GetCorrectionLease(primary.GetConnection());
        var connection = await lease.Pool.RentAsync(default);
        try
        {
            using var pong = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame));
            await Assert.That(pong.AsString()).IsEqualTo("PONG");
        }
        finally { lease.Pool.Return(connection); }
        await Assert.That(server.ReceivedCommands.Count(command => command == "HELLO 3")).IsEqualTo(2);
        await Assert.That(server.ReceivedCommands.Count(command => command == "CLIENT MAINT_NOTIFICATIONS ON")).IsEqualTo(1);
    }

    [Test]
    [Arguments(">4\r\n+MOVING\r\n:1\r\n:10\r\n+host:6379\r\n", "MOVING")]
    [Arguments(">4\r\n+MOVING\r\n:1\r\n:10\r\n_\r\n", "MOVING")]
    [Arguments(">4\r\n+MIGRATING\r\n:1\r\n:2\r\n*1\r\n:7\r\n", "MIGRATING")]
    [Arguments(">3\r\n+MIGRATED\r\n:1\r\n+7\r\n", "MIGRATED")]
    [Arguments(">3\r\n+FAILING_OVER\r\n:1\r\n:2\r\n", "FAILING_OVER")]
    [Arguments(">2\r\n+FAILED_OVER\r\n:1\r\n", "FAILED_OVER")]
    [Arguments(">3\r\n+SMIGRATING\r\n:1\r\n+0,1-16383\r\n", "SMIGRATING")]
    [Arguments(">3\r\n+SMIGRATED\r\n:1\r\n*1\r\n*3\r\n+old:6379\r\n+[::1]:6380\r\n+0,1-16383\r\n", "SMIGRATED")]
    public async Task CopiesValidWireNotifications(string wire, string kind)
    {
        var notification = Parse(wire);
        await Assert.That(notification).IsNotNull();
        await Assert.That(notification!.Kind).IsEqualTo(kind);
        await Assert.That(notification.SequenceId).IsEqualTo(1);
        if (kind == "SMIGRATED")
        {
            await Assert.That(notification.Migrations![0].Target).IsEqualTo(new RespireEndpoint("::1", 6380));
            await Assert.That(notification.Migrations[0].Slots).IsEqualTo("0,1-16383");
        }
    }

    [Test]
    [Arguments(">4\r\n+MOVING\r\n:1\r\n:-1\r\n+host:6379\r\n")]
    [Arguments(">4\r\n+MOVING\r\n:1\r\n:10\r\n+host:70000\r\n")]
    [Arguments(">3\r\n+MIGRATING\r\n+one\r\n:2\r\n")]
    [Arguments(">2\r\n+FAILING_OVER\r\n:1\r\n")]
    [Arguments(">3\r\n+SMIGRATING\r\n:1\r\n+16384\r\n")]
    [Arguments(">3\r\n+SMIGRATING\r\n:1\r\n+10-9\r\n")]
    [Arguments(">3\r\n+SMIGRATED\r\n:1\r\n*1\r\n*2\r\n+old:6379\r\n+new:6379\r\n")]
    public async Task RejectsMalformedWireNotifications(string wire)
        => await Assert.That(Parse(wire)).IsNull();

    [Test]
    public async Task MatchingCompletionDuplicateAndExpiryKeepIndependentWindows()
    {
        var state = new MaintenanceTimeoutState(1000);
        state.Apply(new("MIGRATING", 1), 100);
        state.Apply(new("FAILING_OVER", 2), 200);
        state.Apply(new("MIGRATING", 1), 300);
        state.Apply(new("MIGRATED", 2), 350); // Wrong family/sequence cannot end either window.
        await Assert.That(state.Remaining(400)).IsEqualTo(800);
        state.Apply(new("MIGRATED", 1), 400);
        await Assert.That(state.Remaining(400)).IsEqualTo(800);
        state.Apply(new("FAILED_OVER", 2), 500);
        await Assert.That(state.Remaining(500)).IsEqualTo(0);
        state.Apply(new("MIGRATING", 1), 600);
        await Assert.That(state.Remaining(600)).IsEqualTo(0);
        state.Apply(new("SMIGRATING", 3), 600);
        await Assert.That(state.Remaining(1600)).IsEqualTo(0);
        state.Apply(new("SMIGRATING", 3), 1700); // Expired duplicate cannot reopen the window.
        await Assert.That(state.Remaining(1700)).IsEqualTo(0);
    }

    [Test]
    [Arguments(999)]
    [Arguments(1000)]
    [Arguments(1001)]
    public async Task LateMaintenanceCannotReviveExpiredCommand(long deadline)
    {
        var state = new MaintenanceTimeoutState(5000);
        state.Apply(new("MIGRATING", 1), 1000);
        var pool = new PendingResponsePool(1);
        var ring = new InflightRing(1);
        var source = pool.Rent(commandName: "PING");
        source.Deadline = deadline;
        ring.TryEnqueue(source);
        var window = state.GetWindow(1100)!;
        var remaining = ring.SweepExpired(1100, TimeSpan.FromMilliseconds(200), null,
            deadlineExtension: 1000, maintenanceStarted: window.Started);
        if (deadline <= 1000)
        {
            await Assert.That(remaining).IsEqualTo(-1);
            var error = await Assert.That(async () => await source.Task).ThrowsExactly<RespireTimeoutException>();
            await Assert.That(error!.Timeout).IsEqualTo(TimeSpan.FromMilliseconds(200));
        }
        else
        {
            await Assert.That(remaining).IsEqualTo(901);
            source.TrySetResult(RespValue.Integer(1));
            using var result = await source.Task;
        }
        ring.TryDequeue(out var dequeued);
        dequeued.ReleaseRef();
    }

    [Test]
    public async Task CutoffSurvivesOverlapAndResetsAfterCompletionOrExpiry()
    {
        var state = new MaintenanceTimeoutState(1000);
        state.Apply(new("MIGRATING", 1), 100);
        state.Apply(new("FAILING_OVER", 2), 200);
        state.Apply(new("MIGRATED", 1), 300);
        await Assert.That(state.GetWindow(400)!.Started).IsEqualTo(100);
        state.Apply(new("FAILED_OVER", 2), 500);
        await Assert.That(state.GetWindow(500)).IsNull();
        state.Apply(new("MIGRATING", 3), 600);
        await Assert.That(state.GetWindow(600)!.Started).IsEqualTo(600);
        state.Apply(new("MIGRATING", 4), 1700);
        await Assert.That(state.GetWindow(1700)!.Started).IsEqualTo(1700);
        state.Apply(new("MIGRATING", 4), 1800);
        await Assert.That(state.GetWindow(1800)!.Started).IsEqualTo(1700);
        await Assert.That(state.GetWindow(2700)).IsNull();
    }

    [Test]
    [Arguments("MIGRATING", "MIGRATED")]
    [Arguments("FAILING_OVER", "FAILED_OVER")]
    [Arguments("SMIGRATING", "SMIGRATED")]
    public async Task MaintenanceProtectsPendingAndNewCommandsThenRestoresTimeout(string start, string finish)
    {
        await using var server = Server();
        server.SuppressReply = command => command == "PING";
        await using var connection = await Connect(server, TimeSpan.FromMilliseconds(200));
        var first = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        await WaitForCommands(server, 3);
        await server.SendRawAsync(Start(start, 11));
        await WaitForMaintenance(connection);
        var second = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        await WaitForCommands(server, 4);
        // Cross the ordinary command deadline while the server deliberately withholds replies.
        await Task.Delay(400);
        await Assert.That(first.IsCompleted).IsFalse();
        await Assert.That(second.IsCompleted).IsFalse();
        await server.SendRawAsync(Finish(finish, 11));
        await Assert.That(async () => { using var _ = await first.WaitAsync(TimeSpan.FromSeconds(5)); }).Throws<RespireTimeoutException>();
        await Assert.That(async () => { using var _ = await second.WaitAsync(TimeSpan.FromSeconds(5)); }).Throws<RespireTimeoutException>();
        // Both timed-out slots still consume their own replies; the next response must remain aligned.
        await server.SendRawAsync("+first\r\n+second\r\n"u8.ToArray());
        server.SuppressReply = null;
        using var pong = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame));
        await Assert.That(pong.AsString()).IsEqualTo("PONG");
    }

    [Test]
    public async Task CallerCancellationStillWinsDuringMaintenance()
    {
        await using var server = Server();
        server.SuppressReply = command => command == "PING";
        await using var connection = await Connect(server, null);
        await server.SendRawAsync(Start("MIGRATING", 1));
        await WaitForMaintenance(connection);
        using var cancellation = new CancellationTokenSource();
        var command = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame), cancellation.Token).AsTask();
        await WaitForCommands(server, 3);
        cancellation.Cancel();
        await Assert.That(async () => { using var _ = await command; }).Throws<OperationCanceledException>();
        await Assert.That(connection.IsConnected).IsTrue();
    }

    [Test]
    public async Task MissingCompletionExpiresAndUnlimitedTimeoutStaysUnlimited()
    {
        await using var server = Server();
        server.SuppressReply = command => command == "PING";
        await using var connection = await Connect(server, TimeSpan.FromMilliseconds(150), window: TimeSpan.FromMilliseconds(500));
        await server.SendRawAsync(Start("MIGRATING", 1));
        await WaitForMaintenance(connection);
        var pending = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        await Assert.That(async () => { using var _ = await pending.WaitAsync(TimeSpan.FromSeconds(5)); }).Throws<RespireTimeoutException>();
        await Assert.That(connection.HasMaintenanceWindow).IsFalse();

        await using var unlimitedServer = Server();
        unlimitedServer.SuppressReply = command => command == "PING";
        // Keep the active window long enough for a loaded runner to observe it.
        await using var unlimited = await Connect(unlimitedServer, null, window: TimeSpan.FromSeconds(2));
        await unlimitedServer.SendRawAsync(Start("MIGRATING", 1));
        await WaitForMaintenance(unlimited);
        var unlimitedPending = unlimited.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        await Task.Delay(300);
        await Assert.That(unlimitedPending.IsCompleted).IsFalse();
        await unlimitedServer.SendRawAsync(FakeRespServer.PongReply);
        using var result = await unlimitedPending.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(result.AsString()).IsEqualTo("PONG");
    }

    [Test]
    public async Task ReceiveWatchdogRelaxesAndRestoresOnCompletion()
    {
        await using var server = Server();
        server.SuppressReply = command => command == "PING";
        await using var connection = await Connect(server, null, responseTimeout: TimeSpan.FromMilliseconds(150));
        await server.SendRawAsync(Start("FAILING_OVER", 1));
        await WaitForMaintenance(connection);
        var pending = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        await WaitForCommands(server, 3);
        await Task.Delay(350);
        await Assert.That(connection.IsConnected).IsTrue();
        await server.SendRawAsync(Finish("FAILED_OVER", 1));
        await Assert.That(async () => { using var _ = await pending.WaitAsync(TimeSpan.FromSeconds(3)); }).Throws<RespireConnectionException>();
        await connection.Closed.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Test]
    public async Task FullRingWaitUsesRelaxedDeadlineAndNeverAdmitsExpiredWaiter()
    {
        await using var server = Server();
        server.SuppressReply = command => command == "PING";
        await using var connection = await Connect(server, TimeSpan.FromMilliseconds(150), capacity: 1);
        var accepted = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        await WaitForCommands(server, 3);
        var waiting = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        await server.SendRawAsync(Start("MIGRATING", 1));
        await WaitForMaintenance(connection);
        await Task.Delay(350);
        await Assert.That(waiting.IsCompleted).IsFalse();
        await server.SendRawAsync(Finish("MIGRATED", 1));
        await Assert.That(async () => { using var _ = await waiting.WaitAsync(TimeSpan.FromSeconds(3)); }).Throws<RespireTimeoutException>();
        await Assert.That(async () => { using var _ = await accepted.WaitAsync(TimeSpan.FromSeconds(3)); }).Throws<RespireTimeoutException>();
        await Assert.That(server.ReceivedCommands.Count(command => command == "PING")).IsEqualTo(1);
    }

    [Test]
    public async Task NotificationDiagnosticsCannotBlockReceiveLoop()
    {
        await using var server = Server();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Respire",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity =>
            {
                if (activity.OperationName != "redis.maintenance" || !Equals(activity.GetTagItem("server.port"), server.Port)) return;
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            },
        };
        ActivitySource.AddActivityListener(listener);
        await using var connection = await Connect(server, TimeSpan.FromSeconds(2));
        try
        {
            await server.SendRawAsync(Start("MIGRATING", 1));
            await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5)));
            await Assert.That(entered.IsSet).IsTrue();
            using var result = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            await Assert.That(result.AsString()).IsEqualTo("PONG");
        }
        finally { release.Set(); }
    }

    [Test]
    public async Task ReplayedCompletionDuringHandshakeDoesNotRaiseCurrentEvent()
    {
        await using var server = Server(
            ">2\r\n+MIGRATED\r\n:10\r\n+OK\r\n"u8.ToArray());
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var kinds = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Respire",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.OperationName != "redis.maintenance" || !Equals(activity.GetTagItem("server.port"), server.Port)) return;
                var kind = (string)activity.GetTagItem("respire.maintenance.kind")!;
                kinds.Enqueue(kind);
                if (kind == "FAILED_OVER") completed.TrySetResult();
            },
        };
        ActivitySource.AddActivityListener(listener);
        await using var connection = await Connect(server, TimeSpan.FromSeconds(2));
        await server.SendRawAsync(Start("FAILING_OVER", 11));
        await WaitForMaintenance(connection);
        await server.SendRawAsync(Finish("FAILED_OVER", 11));
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(kinds.Contains("MIGRATED")).IsFalse();
        await Assert.That(kinds.Count).IsEqualTo(2);
    }

    [Test]
    [Arguments("MOVING", ">4\r\n+MOVING\r\n:1\r\n:3\r\n_\r\n")]
    [Arguments("MIGRATING", ">3\r\n+MIGRATING\r\n:1\r\n:1\r\n")]
    [Arguments("MIGRATED", ">2\r\n+MIGRATED\r\n:1\r\n")]
    [Arguments("FAILING_OVER", ">3\r\n+FAILING_OVER\r\n:1\r\n:1\r\n")]
    [Arguments("FAILED_OVER", ">2\r\n+FAILED_OVER\r\n:1\r\n")]
    [Arguments("SMIGRATING", ">3\r\n+SMIGRATING\r\n:1\r\n+0-10\r\n")]
    [Arguments("SMIGRATED", ">3\r\n+SMIGRATED\r\n:1\r\n*1\r\n*3\r\n+old:6379\r\n+new:6380\r\n+0-10\r\n")]
    public async Task EveryNotificationHasTelemetryAndPreservesReplyFifo(string kind, string wire)
    {
        await using var server = Server();
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Respire",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == "redis.maintenance" && Equals(activity.GetTagItem("server.port"), server.Port)
                    && Equals(activity.GetTagItem("respire.maintenance.kind"), kind)) observed.TrySetResult();
            },
        };
        ActivitySource.AddActivityListener(listener);
        await using var connection = await Connect(server, TimeSpan.FromSeconds(2));
        await server.SendRawAsync(Encoding.UTF8.GetBytes(wire));
        using var result = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame));
        await Assert.That(result.AsString()).IsEqualTo("PONG");
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    [Arguments(RespireMaintenanceNotificationMode.Disabled, false)]
    [Arguments(RespireMaintenanceNotificationMode.Enabled, true)]
    public async Task DisabledOrMalformedNotificationsDoNotRelaxTimeout(RespireMaintenanceNotificationMode mode, bool malformed)
    {
        await using var server = Server();
        server.SuppressReply = command => command == "PING";
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port, new()
        {
            Protocol = RespProtocol.Resp3, MaintenanceNotifications = mode,
            CommandTimeout = TimeSpan.FromMilliseconds(150),
        });
        await server.SendRawAsync(malformed
            ? ">3\r\n+SMIGRATING\r\n:1\r\n+invalid-slot\r\n"u8.ToArray()
            : Start("MIGRATING", 1));
        var pending = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        await Assert.That(async () => { using var _ = await pending.WaitAsync(TimeSpan.FromSeconds(3)); }).Throws<RespireTimeoutException>();
        await Assert.That(connection.HasMaintenanceWindow).IsFalse();
        await Assert.That(connection.IsConnected).IsTrue();
    }

    [Test]
    public async Task RelaxationNeverShortensLongerConfiguredTimeout()
    {
        await using var server = Server();
        server.SuppressReply = command => command == "PING";
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port, new()
        {
            Protocol = RespProtocol.Resp3, MaintenanceNotifications = RespireMaintenanceNotificationMode.Enabled,
            CommandTimeout = TimeSpan.FromSeconds(2), ResponseTimeout = TimeSpan.FromSeconds(2),
            MaintenanceRelaxedTimeout = TimeSpan.FromMilliseconds(50),
        });
        await server.SendRawAsync(Start("MIGRATING", 1));
        await WaitForMaintenance(connection);
        var pending = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        await WaitForCommands(server, 3);
        await Task.Delay(250);
        await Assert.That(pending.IsCompleted).IsFalse();
        await server.SendRawAsync(FakeRespServer.PongReply);
        using var result = await pending.WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.That(result.AsString()).IsEqualTo("PONG");
    }

    [Test]
    public async Task UnrelatedResp3PushKindsDoNotAllocateDuringMaintenanceParsing()
    {
        var bytes = ">3\r\n+invalidate\r\n:1\r\n*0\r\n"u8.ToArray();
        var position = 0;
        if (RespParser.TryParseValue(bytes, ref position, out var value) != RespParseStatus.Done)
            throw new Exception("Invalid test fixture");
        using (value)
        {
            _ = MaintenanceNotification.Parse(in value); // JIT warm-up.
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 100; i++) _ = MaintenanceNotification.Parse(in value);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            await Assert.That(allocated).IsEqualTo(0L);
        }
    }

    private static MaintenanceNotification? Parse(string wire)
    {
        var position = 0;
        var bytes = Encoding.UTF8.GetBytes(wire);
        if (RespParser.TryParseValue(bytes, ref position, out var value) != RespParseStatus.Done) throw new Exception("Invalid test fixture");
        using (value) return MaintenanceNotification.Parse(in value);
    }

    private static FakeRespServer Server(byte[]? maintenanceReply = null, int maxConnections = 1) => new(maxConnections, FakeRespServer.PongReply)
    {
        ReplyOverride = (_, command) => command switch
        {
            "HELLO 3" => Hello,
            "CLIENT MAINT_NOTIFICATIONS ON" => maintenanceReply ?? FakeRespServer.OkReply,
            _ => null,
        },
    };

    private static RespireOptions Options(FakeRespServer server) => new()
    {
        Endpoints = { new("127.0.0.1", server.Port) },
        MaintenanceNotifications = RespireMaintenanceNotificationMode.Enabled,
        ThreadPoolMonitoring = false,
    };

    private static Task<RespireConnection> Connect(FakeRespServer server, TimeSpan? timeout,
        TimeSpan? responseTimeout = null, TimeSpan? window = null, int capacity = 16)
        => RespireConnection.ConnectAsync("127.0.0.1", server.Port, new()
        {
            Protocol = RespProtocol.Resp3,
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Enabled,
            CommandTimeout = timeout,
            ResponseTimeout = responseTimeout,
            MaintenanceRelaxedTimeout = TimeSpan.FromSeconds(5),
            MaintenanceWindowTimeout = window ?? TimeSpan.FromSeconds(10),
            MaxInflightCommands = capacity,
        });

    private static byte[] Start(string kind, int sequence) => Encoding.UTF8.GetBytes(
        $">3\r\n+{kind}\r\n:{sequence}\r\n" + (kind == "SMIGRATING" ? "+0-10\r\n" : ":1\r\n"));
    private static byte[] Finish(string kind, int sequence) => Encoding.UTF8.GetBytes(kind == "SMIGRATED"
        ? $">3\r\n+{kind}\r\n:{sequence}\r\n*1\r\n*3\r\n+old:6379\r\n+new:6380\r\n+0-10\r\n"
        : $">2\r\n+{kind}\r\n:{sequence}\r\n");

    private static async Task WaitForCommands(FakeRespServer server, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (server.CommandsSeen < count) await Task.Delay(5, timeout.Token);
    }

    private static async Task WaitForMaintenance(RespireConnection connection)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!connection.HasMaintenanceWindow) await Task.Delay(5, timeout.Token);
    }
}
