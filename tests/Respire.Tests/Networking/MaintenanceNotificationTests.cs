using System.Runtime.CompilerServices;
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
    [Arguments(">3\r\n+SMIGRATING\r\n:1\r\n+1,,2\r\n")]
    [Arguments(">3\r\n+SMIGRATING\r\n:1\r\n+1-\r\n")]
    [Arguments(">3\r\n+SMIGRATING\r\n:1\r\n+-1\r\n")]
    [Arguments(">3\r\n+SMIGRATING\r\n:1\r\n+ 1\r\n")]
    [Arguments(">3\r\n+SMIGRATING\r\n:1\r\n+1,\r\n")]
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
    public async Task CapacityEvictsOldestFinishedIdentityFirst()
    {
        var state = new MaintenanceTimeoutState(1000);
        for (var i = 0; i < 256; i++)
        {
            state.Apply(new("MIGRATING", i), 0);
            state.Apply(new("MIGRATED", i), 0);
        }
        state.Apply(new("MIGRATING", 1000), 10); // Evicts sequence 0.
        state.Apply(new("MIGRATED", 1000), 10);
        state.Apply(new("MIGRATING", 2000), 20); // Evicts sequence 1, not the newer 1000.
        state.Apply(new("MIGRATED", 2000), 20);
        await Assert.That(state.Remaining(30)).IsEqualTo(0);
        state.Apply(new("MIGRATING", 1000), 30); // Recent replay remains suppressed.
        await Assert.That(state.Remaining(30)).IsEqualTo(0);
        state.Apply(new("MIGRATING", 2), 30);
        await Assert.That(state.Remaining(30)).IsEqualTo(0);
        state.Apply(new("MIGRATING", 1), 40); // Evicted identity is treated as new.
        await Assert.That(state.Remaining(40)).IsEqualTo(1000);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task PushesFollowingAcknowledgementInSameReadAreHandled(bool completed)
    {
        var reply = FakeRespServer.OkReply.Concat(Start("MIGRATING", 7));
        if (completed) reply = reply.Concat(Finish("MIGRATED", 7));
        await using var server = Server(reply.ToArray());
        await using var connection = await Connect(server, TimeSpan.FromSeconds(2));
        using var result = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.That(result.AsString()).IsEqualTo("PONG");
        await Assert.That(connection.HasMaintenanceWindow).IsEqualTo(!completed);
    }

    [Test]
    public async Task MovingConnectsTargetBeforePublishingAndReroutesStaleSelection()
    {
        await using var source = Server(maxConnections: 2);
        await using var target = Server(maxConnections: 2);
        await using var multiplexer = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", source.Port,
            options: Options(source).ToConnectionOptions(enableMaintenanceNotifications: true));
        var staleSelection = multiplexer.GetConnection();

        await source.SendRawAsync(Encoding.UTF8.GetBytes(
            $">4\r\n+MOVING\r\n:1\r\n:10\r\n+127.0.0.1:{target.Port}\r\n"));
        await WaitForCommands(target, 2); // HELLO and maintenance negotiation completed on replacement.
        await WaitForRetirement(staleSelection);
        using var pong = await staleSelection.SendAsync(new RawCommand(FakeRespServer.PingFrame))
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await staleSelection.SendFireAndForgetAsync(new RawCommand(FakeRespServer.PingFrame))
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var typedPong = await staleSelection.SendStringAsync(new RawCommand(FakeRespServer.PingFrame))
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForCommands(target, 5);

        await Assert.That(pong.AsString()).IsEqualTo("PONG");
        await Assert.That(typedPong).IsEqualTo("PONG");
        await Assert.That(target.ReceivedCommands.Count(command => command == "PING")).IsEqualTo(3);
        await Assert.That(multiplexer.GetConnection().Port).IsEqualTo(target.Port);
    }

    [Test]
    public async Task MovingReroutePreservesCheckedErrorHandling()
    {
        await using var source = Server(maxConnections: 2);
        await using var target = Server(maxConnections: 2);
        var targetReply = target.ReplyOverride!;
        target.ReplyOverride = (connectionId, command) => command == "PING"
            ? "-ERR target rejected PING\r\n"u8.ToArray()
            : targetReply(connectionId, command);
        await using var multiplexer = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", source.Port,
            options: Options(source).ToConnectionOptions(enableMaintenanceNotifications: true));
        var staleSelection = multiplexer.GetConnection();

        await source.SendRawAsync(Encoding.UTF8.GetBytes(
            $">4\r\n+MOVING\r\n:1\r\n:10\r\n+127.0.0.1:{target.Port}\r\n"));
        await WaitForCommands(target, 2);
        await WaitForRetirement(staleSelection);

        await Assert.That(async () => await staleSelection.SendCheckedAsync(
                new RawCommand(FakeRespServer.PingFrame), commandName: "PING").AsTask())
            .Throws<RespireServerException>();
    }

    [Test]
    public async Task MovingDrainsAcceptedReplyAndReroutesProducerParkedOnFullRing()
    {
        await using var source = Server(maxConnections: 2);
        source.DelayReply(2, 250);
        await using var target = Server(maxConnections: 2);
        var connectionOptions = Options(source).ToConnectionOptions(enableMaintenanceNotifications: true) with
        {
            MaxInflightCommands = 1,
            CommandTimeout = TimeSpan.FromSeconds(5),
        };
        await using var multiplexer = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", source.Port,
            options: connectionOptions);
        var staleSelection = multiplexer.GetConnection();

        var accepted = staleSelection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        await WaitForCommands(source, 3);
        var waitingForCapacity = staleSelection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        await source.SendRawAsync(Encoding.UTF8.GetBytes(
            $">4\r\n+MOVING\r\n:1\r\n:10\r\n+127.0.0.1:{target.Port}\r\n"));
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            while (multiplexer.GetConnection().Port != target.Port)
                await Task.Delay(5, timeout.Token);
        }

        using var acceptedReply = await accepted.WaitAsync(TimeSpan.FromSeconds(5));
        using var reroutedReply = await waitingForCapacity.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(acceptedReply.AsString()).IsEqualTo("PONG");
        await Assert.That(reroutedReply.AsString()).IsEqualTo("PONG");
        await Assert.That(target.ReceivedCommands.Count(command => command == "PING")).IsEqualTo(1);
    }

    [Test]
    public async Task MovingReroutePreservesMaintenanceRelaxedDeadline()
    {
        await using var source = Server(maxConnections: 2);
        source.DelayReply(2, 450);
        await using var target = Server(maxConnections: 2);
        target.DelayReply(2, 450);
        var connectionOptions = Options(source).ToConnectionOptions(enableMaintenanceNotifications: true) with
        {
            MaxInflightCommands = 1,
            CommandTimeout = TimeSpan.FromMilliseconds(300),
            MaintenanceRelaxedTimeout = TimeSpan.FromSeconds(2),
            MaintenanceWindowTimeout = TimeSpan.FromSeconds(4),
        };
        await using var multiplexer = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", source.Port,
            options: connectionOptions);
        var staleSelection = multiplexer.GetConnection();

        var accepted = staleSelection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        await WaitForCommands(source, 3);
        var waitingForCapacity = staleSelection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        await source.SendRawAsync(Start("MIGRATING", 1));
        await WaitForMaintenance(staleSelection);
        await source.SendRawAsync(Moving(1, target.Port));

        using var acceptedReply = await accepted.WaitAsync(TimeSpan.FromSeconds(5));
        using var reroutedReply = await waitingForCapacity.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(acceptedReply.AsString()).IsEqualTo("PONG");
        await Assert.That(reroutedReply.AsString()).IsEqualTo("PONG");
        await Assert.That(target.ReceivedCommands.Count(command => command == "PING")).IsEqualTo(1);
    }

    [Test]
    public async Task NewerMovingCancelsObsoleteTargetConnect()
    {
        await using var source = Server(maxConnections: 2);
        await using var obsoleteTarget = Server(maxConnections: 2);
        obsoleteTarget.DelayReply(0, 2000);
        await using var currentTarget = Server(maxConnections: 2);
        await using var multiplexer = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", source.Port,
            options: Options(source).ToConnectionOptions(enableMaintenanceNotifications: true) with
            {
                ConnectTimeout = TimeSpan.FromSeconds(10),
            });

        await source.SendRawAsync(Moving(1, obsoleteTarget.Port));
        await WaitForCommands(obsoleteTarget, 1);
        await source.SendRawAsync(Moving(2, currentTarget.Port));
        await WaitForPort(multiplexer, currentTarget.Port);
        using var pong = await multiplexer.GetConnection().SendAsync(new RawCommand(FakeRespServer.PingFrame))
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(pong.AsString()).IsEqualTo("PONG");
        await Assert.That(currentTarget.ReceivedCommands.Count(command => command == "PING")).IsEqualTo(1);
    }

    [Test]
    public async Task MovingFromReplacementServerIsHonouredDespiteLowerSequence()
    {
        await using var source = Server(maxConnections: 2);
        await using var target = Server(maxConnections: 2);
        await using var final = Server(maxConnections: 2);
        await using var multiplexer = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", source.Port,
            options: Options(source).ToConnectionOptions(enableMaintenanceNotifications: true));

        await source.SendRawAsync(Moving(7, target.Port));
        await WaitForPort(multiplexer, target.Port);
        // Sequence IDs belong to the announcing server; the replacement starts its own numbering.
        await target.SendRawAsync(Moving(1, final.Port));
        await WaitForPort(multiplexer, final.Port);

        using var pong = await multiplexer.GetConnection().SendAsync(new RawCommand(FakeRespServer.PingFrame))
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(pong.AsString()).IsEqualTo("PONG");
        await Assert.That(final.ReceivedCommands.Count(command => command == "PING")).IsEqualTo(1);
    }

    [Test]
    public async Task MovingParsedBeforePublicationRemainsEligibleAfterCallbackDelay()
    {
        await using var source = Server(maxConnections: 2);
        await using var firstTarget = Server(maxConnections: 2);
        await using var secondTarget = Server(maxConnections: 2);
        await using var delayedTarget = Server(maxConnections: 2);
        await using var multiplexer = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", source.Port,
            options: Options(source).ToConnectionOptions(enableMaintenanceNotifications: true));
        var announcingConnection = multiplexer.GetConnection();
        var waitForPublication = typeof(RespireConnectionMultiplexer).GetMethod("WaitForPublicationAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        await source.SendRawAsync(Moving(1, firstTarget.Port));
        await WaitForPort(multiplexer, firstTarget.Port);
        await ((Task)waitForPublication.Invoke(multiplexer, null)!).WaitAsync(TimeSpan.FromSeconds(5));
        await firstTarget.SendRawAsync(Moving(1, secondTarget.Port));
        await WaitForPort(multiplexer, secondTarget.Port);
        await ((Task)waitForPublication.Invoke(multiplexer, null)!).WaitAsync(TimeSpan.FromSeconds(5));

        var delayedNotification = new MaintenanceNotification("MOVING", 2, 10,
            new RespireEndpoint("127.0.0.1", delayedTarget.Port));
        var queueHandoff = typeof(RespireConnectionMultiplexer).GetMethod("QueueMovingHandoff",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        queueHandoff.Invoke(multiplexer,
            [0, announcingConnection, delayedNotification, announcingConnection.MovingPublicationGeneration]);
        await WaitForPort(multiplexer, delayedTarget.Port);

        using var pong = await multiplexer.GetConnection().SendAsync(new RawCommand(FakeRespServer.PingFrame))
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(pong.AsString()).IsEqualTo("PONG");
        await Assert.That(delayedTarget.ReceivedCommands.Count(command => command == "PING")).IsEqualTo(1);
    }

    [Test]
    public async Task MovingDoesNotRerouteConnectionScopedIdentityCommands()
    {
        await using var source = Server(maxConnections: 2);
        await using var target = Server(maxConnections: 2);
        await using var multiplexer = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", source.Port,
            options: Options(source).ToConnectionOptions(enableMaintenanceNotifications: true));
        var staleSelection = multiplexer.GetConnection();

        await source.SendRawAsync(Moving(1, target.Port));
        await WaitForRetirement(staleSelection);

        // CLIENT ID names the socket that runs it, so a retired socket must not borrow another.
        await Assert.That(async () => await staleSelection.EnsureServerClientIdAsync())
            .Throws<RespireConnectionRetiredException>();
        await Assert.That(target.ReceivedCommands.Contains("CLIENT ID")).IsFalse();
    }

    [Test]
    public async Task LaterMovingStartsWhileEarlierSocketsStillDrain()
    {
        await using var source = Server(maxConnections: 2);
        source.DelayReply(2, 3000);
        await using var target = Server(maxConnections: 2);
        await using var final = Server(maxConnections: 2);
        await using var multiplexer = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", source.Port,
            options: Options(source).ToConnectionOptions(enableMaintenanceNotifications: true));
        var accepted = multiplexer.GetConnection().SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        await WaitForCommands(source, 3);

        await source.SendRawAsync(Moving(1, target.Port));
        await WaitForPort(multiplexer, target.Port);
        await target.SendRawAsync(Moving(1, final.Port));
        // The second handoff must not wait for the first one's three-second drain.
        await WaitForPort(multiplexer, final.Port);
        await Assert.That(accepted.IsCompleted).IsFalse();

        using var reply = await accepted.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(reply.AsString()).IsEqualTo("PONG");
    }

    [Test]
    public async Task MovingRetriesTargetSetupWithinGracePeriod()
    {
        await using var source = Server(maxConnections: 2);
        await using var target = Server(maxConnections: 4);
        var targetReply = target.ReplyOverride!;
        var negotiations = 0;
        target.ReplyOverride = (connectionId, command) =>
            command == "CLIENT MAINT_NOTIFICATIONS ON" && Interlocked.Increment(ref negotiations) == 1
                ? "-ERR maintenance subsystem unavailable\r\n"u8.ToArray()
                : targetReply(connectionId, command);
        await using var multiplexer = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", source.Port,
            options: Options(source).ToConnectionOptions(enableMaintenanceNotifications: true));

        await source.SendRawAsync(Moving(1, target.Port, seconds: 10));
        await WaitForPort(multiplexer, target.Port);

        await Assert.That(Volatile.Read(ref negotiations)).IsEqualTo(2);
    }

    [Test]
    public async Task MovingKeepsCurrentConnectionsWhenTargetStaysUnavailable()
    {
        await using var source = Server(maxConnections: 2);
        await using var target = Server("-ERR maintenance subsystem unavailable\r\n"u8.ToArray(), maxConnections: 64);
        await using var multiplexer = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", source.Port,
            options: Options(source).ToConnectionOptions(enableMaintenanceNotifications: true));

        await source.SendRawAsync(Moving(1, target.Port, seconds: 1));
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            // Wait for more than one setup attempt, then for the grace period to lapse.
            while (target.ReceivedCommands.Count(command => command == "CLIENT MAINT_NOTIFICATIONS ON") < 2)
                await Task.Delay(5, timeout.Token);
        }
        await Task.Delay(1200);

        using var pong = await multiplexer.GetConnection().SendAsync(new RawCommand(FakeRespServer.PingFrame))
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(pong.AsString()).IsEqualTo("PONG");
        await Assert.That(multiplexer.GetConnection().Port).IsEqualTo(source.Port);
    }

    private static byte[] Moving(long sequence, int port, int seconds = 10)
        => Encoding.UTF8.GetBytes($">4\r\n+MOVING\r\n:{sequence}\r\n:{seconds}\r\n+127.0.0.1:{port}\r\n");

    private static async Task WaitForRetirement(RespireConnection connection)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (connection.IsAcceptingCommands) await Task.Delay(5, timeout.Token);
    }

    private static async Task WaitForPort(RespireConnectionMultiplexer multiplexer, int port)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (multiplexer.GetConnection().Port != port) await Task.Delay(5, timeout.Token);
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
    public async Task DeadlineSweepHonorsRerouteMarkerWithoutReextending()
    {
        const long deadline = 2000;
        var pool = new PendingResponsePool(1);
        var ring = new InflightRing(1);
        var source = pool.Rent(commandName: "PING");
        source.Deadline = deadline | (1L << 62);
        ring.TryEnqueue(source);

        var remaining = ring.SweepExpired(deadline - 1, TimeSpan.FromMilliseconds(100), null,
            deadlineExtension: 500, maintenanceStarted: deadline - 500);
        await Assert.That(remaining).IsEqualTo(1);
        remaining = ring.SweepExpired(deadline, TimeSpan.FromMilliseconds(100), null,
            deadlineExtension: 500, maintenanceStarted: deadline - 500);
        await Assert.That(remaining).IsEqualTo(-1);
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
    public async Task CapacityWakeAfterCommandDeadlineTimesOutBeforeSending()
    {
        await using var server = Server();
        server.SuppressReply = command => command == "PING";
        await using var connection = await Connect(server, TimeSpan.FromMilliseconds(200), capacity: 1);
        var first = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        await WaitForCommands(server, 3);
        await server.SendRawAsync(Start("MIGRATING", 11));
        await WaitForMaintenance(connection);

        var waiting = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        await Task.Delay(400);
        await server.SendRawAsync(Finish("MIGRATED", 11).Concat("+first\r\n"u8.ToArray()).ToArray());

        await Assert.That(async () => { using var _ = await waiting.WaitAsync(TimeSpan.FromSeconds(5)); })
            .Throws<RespireTimeoutException>();
        using var firstReply = await first.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(firstReply.AsString()).IsEqualTo("first");
        await Assert.That(server.CommandsSeen).IsEqualTo(3);
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
    [NotInParallel] // The shared no-GC measurement boundary is process-wide.
    public async Task UnrelatedResp3PushKindsDoNotAllocateDuringMaintenanceParsing()
    {
        var bytes = ">3\r\n+invalidate\r\n:1\r\n*0\r\n"u8.ToArray();
        var position = 0;
        if (RespParser.TryParseValue(bytes, ref position, out var value) != RespParseStatus.Done)
            throw new Exception("Invalid test fixture");
        using (value)
        {
            _ = MeasureMaintenanceParsing(value, allocate: false); // JIT warm-up.
            _ = MeasureMaintenanceParsing(value, allocate: true);
            // Concurrent GC can perturb the thread allocation counter. See docs/ALLOCATION_MEASUREMENT.md.
            var (allocated, control) = AllocationMeasurement.WithoutConcurrentGc(() =>
                (MeasureMaintenanceParsing(value, allocate: false), MeasureMaintenanceParsing(value, allocate: true)));
            await Assert.That(allocated).IsEqualTo(0L);
            await Assert.That(control).IsGreaterThanOrEqualTo(100L * 37);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureMaintenanceParsing(RespValue value, bool allocate)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            _ = MaintenanceNotification.Parse(in value);
            if (allocate) GC.KeepAlive(AllocateControl());
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object AllocateControl() => new byte[37];

    [Test]
    public async Task FailingActivityListenerDoesNotSuppressMetricOrLog()
    {
        var host = $"maintenance-isolation-{Guid.NewGuid():N}";
        var counted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var activities = new ActivityListener
        {
            ShouldListenTo = source => source.Name == RespireTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity =>
            {
                if (Equals(activity.GetTagItem("server.address"), host))
                    throw new InvalidOperationException("Listener failure.");
            },
        };
        ActivitySource.AddActivityListener(activities);
        using var meters = MeterFor("respire.maintenance.notifications", (value, tags) =>
        {
            if (HasTag(tags, "server.address", host)) counted.TrySetResult();
        });
        var logger = new RecordingLogger();

        new MaintenanceTelemetry(host, 6379, 0, logger).Publish(new("MIGRATING", 1, 5));

        await counted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await logger.Informed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(logger.Warned).IsTrue();
    }

    [Test]
    [NotInParallel] // Isolate the bounded process-wide diagnostic listener workload.
    public async Task OverflowDropsAreReportedOnceDeliveryResumes()
    {
        var host = $"maintenance-overflow-{Guid.NewGuid():N}";
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var blocked = 0;
        using var activities = new ActivityListener
        {
            ShouldListenTo = source => source.Name == RespireTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity =>
            {
                if (!Equals(activity.GetTagItem("server.address"), host) || Interlocked.Exchange(ref blocked, 1) != 0) return;
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            },
        };
        ActivitySource.AddActivityListener(activities);
        long delivered = 0, dropped = 0;
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var notifications = MeterFor("respire.maintenance.notifications", (value, tags) =>
        {
            if (HasTag(tags, "server.address", host) && Interlocked.Add(ref delivered, value) == 257) drained.TrySetResult();
        });
        using var drops = MeterFor("respire.maintenance.notifications.dropped", (value, tags) =>
        {
            if (HasTag(tags, "server.address", host) && HasTag(tags, "server.port", 6379))
                Interlocked.Add(ref dropped, value);
        });
        var telemetry = new MaintenanceTelemetry(host, 6379, 0, null);

        telemetry.Publish(new("MIGRATING", 0, 5));
        await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5)));
        await Assert.That(entered.IsSet).IsTrue();
        // The first event is in flight; 258 more overflow the 256-entry queue by two.
        for (var i = 1; i <= 258; i++) telemetry.Publish(new("MIGRATING", i, 5));
        release.Set();

        await drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(Interlocked.Read(ref dropped)).IsEqualTo(2L);
    }

    private static System.Diagnostics.Metrics.MeterListener MeterFor(string name,
        MeasurementCallback onMeasurement)
    {
        var listener = new System.Diagnostics.Metrics.MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == RespireTelemetry.SourceName && instrument.Name == name)
                    meterListener.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) => onMeasurement(value, tags));
        listener.Start();
        return listener;
    }

    private delegate void MeasurementCallback(long value, ReadOnlySpan<KeyValuePair<string, object?>> tags);

    private static bool HasTag(ReadOnlySpan<KeyValuePair<string, object?>> tags, string key, object? value)
    {
        foreach (var tag in tags)
            if (tag.Key == key && Equals(tag.Value, value)) return true;
        return false;
    }

    private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger
    {
        internal readonly TaskCompletionSource Informed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal volatile bool Warned;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            // The warning for the failed activity sink precedes the information log.
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning && exception is InvalidOperationException) Warned = true;
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Information) Informed.TrySetResult();
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
