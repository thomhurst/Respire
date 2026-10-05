using System.Buffers;
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
    [Arguments(0)]
    [Arguments(255)]
    [Arguments(-1)]
    public async Task BulkReadByteTracksEverySuccessfulByteButNotEndOfStream(int value)
    {
        using var payload = new RespBulkPayloadPipe();
        if (value >= 0)
        {
            payload.GetMemory(1).Span[0] = (byte)value;
            payload.Advance(1);
            await payload.FlushAsync();
        }
        payload.Complete();
        var progress = typeof(RespBulkPayloadPipe).GetField("_lastReaderProgress",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var oldProgress = Stopwatch.GetTimestamp() - Stopwatch.Frequency * 60;
        progress.SetValue(payload, oldProgress);

        await Assert.That(payload.ReadStream.ReadByte()).IsEqualTo(value);
        var updated = (long)progress.GetValue(payload)!;
        await Assert.That(updated > oldProgress).IsEqualTo(value >= 0);
    }

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
    [Arguments(false)]
    [Arguments(true)]
    public async Task DedicatedOnlyClientFollowsMovingReceivedOnUploadConnection(bool duringHandshake)
    {
        await using var target = Server(maxConnections: 8);
        await using var source = Server(duringHandshake ? FakeRespServer.OkReply.Concat(Moving(1, target.Port)).ToArray() : null,
            maxConnections: 8);
        var targetReply = target.ReplyOverride;
        target.ReplyOverride = (connection, command) => command.StartsWith("SET ")
            ? FakeRespServer.OkReply : targetReply!(connection, command);
        await using var client = RespireClient.Create(Options(source));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var originalPool = client.Core.DedicatedPool;
        await Assert.That(client.Core.Multiplexer.IsConnected).IsFalse();
        await client.Strings.SetAsync("first", new ReadOnlySequence<byte>("one"u8.ToArray()), cancellationToken: timeout.Token);
        if (!duringHandshake)
        {
            await Assert.That(client.Core.Multiplexer.IsConnected).IsFalse();
            var index = source.ReceivedCommands.ToList().IndexOf("SET first one");
            await source.SendRawAsync(Moving(1, target.Port), source.ReceivedConnectionIds[index]);
        }
        while (!originalPool.IsStopping) await Task.Delay(5, timeout.Token);
        await Assert.That(await client.Strings.SetAsync("second", new ReadOnlySequence<byte>("two"u8.ToArray()),
            cancellationToken: timeout.Token)).IsTrue();
        await Assert.That(target.ReceivedCommands).Contains("SET second two");
        await Assert.That(source.ReceivedCommands).DoesNotContain("SET second two");
    }

    [Test]
    [Arguments("standalone", false)]
    [Arguments("standalone", true)]
    [Arguments("cluster", false)]
    [Arguments("cluster", true)]
    [Arguments("sentinel", false)]
    [Arguments("sentinel", true)]
    public async Task SameEndpointMovingReplacesIdleUploadPool(string mode, bool omitTarget)
    {
        await using var server = Server(maxConnections: 12);
        ConfigureMaintenanceRouting(server);
        await using var sentinel = MaintenanceSentinel(server.Port);
        await using var client = await RespireClient.ConnectAsync(MaintenanceRoutingOptions(server, sentinel, mode));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pool = await MaintenancePoolAsync(client, "upload");
        var old = await pool.RentAsync(timeout.Token, kind: DedicatedLeaseKind.Streaming);
        pool.Return(old);
        var multiplexed = mode == "cluster"
            ? await client.Core.Cluster!.GetConnectionAsync(ClusterHash.GetSlot("upload"), timeout.Token, discovery: null)
            : client.Core.Multiplexer.GetConnection();
        var wireId = server.ReceivedConnectionIds[0];
        await server.SendRawAsync(omitTarget ? ">4\r\n+MOVING\r\n:1\r\n:10\r\n_\r\n"u8.ToArray() : Moving(1, server.Port), wireId);
        while (multiplexed.IsAcceptingCommands) await Task.Delay(5, timeout.Token);
        var replacement = await MaintenancePoolAsync(client, "upload");
        await Assert.That(ReferenceEquals(pool, replacement)).IsFalse();
        await Assert.That(pool.IsMovingPublicationCurrent).IsFalse();
        await Assert.That(replacement.IsMovingPublicationCurrent).IsTrue();
        var lease = await replacement.RentAsync(timeout.Token, kind: DedicatedLeaseKind.Streaming);
        try { await Assert.That(ReferenceEquals(old, lease)).IsFalse(); }
        finally { replacement.Return(lease); }
    }

    [Test]
    [Arguments("standalone")]
    [Arguments("cluster")]
    [Arguments("sentinel")]
    public async Task SameEndpointPublicationRevalidatesUploadBeforePoolRetirement(string mode)
    {
        await using var server = Server(maxConnections: 12);
        ConfigureMaintenanceRouting(server);
        var reply = server.ReplyOverride!;
        server.ReplyOverride = (id, command) => command.StartsWith("SET ") ? FakeRespServer.OkReply : reply(id, command);
        await using var sentinel = MaintenanceSentinel(server.Port);
        await using var client = await RespireClient.ConnectAsync(MaintenanceRoutingOptions(server, sentinel, mode));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pool = await MaintenancePoolAsync(client, "upload");
        await using var payload = new PausedFirstReadStream();
        var upload = client.Strings.SetAsync("upload", payload, payload.Length, cancellationToken: timeout.Token).AsTask();
        await payload.Started.Task.WaitAsync(timeout.Token);
        var oldWireId = server.ReceivedConnectionIds[^1];

        // Hold the interval between publication and the pool-refresh callback. Address and
        // old pool remain unchanged; only the publication identity can reject this lease.
        var field = typeof(RespireConnectionMultiplexer).GetField("_activeEndpoint",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        field.SetValue(pool.MovingOwner, Activator.CreateInstance(field.FieldType, "127.0.0.1", server.Port));
        await Assert.That(pool.IsStopping).IsFalse();
        payload.Resume.TrySetResult();
        await Assert.That(await upload.WaitAsync(timeout.Token)).IsTrue();
        var commands = server.ReceivedCommands;
        var ids = server.ReceivedConnectionIds;
        var setIndex = Array.FindIndex(commands.ToArray(), command => command == "SET upload payload");
        await Assert.That(setIndex).IsGreaterThanOrEqualTo(0);
        await Assert.That(ids[setIndex]).IsNotEqualTo(oldWireId);
    }

    [Test]
    [NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DedicatedTelemetryUsesEndpointAfterMoving(bool streaming)
    {
        await using var source = Server(maxConnections: 4);
        await using var target = Server(maxConnections: 4);
        var targetReply = target.ReplyOverride!;
        target.ReplyOverride = (id, command) => command.StartsWith("SET ") ? FakeRespServer.OkReply
            : command.StartsWith("BLPOP ") ? "_\r\n"u8.ToArray() : targetReply(id, command);
        await using var client = await RespireClient.ConnectAsync(Options(source));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var originalPool = client.Core.DedicatedPool;
        await source.SendRawAsync(Moving(1, target.Port), source.ReceivedConnectionIds[0]);
        while (!originalPool.IsStopping) await Task.Delay(5, timeout.Token);

        var started = new System.Collections.Concurrent.ConcurrentQueue<Activity>();
        var stopped = new System.Collections.Concurrent.ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = activitySource => activitySource.Name == "Respire",
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => options.Name is "SET" or "BLPOP"
                ? ActivitySamplingResult.AllDataAndRecorded : ActivitySamplingResult.None,
            ActivityStarted = started.Enqueue,
            ActivityStopped = stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        if (streaming)
            await client.Strings.SetAsync("upload", new ReadOnlySequence<byte>("value"u8.ToArray()), cancellationToken: timeout.Token);
        else
            _ = await client.Lists.LeftPopAsync("queue", waitFor: TimeSpan.FromSeconds(1), cancellationToken: timeout.Token);

        await Assert.That(started.Count).IsEqualTo(1);
        await Assert.That(stopped.Count).IsEqualTo(1);
        await Assert.That(stopped.Single()).IsSameReferenceAs(started.Single());
        await Assert.That(started.Single().GetTagItem("server.address")).IsEqualTo("127.0.0.1");
        await Assert.That(started.Single().GetTagItem("server.port")).IsEqualTo(target.Port);
        await Assert.That(target.ReceivedCommands).Contains(streaming ? "SET upload value" : "BLPOP queue 1");
    }

    [Test]
    [NotInParallel]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task DurabilityTelemetryUsesEndpointAfterMoving(bool aof, bool acquisitionFails)
    {
        await using var source = Server(maxConnections: 4);
        await using var target = Server(maxConnections: 4);
        var targetReply = target.ReplyOverride!;
        target.ReplyOverride = (id, command) => command.StartsWith("SET ") ? FakeRespServer.OkReply
            : command.StartsWith("WAIT ") ? ":1\r\n"u8.ToArray()
            : command.StartsWith("WAITAOF ") ? "*2\r\n:1\r\n:1\r\n"u8.ToArray() : targetReply(id, command);
        await using var client = await RespireClient.ConnectAsync(Options(source) with
        {
            ConnectTimeout = TimeSpan.FromMilliseconds(200),
            CommandTimeout = TimeSpan.FromSeconds(1),
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var originalPool = client.Core.DedicatedPool;
        await source.SendRawAsync(Moving(1, target.Port), source.ReceivedConnectionIds[0]);
        while (!originalPool.IsStopping) await Task.Delay(5, timeout.Token);
        if (acquisitionFails) target.SuppressReply = command => command == "HELLO 3";

        var name = aof ? "WAITAOF SET" : "WAIT SET";
        var started = new System.Collections.Concurrent.ConcurrentQueue<Activity>();
        var stopped = new System.Collections.Concurrent.ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = activitySource => activitySource.Name == "Respire",
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => options.Name == name
                ? ActivitySamplingResult.AllDataAndRecorded : ActivitySamplingResult.None,
            ActivityStarted = started.Enqueue,
            ActivityStopped = stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        using var batch = client.CreateBatch();
        _ = batch.Set("first", "value");
        _ = batch.Set("second", "value");
        if (acquisitionFails)
            await Assert.That(Execute).Throws<RespireException>();
        else
            await Execute();

        await Assert.That(started.Count).IsEqualTo(1);
        await Assert.That(stopped.Count).IsEqualTo(1);
        await Assert.That(stopped.Single()).IsSameReferenceAs(started.Single());
        await Assert.That(started.Single().GetTagItem("server.address")).IsEqualTo("127.0.0.1");
        await Assert.That(started.Single().GetTagItem("server.port")).IsEqualTo(target.Port);
        await Assert.That(stopped.Single().Status == ActivityStatusCode.Error).IsEqualTo(acquisitionFails);

        async Task Execute()
        {
            if (aof) _ = await batch.ExecuteAndWaitForAofAsync(true, 1, TimeSpan.FromSeconds(1), timeout.Token);
            else _ = await batch.ExecuteAndWaitForReplicationAsync(1, TimeSpan.FromSeconds(1), timeout.Token);
        }
    }

    [Test]
    [NotInParallel]
    public async Task DedicatedAcquisitionFailureRecordsOneActivityForIntendedEndpoint()
    {
        await using var server = Server(maxConnections: 4);
        server.SuppressReply = command => command == "HELLO 3";
        await using var client = RespireClient.Create(Options(server) with { ConnectTimeout = TimeSpan.FromMilliseconds(100) });
        var stopped = new System.Collections.Concurrent.ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Respire",
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => options.Name == "SET"
                ? ActivitySamplingResult.AllDataAndRecorded : ActivitySamplingResult.None,
            ActivityStopped = stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        await Assert.That(async () => await client.Strings.SetAsync("upload", new ReadOnlySequence<byte>("value"u8.ToArray())))
            .Throws<Exception>();
        await Assert.That(stopped.Count).IsEqualTo(1);
        await Assert.That(stopped.Single().Status).IsEqualTo(ActivityStatusCode.Error);
        await Assert.That(stopped.Single().GetTagItem("server.port")).IsEqualTo(server.Port);
    }

    [Test]
    public async Task StandaloneUploadAcceptsMixedCaseHost()
    {
        await using var server = Server(maxConnections: 4);
        var options = Options(server);
        options.Endpoints.Clear();
        options.Endpoints.Add(new("LoCaLhOsT", server.Port));
        await using var client = RespireClient.Create(options);
        await Assert.That(await client.Strings.SetAsync("mixed-case", new ReadOnlySequence<byte>("value"u8.ToArray()))).IsTrue();
        await Assert.That(server.ReceivedCommands).Contains("SET mixed-case value");
    }

    [Test]
    [Arguments("standalone")]
    [Arguments("cluster")]
    [Arguments("sentinel")]
    public async Task DisposeAbortsUploadWhileMovedPoolIsRetiring(string mode)
    {
        await using var source = Server(maxConnections: 4);
        await using var target = Server(maxConnections: 4);
        source.SuppressReply = command => command.StartsWith("SET ");
        ConfigureMaintenanceRouting(source);
        ConfigureMaintenanceRouting(target);
        await using var sentinel = MaintenanceSentinel(source.Port);
        await using var client = await RespireClient.ConnectAsync(MaintenanceRoutingOptions(source, sentinel, mode));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var originalPool = await MaintenancePoolAsync(client, "pending");
        var upload = client.Strings.SetAsync("pending", new ReadOnlySequence<byte>("value"u8.ToArray()),
            cancellationToken: timeout.Token).AsTask();
        while (!source.ReceivedCommands.Contains("SET pending value")) await Task.Delay(5, timeout.Token);
        await source.SendRawAsync(Moving(1, target.Port), source.ReceivedConnectionIds[0]);
        while (!originalPool.IsStopping) await Task.Delay(5, timeout.Token);
        await client.DisposeAsync().AsTask().WaitAsync(timeout.Token);
        await Assert.That(async () => await upload.WaitAsync(timeout.Token)).Throws<RespireConnectionException>();
        await originalPool.RetireAsync().AsTask().WaitAsync(timeout.Token);
        await Assert.That(originalPool.CaptureRetirementState().Borrowed).IsEqualTo(0);
    }

    [Test]
    [NotInParallel]
    [Arguments("standalone")]
    [Arguments("cluster")]
    [Arguments("sentinel")]
    public async Task MovingReroutesUploadPausedBeforeHeader(string mode)
    {
        await using var source = Server(maxConnections: 4);
        await using var target = Server(maxConnections: 4);
        var targetReply = target.ReplyOverride;
        target.ReplyOverride = (connection, command) => command.StartsWith("SET ")
            ? FakeRespServer.OkReply : targetReply!(connection, command);
        ConfigureMaintenanceRouting(source);
        ConfigureMaintenanceRouting(target);
        await using var sentinel = MaintenanceSentinel(source.Port);
        await using var client = await RespireClient.ConnectAsync(MaintenanceRoutingOptions(source, sentinel, mode));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var originalPool = await MaintenancePoolAsync(client, "moved-upload");
        var started = new System.Collections.Concurrent.ConcurrentQueue<Activity>();
        var stopped = new System.Collections.Concurrent.ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = activitySource => activitySource.Name == "Respire",
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => options.Name == "SET"
                ? ActivitySamplingResult.AllDataAndRecorded : ActivitySamplingResult.None,
            ActivityStarted = activity => { if (activity.OperationName == "SET") started.Enqueue(activity); },
            ActivityStopped = activity => { if (activity.OperationName == "SET") stopped.Enqueue(activity); },
        };
        ActivitySource.AddActivityListener(listener);
        await using var payload = new PausedFirstReadStream();
        var upload = client.Strings.SetAsync("moved-upload", payload, payload.Length,
            cancellationToken: timeout.Token).AsTask();
        await payload.Started.Task.WaitAsync(timeout.Token);
        await source.SendRawAsync(Moving(1, target.Port), source.ReceivedConnectionIds[0]);
        while (!originalPool.IsStopping) await Task.Delay(5, timeout.Token);
        payload.Resume.TrySetResult();
        await Assert.That(await upload.WaitAsync(timeout.Token)).IsTrue();
        await Assert.That(source.ReceivedCommands.Any(command => command.StartsWith("SET "))).IsFalse();
        await Assert.That(target.ReceivedCommands).Contains("SET moved-upload payload");
        await Assert.That(started.Count).IsEqualTo(1);
        await Assert.That(stopped.Count).IsEqualTo(1);
        await Assert.That(stopped.Single()).IsSameReferenceAs(started.Single());
        await Assert.That(stopped.Single().GetTagItem("server.address")).IsEqualTo("127.0.0.1");
        await Assert.That(stopped.Single().GetTagItem("server.port")).IsEqualTo(target.Port);
    }

    [Test]
    [Arguments("standalone")]
    [Arguments("cluster")]
    [Arguments("sentinel")]
    public async Task ReroutedUploadAcquisitionReportsMaintenanceRelaxedTimeout(string mode)
    {
        await using var source = Server(maxConnections: 4);
        await using var target = Server(maxConnections: 4);
        ConfigureMaintenanceRouting(source);
        ConfigureMaintenanceRouting(target);
        await using var sentinel = MaintenanceSentinel(source.Port);
        var relaxed = TimeSpan.FromSeconds(3);
        var stallAcquisition = false;
        await using var client = await RespireClient.ConnectAsync(MaintenanceRoutingOptions(source, sentinel, mode) with
        {
            CommandTimeout = TimeSpan.FromSeconds(1),
            MaintenanceRelaxedTimeout = relaxed,
            MaintenanceWindowTimeout = TimeSpan.FromSeconds(10),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            TestingStreamFactory = async (host, port, token) =>
            {
                if (Volatile.Read(ref stallAcquisition)) await Task.Delay(Timeout.InfiniteTimeSpan, token);
                var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
                try { await socket.ConnectAsync(host, port, token); }
                catch { socket.Dispose(); throw; }
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            },
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var originalPool = await MaintenancePoolAsync(client, "moved-upload");
        var lease = await originalPool.RentAsync(timeout.Token, kind: DedicatedLeaseKind.Streaming);
        // Sentinel catch-up ROLE traffic can arrive on another socket after this rental.
        // Identify the exact upload lease instead of using the most recently active socket.
        using var barrier = await lease.SendAsync(new Cmd1(new Verb(-1, "ECHO"), "maintenance-upload-lease"), timeout.Token);
        var leaseCommand = source.ReceivedCommands.ToList().FindIndex(command => command == "ECHO maintenance-upload-lease");
        await source.SendRawAsync(Start("MIGRATING", 1), source.ReceivedConnectionIds[leaseCommand]);
        await WaitForMaintenance(lease);
        originalPool.Return(lease);

        await using var payload = new PausedFirstReadStream();
        var upload = client.Strings.SetAsync("moved-upload", payload, payload.Length,
            cancellationToken: timeout.Token).AsTask();
        await payload.Started.Task.WaitAsync(timeout.Token);
        await source.SendRawAsync(Moving(1, target.Port), source.ReceivedConnectionIds[0]);
        while (!originalPool.IsStopping) await Task.Delay(5, timeout.Token);
        // The command connection has completed handoff. Stall only the replacement upload lease.
        Volatile.Write(ref stallAcquisition, true);
        payload.Resume.TrySetResult();

        var error = await Assert.That(async () => await upload.WaitAsync(timeout.Token))
            .Throws<RespireTimeoutException>();
        await Assert.That(error!.Timeout).IsEqualTo(relaxed);
        await Assert.That(error.Diagnostics.Stage).IsEqualTo(RespireCommandStage.Connecting);
        await Assert.That(target.ReceivedCommands.Any(command => command.StartsWith("SET "))).IsFalse();
    }

    private static void ConfigureMaintenanceRouting(FakeRespServer server)
    {
        var original = server.ReplyOverride!;
        server.ReplyOverride = (id, command) => command switch
        {
            "ROLE" => "*3\r\n$6\r\nmaster\r\n:0\r\n*0\r\n"u8.ToArray(),
            "CLUSTER SLOTS" => Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{server.Port}\r\n"),
            _ => original(id, command),
        };
    }

    private static FakeRespServer MaintenanceSentinel(int port) => new(8, FakeRespServer.OkReply)
    {
        ReplyOverride = (_, command) => command.StartsWith("SENTINEL GET-MASTER-ADDR")
            ? Encoding.ASCII.GetBytes($"*2\r\n$9\r\n127.0.0.1\r\n${port.ToString().Length}\r\n{port}\r\n")
            : "*0\r\n"u8.ToArray(),
    };

    private static RespireOptions MaintenanceRoutingOptions(FakeRespServer source, FakeRespServer sentinel, string mode)
    {
        var options = Options(source) with { UseCluster = mode == "cluster", SentinelPrimaryName = mode == "sentinel" ? "mymaster" : null };
        if (mode == "sentinel")
        {
            options.Endpoints.Clear();
            options.Endpoints.Add(new("127.0.0.1", sentinel.Port));
        }
        return options;
    }

    private static ValueTask<DedicatedConnectionPool> MaintenancePoolAsync(RespireClient client, string key)
        => client.Core.Cluster is { } cluster
            ? cluster.GetDedicatedPoolAsync(ClusterHash.GetSlot(key), CancellationToken.None, discovery: null)
            : client.Core.GetDedicatedPoolAsync(CancellationToken.None);

    private sealed class PausedFirstReadStream() : MemoryStream("payload"u8.ToArray())
    {
        public override bool CanSeek => false;
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Resume.Task.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MovingKeepsCorrectionFenceOnOriginalEndpoint(bool retainOriginalConnection)
    {
        await using var source = Server(maxConnections: 4);
        await using var target = Server(maxConnections: 4);
        var sourceReply = source.ReplyOverride;
        source.ReplyOverride = (connection, command) => command.StartsWith("CLIENT KILL ")
            ? ":1\r\n"u8.ToArray() : sourceReply!(connection, command);
        await using var client = await RespireClient.ConnectAsync(Options(source));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var originalPool = client.Core.DedicatedPool;
        var original = client.Core.Multiplexer.GetConnection();
        Task<RespValue>? pending = null;
        if (retainOriginalConnection)
        {
            source.SuppressReply = command => command == "PING";
            pending = original.SendAsync(new RawCommand(FakeRespServer.PingFrame), timeout.Token).AsTask();
            while (!source.ReceivedCommands.Contains("PING")) await Task.Delay(5, timeout.Token);
        }
        await source.SendRawAsync(Moving(1, target.Port), source.ReceivedConnectionIds[0]);
        while (!originalPool.IsStopping) await Task.Delay(5, timeout.Token);
        var fence = client.FenceCorrectionConnectionAsync(new(new("127.0.0.1", source.Port), 42,
            Connection: retainOriginalConnection ? original : null), timeout.Token).AsTask();
        await fence.WaitAsync(timeout.Token);
        if (pending is not null)
            await Assert.That(async () => { using var reply = await pending.WaitAsync(timeout.Token); })
                .Throws<RespireConnectionException>();
        await Assert.That(source.ReceivedCommands.Any(command => command.StartsWith("CLIENT KILL ID 42"))).IsTrue();
        await Assert.That(target.ReceivedCommands.Any(command => command.StartsWith("CLIENT KILL "))).IsFalse();
    }

    [Test]
    [Arguments("standalone", false)]
    [Arguments("standalone", true)]
    [Arguments("cluster", false)]
    [Arguments("cluster", true)]
    [Arguments("sentinel", false)]
    [Arguments("sentinel", true)]
    public async Task MovingRetriesDedicatedHandshakeRetiredBeforeDispatch(string mode, bool streaming)
    {
        await using var source = Server(maxConnections: 8);
        await using var target = Server(maxConnections: 8);
        ConfigureMaintenanceRouting(source);
        ConfigureMaintenanceRouting(target);
        await using var sentinel = MaintenanceSentinel(source.Port);
        await using var client = await RespireClient.ConnectAsync(MaintenanceRoutingOptions(source, sentinel, mode));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var originalPool = await MaintenancePoolAsync(client, "lease");
        ValueTask<(DedicatedConnectionPool Pool, RespireConnection Connection)> Rent(CancellationToken token)
            => client.Core.Cluster is { } cluster
                ? cluster.RentDedicatedConnectionAsync(originalPool, ClusterHash.GetSlot("lease"), token,
                    discovery: null, kind: streaming ? DedicatedLeaseKind.Streaming : DedicatedLeaseKind.Ordinary)
                : client.Core.RentDedicatedConnectionAsync(originalPool, token,
                    kind: streaming ? DedicatedLeaseKind.Streaming : DedicatedLeaseKind.Ordinary);
        var handshake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SuppressReply = command =>
        {
            if (command != "HELLO 3") return false;
            handshake.TrySetResult();
            return true;
        };
        var rental = Rent(timeout.Token).AsTask();
        await handshake.Task.WaitAsync(timeout.Token);
        await source.SendRawAsync(Moving(1, target.Port), source.ReceivedConnectionIds[0]);
        var (owner, lease) = await rental.WaitAsync(timeout.Token);
        await Assert.That(ReferenceEquals(owner, await MaintenancePoolAsync(client, "lease"))).IsTrue();
        await Assert.That(lease.Port).IsEqualTo(target.Port);
        owner.Return(lease);
        await originalPool.RetireAsync().AsTask().WaitAsync(timeout.Token);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var error = await Assert.That(async () => await Rent(cancelled.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(cancelled.Token);
    }

    [Test]
    [Arguments("standalone", false, false)]
    [Arguments("standalone", true, true)]
    [Arguments("cluster", false, false)]
    [Arguments("cluster", true, true)]
    [Arguments("sentinel", false, false)]
    [Arguments("sentinel", true, true)]
    public async Task MovingReplacesUploadPoolAndDrainsAcceptedUpload(string mode, bool reuseIdle, bool fromUpload)
    {
        await using var source = Server(maxConnections: 8);
        await using var target = Server(maxConnections: 8);
        var targetReply = target.ReplyOverride;
        target.ReplyOverride = (connection, command) => command.StartsWith("SET ")
            ? FakeRespServer.OkReply : targetReply!(connection, command);
        source.SuppressReply = command => command.StartsWith("SET ");
        ConfigureMaintenanceRouting(source);
        ConfigureMaintenanceRouting(target);
        await using var sentinel = MaintenanceSentinel(source.Port);
        await using var client = await RespireClient.ConnectAsync(MaintenanceRoutingOptions(source, sentinel, mode));
        var selected = client.Core.Multiplexer.GetConnection();
        var originalPool = await MaintenancePoolAsync(client, "old-upload");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var upload = client.Strings.SetAsync("old-upload", new ReadOnlySequence<byte>("old"u8.ToArray()),
            cancellationToken: timeout.Token).AsTask();
        while (!source.ReceivedCommands.Contains("SET old-upload old")) await Task.Delay(5, timeout.Token);
        var index = source.ReceivedCommands.ToList().IndexOf("SET old-upload old");
        var uploadConnection = source.ReceivedConnectionIds[index];
        await source.SendRawAsync(Moving(1, target.Port), fromUpload ? uploadConnection : source.ReceivedConnectionIds[0]);
        await WaitForRetirement(selected);
        // Reproduce selection before publication followed by rental after retirement.
        while (!originalPool.IsStopping) await Task.Delay(5, timeout.Token);
        await Assert.That(originalPool.IsStopping).IsTrue();
        var (owner, lease) = client.Core.Cluster is { } cluster
            ? await cluster.RentDedicatedConnectionAsync(originalPool, ClusterHash.GetSlot("old-upload"), timeout.Token,
                discovery: null, reuseIdle: reuseIdle, kind: DedicatedLeaseKind.Streaming)
            : await client.Core.RentDedicatedConnectionAsync(originalPool, timeout.Token,
                reuseIdle: reuseIdle, kind: DedicatedLeaseKind.Streaming);
        await Assert.That(ReferenceEquals(owner, await MaintenancePoolAsync(client, "old-upload"))).IsTrue();
        await Assert.That(lease.Port).IsEqualTo(target.Port);
        owner.Return(lease);
        await Assert.That(await client.Strings.SetAsync("new-upload", new ReadOnlySequence<byte>("new"u8.ToArray()),
            cancellationToken: timeout.Token)).IsTrue();
        await Assert.That(originalPool.IsStopping).IsTrue();
        await Assert.That(ReferenceEquals(originalPool, await MaintenancePoolAsync(client, "old-upload"))).IsFalse();
        await Assert.That(target.ReceivedCommands).Contains("SET new-upload new");
        await Assert.That(upload.IsCompleted).IsFalse();
        await source.SendRawAsync(FakeRespServer.OkReply, uploadConnection);
        await Assert.That(await upload.WaitAsync(timeout.Token)).IsTrue();
        await originalPool.RetireAsync().AsTask().WaitAsync(timeout.Token);
        await Assert.That(source.ReceivedCommands.Any(command => command.StartsWith("SET new-upload "))).IsFalse();
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
    public async Task StreamedMovingReroutePreservesMaintenanceRelaxedDeadline()
    {
        await using var sourceServer = Server(maxConnections: 2);
        await using var targetServer = Server(maxConnections: 2);
        var options = Options(sourceServer).ToConnectionOptions(enableMaintenanceNotifications: true) with
        {
            CommandTimeout = TimeSpan.FromMilliseconds(300),
            MaintenanceRelaxedTimeout = TimeSpan.FromSeconds(2),
            MaintenanceWindowTimeout = TimeSpan.FromSeconds(4),
        };
        await using var multiplexer = await RespireConnectionMultiplexer.CreateAsync(
            "127.0.0.1", sourceServer.Port, options: options);
        var staleSelection = multiplexer.GetConnection();
        await sourceServer.SendRawAsync(Start("MIGRATING", 1));
        await WaitForMaintenance(staleSelection);

        var pipe = new System.IO.Pipelines.Pipe();
        await using var input = pipe.Reader.AsStream();
        var command = new StreamedSetCommand((RespireValue)"key", input, 4, default, SetWhen.Always);
        var set = staleSelection.SendCheckedAsync(in command, commandName: "SET").AsTask();
        await pipe.Writer.WriteAsync("da"u8.ToArray());
        await Task.Delay(400); // Exceed the normal 300 ms deadline while the source is being read.
        await sourceServer.SendRawAsync(Moving(1, targetServer.Port));
        await WaitForPort(multiplexer, targetServer.Port);
        await pipe.Writer.WriteAsync("ta"u8.ToArray());

        using var reply = await set.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(reply.IsError).IsFalse();
        await Assert.That(targetServer.ReceivedCommands.Contains("SET key data")).IsTrue();
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
    public async Task MovingFromRestartedServerAtSameAddressIsHonouredOnceItsFenceLapses()
    {
        await using var source = Server(maxConnections: 4);
        await using var target = Server(maxConnections: 2);
        await using var multiplexer = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", source.Port,
            options: Options(source).ToConnectionOptions(enableMaintenanceNotifications: true));
        var announcingConnection = multiplexer.GetConnection();
        var coordinator = typeof(RespireConnectionMultiplexer)
            .GetField("_moving", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(multiplexer)!;
        var movingSequences = (System.Collections.IDictionary)coordinator.GetType()
            .GetField("_sequences", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(coordinator)!;
        var notification = new MaintenanceNotification("MOVING", 1, 10,
            new RespireEndpoint("127.0.0.1", target.Port));

        // Sequence 7's grace period is still running, so a lower sequence is a repeat.
        movingSequences[announcingConnection.PeerKey] = (7L, Environment.TickCount64 + 60_000);
        QueueMovingHandoff(multiplexer, 0, announcingConnection, new MovingAnnouncement(notification,
            announcingConnection.MovingPublicationGeneration, 0, Environment.TickCount64));
        await Task.Delay(100);
        await Assert.That(multiplexer.GetConnection().Port).IsEqualTo(source.Port);

        // Once that grace period has ended, the restarted server's own numbering is followed.
        movingSequences[announcingConnection.PeerKey] = (7L, Environment.TickCount64 - 1);
        announcingConnection.LastQueuedMovingSequence = long.MinValue; // A fresh socket to the restarted peer.
        QueueMovingHandoff(multiplexer, 0, announcingConnection, new MovingAnnouncement(notification,
            announcingConnection.MovingPublicationGeneration, 0, Environment.TickCount64));
        await WaitForPort(multiplexer, target.Port);
    }

    [Test]
    public async Task MovingParsedBeforePublicationIsHonouredWhenCallbackRunsAfterPublication()
    {
        await using var source = Server(maxConnections: 2);
        await using var firstTarget = Server(maxConnections: 2);
        await using var delayedTarget = Server(maxConnections: 2);
        await using var multiplexer = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", source.Port,
            options: Options(source).ToConnectionOptions(enableMaintenanceNotifications: true));
        var announcingConnection = multiplexer.GetConnection();
        // Parsed while published and before the first handoff, but handled after it.
        var delayed = multiplexer.CaptureMovingAnnouncement(0, announcingConnection,
            new MaintenanceNotification("MOVING", 2, 10, new RespireEndpoint("127.0.0.1", delayedTarget.Port)));

        await source.SendRawAsync(Moving(1, firstTarget.Port));
        await WaitForPort(multiplexer, firstTarget.Port);
        QueueMovingHandoff(multiplexer, 0, announcingConnection, delayed);
        await WaitForPort(multiplexer, delayedTarget.Port);

        using var pong = await multiplexer.GetConnection().SendAsync(new RawCommand(FakeRespServer.PingFrame))
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(pong.AsString()).IsEqualTo("PONG");
        await Assert.That(delayedTarget.ReceivedCommands.Count(command => command == "PING")).IsEqualTo(1);
    }

    [Test]
    public async Task MovingParsedFromOldSocketIsRejectedAfterMultiplePublications()
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
        // Parsed while published, before either handoff (epoch 0) published.
        queueHandoff.Invoke(multiplexer, [0, announcingConnection,
            new MovingAnnouncement(delayedNotification, announcingConnection.MovingPublicationGeneration, 0,
                Environment.TickCount64)]);
        await Assert.That(multiplexer.GetConnection().Port).IsEqualTo(secondTarget.Port);

        using var pong = await multiplexer.GetConnection().SendAsync(new RawCommand(FakeRespServer.PingFrame))
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(pong.AsString()).IsEqualTo("PONG");
        await Assert.That(delayedTarget.ReceivedCommands.Count(command => command == "PING")).IsEqualTo(0);
    }

    [Test]
    public async Task MovingCacheFenceRunsAfterOldSocketsStopAcceptingCommands()
    {
        await using var source = Server(maxConnections: 2);
        await using var target = Server(maxConnections: 2);
        RespireConnection? oldConnection = null;
        var fenceSawRetiredSocket = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var initialFlushes = 0;
        await using var multiplexer = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", source.Port,
            options: Options(source).ToConnectionOptions(enableMaintenanceNotifications: true) with
            {
                CredentialCacheInvalidation = () => { Interlocked.Increment(ref initialFlushes); return 0; },
                CredentialCacheRetirementFence = () =>
                {
                    fenceSawRetiredSocket.TrySetResult(oldConnection is { IsAcceptingCommands: false });
                },
            });
        oldConnection = multiplexer.GetConnection();

        await source.SendRawAsync(Moving(1, target.Port));
        await WaitForPort(multiplexer, target.Port);

        await Assert.That(await fenceSawRetiredSocket.Task.WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();
        await Assert.That(initialFlushes).IsEqualTo(1);
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
    public async Task MovingCannotTransferPinnedRoleOutsideItsReservation()
    {
        await using var source = Server(maxConnections: 2);
        await using var target = Server(maxConnections: 2);
        await using var multiplexer = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", source.Port,
            options: Options(source).ToConnectionOptions(enableMaintenanceNotifications: true));
        await using var sampler = new ReadLatencySampler<RespireConnection>((_, _) => ValueTask.FromResult(10L));
        var reserved = multiplexer.GetConnection();
        await Assert.That(sampler.TryReserveForValidation(reserved, out var reservation)).IsTrue();
        using (reservation)
        {
            await source.SendRawAsync(Moving(1, target.Port));
            await WaitForRetirement(reserved);
            await Assert.That(async () => await reserved.SendAsync(new Cmd(Verbs.Role), pinToConnection: true))
                .Throws<RespireConnectionRetiredException>();
            await Assert.That(target.ReceivedCommands.Contains("ROLE")).IsFalse();
            await Assert.That(await sampler.GetLatencyAsync(reserved, default)).IsEqualTo(ReadLatencyResult.Pending);
            var replacement = multiplexer.GetConnection();
            await Assert.That(replacement.Port).IsEqualTo(target.Port);
            await Assert.That(await sampler.GetLatencyAsync(replacement, default)).IsEqualTo(ReadLatencyResult.Measured(10));
        }
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task TopologyQueryReportsTheSocketThatAnsweredAfterMoving(bool withOwner)
    {
        await using var source = Server(maxConnections: 2);
        await using var target = Server(maxConnections: 2);
        await using var multiplexer = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", source.Port,
            options: Options(source).ToConnectionOptions(enableMaintenanceNotifications: true));
        var selected = multiplexer.GetConnection();
        await source.SendRawAsync(Moving(1, target.Port));
        await WaitForRetirement(selected);
        await WaitForPort(multiplexer, target.Port);

        if (withOwner)
        {
            // The empty-host fallback reads the responder's host, so a retired selection must
            // be replaced explicitly rather than rerouted behind the caller's back.
            var (responder, reply) = await ClusterRouter.SendTopologyQueryAsync(selected, multiplexer, default);
            using (reply)
            {
                await Assert.That(ReferenceEquals(responder, selected)).IsFalse();
                await Assert.That(responder.Port).IsEqualTo(target.Port);
            }
            await Assert.That(target.ReceivedCommands.Contains("CLUSTER SLOTS")).IsTrue();
        }
        else
        {
            // A caller-owned query socket has no replacement; it fails instead of moving.
            await Assert.That(async () => await ClusterRouter.SendTopologyQueryAsync(selected, null, default))
                .Throws<RespireConnectionRetiredException>();
            await Assert.That(target.ReceivedCommands.Contains("CLUSTER SLOTS")).IsFalse();
        }
        await Assert.That(source.ReceivedCommands.Contains("CLUSTER SLOTS")).IsFalse();
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

    [Test]
    public async Task MovingParsedBeforeReconnectReplacesAnnouncingSocketIsStillFollowed()
    {
        await using var source = Server(maxConnections: 2);
        await using var target = Server(maxConnections: 2);
        await using var multiplexer = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", source.Port,
            options: Options(source).ToConnectionOptions(enableMaintenanceNotifications: true));
        var announcingConnection = multiplexer.GetConnection();
        // Captured as the receive loop would, while the socket is still published.
        var announcement = multiplexer.CaptureMovingAnnouncement(0, announcingConnection,
            new MaintenanceNotification("MOVING", 1, 10, new RespireEndpoint("127.0.0.1", target.Port)));

        // A reconnect (not a handoff) replaces the socket before the callback runs.
        source.CloseConnections();
        await WaitForReplacement(multiplexer, announcingConnection);
        QueueMovingHandoff(multiplexer, 0, announcingConnection, announcement);

        await WaitForPort(multiplexer, target.Port);
        using var pong = await multiplexer.GetConnection().SendAsync(new RawCommand(FakeRespServer.PingFrame))
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(pong.AsString()).IsEqualTo("PONG");
    }

    [Test]
    public async Task MovingGracePeriodStartsWhenThePushWasParsed()
    {
        await using var source = Server(maxConnections: 2);
        await using var target = Server("-ERR maintenance subsystem unavailable\r\n"u8.ToArray(), maxConnections: 64);
        await using var multiplexer = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", source.Port,
            options: Options(source).ToConnectionOptions(enableMaintenanceNotifications: true));
        var connection = multiplexer.GetConnection();
        // Parsed 20 seconds ago with a 10 second grace, for example behind a slow sibling
        // handshake. Replaying it must not restart the grace period.
        var announcement = new MovingAnnouncement(
            new MaintenanceNotification("MOVING", 1, 10, new RespireEndpoint("127.0.0.1", target.Port)),
            connection.MovingPublicationGeneration, 0, Environment.TickCount64 - 20_000);

        QueueMovingHandoff(multiplexer, 0, connection, announcement);
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            while (!target.ReceivedCommands.Contains("CLIENT MAINT_NOTIFICATIONS ON"))
                await Task.Delay(5, timeout.Token);
        }
        await Task.Delay(500);

        // The expired grace period allows the one setup attempt and no retries.
        await Assert.That(target.ReceivedCommands.Count(command => command == "CLIENT MAINT_NOTIFICATIONS ON")).IsEqualTo(1);
        await Assert.That(multiplexer.GetConnection().Port).IsEqualTo(source.Port);
    }

    [Test]
    public async Task SequenceFenceLapsesAfterItsGracePeriodForTheSamePeer()
    {
        await using var source = Server(maxConnections: 2);
        await using var unavailable = Server("-ERR maintenance subsystem unavailable\r\n"u8.ToArray(), maxConnections: 64);
        await using var target = Server(maxConnections: 4);
        await using var multiplexer = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", source.Port,
            connectionCount: 2, options: Options(source).ToConnectionOptions(enableMaintenanceNotifications: true));

        await source.SendRawAsync(Moving(5, unavailable.Port, seconds: 1), connectionId: 0);
        await Task.Delay(1300); // The handoff gives up and the sequence-5 fence lapses.
        // The same address now numbers from 1, as a restarted server would. Both old sockets
        // are still connected, so only the lapse lets this through.
        await source.SendRawAsync(Moving(1, target.Port), connectionId: 1);

        await WaitForPort(multiplexer, target.Port);
    }

    [Test]
    public async Task DuplicateMovingOnSiblingSocketsHandsOffOnce()
    {
        await using var source = Server(maxConnections: 2);
        await using var target = Server(maxConnections: 4);
        // Keep the original sockets current until both duplicate announcements arrive.
        target.DelayCommand("HELLO", 1000);
        await using var multiplexer = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", source.Port,
            connectionCount: 2, options: Options(source).ToConnectionOptions(enableMaintenanceNotifications: true));

        await source.SendRawAsync(Moving(3, target.Port), connectionId: 0);
        await source.SendRawAsync(Moving(3, target.Port), connectionId: 1);
        await WaitForPort(multiplexer, target.Port);
        await Task.Delay(300);

        // One handoff connects two replacements: two HELLOs, not four.
        await Assert.That(target.ReceivedCommands.Count(command => command == "HELLO 3")).IsEqualTo(2);
    }

    [Test]
    public async Task MovingReroutesMultiReplyAndBulkStreamSendsRejectedBeforeAdmission()
    {
        await using var source = Server(maxConnections: 2);
        await using var target = Server(maxConnections: 2);
        var targetReply = target.ReplyOverride!;
        target.ReplyOverride = (connectionId, command) => command == "GET key"
            ? "$5\r\nvalue\r\n"u8.ToArray()
            : targetReply(connectionId, command);
        await using var multiplexer = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", source.Port,
            options: Options(source).ToConnectionOptions(enableMaintenanceNotifications: true));
        var staleSelection = multiplexer.GetConnection();

        await source.SendRawAsync(Moving(1, target.Port));
        await WaitForRetirement(staleSelection);
        using var validated = await staleSelection.SendValidatedPrefixedAsync(
                new RawCommand(FakeRespServer.PingFrame), new RawCommand(FakeRespServer.PingFrame), commandName: "PING")
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await using var stream = await staleSelection.SendBulkStreamAsync(
                new RawCommand("*2\r\n$3\r\nGET\r\n$3\r\nkey\r\n"u8.ToArray()), commandName: "GET")
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        using var reader = new StreamReader(stream!);
        var value = await reader.ReadToEndAsync();

        await Assert.That(validated.AsString()).IsEqualTo("PONG");
        await Assert.That(value).IsEqualTo("value");
        await Assert.That(target.ReceivedCommands.Count(command => command == "PING")).IsEqualTo(2);
        await Assert.That(source.ReceivedCommands.Contains("GET key")).IsFalse();
    }

    [Test]
    public async Task MovingReroutesStreamedSetRejectedBeforeAdmission()
    {
        await using var source = Server(maxConnections: 2);
        await using var target = Server(maxConnections: 2);
        var targetReply = target.ReplyOverride!;
        target.ReplyOverride = (connectionId, command) => command.StartsWith("SET key ", StringComparison.Ordinal)
            ? "+OK\r\n"u8.ToArray()
            : targetReply(connectionId, command);
        await using var multiplexer = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", source.Port,
            options: Options(source).ToConnectionOptions(enableMaintenanceNotifications: true));
        var staleSelection = multiplexer.GetConnection();

        await source.SendRawAsync(Moving(1, target.Port));
        await WaitForCommands(target, 2);
        await WaitForRetirement(staleSelection);
        using var payload = new MemoryStream("data"u8.ToArray());
        var command = new StreamedSetCommand((RespireValue)"key", payload, 4, default, SetWhen.Always);

        using var result = await staleSelection.SendCheckedAsync(in command, commandName: "SET")
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForCommands(target, 3);

        await Assert.That(result.AsString()).IsEqualTo("OK");
        await Assert.That(target.ReceivedCommands.Count(command => command.StartsWith("SET key ", StringComparison.Ordinal))).IsEqualTo(1);
        await Assert.That(source.ReceivedCommands.Any(command => command.StartsWith("SET ", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task MovingPublishedAfterGracePeriodAbortsOldSocketWork()
    {
        await using var source = Server(maxConnections: 2);
        source.SuppressReply = command => command == "PING";
        await using var target = Server(maxConnections: 2);
        target.DelayReply(0, 1500); // The target handshake outlasts the one-second grace.
        await using var multiplexer = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", source.Port,
            options: Options(source).ToConnectionOptions(enableMaintenanceNotifications: true) with
            {
                ConnectTimeout = TimeSpan.FromSeconds(10),
                CommandTimeout = TimeSpan.FromSeconds(30),
            });
        var accepted = multiplexer.GetConnection().SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        await WaitForCommands(source, 3);

        await source.SendRawAsync(Moving(1, target.Port, seconds: 1));
        await WaitForPort(multiplexer, target.Port);

        // The source never answers, and the old socket is aborted as soon as the late handoff publishes.
        await Assert.That(async () => await accepted.WaitAsync(TimeSpan.FromSeconds(5))).Throws<RespireException>();
    }

    [Test]
    public async Task DisposingDuringMovingHandoffCompletesPromptly()
    {
        await using var source = Server(maxConnections: 2);
        await using var target = Server(maxConnections: 2);
        target.DelayReply(0, 30_000);
        var multiplexer = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", source.Port,
            options: Options(source).ToConnectionOptions(enableMaintenanceNotifications: true) with
            {
                ConnectTimeout = TimeSpan.FromSeconds(60),
            });

        await source.SendRawAsync(Moving(1, target.Port, seconds: 60));
        await WaitForCommands(target, 1); // The replacement handshake is in progress.
        await multiplexer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(multiplexer.IsRetired).IsTrue();
    }

    [Test]
    public async Task DisposingWhileOldMovingSocketsDrainAbortsThemPromptly()
    {
        await using var source = Server(maxConnections: 2);
        source.SuppressReply = command => command == "PING";
        await using var target = Server(maxConnections: 2);
        var multiplexer = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", source.Port,
            options: Options(source).ToConnectionOptions(enableMaintenanceNotifications: true) with
            {
                CommandTimeout = TimeSpan.FromSeconds(120),
            });
        var accepted = multiplexer.GetConnection().SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        await WaitForCommands(source, 3);

        // The old socket holds an accepted command that never completes, inside a long grace.
        await source.SendRawAsync(Moving(1, target.Port, seconds: 60));
        await WaitForPort(multiplexer, target.Port);
        await Assert.That(accepted.IsCompleted).IsFalse();

        // The old socket is no longer published, so only the drain can close it on disposal.
        await multiplexer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(async () => await accepted.WaitAsync(TimeSpan.FromSeconds(5))).Throws<RespireException>();
    }

    [Test]
    public async Task FailingRetirementCacheFenceObserverDoesNotFailPublishedHandoff()
    {
        // A distinctive count identifies this test's continuity flush among process-wide metrics.
        const int continuityEvictions = 7919;
        await using var source = Server(maxConnections: 2);
        await using var target = Server(maxConnections: 2);
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var evictions = MeterFor("redis.client.csc.evictions", (value, _) =>
        {
            if (value == continuityEvictions) published.TrySetResult();
        });
        var logger = new HandoffFailureLogger();
        await using var multiplexer = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", source.Port,
            logger: logger,
            options: Options(source).ToConnectionOptions(enableMaintenanceNotifications: true) with
            {
                CredentialCacheInvalidation = () => continuityEvictions,
                CredentialCacheRetirementFence = () => throw new InvalidOperationException("Metrics observer failure."),
            });

        await source.SendRawAsync(Moving(1, target.Port));
        await WaitForPort(multiplexer, target.Port);

        // The first continuity flush still publishes its metrics after the fence throws.
        await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(logger.HandoffFailed).IsFalse();
        using var pong = await multiplexer.GetConnection().SendAsync(new RawCommand(FakeRespServer.PingFrame))
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(pong.AsString()).IsEqualTo("PONG");
    }

    private sealed class HandoffFailureLogger : Microsoft.Extensions.Logging.ILogger
    {
        internal volatile bool HandoffFailed;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning && exception is InvalidOperationException)
                HandoffFailed = true;
        }
    }

    [Test]
    public async Task RapidMovingBurstEndsOnTheNewestTarget()
    {
        await using var source = Server(maxConnections: 2);
        var obsoleteTargets = new FakeRespServer[3];
        for (var i = 0; i < obsoleteTargets.Length; i++)
        {
            obsoleteTargets[i] = Server(maxConnections: 16);
            obsoleteTargets[i].DelayReply(0, 200);
        }
        await using var final = Server(maxConnections: 2);
        try
        {
            await using var multiplexer = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", source.Port,
                options: Options(source).ToConnectionOptions(enableMaintenanceNotifications: true));

            for (var sequence = 1; sequence <= 12; sequence++)
                await source.SendRawAsync(Moving(sequence, obsoleteTargets[sequence % obsoleteTargets.Length].Port));
            await source.SendRawAsync(Moving(13, final.Port));

            await WaitForPort(multiplexer, final.Port);
            using var pong = await multiplexer.GetConnection().SendAsync(new RawCommand(FakeRespServer.PingFrame))
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(pong.AsString()).IsEqualTo("PONG");
            await Assert.That(final.ReceivedCommands.Count(command => command == "PING")).IsEqualTo(1);
        }
        finally
        {
            foreach (var obsolete in obsoleteTargets) await obsolete.DisposeAsync();
        }
    }

    private static void QueueMovingHandoff(RespireConnectionMultiplexer multiplexer, int slot,
        RespireConnection connection, MovingAnnouncement announcement)
        => typeof(RespireConnectionMultiplexer).GetMethod("QueueMovingHandoff",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(multiplexer, [slot, connection, announcement]);

    private static async Task WaitForReplacement(RespireConnectionMultiplexer multiplexer, RespireConnection replaced)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            try
            {
                if (multiplexer.GetConnection() is { IsAcceptingCommands: true } current
                    && !ReferenceEquals(current, replaced)) return;
            }
            catch (RespireException) { }
            await Task.Delay(5, timeout.Token);
        }
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
        source.Deadline = CommandDeadline.At(deadline);
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

    // The streamed SET timer and the full-ring capacity wait compute their deadline with
    // MaintenanceTimeoutState.RemainingUntilDeadline; the in-flight sweep applies the same rule per
    // entry. Run identical windows through both so streamed and ordinary commands cannot drift.
    [Test]
    [Arguments(900L, 1100L, true)]   // Expired before maintenance started: never revived.
    [Arguments(1000L, 1100L, true)]  // Deadline exactly at the window start: not relaxed.
    [Arguments(1001L, 1100L, true)]  // Relaxed by the window.
    [Arguments(1001L, 2100L, true)]  // Relaxed deadline has also passed.
    [Arguments(1250L, 1100L, false)] // Relaxed timeout shorter than normal: never shortened.
    [Arguments(1250L, 7000L, true)]  // Window expired: the normal deadline applies again.
    [Arguments(1250L, 1100L, null)]  // No maintenance at all.
    public async Task StreamedAndSweptDeadlinesAgreeAcrossMaintenanceWindows(long deadline, long now, bool? longerRelaxation)
    {
        var normal = TimeSpan.FromMilliseconds(200);
        var relaxed = longerRelaxation == false ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromMilliseconds(1200);
        MaintenanceTimeoutState? state = null;
        if (longerRelaxation is not null)
        {
            state = new MaintenanceTimeoutState(5000);
            state.Apply(new("MIGRATING", 1), 1000);
        }

        var streamedRemaining = MaintenanceTimeoutState.RemainingUntilDeadline(state, normal, relaxed, deadline, now,
            out var streamedTimeout, out _);

        // The sweep path, exactly as SweepCommandDeadlinesAsync feeds the ring.
        var window = state?.GetWindow(now);
        var sweepTimeout = window is not null && relaxed > normal ? relaxed : normal;
        var pool = new PendingResponsePool(1);
        var ring = new InflightRing(1);
        var source = pool.Rent(commandName: "PING");
        source.Deadline = CommandDeadline.At(deadline);
        ring.TryEnqueue(source);
        var sweptRemaining = ring.SweepExpired(now, normal, null,
            (long)(sweepTimeout - normal).TotalMilliseconds, window?.Started ?? long.MaxValue);

        if (streamedRemaining <= 0)
        {
            await Assert.That(sweptRemaining).IsEqualTo(-1);
            var error = await Assert.That(async () => await source.Task).ThrowsExactly<RespireTimeoutException>();
            await Assert.That(error!.Timeout).IsEqualTo(streamedTimeout);
        }
        else
        {
            await Assert.That(sweptRemaining).IsEqualTo(streamedRemaining);
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
        source.Deadline = CommandDeadline.At(deadline).Relax(0);
        ring.TryEnqueue(source);

        var remaining = ring.SweepExpired(deadline - 1, TimeSpan.FromMilliseconds(100), null,
            deadlineExtension: 500, maintenanceStarted: deadline - 500);
        await Assert.That(remaining).IsEqualTo(1);
        remaining = ring.SweepExpired(deadline, TimeSpan.FromMilliseconds(100), null,
            deadlineExtension: 500, maintenanceStarted: deadline - 500);
        await Assert.That(remaining).IsEqualTo(-1);
    }

    [Test]
    public async Task DeadlineSweepReportsRelaxedTimeoutForReroutedCommand()
    {
        const long deadline = 2000;
        var pool = new PendingResponsePool(1);
        var ring = new InflightRing(1);
        var source = pool.Rent(commandName: "PING");
        source.Deadline = CommandDeadline.At(deadline).Relax(0);
        ring.TryEnqueue(source);

        await Assert.That(ring.SweepExpired(deadline, TimeSpan.FromSeconds(10), null,
            alreadyRelaxedTimeout: TimeSpan.FromSeconds(30))).IsEqualTo(-1);
        var error = await Assert.That(async () => await source.Task).ThrowsExactly<RespireTimeoutException>();
        await Assert.That(error!.Timeout).IsEqualTo(TimeSpan.FromSeconds(30));
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
    [Arguments(false)]
    [Arguments(true)]
    public async Task ClientUploadLeaseUsesMaintenanceAndKeepsBlockingLeasesSeparate(bool cluster)
    {
        await using var server = Server(maxConnections: 5);
        server.ReplyOverride = (_, command) => command switch
        {
            "HELLO 3" => Hello,
            "CLIENT MAINT_NOTIFICATIONS ON" => FakeRespServer.OkReply,
            "CLUSTER SLOTS" => Encoding.ASCII.GetBytes(
                $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{server.Port}\r\n"),
            _ => FakeRespServer.OkReply,
        };
        await using var client = RespireClient.Create(Options(server) with
        {
            UseCluster = cluster,
            CommandTimeout = TimeSpan.FromMilliseconds(150),
            MaintenanceRelaxedTimeout = TimeSpan.FromSeconds(5),
        });
        var pool = cluster
            ? await client.Core.Cluster!.GetDedicatedPoolAsync(null, CancellationToken.None, discovery: null)
            : await client.Core.GetDedicatedPoolAsync(CancellationToken.None);
        var blocking = await pool.RentAsync(CancellationToken.None);
        pool.Return(blocking);
        var connection = await pool.RentAsync(CancellationToken.None, kind: DedicatedLeaseKind.Streaming);
        await Assert.That(ReferenceEquals(blocking, connection)).IsFalse();
        await server.SendRawAsync(Start("MIGRATING", 1), server.ReceivedConnectionIds[^1]);
        await WaitForMaintenance(connection);
        pool.Return(connection);

        var pipe = new System.IO.Pipelines.Pipe();
        await using var source = pipe.Reader.AsStream();
        try
        {
            var upload = client.Strings.SetAsync("key", source, 4).AsTask();
            await pipe.Writer.WriteAsync("da"u8.ToArray());
            await Task.Delay(400);
            await Assert.That(upload.IsCompleted).IsFalse();
            await pipe.Writer.WriteAsync("ta"u8.ToArray());
            await Assert.That(await upload.WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();
            var nextBlocking = await pool.RentAsync(CancellationToken.None);
            await Assert.That(ReferenceEquals(blocking, nextBlocking)).IsTrue();
            pool.Return(nextBlocking);
        }
        finally { await pipe.Writer.CompleteAsync(); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MaintenanceLeaseKindCanRetainAllFourIdleConnections(bool streaming)
    {
        await using var server = Server(maxConnections: 8);
        await using var client = RespireClient.Create(Options(server));
        var pool = await client.Core.GetDedicatedPoolAsync(CancellationToken.None);
        var kind = streaming ? DedicatedLeaseKind.Streaming : DedicatedLeaseKind.Ordinary;
        var connections = new RespireConnection[4];
        for (var index = 0; index < connections.Length; index++)
            connections[index] = await pool.RentAsync(CancellationToken.None, kind: kind);
        foreach (var connection in connections) pool.Return(connection);
        var reused = new RespireConnection[4];
        for (var index = 0; index < reused.Length; index++)
        {
            reused[index] = await pool.RentAsync(CancellationToken.None, kind: kind);
            await Assert.That(ReferenceEquals(reused[index], connections[3 - index])).IsTrue();
        }
        foreach (var connection in reused) pool.Return(connection);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MaintenanceLeaseKindsShareIdleCapacityWhenOneKindFillsThePool(bool streamingFirst)
    {
        await using var server = Server(maxConnections: 8);
        server.ReplyOverride = (_, command) => command == "HELLO 3" ? Hello : FakeRespServer.OkReply;
        await using var client = RespireClient.Create(Options(server));
        var pool = await client.Core.GetDedicatedPoolAsync(CancellationToken.None);
        var first = new RespireConnection[4];
        for (var index = 0; index < first.Length; index++)
            first[index] = await pool.RentAsync(CancellationToken.None, kind: streamingFirst ? DedicatedLeaseKind.Streaming : DedicatedLeaseKind.Ordinary);
        foreach (var connection in first) pool.Return(connection);
        var other = await pool.RentAsync(CancellationToken.None, kind: streamingFirst ? DedicatedLeaseKind.Ordinary : DedicatedLeaseKind.Streaming);
        pool.Return(other);
        var reused = await pool.RentAsync(CancellationToken.None, kind: streamingFirst ? DedicatedLeaseKind.Ordinary : DedicatedLeaseKind.Streaming);
        await Assert.That(ReferenceEquals(reused, other)).IsTrue();
        pool.Return(reused);
        var retained = await pool.RentAsync(CancellationToken.None, kind: streamingFirst ? DedicatedLeaseKind.Streaming : DedicatedLeaseKind.Ordinary);
        await Assert.That(ReferenceEquals(retained, first[3])).IsTrue();
        pool.Return(retained);
    }

    [Test]
    public async Task StreamedSetUploadUsesRelaxedDeadlineDuringMaintenance()
    {
        await using var server = Server();
        await using var connection = await Connect(server, TimeSpan.FromMilliseconds(150));
        await server.SendRawAsync(Start("MIGRATING", 1));
        await WaitForMaintenance(connection);

        var pipe = new System.IO.Pipelines.Pipe();
        await using var source = pipe.Reader.AsStream();
        var command = new StreamedSetCommand((RespireValue)"key", source, 4, default, SetWhen.Always);
        var set = connection.SendCheckedAsync(in command, commandName: "SET").AsTask();
        // Outlast the normal 150 ms command timeout mid-upload; the relaxed 5 s deadline applies.
        await pipe.Writer.WriteAsync("da"u8.ToArray());
        await Task.Delay(400);
        await Assert.That(set.IsCompleted).IsFalse();
        await Assert.That(connection.IsConnected).IsTrue();
        await pipe.Writer.WriteAsync("ta"u8.ToArray());

        using var reply = await set.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(reply.IsError).IsFalse();
        await Assert.That(connection.IsConnected).IsTrue();
        await Assert.That(server.ReceivedCommands.Contains("SET key data")).IsTrue();
    }

    [Test]
    public async Task StreamedSetUploadRestoresNormalDeadlineWhenMaintenanceCompletes()
    {
        await using var server = Server();
        // MaintenanceRelaxedTimeout is 5 s and the window lasts 10 s; completion must end both early.
        await using var connection = await Connect(server, TimeSpan.FromMilliseconds(150));
        await server.SendRawAsync(Start("MIGRATING", 1));
        await WaitForMaintenance(connection);

        var pipe = new System.IO.Pipelines.Pipe();
        await using var source = pipe.Reader.AsStream();
        var command = new StreamedSetCommand((RespireValue)"key", source, 4, default, SetWhen.Always);
        var set = connection.SendCheckedAsync(in command, commandName: "SET").AsTask();
        await pipe.Writer.WriteAsync("da"u8.ToArray());
        await Task.Delay(400);
        await Assert.That(set.IsCompleted).IsFalse();

        await server.SendRawAsync(Finish("MIGRATED", 1));
        await Assert.That(async () => { using var _ = await set.WaitAsync(TimeSpan.FromSeconds(3)); })
            .Throws<RespireTimeoutException>();
        // The deadline fired while the first chunk was still being read, before any header bytes
        // were queued, so the connection stays usable.
        await Assert.That(connection.IsConnected).IsTrue();
    }

    [Test]
    public async Task StreamedSetTimeoutReportsRelaxedDeadline()
    {
        await using var server = Server();
        await using var connection = await Connect(server, TimeSpan.FromMilliseconds(150),
            window: TimeSpan.FromSeconds(2), relaxed: TimeSpan.FromMilliseconds(500));
        await server.SendRawAsync(Start("MIGRATING", 1));
        await WaitForMaintenance(connection);

        var pipe = new System.IO.Pipelines.Pipe();
        await using var source = pipe.Reader.AsStream();
        var command = new StreamedSetCommand((RespireValue)"key", source, 4, default, SetWhen.Always);
        var set = connection.SendCheckedAsync(in command, commandName: "SET").AsTask();
        await pipe.Writer.WriteAsync("da"u8.ToArray());

        var error = await Assert.That(async () => { using var _ = await set.WaitAsync(TimeSpan.FromSeconds(3)); })
            .Throws<RespireTimeoutException>();
        await Assert.That(error!.Timeout).IsEqualTo(TimeSpan.FromMilliseconds(500));
        // Only the first chunk was being read, so no partial frame needed the connection closed.
        await Assert.That(connection.IsConnected).IsTrue();
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
        using var meters = MeterFor("redis.client.maintenance.notifications", (value, tags) =>
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
        using var notifications = MeterFor("redis.client.maintenance.notifications", (value, tags) =>
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

    [Test]
    [Arguments(">3\r\n+SMIGRATED\r\n:1\r\n*1\r\n*3\r\n+old:6379\r\n+new:6380\r\n+0-10\r\n", true)]
    // Identified by kind alone, before the payload is validated.
    [Arguments(">3\r\n+SMIGRATED\r\n:1\r\n*1\r\n*2\r\n+old:6379\r\n+new:6379\r\n", true)]
    [Arguments(">3\r\n$9\r\nSMIGRATED\r\n:1\r\n*0\r\n", true)]
    [Arguments(">3\r\n+SMIGRATING\r\n:1\r\n+0-10\r\n", false)]
    [Arguments(">2\r\n+MIGRATED\r\n:1\r\n", false)]
    [Arguments(">0\r\n", false)]
    [Arguments("*3\r\n+SMIGRATED\r\n:1\r\n*0\r\n", false)]
    public async Task IdentifiesSlotMigrationPushBeforeParsing(string wire, bool expected)
    {
        var position = 0;
        var bytes = Encoding.UTF8.GetBytes(wire);
        if (RespParser.TryParseValue(bytes, ref position, out var value) != RespParseStatus.Done) throw new Exception("Invalid test fixture");
        bool actual;
        using (value) actual = MaintenanceNotification.IsSlotMigrationPush(in value);
        await Assert.That(actual).IsEqualTo(expected);
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
        TimeSpan? responseTimeout = null, TimeSpan? window = null, int capacity = 16, TimeSpan? relaxed = null)
        => RespireConnection.ConnectAsync("127.0.0.1", server.Port, new()
        {
            Protocol = RespProtocol.Resp3,
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Enabled,
            CommandTimeout = timeout,
            ResponseTimeout = responseTimeout,
            MaintenanceRelaxedTimeout = relaxed ?? TimeSpan.FromSeconds(5),
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
