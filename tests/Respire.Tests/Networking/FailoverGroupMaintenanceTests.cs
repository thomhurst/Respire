using System.Text;
using Respire.Internal;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class FailoverGroupMaintenanceTests
{
    [Test]
    [Arguments("MIGRATING")]
    [Arguments("FAILING_OVER")]
    public async Task MemberCommandsKeepRelaxedTimeoutDuringMaintenance(string kind)
    {
        await using var server = Server();
        var candidate = Candidate(server);
        candidate = candidate with { Options = candidate.Options with { CommandTimeout = TimeSpan.FromSeconds(1) } };
        await using var group = await RespireFailoverGroup.ConnectAsync([candidate], ManualOptions() with
        {
            ProbeInterval = TimeSpan.FromMilliseconds(15),
        });
        var connection = ((RespireClient)group.ActiveClient).Core.Multiplexer.GetConnection();
        await server.SendRawAsync(Start(kind));
        await WaitUntilAsync(() => connection.HasMaintenanceWindow);
        server.DelayCommand("PING", 1500);

        await group.ActiveClient.PingAsync();
        await Assert.That(group.IsConnected).IsTrue();
        await Assert.That(group.GetEndpointStatuses()[0].ConsecutiveFailures).IsEqualTo(0);
    }

    [Test]
    public async Task MaintenanceCannotEstablishInitialHealth()
    {
        await using var stalled = Server();
        await using var fallback = Server();
        var replies = stalled.ReplyOverride!;
        stalled.ReplyOverride = (id, command) => command == "CLIENT MAINT_NOTIFICATIONS ON"
            ? [.. FakeRespServer.OkReply, .. Start("MIGRATING")] : replies(id, command);
        stalled.SuppressReply = command => command == "PING";
        await using var group = await RespireFailoverGroup.ConnectAsync(
            [Candidate(stalled), Candidate(fallback, priority: 1)], ManualOptions());

        await Assert.That(group.ActiveClient.Endpoint.Port).IsEqualTo(fallback.Port);
        await Assert.That(group.GetEndpointStatuses()[0].ConsecutiveFailures).IsEqualTo(1);
    }

    [Test]
    public async Task WindowsKeepSeparateSocketSequencesAndObserveCompletions()
    {
        var tracker = new FailoverMaintenanceWindows();
        var first = new MaintenanceTimeoutState(5000);
        var second = new MaintenanceTimeoutState(5000);
        var now = Environment.TickCount64;
        tracker.Observe(first, first.Apply(new("MIGRATING", 1), now));
        tracker.Observe(second, second.Apply(new("MIGRATING", 1), now));

        tracker.Observe(first, first.Apply(new("MIGRATED", 1), now));
        await Assert.That(tracker.IsActive).IsTrue();
        tracker.Observe(second, second.Apply(new("MIGRATED", 1), now));
        await Assert.That(tracker.IsActive).IsFalse();
        tracker.Observe(first, first.Apply(new("FAILING_OVER", 2), now));
        await Assert.That(tracker.IsActive).IsTrue();
    }

    [Test]
    [Arguments("MIGRATING", "MIGRATED")]
    [Arguments("FAILING_OVER", "FAILED_OVER")]
    public async Task MaintenanceDuringProbeDoesNotCountFailureAndCompletionResumesCounting(string start, string finish)
    {
        await using var server = Server();
        await using var group = await RespireFailoverGroup.ConnectAsync([Candidate(server)], ManualOptions());
        var client = (RespireClient)group.ActiveClient;
        var connection = client.Core.Multiplexer.GetConnection();
        var replies = server.ReplyOverride!;
        server.ReplyOverride = (id, command) => command == "PING" ? Start(start) : replies(id, command);

        // Start maintenance while the health probe is already waiting for its PONG.
        await group.ForTests.ProbeAsync(0);
        await Assert.That(group.GetEndpointStatuses()[0].ConsecutiveFailures).IsEqualTo(0);
        await Assert.That(group.GetEndpointStatuses()[0].IsHealthy).IsTrue();
        await Assert.That(group.ForTests.Circuit(0).State).IsEqualTo(EndpointCircuitState.Closed);
        var commands = server.CommandsSeen;
        await group.ForTests.ProbeAsync(0);
        await Assert.That(server.CommandsSeen).IsEqualTo(commands);

        // The late PONG drains the cancelled probe's FIFO entry before the next probe.
        await server.SendRawAsync([.. Finish(finish), .. FakeRespServer.PongReply]);
        await WaitUntilAsync(() => !connection.HasMaintenanceWindow);
        server.ReplyOverride = (id, command) => command == "PING" ? "-ERR unavailable\r\n"u8.ToArray() : replies(id, command);
        await group.ForTests.ProbeAsync(0);
        await Assert.That(group.GetEndpointStatuses()[0].ConsecutiveFailures).IsEqualTo(1);
        await Assert.That(group.GetEndpointStatuses()[0].IsHealthy).IsFalse();
    }

    [Test]
    [Arguments("MIGRATING", "MIGRATED")]
    [Arguments("FAILING_OVER", "FAILED_OVER")]
    public async Task MaintenanceCompletedDuringProbeDoesNotCountFailure(string start, string finish)
    {
        await using var server = Server();
        await using var group = await RespireFailoverGroup.ConnectAsync([Candidate(server)], ManualOptions());
        var connection = ((RespireClient)group.ActiveClient).Core.Multiplexer.GetConnection();
        var replies = server.ReplyOverride!;
        server.ReplyOverride = (id, command) => command == "PING"
            ? [.. Start(start), .. Finish(finish), .. "-ERR unavailable\r\n"u8.ToArray()]
            : replies(id, command);

        // Both pushes precede the error on the wire, so the window closes before the probe fails.
        await group.ForTests.ProbeAsync(0);
        await Assert.That(connection.HasMaintenanceWindow).IsFalse();
        await Assert.That(group.GetEndpointStatuses()[0].ConsecutiveFailures).IsEqualTo(0);
        await Assert.That(group.GetEndpointStatuses()[0].IsHealthy).IsTrue();
        await Assert.That(group.ForTests.Circuit(0).State).IsEqualTo(EndpointCircuitState.Closed);

        // A subsequent failure that does not overlap maintenance must still open the circuit.
        server.ReplyOverride = (id, command) => command == "PING" ? "-ERR unavailable\r\n"u8.ToArray() : replies(id, command);
        await group.ForTests.ProbeAsync(0);
        await Assert.That(group.GetEndpointStatuses()[0].ConsecutiveFailures).IsEqualTo(1);
        await Assert.That(group.GetEndpointStatuses()[0].IsHealthy).IsFalse();
    }

    [Test]
    [Arguments("MIGRATING", "MIGRATED", true)]
    [Arguments("FAILING_OVER", "FAILED_OVER", true)]
    [Arguments("MIGRATING", "MIGRATED", false)]
    [Arguments("FAILING_OVER", "FAILED_OVER", false)]
    public async Task ReplayedClosedMaintenanceDuringProbeCountsFailure(string start, string finish, bool completed)
    {
        await using var server = Server();
        await using var group = await RespireFailoverGroup.ConnectAsync(
            [Candidate(server, window: TimeSpan.FromSeconds(1))], ManualOptions());
        var connection = ((RespireClient)group.ActiveClient).Core.Multiplexer.GetConnection();
        await server.SendRawAsync(Start(start));
        await WaitUntilAsync(() => connection.HasMaintenanceWindow);
        if (completed) await server.SendRawAsync(Finish(finish));
        await WaitUntilAsync(() => !connection.HasMaintenanceWindow);
        var replies = server.ReplyOverride!;
        server.ReplyOverride = (id, command) => command == "PING"
            ? [.. Start(start), .. "-ERR unavailable\r\n"u8.ToArray()]
            : replies(id, command);

        // Replaying the closed sequence cannot turn an ordinary failed probe into maintenance.
        await group.ForTests.ProbeAsync(0);
        await Assert.That(connection.HasMaintenanceWindow).IsFalse();
        await Assert.That(group.GetEndpointStatuses()[0].ConsecutiveFailures).IsEqualTo(1);
        await Assert.That(group.GetEndpointStatuses()[0].IsHealthy).IsFalse();
        await Assert.That(group.ForTests.Circuit(0).State).IsEqualTo(EndpointCircuitState.Open);
    }

    [Test]
    [Arguments("MIGRATED")]
    [Arguments("FAILED_OVER")]
    public async Task CompletionWithoutMaintenanceDuringProbeCountsFailure(string finish)
    {
        await using var server = Server();
        await using var group = await RespireFailoverGroup.ConnectAsync([Candidate(server)], ManualOptions());
        var replies = server.ReplyOverride!;
        server.ReplyOverride = (id, command) => command == "PING"
            ? [.. Finish(finish), .. "-ERR unavailable\r\n"u8.ToArray()]
            : replies(id, command);

        await group.ForTests.ProbeAsync(0);
        await Assert.That(group.GetEndpointStatuses()[0].ConsecutiveFailures).IsEqualTo(1);
        await Assert.That(group.GetEndpointStatuses()[0].IsHealthy).IsFalse();
    }

    [Test]
    public async Task ZeroGraceMovingRecordsOverlapWithoutOpeningWindow()
    {
        var tracker = new FailoverMaintenanceWindows();
        var state = new MaintenanceTimeoutState(5000);
        var generation = tracker.Generation;
        var notification = new MaintenanceNotification("MOVING", 1, 0, new RespireEndpoint("127.0.0.1", 6379));
        tracker.Observe(state, state.Apply(notification, Environment.TickCount64));

        await Assert.That(tracker.Generation).IsGreaterThan(generation);
        await Assert.That(tracker.IsActive).IsFalse();
        generation = tracker.Generation;
        tracker.Observe(state, state.Apply(notification, Environment.TickCount64));
        await Assert.That(tracker.Generation).IsEqualTo(generation);
    }

    [Test]
    public async Task ZeroGraceMovingDuringProbeDoesNotCountFailure()
    {
        await using var source = Server();
        await using var target = Server();
        await using var group = await RespireFailoverGroup.ConnectAsync([Candidate(source)], ManualOptions());
        var original = group.ActiveClient;
        var multiplexer = ((RespireClient)original).Core.Multiplexer;
        var sourceConnection = multiplexer.GetConnection();
        var replies = source.ReplyOverride!;
        target.DelayCommand("HELLO", 200);
        source.ReplyOverride = (id, command) => command == "PING"
            ? [.. Encoding.UTF8.GetBytes($">4\r\n+MOVING\r\n:1\r\n:0\r\n+127.0.0.1:{target.Port}\r\n"),
                .. "-ERR unavailable\r\n"u8.ToArray()]
            : replies(id, command);

        // Zero grace closes the window immediately, but the handoff still overlaps this probe.
        await group.ForTests.ProbeAsync(0);
        await Assert.That(sourceConnection.HasMaintenanceWindow).IsFalse();
        await Assert.That(group.GetEndpointStatuses()[0].ConsecutiveFailures).IsEqualTo(0);
        await Assert.That(group.GetEndpointStatuses()[0].IsHealthy).IsTrue();
        await Assert.That(group.ForTests.Circuit(0).State).IsEqualTo(EndpointCircuitState.Closed);
        await WaitUntilAsync(() => multiplexer.ActiveConnectionEndpoint.Port == target.Port);
        await group.ActiveClient.PingAsync();
        await Assert.That(ReferenceEquals(group.ActiveClient, original)).IsTrue();

        // A failure after the handoff must resume normal circuit policy.
        var targetReplies = target.ReplyOverride!;
        target.ReplyOverride = (id, command) => command == "PING" ? "-ERR unavailable\r\n"u8.ToArray() : targetReplies(id, command);
        await group.ForTests.ProbeAsync(0);
        await Assert.That(group.GetEndpointStatuses()[0].ConsecutiveFailures).IsEqualTo(1);
        await Assert.That(group.GetEndpointStatuses()[0].IsHealthy).IsFalse();
    }

    [Test]
    public async Task MaintenanceWindowSurvivesSocketLossButExpires()
    {
        await using var server = Server();
        await using var group = await RespireFailoverGroup.ConnectAsync(
            [Candidate(server, window: TimeSpan.FromSeconds(1))], ManualOptions());
        var connection = ((RespireClient)group.ActiveClient).Core.Multiplexer.GetConnection();
        await server.SendRawAsync(Start("FAILING_OVER"));
        await WaitUntilAsync(() => connection.HasMaintenanceWindow);
        await server.DisposeAsync();

        await group.ForTests.ProbeAsync(0);
        await Assert.That(group.GetEndpointStatuses()[0].ConsecutiveFailures).IsEqualTo(0);
        await WaitUntilAsync(() => !connection.HasMaintenanceWindow);
        await group.ForTests.ProbeAsync(0);
        await Assert.That(group.GetEndpointStatuses()[0].ConsecutiveFailures).IsEqualTo(1);
        await Assert.That(group.GetEndpointStatuses()[0].IsHealthy).IsFalse();
    }

    [Test]
    public async Task MovingStaysInMemberAndRealOutageSwitchesGroup()
    {
        await using var source = Server();
        await using var target = Server();
        await using var fallback = Server();
        await using var group = await RespireFailoverGroup.ConnectAsync(
            [Candidate(source), Candidate(fallback, priority: 1)], ManualOptions() with
            {
                ProbeInterval = TimeSpan.FromMilliseconds(15),
                ProbeTimeout = TimeSpan.FromSeconds(1),
            });
        var original = group.ActiveClient;
        var switches = 0;
        group.EndpointSwitched += _ => Interlocked.Increment(ref switches);
        var multiplexer = ((RespireClient)original).Core.Multiplexer;
        var originalConnection = multiplexer.GetConnection();
        target.DelayCommand("HELLO", 200);
        await source.SendRawAsync(Encoding.UTF8.GetBytes(
            $">4\r\n+MOVING\r\n:1\r\n:2\r\n+127.0.0.1:{target.Port}\r\n"));
        await WaitUntilAsync(() => originalConnection.HasMaintenanceWindow);
        // Lose the source while the replacement is still negotiating its protocol.
        await source.DisposeAsync();
        await WaitUntilAsync(() => multiplexer.ActiveConnectionEndpoint.Port == target.Port);
        await group.ActiveClient.PingAsync();
        await Assert.That(ReferenceEquals(group.ActiveClient, original)).IsTrue();
        await Assert.That(Volatile.Read(ref switches)).IsEqualTo(0);
        await Assert.That(group.GetEndpointStatuses()[0].ConsecutiveFailures).IsEqualTo(0);

        await target.DisposeAsync();
        await WaitUntilAsync(() => !ReferenceEquals(group.ActiveClient, original));
        await Assert.That(group.ActiveClient.Endpoint.Port).IsEqualTo(fallback.Port);
        await Assert.That(Volatile.Read(ref switches)).IsEqualTo(1);
    }

    private static FakeRespServer Server() => new(8, FakeRespServer.PongReply)
    {
        ReplyOverride = (_, command) => command switch
        {
            "HELLO 3" => "%1\r\n+proto\r\n:3\r\n"u8.ToArray(),
            "CLIENT MAINT_NOTIFICATIONS ON" => FakeRespServer.OkReply,
            _ => null,
        },
    };

    private static RespireFailoverCandidate Candidate(FakeRespServer server, int priority = 0, TimeSpan? window = null)
        => new(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)],
            Connections = 1,
            Protocol = RespProtocol.Resp3,
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Enabled,
            MaintenanceWindowTimeout = window ?? TimeSpan.FromSeconds(5),
            ConnectTimeout = TimeSpan.FromSeconds(2),
            CommandTimeout = TimeSpan.FromSeconds(3),
            ThreadPoolMonitoring = false,
        }, priority);

    private static RespireFailoverGroupOptions ManualOptions() => new()
    {
        ProbeInterval = TimeSpan.FromHours(1),
        ProbeTimeout = TimeSpan.FromSeconds(2),
        FailureThreshold = 1,
        CircuitOpenDuration = TimeSpan.FromMinutes(1),
    };

    private static byte[] Start(string kind) => Encoding.UTF8.GetBytes($">3\r\n+{kind}\r\n:1\r\n:1\r\n");
    private static byte[] Finish(string kind) => Encoding.UTF8.GetBytes($">2\r\n+{kind}\r\n:1\r\n");

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
