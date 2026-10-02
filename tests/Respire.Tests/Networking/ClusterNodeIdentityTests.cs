using System.Text;
using Microsoft.Extensions.Logging;
using Respire.Commands;
using Respire.Infrastructure;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClusterNodeIdentityTests
{
    [Test]
    public async Task HandshakeMigrationsReplayInReceiveOrderAfterPublication()
    {
        var pushes = "+OK\r\n>3\r\n+SMIGRATED\r\n:1\r\n*1\r\n*3\r\n+source:7000\r\n+target:7001\r\n+0\r\n"
            + ">3\r\n+SMIGRATED\r\n:2\r\n*1\r\n*3\r\n+target:7001\r\n+last:7002\r\n+0\r\n";
        await using var server = new FakeRespServer
        {
            ReplyOverride = (_, command) => command switch
            {
                "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
                "CLIENT MAINT_NOTIFICATIONS ON" => Encoding.ASCII.GetBytes(pushes),
                _ => FakeRespServer.OkReply,
            },
        };
        var options = new RespireOptions
        {
            Protocol = RespProtocol.Resp3,
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Enabled,
            Endpoints = [new("127.0.0.1", server.Port)],
        };
        var connectionOptions = options.ToConnectionOptions(enableMaintenanceNotifications: true);
        await using var node = RespireConnectionMultiplexer.Create("127.0.0.1", server.Port, options: connectionOptions);
        var sequences = new List<long>();
        node.MaintenanceNotificationReceived += (_, _, notification, _) => sequences.Add(notification.SequenceId);
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port, connectionOptions);
        // The reply establishes that both preceding pushes have passed the receive loop.
        await connection.DrainPendingMaintenanceNotificationsAsync(CancellationToken.None);
        await Assert.That(sequences.Count).IsEqualTo(0);
        connection.Multiplexer = node;
        connection.ReplayUnpublishedMigrations();
        connection.ReplayUnpublishedMigrations();
        await Assert.That(sequences).IsEquivalentTo(new long[] { 1, 2 });
        await Assert.That(sequences[0]).IsEqualTo(1L);
    }

    [Test]
    public async Task RetirementBarrierTimeoutPreservesActiveUpload()
    {
        await using var server = new FakeRespServer
        {
            ReplyOverride = (_, command) => command == "HELLO 3"
                ? "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray() : FakeRespServer.OkReply,
            SuppressReply = static command => command == "PING",
        };
        var options = new RespireOptions
        {
            Protocol = RespProtocol.Resp3,
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Enabled,
            CommandTimeout = TimeSpan.FromSeconds(10),
            ConnectTimeout = TimeSpan.FromMilliseconds(100),
            Endpoints = [new("127.0.0.1", server.Port)],
            Connections = 1,
        };
        await using var node = await RespireConnectionMultiplexer.CreateAsync(
            "127.0.0.1", server.Port, options: options.ToConnectionOptions(enableMaintenanceNotifications: true));
        var connection = node.GetConnection();
        await using var payload = new PausedUploadStream();
        var command = new StreamedSetCommand((RespireValue)"upload", payload, payload.Length, default, SetWhen.Always);
        var upload = connection.SendCheckedAsync(in command, commandName: "SET").AsTask();
        await payload.Paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var retirement = node.RetireAsync();
            await Task.Delay(300);
            await Assert.That(connection.IsConnected).IsTrue();
            await Assert.That(retirement.IsCompleted).IsFalse();
            payload.Resume.TrySetResult();
            using var reply = await upload.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(reply.AsString()).IsEqualTo("OK");
            await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { payload.Resume.TrySetResult(); }
    }

    private sealed class PausedUploadStream() : MemoryStream(new byte[RespireConnection.StreamChunkSize * 2])
    {
        internal TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position > 0)
            {
                Paused.TrySetResult();
                await Resume.Task.WaitAsync(cancellationToken);
            }
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    [Test]
    public async Task RetirementSendsMaintenanceBarrierBeforeRetiringConnections()
    {
        await using var server = new FakeRespServer
        {
            ReplyOverride = (_, command) => command switch
            {
                "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
                "CLIENT MAINT_NOTIFICATIONS ON" => FakeRespServer.OkReply,
                "PING" => FakeRespServer.PongReply,
                _ => FakeRespServer.OkReply,
            },
        };
        var options = new RespireOptions
        {
            Protocol = RespProtocol.Resp3,
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Enabled,
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            Connections = 1,
        };
        await using var node = await RespireConnectionMultiplexer.CreateAsync(
            "127.0.0.1", server.Port, options: options.ToConnectionOptions(enableMaintenanceNotifications: true));

        await node.RetireAsync();

        await Assert.That(server.ReceivedCommands).Contains("PING");
    }

    [Test]
    public async Task RetirementStopsLateSerializedCommandsBeforeTheMaintenanceBarrier()
    {
        await using var server = new FakeRespServer
        {
            ReplyOverride = (_, command) => command switch
            {
                "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
                "CLIENT MAINT_NOTIFICATIONS ON" => FakeRespServer.OkReply,
                "PING" => FakeRespServer.PongReply,
                _ => FakeRespServer.OkReply,
            },
        };
        var options = new RespireOptions
        {
            Protocol = RespProtocol.Resp3,
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Enabled,
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            Connections = 1,
        };
        await using var node = await RespireConnectionMultiplexer.CreateAsync(
            "127.0.0.1", server.Port, options: options.ToConnectionOptions(enableMaintenanceNotifications: true));
        var connection = node.GetConnection();
        var enteredWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var send = Task.Factory.StartNew(async () =>
        {
            using var reply = await connection.SendAsync(new BlockedWriteCommand(enteredWrite, releaseWrite));
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        await enteredWrite.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var retirement = node.RetireAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!server.ReceivedCommands.Contains("PING")) await Task.Delay(10, timeout.Token);
        releaseWrite.TrySetResult();

        await Assert.That(async () => await send).ThrowsExactly<RespireConnectionRetiredException>();
        await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(server.ReceivedCommands.Contains("ECHO late")).IsFalse();
    }

    [Test]
    public async Task RetirementBoundsDrainWhenCommandTimeoutIsDisabled()
    {
        await using var server = new FakeRespServer
        {
            ReplyOverride = (_, command) => command switch
            {
                "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
                "CLIENT MAINT_NOTIFICATIONS ON" => FakeRespServer.OkReply,
                _ => FakeRespServer.OkReply,
            },
            SuppressReply = static command => command is "PING" or "ECHO stuck",
        };
        var options = new RespireOptions
        {
            Protocol = RespProtocol.Resp3,
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Enabled,
            MaintenanceRelaxedTimeout = TimeSpan.FromMilliseconds(25),
            CommandTimeout = null,
            ConnectTimeout = TimeSpan.FromMilliseconds(50),
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            Connections = 1,
        };
        await using var node = await RespireConnectionMultiplexer.CreateAsync(
            "127.0.0.1", server.Port,
            options: options.ToConnectionOptions(enableMaintenanceNotifications: true) with
            {
                RetirementDrainFallbackTimeout = TimeSpan.FromMilliseconds(150),
            });
        var connection = node.GetConnection();
        var stuckCommand = new RawCommand("*2\r\n$4\r\nECHO\r\n$5\r\nstuck\r\n"u8.ToArray());
        var stuckReply = connection.SendAsync(in stuckCommand).AsTask();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!server.ReceivedCommands.Contains("ECHO stuck")) await Task.Delay(10, timeout.Token);

        await node.RetireAsync().WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.That(async () => await stuckReply.WaitAsync(TimeSpan.FromSeconds(1)))
            .Throws<RespireConnectionException>();
    }

    [Test]
    public async Task RetirementAbortsBulkStreamAfterIdleGrace()
    {
        await using var server = new FakeRespServer
        {
            ReplyOverride = (_, command) => command switch
            {
                "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
                "CLIENT MAINT_NOTIFICATIONS ON" => FakeRespServer.OkReply,
                "GET key" => "$10\r\nhello"u8.ToArray(),
                _ => FakeRespServer.OkReply,
            },
            SuppressReply = static command => command == "PING",
        };
        var options = new RespireOptions
        {
            Protocol = RespProtocol.Resp3,
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Enabled,
            MaintenanceRelaxedTimeout = TimeSpan.FromMilliseconds(25),
            CommandTimeout = null,
            ConnectTimeout = TimeSpan.FromMilliseconds(50),
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            Connections = 1,
        };
        await using var node = await RespireConnectionMultiplexer.CreateAsync(
            "127.0.0.1", server.Port,
            options: options.ToConnectionOptions(enableMaintenanceNotifications: true) with
            {
                RetirementDrainFallbackTimeout = TimeSpan.FromMilliseconds(150),
            });
        var connection = node.GetConnection();
        var command = new Cmd1(Verbs.Get, "key");
        var stream = await connection.SendBulkStreamAsync(in command, commandName: "GET")
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var read = stream!.CopyToAsync(Stream.Null);

        await node.RetireAsync().WaitAsync(TimeSpan.FromSeconds(3));
        var error = await Assert.That(async () => await read).Throws<Exception>();
        await Assert.That(error is OperationCanceledException or RespireConnectionException).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RetirementWaitsThroughMaintenanceRelaxedCommandDeadline(bool disableCommandTimeout)
    {
        await using var server = new FakeRespServer
        {
            ReplyOverride = (_, command) => command switch
            {
                "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
                "CLIENT MAINT_NOTIFICATIONS ON" => FakeRespServer.OkReply,
                "ECHO x" => "+x\r\n"u8.ToArray(),
                _ => FakeRespServer.OkReply,
            },
            SuppressReply = static command => command == "PING",
        };
        server.DelayCommand("ECHO", 250);
        var options = new RespireOptions
        {
            Protocol = RespProtocol.Resp3,
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Enabled,
            MaintenanceRelaxedTimeout = TimeSpan.FromMilliseconds(600),
            MaintenanceWindowTimeout = TimeSpan.FromSeconds(2),
            CommandTimeout = disableCommandTimeout ? null : TimeSpan.FromMilliseconds(100),
            // Connection setup is outside the maintenance deadline under test.
            ConnectTimeout = TimeSpan.FromSeconds(5),
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            Connections = 1,
        };
        await using var node = await RespireConnectionMultiplexer.CreateAsync(
            "127.0.0.1", server.Port, options: options.ToConnectionOptions(enableMaintenanceNotifications: true));
        var connection = node.GetConnection();
        await server.SendRawAsync(">4\r\n+MOVING\r\n:1\r\n:10\r\n_\r\n"u8.ToArray());
        using var pushTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!connection.HasMaintenanceWindow) await Task.Delay(10, pushTimeout.Token);

        var acceptedCommand = connection.SendAsync(new RawCommand("*2\r\n$4\r\nECHO\r\n$1\r\nx\r\n"u8.ToArray())).AsTask();
        using var commandTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (server.ReceivedCommands.Count(command => command == "ECHO x") == 0)
            await Task.Delay(10, commandTimeout.Token);

        var retirement = node.RetireAsync();
        using var reply = await acceptedCommand.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(reply.IsError).IsFalse();
        await retirement.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task RetirementBarrierTimeoutWaitsForActiveBulkStream()
    {
        var partialPayload = "$10\r\nhello"u8.ToArray();
        await using var server = new FakeRespServer
        {
            ReplyOverride = (_, command) => command switch
            {
                "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
                "CLIENT MAINT_NOTIFICATIONS ON" => FakeRespServer.OkReply,
                "GET key" => partialPayload,
                _ => FakeRespServer.OkReply,
            },
            SuppressReply = static command => command == "PING",
        };
        var options = new RespireOptions
        {
            Protocol = RespProtocol.Resp3,
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Enabled,
            CommandTimeout = TimeSpan.FromMilliseconds(200),
            ConnectTimeout = TimeSpan.FromMilliseconds(100),
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            Connections = 1,
        };
        await using var node = await RespireConnectionMultiplexer.CreateAsync(
            "127.0.0.1", server.Port, options: options.ToConnectionOptions(enableMaintenanceNotifications: true));
        var connection = node.GetConnection();
        var get = new Cmd1(Verbs.Get, "key");
        var stream = await connection.SendBulkStreamAsync(in get, commandName: "GET")
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(stream).IsNotNull();

        var connectionId = server.ReceivedConnectionIds[^1];
        var retirement = node.RetireAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(400));
        await Assert.That(retirement.IsCompleted).IsFalse();
        await Assert.That(connection.IsConnected).IsTrue();

        await server.SendRawAsync("world\r\n"u8.ToArray(), connectionId);
        using var reader = new StreamReader(stream!);
        await Assert.That(await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5)))
            .IsEqualTo("helloworld");
        await retirement.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task SmigratedUpdatesOwnedSlotsOnceAndRetiresTheLastSourceSlot()
    {
        var options = Options(6379);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", 6379,
            options: options.ToConnectionOptions(enableMaintenanceNotifications: true));
        await using var router = new ClusterRouter(options, primary);
        var sourceEndpoint = new RespireEndpoint("source", 7000);
        var targetEndpoint = new RespireEndpoint("target", 7001);
        var duplicateTargetEndpoint = new RespireEndpoint("other", 7002);
        var sentinelEndpoint = new RespireEndpoint("sentinel", 7004);
        var source = router.GetMultiplexer(sourceEndpoint);
        var target = router.GetMultiplexer(targetEndpoint);
        var duplicateTarget = router.GetMultiplexer(duplicateTargetEndpoint);
        var askEndpoint = new RespireEndpoint("ask-only", 7003);
        var askNode = router.GetOrCreateNode(askEndpoint, observe: true, redirect: true);
        router.SetSlotOwner(0, source);
        router.SetSlotOwner(1, source);
        router.SetSlotOwner(2, target);
        router.SetSeed(source);
        var topologyChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        router.TopologyChanged += (_, _, _) => topologyChanged.TrySetResult();
        var connection = new object();

        source.PublishMaintenanceNotification(connection, new("SMIGRATED", 42, Migrations:
            [new(sourceEndpoint, targetEndpoint, "0-1")]));
        await topologyChanged.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(source.IsRetired).IsTrue();

        await Assert.That(ReferenceEquals(router.GetKnownSlotOwner(0), target)).IsTrue();
        await Assert.That(ReferenceEquals(router.GetKnownSlotOwner(1), target)).IsTrue();
        await Assert.That(ReferenceEquals(router.GetMultiplexer(askEndpoint), askNode)).IsTrue();
        await Assert.That(ReferenceEquals(router.Seed, target)).IsTrue();

        // The duplicate ID is ignored. The worker is FIFO, so once the following notification
        // has been applied the duplicate has already been processed.
        topologyChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        target.PublishMaintenanceNotification(connection, new("SMIGRATED", 42, Migrations:
            [new(targetEndpoint, duplicateTargetEndpoint, "0-1")]));
        target.PublishMaintenanceNotification(connection, new("SMIGRATED", 43, Migrations:
            [new(targetEndpoint, sentinelEndpoint, "2")]));
        await topologyChanged.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // TopologyChanged fires after the slot is published, so no polling is needed here.
        await Assert.That(router.GetKnownSlotOwner(2)?.Port).IsEqualTo(7004);
        await Assert.That(ReferenceEquals(router.GetKnownSlotOwner(0), target)).IsTrue();
        await Assert.That(ReferenceEquals(router.GetKnownSlotOwner(1), target)).IsTrue();
        await Assert.That(ReferenceEquals(source, duplicateTarget)).IsFalse();
    }

    [Test]
    public async Task RedirectProtectedZeroSlotNodeKeepsItsMaintenanceHandler()
    {
        var options = Options(6379);
        await using var primary = RespireConnectionMultiplexer.Create("redirect-protected-test", 6379,
            options: options.ToConnectionOptions(enableMaintenanceNotifications: true));
        await using var router = new ClusterRouter(options, primary);
        var sourceEndpoint = new RespireEndpoint("redirect-protected-test", 7000);
        var targetEndpoint = new RespireEndpoint("target", 7001);
        var source = router.GetMultiplexer(sourceEndpoint);
        router.SetSlotOwner(0, source);
        router.GetOrCreateNode(sourceEndpoint, observe: true, redirect: true);

        var malformedSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new System.Diagnostics.Metrics.MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == RespireTelemetry.SourceName
                    && instrument.Name == "respire.cluster.slot_migrations.skipped")
                    meterListener.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var isSource = false;
            var isMalformed = false;
            foreach (var tag in tags)
            {
                if (tag.Key == "server.address" && Equals(tag.Value, sourceEndpoint.Host))
                    isSource = true;
                if (tag.Key == "reason" && Equals(tag.Value, "malformed"))
                    isMalformed = true;
            }
            if (isSource && isMalformed) malformedSeen.TrySetResult();
        });
        listener.Start();

        var topologyChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        router.TopologyChanged += (_, _, _) => topologyChanged.TrySetResult();
        var connection = new object();
        source.PublishMaintenanceNotification(connection, new("SMIGRATED", 1, Migrations:
            [new(sourceEndpoint, targetEndpoint, "0")]));
        await topologyChanged.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(router.GetKnownSlotOwner(0)?.Port).IsEqualTo(targetEndpoint.Port);
        await Assert.That(source.IsRetired).IsFalse();

        source.PublishMaintenanceNotification(connection, new("SMIGRATED", 2, Migrations:
            [new(sourceEndpoint, targetEndpoint, "invalid")]));
        await malformedSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task ConfiguredZeroSlotSeedKeepsItsMaintenanceHandler()
    {
        var options = Options(6379);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", 6379,
            options: options.ToConnectionOptions(enableMaintenanceNotifications: true));
        await using var router = new ClusterRouter(options, primary);
        var seedEndpoint = new RespireEndpoint("127.0.0.1", 6379);
        var firstTargetEndpoint = new RespireEndpoint("first-target", 7001);
        var secondTargetEndpoint = new RespireEndpoint("second-target", 7002);
        var firstTarget = router.GetMultiplexer(firstTargetEndpoint);
        var secondTarget = router.GetMultiplexer(secondTargetEndpoint);
        router.SetSlotOwner(0, primary);
        router.SetSlotOwner(1, firstTarget);
        router.SetSeed(primary);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        router.TopologyChanged += (_, _, _) => changed.TrySetResult();
        var connection = new object();

        primary.PublishMaintenanceNotification(connection, new("SMIGRATED", 1, Migrations:
            [new(seedEndpoint, firstTargetEndpoint, "0")]));
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(router.GetKnownSlotOwner(0)?.Port).IsEqualTo(firstTargetEndpoint.Port);
        await Assert.That(primary.IsRetired).IsFalse();

        // Discovery omits the seed now that it owns no slots; retention must preserve its handler.
        router.ApplyTopology([new(0, 1, firstTargetEndpoint, "first-target", [])], router.TopologyVersion, 1L);

        changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.PublishMaintenanceNotification(connection, new("SMIGRATED", 2, Migrations:
            [new(firstTargetEndpoint, secondTargetEndpoint, "1")]));
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(router.GetKnownSlotOwner(1)?.Port).IsEqualTo(secondTargetEndpoint.Port);
    }

    [Test]
    public async Task NodeRetiredHandlerCanDisposeTheRouterFromTheSmigratedWorker()
    {
        var options = Options(6379);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", 6379,
            options: options.ToConnectionOptions(enableMaintenanceNotifications: true));
        var router = new ClusterRouter(options, primary);
        var sourceEndpoint = new RespireEndpoint("source", 7000);
        var targetEndpoint = new RespireEndpoint("target", 7001);
        var source = router.GetMultiplexer(sourceEndpoint);
        router.GetMultiplexer(targetEndpoint);
        router.SetSlotOwner(0, source);
        var disposed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Synchronous disposal from the worker's own callback must not wait for that worker.
        router.NodeRetired += _ => disposed.TrySetResult(router.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)));

        source.PublishMaintenanceNotification(new object(), new("SMIGRATED", 1, Migrations:
            [new(sourceEndpoint, targetEndpoint, "0")]));

        await Assert.That(await disposed.Task.WaitAsync(TimeSpan.FromSeconds(10))).IsTrue();
        await router.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task ClientDisposalFromRetiredNodeCallbackKeepsWorkerContextAcrossPoolAwait()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        var endpoint = new RespireEndpoint("127.0.0.1", server.Port);
        await using var client = RespireClient.Create(Options(server.Port));
        var core = client.Core;
        var pool = core.CreateServerPool(endpoint);
        await using var activeConnection = await pool.RentAsync(CancellationToken.None);

        var router = core.Cluster!;
        var sourceEndpoint = new RespireEndpoint("source", 7000);
        var targetEndpoint = new RespireEndpoint("target", 7001);
        var source = router.GetMultiplexer(sourceEndpoint);
        router.GetMultiplexer(targetEndpoint);
        router.SetSlotOwner(0, source);
        core.NotifyCommandStateChanged(source, 0, RespireConnectionState.Reconnecting);

        var disposed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += change =>
        {
            if (change.Endpoint == sourceEndpoint && change.State == RespireConnectionState.Connected)
                disposed.TrySetResult(client.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)));
        };

        source.PublishMaintenanceNotification(new object(), new("SMIGRATED", 1,
            Migrations: [new(sourceEndpoint, targetEndpoint, "0")]));

        await Assert.That(await disposed.Task.WaitAsync(TimeSpan.FromSeconds(10))).IsTrue();
    }

    [Test]
    public async Task DisposalStartedOnTaskRunWaitsForSmigratedWorker()
    {
        var options = Options(6379);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", 6379,
            options: options.ToConnectionOptions(enableMaintenanceNotifications: true));
        var router = new ClusterRouter(options, primary);
        var sourceEndpoint = new RespireEndpoint("source", 7000);
        var targetEndpoint = new RespireEndpoint("target", 7001);
        var source = router.GetMultiplexer(sourceEndpoint);
        router.GetMultiplexer(targetEndpoint);
        router.SetSlotOwner(0, source);
        Task? disposal = null;
        var workerReleased = false;
        var releasedWhenDisposalReturned = false;
        var topologyCallbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueWorker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        router.NodeRetired += _ => disposal = Task.Run(async () =>
        {
            await router.DisposeAsync();
            releasedWhenDisposalReturned = Volatile.Read(ref workerReleased);
        });
        router.TopologyChanged += (_, _, _) =>
        {
            topologyCallbackEntered.TrySetResult();
            continueWorker.Task.GetAwaiter().GetResult();
        };

        source.PublishMaintenanceNotification(new object(), new("SMIGRATED", 1, Migrations:
            [new(sourceEndpoint, targetEndpoint, "0")]));

        await topologyCallbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => router.IsDisposed);
        // Disposal has started. Joining the worker is its last blocking step, so give it room to
        // get there; a disposal that skipped the join would return within this window.
        await Task.WhenAny(disposal!, Task.Delay(TimeSpan.FromMilliseconds(500)));
        await Assert.That(disposal!.IsCompleted).IsFalse();
        Volatile.Write(ref workerReleased, true);
        continueWorker.TrySetResult();
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        // Not timing-based: disposal returned only after the worker callback was released.
        await Assert.That(releasedWhenDisposalReturned).IsTrue();
        await router.DisposeAsync();
    }

    [Test, NotInParallel]
    public async Task QueueOverflowDropsOldestAndCountsTheDrops()
    {
        using var logger = new WarningCaptureLogger();
        using var inReceiveCallback = new ThreadLocal<bool>(() => false);
        var metricRanOnReceiveThread = 0;
        var metricHadSenderTags = 0;
        const long expectedDrops = 372;
        long metricDroppedCount = 0;
        var allMetricsReported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var metricReported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMetricCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new System.Diagnostics.Metrics.MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == RespireTelemetry.SourceName
                    && instrument.Name == "respire.cluster.slot_migrations.skipped")
                    meterListener.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            var isQueueDrop = false;
            var hasSenderTags = false;
            foreach (var tag in tags)
            {
                if (tag.Key == "reason" && Equals(tag.Value, "queue_full")) isQueueDrop = true;
                if (tag.Key is "server.address" or "server.port") hasSenderTags = true;
            }
            if (isQueueDrop)
            {
                if (Interlocked.Add(ref metricDroppedCount, measurement) == expectedDrops)
                    allMetricsReported.TrySetResult();
                if (hasSenderTags) Interlocked.Exchange(ref metricHadSenderTags, 1);
                if (inReceiveCallback.Value) Interlocked.Exchange(ref metricRanOnReceiveThread, 1);
                metricReported.TrySetResult();
                releaseMetricCallback.Task.GetAwaiter().GetResult();
            }
        });
        listener.Start();
        var options = Options(6379) with { LoggerFactory = logger };
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", 6379,
            options: options.ToConnectionOptions(enableMaintenanceNotifications: true));
        await using var router = new ClusterRouter(options, primary);
        await Assert.That(router.NextTopologyRefresh().Wait).IsNull();
        var sourceEndpoint = new RespireEndpoint("source", 7000);
        var secondSourceEndpoint = new RespireEndpoint("second-source", 7002);
        var targetEndpoint = new RespireEndpoint("target", 7001);
        var source = router.GetMultiplexer(sourceEndpoint);
        var secondSource = router.GetMultiplexer(secondSourceEndpoint);
        router.GetMultiplexer(targetEndpoint);
        for (var slot = 0; slot <= 501; slot++)
            router.SetSlotOwner(slot, slot % 2 == 0 ? source : secondSource);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lastApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        router.TopologyChanged += (_, _, _) =>
        {
            if (blocked.TrySetResult()) release.Task.GetAwaiter().GetResult();
            if (router.GetKnownSlotOwner(500)?.Port == targetEndpoint.Port) lastApplied.TrySetResult();
        };
        var connection = new object();
        var secondConnection = new object();

        // The worker takes slot 0 and blocks, leaving the whole 128-item queue empty.
        inReceiveCallback.Value = true;
        try
        {
            source.PublishMaintenanceNotification(connection, new("SMIGRATED", 0, Migrations:
                [new(sourceEndpoint, targetEndpoint, "0")]));
        }
        finally
        {
            inReceiveCallback.Value = false;
        }
        try
        {
            await blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var slot = 1; slot <= 500; slot++)
            {
                var notificationSource = slot % 2 == 0 ? source : secondSource;
                var notificationSourceEndpoint = slot % 2 == 0 ? sourceEndpoint : secondSourceEndpoint;
                var notificationConnection = slot % 2 == 0 ? connection : secondConnection;
                inReceiveCallback.Value = true;
                try
                {
                    notificationSource.PublishMaintenanceNotification(notificationConnection, new("SMIGRATED", slot, Migrations:
                        [new(notificationSourceEndpoint, targetEndpoint, slot.ToString(System.Globalization.CultureInfo.InvariantCulture))]));
                }
                finally
                {
                    inReceiveCallback.Value = false;
                }
            }
            await Assert.That(router.SmigratedNotificationsDropped).IsEqualTo(expectedDrops);
            try
            {
                await metricReported.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Assert.That(router.SmigratedDropDiagnosticsQueued).IsEqualTo(1);
                var refreshDecision = router.NextTopologyRefresh();
                await Assert.That(refreshDecision.Run || refreshDecision.Wait is not null).IsTrue();
            }
            finally
            {
                releaseMetricCallback.TrySetResult();
            }
            await allMetricsReported.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await logger.WarningReported.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(metricRanOnReceiveThread).IsEqualTo(0);
            await Assert.That(metricDroppedCount).IsEqualTo(expectedDrops);
            await Assert.That(metricHadSenderTags).IsEqualTo(0);
            await Assert.That(logger.WarningCount).IsEqualTo(1);
            await Assert.That(logger.LastWarning).Contains("Cluster SMIGRATED queue is full");

            release.TrySetResult();
            await lastApplied.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // The 372 oldest queued notifications were dropped; MOVED/discovery would repair them.
            await Assert.That(ReferenceEquals(router.GetKnownSlotOwner(1), secondSource)).IsTrue();
            await Assert.That(ReferenceEquals(router.GetKnownSlotOwner(2), source)).IsTrue();
            await Assert.That(ReferenceEquals(router.GetKnownSlotOwner(372), source)).IsTrue();
            await Assert.That(router.GetKnownSlotOwner(373)?.Port).IsEqualTo(targetEndpoint.Port);
            await Assert.That(router.GetKnownSlotOwner(500)?.Port).IsEqualTo(targetEndpoint.Port);
            await Assert.That(ReferenceEquals(router.GetKnownSlotOwner(501), secondSource)).IsTrue();
        }
        finally
        {
            releaseMetricCallback.TrySetResult();
            release.TrySetResult();
        }
    }

    [Test]
    public async Task DisposalWhileNotificationsAreQueuedDiscardsThem()
    {
        var options = Options(6379);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", 6379,
            options: options.ToConnectionOptions(enableMaintenanceNotifications: true));
        var router = new ClusterRouter(options, primary);
        var sourceEndpoint = new RespireEndpoint("source", 7000);
        var targetEndpoint = new RespireEndpoint("target", 7001);
        var source = router.GetMultiplexer(sourceEndpoint);
        router.GetMultiplexer(targetEndpoint);
        for (var slot = 0; slot <= 3; slot++) router.SetSlotOwner(slot, source);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        router.TopologyChanged += (_, _, _) =>
        {
            if (blocked.TrySetResult()) release.Task.GetAwaiter().GetResult();
        };
        var connection = new object();
        source.PublishMaintenanceNotification(connection, new("SMIGRATED", 0, Migrations:
            [new(sourceEndpoint, targetEndpoint, "0")]));
        await blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var slot = 1; slot <= 3; slot++)
            source.PublishMaintenanceNotification(connection, new("SMIGRATED", slot, Migrations:
                [new(sourceEndpoint, targetEndpoint, slot.ToString(System.Globalization.CultureInfo.InvariantCulture))]));

        var disposal = Task.Run(async () => await router.DisposeAsync());
        await WaitUntilAsync(() => router.IsDisposed);
        await Assert.That(disposal.IsCompleted).IsFalse();
        release.TrySetResult();
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));

        // Disposal joined the worker, which drained the queue without applying anything.
        for (var slot = 1; slot <= 3; slot++)
            await Assert.That(ReferenceEquals(router.GetKnownSlotOwner(slot), source)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EarlierDependentMigrationAppliesInEitherWorkerOrder(bool predecessorFirst)
    {
        var options = Options(6379);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", 6379,
            options: options.ToConnectionOptions(enableMaintenanceNotifications: true));
        await using var router = new ClusterRouter(options, primary);
        var aEndpoint = new RespireEndpoint("a", 7000);
        var bEndpoint = new RespireEndpoint("b", 7001);
        var cEndpoint = new RespireEndpoint("c", 7002);
        var a = router.GetMultiplexer(aEndpoint);
        var b = router.GetMultiplexer(bEndpoint);
        router.SetSlotOwner(0, a);
        router.SetSlotOwner(1, a);

        // B->C is received first, so it has the lower fence token, even when worker order differs.
        var bc = router.CaptureSmigratedNotification(b, new object(),
            new("SMIGRATED", 1, Migrations: [new(bEndpoint, cEndpoint, "0")]));
        var ab = router.CaptureSmigratedNotification(a, new object(),
            new("SMIGRATED", 1, Migrations: [new(aEndpoint, bEndpoint, "0")]));

        if (predecessorFirst)
        {
            router.ApplySmigratedNotification(ab);
            router.ApplySmigratedNotification(bc);
        }
        else
        {
            router.ApplySmigratedNotification(bc);
            await Assert.That(ReferenceEquals(router.GetKnownSlotOwner(0), a)).IsTrue();
            router.ApplySmigratedNotification(ab);
        }

        await Assert.That(router.GetKnownSlotOwner(0)?.Port).IsEqualTo(cEndpoint.Port);
        await Assert.That(ReferenceEquals(router.GetKnownSlotOwner(1), a)).IsTrue();
    }

    [Test]
    public async Task DeferredMigrationCannotOverrideALaterOwnerChange()
    {
        var options = Options(6379);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", 6379,
            options: options.ToConnectionOptions(enableMaintenanceNotifications: true));
        await using var router = new ClusterRouter(options, primary);
        var aEndpoint = new RespireEndpoint("a", 7000);
        var bEndpoint = new RespireEndpoint("b", 7001);
        var cEndpoint = new RespireEndpoint("c", 7002);
        var dEndpoint = new RespireEndpoint("d", 7003);
        var a = router.GetMultiplexer(aEndpoint);
        var d = router.GetMultiplexer(dEndpoint);
        router.SetSlotOwner(0, a);
        var staleBc = router.CaptureSmigratedNotification(a, new object(),
            new("SMIGRATED", 1, Migrations: [new(bEndpoint, cEndpoint, "0")]));
        router.ApplySmigratedNotification(staleBc);

        // Redirects move the slot away and back, then a fresh A->B is received.
        router.SetSlotOwner(0, d);
        router.SetSlotOwner(0, a);
        router.ApplySmigratedNotification(router.CaptureSmigratedNotification(a, new object(),
            new("SMIGRATED", 1, Migrations: [new(aEndpoint, bEndpoint, "0")])));

        await Assert.That(router.GetKnownSlotOwner(0)?.Port).IsEqualTo(bEndpoint.Port);
    }

    [Test]
    public async Task MalformedSequenceIsConsumedAndSkippedMetricRunsOutsideTopologyLock()
    {
        var options = Options(6399);
        await using var primary = RespireConnectionMultiplexer.Create("smigrated-metric-test", 6399,
            options: options.ToConnectionOptions(enableMaintenanceNotifications: true));
        await using var router = new ClusterRouter(options, primary);
        var source = new RespireEndpoint("source", 7000);
        var target = new RespireEndpoint("target", 7001);
        var duplicateCount = 0L;
        var malformedCount = 0L;
        var metricCallbackReentered = false;
        using var listener = new System.Diagnostics.Metrics.MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == RespireTelemetry.SourceName
                    && instrument.Name == "respire.cluster.slot_migrations.skipped")
                    meterListener.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var isTestServer = false;
            var isDuplicate = false;
            var isMalformed = false;
            foreach (var tag in tags)
            {
                if (tag.Key == "server.address" && Equals(tag.Value, "smigrated-metric-test"))
                    isTestServer = true;
                if (tag.Key == "reason" && Equals(tag.Value, "duplicate"))
                    isDuplicate = true;
                if (tag.Key == "reason" && Equals(tag.Value, "malformed"))
                    isMalformed = true;
            }
            if (isTestServer && isMalformed) Interlocked.Increment(ref malformedCount);
            if (isTestServer && isDuplicate)
            {
                Interlocked.Increment(ref duplicateCount);
                metricCallbackReentered = Task.Run(() => router.GetMultiplexer(new("reentered", 7002)))
                    .Wait(TimeSpan.FromSeconds(2));
            }
        });
        listener.Start();

        var scope = new object();
        var malformed = router.CaptureSmigratedNotification(primary, scope,
            new("SMIGRATED", 7, Migrations: [new(source, target, "invalid")]));
        router.ApplySmigratedNotification(malformed);
        router.ApplySmigratedNotification(malformed);

        await Assert.That(duplicateCount).IsEqualTo(1L);
        await Assert.That(malformedCount).IsEqualTo(1L);
        await Assert.That(metricCallbackReentered).IsTrue();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task DeferredMigrationChainResolvesWhenItsFirstLinkArrives(bool receivedInChainOrder)
    {
        var options = Options(6379);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", 6379,
            options: options.ToConnectionOptions(enableMaintenanceNotifications: true));
        await using var router = new ClusterRouter(options, primary);
        const int links = 64;
        var endpoints = Enumerable.Range(0, links + 1).Select(i => new RespireEndpoint($"n{i}", 7000 + i)).ToArray();
        var first = router.GetMultiplexer(endpoints[0]);
        router.SetSlotOwner(0, first);
        router.SetSlotOwner(1, first);

        var order = receivedInChainOrder ? Enumerable.Range(0, links) : Enumerable.Range(0, links).Reverse();
        var captured = new ClusterRouter.QueuedSmigratedNotification[links];
        foreach (var i in order)
            captured[i] = router.CaptureSmigratedNotification(first, new object(),
                new("SMIGRATED", 1, Migrations: [new(endpoints[i], endpoints[i + 1], "0-1")]));

        // Every link but the first waits; the full deferral list (63 entries) then resolves in
        // one call when the first link applies.
        for (var i = links - 1; i >= 1; i--) router.ApplySmigratedNotification(captured[i]);
        await Assert.That(ReferenceEquals(router.GetKnownSlotOwner(0), first)).IsTrue();
        router.ApplySmigratedNotification(captured[0]);

        await Assert.That(router.GetKnownSlotOwner(0)?.Port).IsEqualTo(7000 + links);
        await Assert.That(router.GetKnownSlotOwner(1)?.Port).IsEqualTo(7000 + links);
    }

    [Test]
    public async Task DeferredMigrationExpiresWhenItsDependencyArrivesTooLate()
    {
        var options = Options(6379);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", 6379,
            options: options.ToConnectionOptions(enableMaintenanceNotifications: true));
        var now = 1_000L;
        await using var router = new ClusterRouter(options, primary, migrationClock: () => now);
        var aEndpoint = new RespireEndpoint("a", 7000);
        var bEndpoint = new RespireEndpoint("b", 7001);
        var cEndpoint = new RespireEndpoint("c", 7002);
        var a = router.GetMultiplexer(aEndpoint);
        router.SetSlotOwner(0, a);
        router.SetSlotOwner(1, a);

        router.ApplySmigratedNotification(router.CaptureSmigratedNotification(a, new object(),
            new("SMIGRATED", 1, Migrations: [new(bEndpoint, cEndpoint, "0")])));
        now += 29_999;
        router.ApplySmigratedNotification(router.CaptureSmigratedNotification(a, new object(),
            new("SMIGRATED", 1, Migrations: [new(bEndpoint, cEndpoint, "1")])));
        now += 1;
        // The first B->C entry is now 30 seconds old and expires; the second still applies.
        router.ApplySmigratedNotification(router.CaptureSmigratedNotification(a, new object(),
            new("SMIGRATED", 1, Migrations: [new(aEndpoint, bEndpoint, "0-1")])));

        await Assert.That(router.GetKnownSlotOwner(0)?.Port).IsEqualTo(bEndpoint.Port);
        await Assert.That(router.GetKnownSlotOwner(1)?.Port).IsEqualTo(cEndpoint.Port);
    }

    [Test]
    [NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DeferredSkipMetricKeepsOriginalSender(bool evict)
    {
        var options = Options(6379);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", 6379,
            options: options.ToConnectionOptions(enableMaintenanceNotifications: true));
        var now = 1_000L;
        await using var router = new ClusterRouter(options, primary, migrationClock: () => now);
        var original = router.GetMultiplexer(new("original-metric-sender", 7100));
        var later = router.GetMultiplexer(new("later-metric-sender", 7101));
        var recorded = new List<(string? Host, int? Port)>();
        using var listener = new System.Diagnostics.Metrics.MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == RespireTelemetry.SourceName
                    && instrument.Name == "respire.cluster.slot_migrations.skipped")
                    meterListener.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string? reason = null;
            string? host = null;
            int? port = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "reason") reason = tag.Value as string;
                if (tag.Key == "server.address") host = tag.Value as string;
                if (tag.Key == "server.port") port = tag.Value as int?;
            }
            if (reason == (evict ? "deferral_evicted" : "deferral_expired")) recorded.Add((host, port));
        });
        listener.Start();
        var migration = new MaintenanceSlotMigration(new("absent-source", 7200), new("target", 7201), "0");
        router.ApplySmigratedNotification(router.CaptureSmigratedNotification(original, new object(),
            new("SMIGRATED", 1, Migrations: [migration])));
        if (!evict) now += 30_000;
        for (var i = 0; i < (evict ? 64 : 1); i++)
            router.ApplySmigratedNotification(router.CaptureSmigratedNotification(later, new object(),
                new("SMIGRATED", 1, Migrations: [migration])));

        await Assert.That(recorded.Count).IsEqualTo(1);
        await Assert.That(recorded[0].Host).IsEqualTo(original.Host);
        await Assert.That(recorded[0].Port).IsEqualTo(original.Port);
    }

    [Test]
    public async Task ResentSequenceIdIsIgnoredEvenAfterItsFirstCopyWasFenced()
    {
        var options = Options(6379);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", 6379,
            options: options.ToConnectionOptions(enableMaintenanceNotifications: true));
        await using var router = new ClusterRouter(options, primary);
        var sourceEndpoint = new RespireEndpoint("source", 7000);
        var targetEndpoint = new RespireEndpoint("target", 7001);
        var source = router.GetMultiplexer(sourceEndpoint);
        var other = router.GetMultiplexer(new RespireEndpoint("other", 7002));
        router.SetSlotOwner(0, source);
        var connection = new object();

        // The first copy is fenced by redirects made after it was received.
        var fenced = router.CaptureSmigratedNotification(source, connection,
            new("SMIGRATED", 5, Migrations: [new(sourceEndpoint, targetEndpoint, "0")]));
        router.SetSlotOwner(0, other);
        router.SetSlotOwner(0, source);
        router.ApplySmigratedNotification(fenced);
        await Assert.That(ReferenceEquals(router.GetKnownSlotOwner(0), source)).IsTrue();

        // A resend with the same ID on the same connection is a replay and is not re-evaluated,
        // even though it would now apply.
        router.ApplySmigratedNotification(router.CaptureSmigratedNotification(source, connection,
            new("SMIGRATED", 5, Migrations: [new(sourceEndpoint, targetEndpoint, "0")])));
        await Assert.That(ReferenceEquals(router.GetKnownSlotOwner(0), source)).IsTrue();

        router.ApplySmigratedNotification(router.CaptureSmigratedNotification(source, connection,
            new("SMIGRATED", 6, Migrations: [new(sourceEndpoint, targetEndpoint, "0")])));
        await Assert.That(router.GetKnownSlotOwner(0)?.Port).IsEqualTo(targetEndpoint.Port);
    }

    [Test]
    public async Task ResentSequenceIdIsIgnoredAfterMoreThanTheRecentSequenceWindow()
    {
        var options = Options(6379);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", 6379,
            options: options.ToConnectionOptions(enableMaintenanceNotifications: true));
        await using var router = new ClusterRouter(options, primary);
        var sourceEndpoint = new RespireEndpoint("source", 7000);
        var targetEndpoint = new RespireEndpoint("target", 7001);
        var source = router.GetMultiplexer(sourceEndpoint);
        router.SetSlotOwner(0, source);
        router.SetSlotOwner(1, source);
        var connection = new object();

        router.ApplySmigratedNotification(router.CaptureSmigratedNotification(source, connection,
            new("SMIGRATED", 5, Migrations: [new(sourceEndpoint, targetEndpoint, "0")])));
        router.ApplySmigratedNotification(router.CaptureSmigratedNotification(source, connection,
            new("SMIGRATED", 6, Migrations: [new(targetEndpoint, sourceEndpoint, "0")])));
        for (var sequence = 7; sequence <= 300; sequence++)
        {
            router.ApplySmigratedNotification(router.CaptureSmigratedNotification(source, connection,
                new("SMIGRATED", sequence, Migrations: [new(sourceEndpoint, sourceEndpoint, "0")])));
        }

        // The old copy would move the slot to target if the deduplication fence forgot sequence 5.
        router.ApplySmigratedNotification(router.CaptureSmigratedNotification(source, connection,
            new("SMIGRATED", 5, Migrations: [new(sourceEndpoint, targetEndpoint, "0")])));

        await Assert.That(ReferenceEquals(router.GetKnownSlotOwner(0), source)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OlderMigrationCannotMoveASlotThatLeftAndReturnedToItsSource(bool sourceRetiresInBetween)
    {
        var options = Options(6379);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", 6379,
            options: options.ToConnectionOptions(enableMaintenanceNotifications: true));
        await using var router = new ClusterRouter(options, primary);
        var aEndpoint = new RespireEndpoint("a", 7000);
        var bEndpoint = new RespireEndpoint("b", 7001);
        var cEndpoint = new RespireEndpoint("c", 7002);
        var a = router.GetMultiplexer(aEndpoint);
        router.SetSlotOwner(0, a);
        router.SetSlotOwner(1, a);
        // Without another slot, A->B retires A, and B->A brings the slots back on a new transport.
        if (!sourceRetiresInBetween) router.SetSlotOwner(2, a);

        // Received in this order on different connections: A->C (slot 0 only), B->A, A->B.
        var ac = router.CaptureSmigratedNotification(a, new object(),
            new("SMIGRATED", 1, Migrations: [new(aEndpoint, cEndpoint, "0")]));
        var ba = router.CaptureSmigratedNotification(a, new object(),
            new("SMIGRATED", 1, Migrations: [new(bEndpoint, aEndpoint, "0-1")]));
        var ab = router.CaptureSmigratedNotification(a, new object(),
            new("SMIGRATED", 1, Migrations: [new(aEndpoint, bEndpoint, "0-1")]));

        // The worker sees A->B, then the dependent B->A, so A owns the slots again.
        router.ApplySmigratedNotification(ab);
        await Assert.That(a.IsRetired).IsEqualTo(sourceRetiresInBetween);
        router.ApplySmigratedNotification(ba);
        var returned = router.GetKnownSlotOwner(0);
        await Assert.That(returned?.Port).IsEqualTo(aEndpoint.Port);
        await Assert.That(ReferenceEquals(returned, a)).IsEqualTo(!sourceRetiresInBetween);

        // A moved slot 0 away after A->C was received, so A->C predates A's current ownership.
        router.ApplySmigratedNotification(ac);
        await Assert.That(ReferenceEquals(router.GetKnownSlotOwner(0), returned)).IsTrue();
        await Assert.That(ReferenceEquals(router.GetKnownSlotOwner(1), returned)).IsTrue();

        // A migration received after the round trip still applies.
        router.ApplySmigratedNotification(router.CaptureSmigratedNotification(a, new object(),
            new("SMIGRATED", 2, Migrations: [new(aEndpoint, cEndpoint, "0")])));
        await Assert.That(router.GetKnownSlotOwner(0)?.Port).IsEqualTo(cEndpoint.Port);
    }

    [Test]
    public async Task PushCapturedBeforeItsSenderRetiredIsStillDelivered()
    {
        var options = Options(6379);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", 6379,
            options: options.ToConnectionOptions(enableMaintenanceNotifications: true));
        await using var router = new ClusterRouter(options, primary);
        var aEndpoint = new RespireEndpoint("a", 7000);
        var bEndpoint = new RespireEndpoint("b", 7001);
        var cEndpoint = new RespireEndpoint("c", 7002);
        var a = router.GetMultiplexer(aEndpoint);
        router.SetSlotOwner(0, a);
        var topologyChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        router.TopologyChanged += (_, _, _) =>
        {
            if (router.GetKnownSlotOwner(0)?.Port == cEndpoint.Port) topologyChanged.TrySetResult();
        };

        // One receive loop stamps a B->C push, then pauses while another connection retires A.
        using var bcCapture = ClusterSlotMutationClock.BeginCapture();
        var bcToken = bcCapture.Token;

        // Meanwhile A->B, received on another connection, retires A and detaches its handlers.
        router.ApplySmigratedNotification(router.CaptureSmigratedNotification(a, new object(),
            new("SMIGRATED", 1, Migrations: [new(aEndpoint, bEndpoint, "0")])));
        await Assert.That(a.IsRetired).IsTrue();
        await Assert.That(a.CaptureMaintenanceHandlers()).IsNull();
        var handlers = a.CaptureMaintenanceHandlers(bcToken);
        await Assert.That(handlers).IsNotNull();
        await Assert.That(a.MaintenanceHandlerEpochCount).IsGreaterThan(0);
        bcCapture.Dispose();
        await Assert.That(a.MaintenanceHandlerEpochCount).IsEqualTo(0);

        // The paused loop resumes. Its earlier token selects the handler epoch active at receipt.
        a.PublishMaintenanceNotification(handlers, new object(),
            new("SMIGRATED", 1, Migrations: [new(bEndpoint, cEndpoint, "0")]), bcToken);
        await topologyChanged.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(router.GetKnownSlotOwner(0)?.Port).IsEqualTo(cEndpoint.Port);
    }

    [Test]
    public async Task ThrowingMetricListenerDoesNotEscapeQueueDropsOrTheWorker()
    {
        const string throwingHost = "metric-listener-throws";
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (ReferenceEquals(instrument, RespireTelemetry.ClusterSlotMigrationsSkipped))
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "server.address" && Equals(tag.Value, throwingHost))
                    throw new InvalidOperationException("listener failure");
        });
        listener.Start();

        var options = Options(6379);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", 6379,
            options: options.ToConnectionOptions(enableMaintenanceNotifications: true));
        await using var router = new ClusterRouter(options, primary);
        var sourceEndpoint = new RespireEndpoint(throwingHost, 7000);
        var targetEndpoint = new RespireEndpoint("target", 7001);
        var source = router.GetMultiplexer(sourceEndpoint);
        for (var slot = 0; slot <= 130; slot++) router.SetSlotOwner(slot, source);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lastApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        router.TopologyChanged += (_, _, _) =>
        {
            if (blocked.TrySetResult()) release.Task.GetAwaiter().GetResult();
            if (router.GetKnownSlotOwner(130)?.Port == targetEndpoint.Port) lastApplied.TrySetResult();
        };
        var connection = new object();

        source.PublishMaintenanceNotification(connection, new("SMIGRATED", 0, Migrations:
            [new(sourceEndpoint, targetEndpoint, "0")]));
        await blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Overflow diagnostics run off this receive-loop callback; a throwing listener must not escape.
        for (var slot = 1; slot <= 130; slot++)
            source.PublishMaintenanceNotification(connection, new("SMIGRATED", slot, Migrations:
                [new(sourceEndpoint, targetEndpoint, slot.ToString(System.Globalization.CultureInfo.InvariantCulture))]));
        await Assert.That(router.SmigratedNotificationsDropped).IsEqualTo(2);
        release.TrySetResult();
        await lastApplied.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // On the worker, a skipped entry (here a duplicate ID) is counted mid-notification; the
        // listener failure must not abandon the rest of the worker's processing.
        router.ApplySmigratedNotification(router.CaptureSmigratedNotification(source, connection,
            new("SMIGRATED", 130, Migrations: [new(sourceEndpoint, targetEndpoint, "1")])));
        router.ApplySmigratedNotification(router.CaptureSmigratedNotification(source, connection,
            new("SMIGRATED", 131, Migrations: [new(sourceEndpoint, targetEndpoint, "1")])));
        await Assert.That(router.GetKnownSlotOwner(1)?.Port).IsEqualTo(targetEndpoint.Port);
    }

    [Test]
    public async Task ThrowingErrorLoggerDoesNotFaultTheWorker()
    {
        using var logger = new ThrowingErrorLogger();
        var options = Options(6379) with { LoggerFactory = logger };
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", 6379,
            options: options.ToConnectionOptions(enableMaintenanceNotifications: true));
        await using var router = new ClusterRouter(options, primary);
        var sourceEndpoint = new RespireEndpoint("source", 7000);
        var targetEndpoint = new RespireEndpoint("target", 7001);
        var source = router.GetMultiplexer(sourceEndpoint);
        router.SetSlotOwner(0, source);
        router.SetSlotOwner(1, source);
        var secondApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        router.TopologyChanged += (_, _, _) =>
        {
            if (router.GetKnownSlotOwner(1)?.Port == targetEndpoint.Port) secondApplied.TrySetResult();
            else throw new InvalidOperationException("topology callback failure");
        };
        var connection = new object();

        // The first callback throws, and the worker's error log throws too. The worker must
        // survive both and apply the next notification.
        source.PublishMaintenanceNotification(connection, new("SMIGRATED", 1, Migrations:
            [new(sourceEndpoint, targetEndpoint, "0")]));
        source.PublishMaintenanceNotification(connection, new("SMIGRATED", 2, Migrations:
            [new(sourceEndpoint, targetEndpoint, "1")]));

        await secondApplied.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(logger.ErrorCount).IsEqualTo(1);
    }

    [Test]
    public async Task SkippedMetricListenerCanDisposeTheRouterAfterTheSourceRetires()
    {
        const string disposingHost = "metric-listener-disposes";
        var options = Options(6379);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", 6379,
            options: options.ToConnectionOptions(enableMaintenanceNotifications: true));
        var now = 1_000L;
        var router = new ClusterRouter(options, primary, migrationClock: () => now);
        var aEndpoint = new RespireEndpoint(disposingHost, 7000);
        var bEndpoint = new RespireEndpoint("b", 7001);
        var cEndpoint = new RespireEndpoint("c", 7002);
        var a = router.GetMultiplexer(aEndpoint);
        router.SetSlotOwner(0, a);

        bool? disposedFromListener = null;
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (ReferenceEquals(instrument, RespireTelemetry.ClusterSlotMigrationsSkipped))
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "server.address" && Equals(tag.Value, disposingHost) && disposedFromListener is null)
                    disposedFromListener = router.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        });
        listener.Start();

        // B->C waits for B to own slot 1, and expires before A->B arrives.
        router.ApplySmigratedNotification(router.CaptureSmigratedNotification(a, new object(),
            new("SMIGRATED", 1, Migrations: [new(bEndpoint, cEndpoint, "1")])));
        now += 30_000;
        // A->B moves A's last slot, so A retires; the expiry metric runs in the same call.
        router.ApplySmigratedNotification(router.CaptureSmigratedNotification(a, new object(),
            new("SMIGRATED", 2, Migrations: [new(aEndpoint, bEndpoint, "0")])));

        // Disposal waits for A's retirement drain, so the drain must have started first.
        await Assert.That(disposedFromListener).IsEqualTo(true);
        await router.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task MaintenanceHandlerCaptureFollowsSubscriptionAndRetirement()
    {
        await using var node = RespireConnectionMultiplexer.Create("127.0.0.1", 6379,
            options: Options(6379).ToConnectionOptions(enableMaintenanceNotifications: true));
        MaintenanceNotificationHandler handler = (_, _, _, _) => { };

        await Assert.That(node.CaptureMaintenanceHandlers()).IsNull();
        node.MaintenanceNotificationReceived += handler;
        await Assert.That(node.CaptureMaintenanceHandlers()).IsEqualTo(handler);
        node.MaintenanceNotificationReceived -= handler;
        await Assert.That(node.CaptureMaintenanceHandlers()).IsNull();

        node.MaintenanceNotificationReceived += handler;
        _ = node.RetireAsync();
        await Assert.That(node.CaptureMaintenanceHandlers()).IsNull();

        await using var cyclingNode = RespireConnectionMultiplexer.Create("127.0.0.1", 6379,
            options: Options(6379).ToConnectionOptions(enableMaintenanceNotifications: true));
        for (var i = 0; i < 512; i++)
        {
            cyclingNode.MaintenanceNotificationReceived += handler;
            cyclingNode.MaintenanceNotificationReceived -= handler;
        }
        await WaitUntilAsync(() =>
        {
            cyclingNode.PruneMaintenanceHandlerEpochs();
            return cyclingNode.MaintenanceHandlerEpochCount == 0;
        });
    }

    [Test]
    public async Task SmigratedWorkerCallbacksRunWithTheWorkerMarker()
    {
        // Disposal from a worker callback relies on this marker; an await inside the apply path
        // would lose it, so pin the apply method as synchronous too.
        var apply = typeof(ClusterRouter).GetMethod(nameof(ClusterRouter.ApplySmigratedNotification),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await Assert.That(apply.ReturnType).IsEqualTo(typeof(void));
        await Assert.That(apply.IsDefined(typeof(System.Runtime.CompilerServices.AsyncStateMachineAttribute), false)).IsFalse();

        var options = Options(6379);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", 6379,
            options: options.ToConnectionOptions(enableMaintenanceNotifications: true));
        await using var router = new ClusterRouter(options, primary);
        var sourceEndpoint = new RespireEndpoint("source", 7000);
        var targetEndpoint = new RespireEndpoint("target", 7001);
        var source = router.GetMultiplexer(sourceEndpoint);
        router.SetSlotOwner(0, source);
        var retiredOnWorker = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var changedOnWorker = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        router.NodeRetired += _ => retiredOnWorker.TrySetResult(router.IsOnSmigratedWorker);
        router.TopologyChanged += (_, _, _) => changedOnWorker.TrySetResult(router.IsOnSmigratedWorker);

        source.PublishMaintenanceNotification(new object(), new("SMIGRATED", 1, Migrations:
            [new(sourceEndpoint, targetEndpoint, "0")]));

        await Assert.That(await retiredOnWorker.Task.WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();
        await Assert.That(await changedOnWorker.Task.WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();
        await Assert.That(router.IsOnSmigratedWorker).IsFalse();
    }

    [Test]
    public async Task MalformedEntryLoggerFailureDoesNotDiscardTheOtherEntries()
    {
        using var logger = new ThrowingDebugLogger();
        var options = Options(6379) with { LoggerFactory = logger };
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", 6379,
            options: options.ToConnectionOptions(enableMaintenanceNotifications: true));
        await using var router = new ClusterRouter(options, primary);
        var sourceEndpoint = new RespireEndpoint("source", 7000);
        var targetEndpoint = new RespireEndpoint("target", 7001);
        var source = router.GetMultiplexer(sourceEndpoint);
        router.SetSlotOwner(0, source);
        router.SetSlotOwner(1, source);

        router.ApplySmigratedNotification(router.CaptureSmigratedNotification(source, new object(),
            new("SMIGRATED", 1, Migrations:
            [
                new(sourceEndpoint, targetEndpoint, "0-"),
                new(sourceEndpoint, targetEndpoint, "1"),
            ])));

        await Assert.That(ReferenceEquals(router.GetKnownSlotOwner(0), source)).IsTrue();
        await Assert.That(router.GetKnownSlotOwner(1)?.Port).IsEqualTo(targetEndpoint.Port);
        await Assert.That(logger.DebugCount).IsEqualTo(1);
    }

    [Test]
    public async Task ReceiveTimeTokenFencesRouteChangesMadeBeforeTheCallbackRuns()
    {
        var options = Options(6379);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", 6379,
            options: options.ToConnectionOptions(enableMaintenanceNotifications: true));
        await using var router = new ClusterRouter(options, primary);
        var sourceEndpoint = new RespireEndpoint("source", 7000);
        var targetEndpoint = new RespireEndpoint("target", 7001);
        var otherEndpoint = new RespireEndpoint("other", 7002);
        var source = router.GetMultiplexer(sourceEndpoint);
        var other = router.GetMultiplexer(otherEndpoint);
        router.SetSlotOwner(0, source);
        router.SetSlotOwner(1, source);

        // The receive loop stamped the push, then a redirect moved slot 0 away and back before
        // the maintenance callback ran.
        var receivedAt = ClusterSlotMutationClock.Next();
        router.SetSlotOwner(0, other);
        router.SetSlotOwner(0, source);
        var topologyChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        router.TopologyChanged += (_, _, _) => topologyChanged.TrySetResult();
        var connection = new object();
        source.PublishMaintenanceNotification(connection, new("SMIGRATED", 1, Migrations:
            [new(sourceEndpoint, targetEndpoint, "0")]), receivedAt);
        source.PublishMaintenanceNotification(connection, new("SMIGRATED", 2, Migrations:
            [new(sourceEndpoint, targetEndpoint, "1")]));
        await topologyChanged.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(ReferenceEquals(router.GetKnownSlotOwner(0), source)).IsTrue();
        await Assert.That(router.GetKnownSlotOwner(1)?.Port).IsEqualTo(targetEndpoint.Port);
    }

    [Test]
    public async Task SmigratedSlotListsBoundEnumeratedRanges()
    {
        var options = Options(6379);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", 6379,
            options: options.ToConnectionOptions(enableMaintenanceNotifications: true));
        await using var router = new ClusterRouter(options, primary);
        var sourceEndpoint = new RespireEndpoint("source", 7000);
        var targetEndpoint = new RespireEndpoint("target", 7001);
        var source = router.GetMultiplexer(sourceEndpoint);
        var target = router.GetMultiplexer(targetEndpoint);
        router.SetSlotOwner(0, source);
        router.SetSlotOwner(1, source);
        router.SetSlotOwner(16383, source);

        // Repeated full ranges exceed the slot count in enumeration and are rejected outright.
        var repeated = string.Join(',', Enumerable.Repeat("0-16383", 2));
        router.ApplySmigratedNotification(router.CaptureSmigratedNotification(source, new object(),
            new("SMIGRATED", 1, Migrations: [new(sourceEndpoint, targetEndpoint, repeated)])));
        await Assert.That(ReferenceEquals(router.GetKnownSlotOwner(0), source)).IsTrue();

        // Small overlaps stay within the bound and move each slot once.
        router.ApplySmigratedNotification(router.CaptureSmigratedNotification(source, new object(),
            new("SMIGRATED", 2, Migrations: [new(sourceEndpoint, targetEndpoint, "0-1,1,0-1")])));
        await Assert.That(ReferenceEquals(router.GetKnownSlotOwner(0), target)).IsTrue();
        await Assert.That(ReferenceEquals(router.GetKnownSlotOwner(1), target)).IsTrue();
        await Assert.That(ReferenceEquals(router.GetKnownSlotOwner(16383), source)).IsTrue();
    }

    [Test]
    public async Task SmigratedSequenceIdsAreScopedToTheReceivingConnection()
    {
        var options = Options(6379);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", 6379,
            options: options.ToConnectionOptions(enableMaintenanceNotifications: true));
        await using var router = new ClusterRouter(options, primary);
        var sourceEndpoint = new RespireEndpoint("source", 7000);
        var targetEndpoint = new RespireEndpoint("target", 7001);
        var source = router.GetMultiplexer(sourceEndpoint);
        var target = router.GetMultiplexer(targetEndpoint);
        router.SetSlotOwner(0, source);
        router.SetSlotOwner(1, source);

        router.ApplySmigratedNotification(router.CaptureSmigratedNotification(source, new object(),
            new("SMIGRATED", 1, Migrations: [new(sourceEndpoint, targetEndpoint, "0")])));
        // A reconnected connection restarts its sequence IDs.
        router.ApplySmigratedNotification(router.CaptureSmigratedNotification(source, new object(),
            new("SMIGRATED", 1, Migrations: [new(sourceEndpoint, targetEndpoint, "1")])));

        await Assert.That(ReferenceEquals(router.GetKnownSlotOwner(0), target)).IsTrue();
        await Assert.That(ReferenceEquals(router.GetKnownSlotOwner(1), target)).IsTrue();
    }

    [Test]
    public async Task QueuedSmigratedNotificationsApplyInArrivalOrder()
    {
        var options = Options(6379);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", 6379,
            options: options.ToConnectionOptions(enableMaintenanceNotifications: true));
        await using var router = new ClusterRouter(options, primary);
        var aEndpoint = new RespireEndpoint("a", 7000);
        var bEndpoint = new RespireEndpoint("b", 7001);
        var cEndpoint = new RespireEndpoint("c", 7002);
        var a = router.GetMultiplexer(aEndpoint);
        var b = router.GetMultiplexer(bEndpoint);
        router.GetMultiplexer(cEndpoint);
        router.SetSlotOwner(0, a);
        router.SetSlotOwner(1, b);
        var connection = new object();

        // Both are captured before the worker applies either one.
        var first = router.CaptureSmigratedNotification(a, connection,
            new("SMIGRATED", 1, Migrations: [new(aEndpoint, bEndpoint, "0")]));
        var second = router.CaptureSmigratedNotification(a, connection,
            new("SMIGRATED", 2, Migrations: [new(bEndpoint, cEndpoint, "0")]));
        router.ApplySmigratedNotification(first);
        router.ApplySmigratedNotification(second);

        var owner = router.GetKnownSlotOwner(0);
        await Assert.That(owner?.Host).IsEqualTo(cEndpoint.Host);
        await Assert.That(owner?.Port).IsEqualTo(cEndpoint.Port);
    }

    [Test]
    public async Task OvertakenSmigratedCallbackCannotOverwriteLaterOwnerChanges()
    {
        var options = Options(6379);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", 6379,
            options: options.ToConnectionOptions(enableMaintenanceNotifications: true));
        await using var router = new ClusterRouter(options, primary);
        var aEndpoint = new RespireEndpoint("a", 7000);
        var bEndpoint = new RespireEndpoint("b", 7001);
        var cEndpoint = new RespireEndpoint("c", 7002);
        var a = router.GetMultiplexer(aEndpoint);
        router.GetMultiplexer(bEndpoint);
        router.GetMultiplexer(cEndpoint);
        router.SetSlotOwner(0, a);
        var connection = new object();

        // Capture the old callback before later callbacks, then apply those later callbacks first.
        var overtaken = router.CaptureSmigratedNotification(a, connection,
            new("SMIGRATED", 1, Migrations: [new(aEndpoint, bEndpoint, "0")]));
        var laterOwner = router.CaptureSmigratedNotification(a, connection,
            new("SMIGRATED", 2, Migrations: [new(aEndpoint, cEndpoint, "0")]));
        var laterReturn = router.CaptureSmigratedNotification(a, connection,
            new("SMIGRATED", 3, Migrations: [new(cEndpoint, aEndpoint, "0")]));

        router.ApplySmigratedNotification(laterOwner);
        router.ApplySmigratedNotification(laterReturn);
        router.ApplySmigratedNotification(overtaken);

        var owner = router.GetKnownSlotOwner(0);
        await Assert.That(owner?.Host).IsEqualTo(aEndpoint.Host);
        await Assert.That(owner?.Port).IsEqualTo(aEndpoint.Port);
    }

    [Test]
    public async Task FencedSmigratedNotificationDoesNotObserveItsTarget()
    {
        var options = Options(6379);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", 6379,
            options: options.ToConnectionOptions(enableMaintenanceNotifications: true));
        await using var router = new ClusterRouter(options, primary);
        var sourceEndpoint = new RespireEndpoint("source", 7000);
        var otherEndpoint = new RespireEndpoint("other", 7001);
        var targetEndpoint = new RespireEndpoint("unused-target", 7002);
        var source = router.GetMultiplexer(sourceEndpoint);
        var other = router.GetMultiplexer(otherEndpoint);
        router.SetSlotOwner(0, other);
        router.SetSlotOwner(1, source);

        router.ApplySmigratedNotification(router.CaptureSmigratedNotification(source, new object(),
            new("SMIGRATED", 1, Migrations: [new(sourceEndpoint, targetEndpoint, "0")])));

        await Assert.That(ReferenceEquals(router.GetKnownSlotOwner(0), other)).IsTrue();
        await Assert.That(router.GetActiveEndpoints().Any(endpoint => endpoint.Port == targetEndpoint.Port)).IsFalse();
    }

    [Test]
    public async Task QueuedSmigratedNotificationCannotOverwriteAnAbaSlotChange()
    {
        var options = Options(6379);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", 6379,
            options: options.ToConnectionOptions(enableMaintenanceNotifications: true));
        await using var router = new ClusterRouter(options, primary);
        var sourceEndpoint = new RespireEndpoint("source", 7000);
        var targetEndpoint = new RespireEndpoint("target", 7001);
        var intermediateEndpoint = new RespireEndpoint("intermediate", 7002);
        var source = router.GetMultiplexer(sourceEndpoint);
        var intermediate = router.GetMultiplexer(intermediateEndpoint);
        router.SetSlotOwner(0, source);
        var queued = router.CaptureSmigratedNotification(source, new object(),
            new("SMIGRATED", 7, Migrations: [new(sourceEndpoint, targetEndpoint, "0")]));

        router.SetSlotOwner(0, intermediate);
        router.SetSlotOwner(0, source);
        router.ApplySmigratedNotification(queued);

        await Assert.That(ReferenceEquals(router.GetKnownSlotOwner(0), source)).IsTrue();
    }

    [Test]
    [NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OwnerPublicationWaitsForItsMutationStamp(bool clear)
    {
        var options = Options(6379);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", 6379,
            options: options.ToConnectionOptions(enableMaintenanceNotifications: true));
        await using var router = new ClusterRouter(options, primary);
        var source = router.GetMultiplexer(new RespireEndpoint("source", 7000));
        var target = router.GetMultiplexer(new RespireEndpoint("target", 7001));
        router.SetSlotOwner(0, source);
        router.SetSlotOwner(1, source);
        router.SetSlotOwner(2, target); // Observe both nodes before blocking the mutation clock.
        var clockGate = typeof(ClusterSlotMutationClock).GetField("s_gate",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                if (clear) router.ClearSlotOwner(0, source);
                else router.SetSlotOwner(0, target);
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        bool blocked;
        RespireConnectionMultiplexer? observed;
        lock (clockGate)
        {
            worker.Start();
            blocked = SpinWait.SpinUntil(() => (worker.ThreadState & ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(5));
            observed = router.GetKnownSlotOwner(0);
        }
        var finished = worker.Join(TimeSpan.FromSeconds(5));

        await Assert.That(blocked).IsTrue();
        await Assert.That(finished).IsTrue();
        await Assert.That(failure).IsNull();
        await Assert.That(ReferenceEquals(observed, source)).IsTrue();
        await Assert.That(ReferenceEquals(router.GetKnownSlotOwner(0), clear ? null : target)).IsTrue();
    }

    [Test]
    public async Task SameOwnerRedirectDoesNotFenceQueuedSmigratedNotification()
    {
        var options = Options(6379);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", 6379,
            options: options.ToConnectionOptions(enableMaintenanceNotifications: true));
        await using var router = new ClusterRouter(options, primary);
        var sourceEndpoint = new RespireEndpoint("source", 7000);
        var targetEndpoint = new RespireEndpoint("target", 7001);
        var source = router.GetMultiplexer(sourceEndpoint);
        var target = router.GetMultiplexer(targetEndpoint);
        router.SetSlotOwner(0, source);
        var queued = router.CaptureSmigratedNotification(source, new object(),
            new("SMIGRATED", 8, Migrations: [new(sourceEndpoint, targetEndpoint, "0")]));

        router.SetSlotOwner(0, source);
        router.ApplySmigratedNotification(queued);

        await Assert.That(ReferenceEquals(router.GetKnownSlotOwner(0), target)).IsTrue();
    }

    [Test]
    public async Task SameOwnerRedirectFencesOlderDiscovery()
    {
        var options = Options(6379);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", 6379,
            options: options.ToConnectionOptions(enableMaintenanceNotifications: true));
        await using var router = new ClusterRouter(options, primary);
        var ownerEndpoint = new RespireEndpoint("owner", 7000);
        var staleEndpoint = new RespireEndpoint("stale", 7001);
        var owner = router.GetMultiplexer(ownerEndpoint);
        router.SetSlotOwner(0, owner);
        var capturedVersion = router.TopologyVersion;

        router.SetSlotOwner(0, owner);
        List<ClusterTopologyRange> stale = [new(0, 0, staleEndpoint, "stale", [])];
        router.ApplyTopology(stale, capturedVersion, 1L);

        await Assert.That(ReferenceEquals(router.GetKnownSlotOwner(0), owner)).IsTrue();
    }

    [Test]
    public async Task QueuedSmigratedNotificationCannotOverwriteDiscoveryAbaChange()
    {
        var options = Options(6379);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", 6379,
            options: options.ToConnectionOptions(enableMaintenanceNotifications: true));
        await using var router = new ClusterRouter(options, primary);
        var sourceEndpoint = new RespireEndpoint("source", 7000);
        var intermediateEndpoint = new RespireEndpoint("intermediate", 7001);
        var targetEndpoint = new RespireEndpoint("target", 7002);
        var source = router.GetMultiplexer(sourceEndpoint);
        router.SetSlotOwner(0, source);
        router.SetSlotOwner(1, source);
        var queued = router.CaptureSmigratedNotification(source, new object(),
            new("SMIGRATED", 9, Migrations: [new(sourceEndpoint, targetEndpoint, "0")]));
        List<ClusterTopologyRange> intermediateSnapshot =
        [
            new(0, 0, intermediateEndpoint, "intermediate", []),
            new(1, 1, sourceEndpoint, "source", []),
        ];
        router.ApplyTopology(intermediateSnapshot, router.TopologyVersion, 1L);
        List<ClusterTopologyRange> sourceSnapshot =
        [
            new(0, 0, sourceEndpoint, "source", []),
            new(1, 1, sourceEndpoint, "source", []),
        ];
        router.ApplyTopology(sourceSnapshot, router.TopologyVersion, 2L);

        router.ApplySmigratedNotification(queued);

        await Assert.That(ReferenceEquals(router.GetKnownSlotOwner(0), source)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CacheMetricsRunOutsideMembershipAndHealthGates(bool retirement)
    {
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = { new RespireEndpoint("localhost") }, UseCluster = true, ClientSideCache = new(),
        });
        var core = client.Core;
        var router = core.Cluster!;
        var original = core.Multiplexer;
        router.SetSlotOwner(0, original);
        var healthGate = typeof(ClientCore).GetField("_stateGate",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(core)!;
        RespireKey key = "cached";
        var token = core.ClientCache!.BeginRead(in key);
        var value = RespValue.BulkString("value"u8.ToArray());
        core.ClientCache.CompleteRead(in token, in value, allowInsert: true);
        var callbackThread = Environment.CurrentManagedThreadId;
        var measurements = 0;
        var gateHeld = false;
        // Initialize the static instruments before subscribing: publication during their
        // type initializer runs before each readonly field has received its reference.
        var evictionInstrument = RespireTelemetry.ClientCacheEvictions;
        var continuityInstrument = RespireTelemetry.ClientCacheContinuityFlushes;
        using var listener = new System.Diagnostics.Metrics.MeterListener
        {
            InstrumentPublished = (instrument, current) =>
            {
                if (ReferenceEquals(instrument, evictionInstrument)
                    || ReferenceEquals(instrument, continuityInstrument))
                {
                    current.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) =>
        {
            if (Environment.CurrentManagedThreadId != callbackThread) return;
            measurements++;
            gateHeld |= Monitor.IsEntered(healthGate) || Monitor.IsEntered(router.NodeStateGate);
        });
        listener.Start();
        if (retirement)
        {
            router.SetSlotOwner(0, router.GetMultiplexer(new RespireEndpoint("replacement")));
        }
        else
        {
            core.NotifyCommandStateChanged(original, 0, RespireConnectionState.Reconnecting);
        }
        listener.Dispose();
        await Assert.That(measurements).IsEqualTo(2);
        await Assert.That(gateHeld).IsFalse();
    }

    [Test]
    public async Task StaleSnapshotCannotReplaceCurrentIdentityOrSeed()
    {
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = { new RespireEndpoint("localhost") }, UseCluster = true,
        });
        var router = client.Core.Cluster!;
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var apply = typeof(ClusterRouter).GetMethod("ApplyTopology", flags)!;
        var version = typeof(ClusterRouter).GetField("_topologyVersion", flags)!;
        typeof(ClusterRouter).GetField("_seed", flags)!.SetValue(router, client.Core.Multiplexer);
        var endpoint = new RespireEndpoint("localhost");
        List<ClusterTopologyRange> original = [new(0, 16383, endpoint, "old-id", [])];
        List<ClusterTopologyRange> replacement = [new(0, 16383, endpoint, "new-id", [])];
        apply.Invoke(router, [original, 0L, 1L]);
        var capturedVersion = (long)version.GetValue(router)!;
        // Two discoveries begin together. The replacement reply publishes before the old one.
        apply.Invoke(router, [replacement, capturedVersion, 3L]);
        var current = router.GetMultiplexer(endpoint);
        apply.Invoke(router, [original, capturedVersion, 2L]);
        var slots = (RespireConnectionMultiplexer?[])typeof(ClusterRouter).GetField("_slots", flags)!.GetValue(router)!;
        await Assert.That(ReferenceEquals(slots[0], current)).IsTrue();
        await Assert.That(ReferenceEquals(router.GetMultiplexer(endpoint), current)).IsTrue();
        await Assert.That(ReferenceEquals(typeof(ClusterRouter).GetField("_seed", flags)!.GetValue(router), current)).IsTrue();
    }

    [Test]
    public async Task ReplicaPromotionCreatesPrimaryTransportAndDropsReplicaIdentity()
    {
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = { new RespireEndpoint("localhost") }, UseCluster = true,
        });
        var router = client.Core.Cluster!;
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var apply = typeof(ClusterRouter).GetMethod("ApplyTopology", flags)!;
        var primaryEndpoint = new RespireEndpoint("primary.local", 6379);
        var replicaEndpoint = new RespireEndpoint("replica.local", 6380);
        List<ClusterTopologyRange> replicaTopology =
        [
            new(0, 16383, primaryEndpoint, "primary-id", [])
            {
                Replicas = [new ClusterTopologyReplica(replicaEndpoint, "replica-id", [])],
            },
        ];

        apply.Invoke(router, [replicaTopology, 0L, 1L]);
        var replicaRoutes = (ClusterReplicaSet?[])typeof(ClusterRouter)
            .GetField("_replicasBySlot", flags)!.GetValue(router)!;
        var replica = replicaRoutes[0]!.Nodes[0];
        await Assert.That(replica.Options.ReadOnly).IsTrue();

        List<ClusterTopologyRange> promotedTopology = [new(0, 16383, replicaEndpoint, "replica-id", [])];
        apply.Invoke(router, [promotedTopology, 0L, 2L]);
        var primary = router.GetMultiplexer(replicaEndpoint);
        var replicas = ((ClusterNodeIdentityIndex)typeof(ClusterRouter).GetField("_identities", flags)!
            .GetValue(router)!).Replicas;

        await Assert.That(primary.Options.ReadOnly).IsFalse();
        await Assert.That(ReferenceEquals(primary, replica)).IsFalse();
        await Assert.That(replicas.TryGetById("replica-id", out _)).IsFalse();
        // The promoted node's read-only transport leaves the replica map with the refresh.
        await Assert.That(replicas.ContainsEndpoint(replicaEndpoint)).IsFalse();
        await Assert.That(replicas.IsCurrent(replica)).IsFalse();
        await Assert.That(replicaRoutes[0]!.Nodes).IsEmpty();
    }

    [Test]
    public async Task EndpointThatIsPrimaryAndReplicaKeepsSeparateReadOnlyTransport()
    {
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = { new RespireEndpoint("localhost") }, UseCluster = true,
        });
        var router = client.Core.Cluster!;
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var apply = typeof(ClusterRouter).GetMethod("ApplyTopology", flags)!;
        var first = new RespireEndpoint("first.local", 6379);
        var second = new RespireEndpoint("second.local", 6379);
        // Each node is a primary for one range and a replica for the other.
        List<ClusterTopologyRange> topology =
        [
            new(0, 8191, first, "first-id", []) { Replicas = [new ClusterTopologyReplica(second, "second-id", [])] },
            new(8192, 16383, second, "second-id", []) { Replicas = [new ClusterTopologyReplica(first, "first-id", [])] },
        ];

        apply.Invoke(router, [topology, 0L, 1L]);
        var replicaRoutes = (ClusterReplicaSet?[])typeof(ClusterRouter)
            .GetField("_replicasBySlot", flags)!.GetValue(router)!;
        var secondAsReplica = replicaRoutes[0]!.Nodes[0];
        var secondAsPrimary = router.GetMultiplexer(second);

        // READONLY is connection state, so the replica role needs its own transport.
        await Assert.That(ReferenceEquals(secondAsReplica, secondAsPrimary)).IsFalse();
        await Assert.That(secondAsReplica.Options.ReadOnly).IsTrue();
        await Assert.That(secondAsPrimary.Options.ReadOnly).IsFalse();
        await Assert.That(ReferenceEquals(replicaRoutes[0], replicaRoutes[8191])).IsTrue();
        await Assert.That(ReferenceEquals(replicaRoutes[0], replicaRoutes[8192])).IsFalse();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task StaleDiscoveryPreservesRedirectEndpointMappings(bool advertisedAsPreferred, bool knownIdentity)
    {
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = { new RespireEndpoint("localhost") }, UseCluster = true,
        });
        var router = client.Core.Cluster!;
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var apply = typeof(ClusterRouter).GetMethod("ApplyTopology", flags)!;
        var originalEndpoint = new RespireEndpoint("localhost");
        var redirectedEndpoint = new RespireEndpoint("redirected");
        List<ClusterTopologyRange> initial = knownIdentity
            ? [new(0, 8191, originalEndpoint, "original-id", []), new(8192, 16383, redirectedEndpoint, "redirect-id", [])]
            : [new(0, 16383, originalEndpoint, "original-id", [])];
        apply.Invoke(router, [initial, 0L, 1L]);
        // Discovery begins, then MOVED installs a new physical endpoint before its reply arrives.
        var capturedVersion = (long)typeof(ClusterRouter).GetField("_topologyVersion", flags)!.GetValue(router)!;
        var redirected = router.GetMultiplexer(redirectedEndpoint);
        router.SetSlotOwner(0, redirected);
        var staleId = knownIdentity ? "redirect-id" : "original-id";
        List<ClusterTopologyRange> stale = advertisedAsPreferred
            ? [new(0, 16383, redirectedEndpoint, staleId, [])]
            : [new(0, 16383, originalEndpoint, staleId, [redirectedEndpoint])];
        apply.Invoke(router, [stale, capturedVersion, 2L]);
        var slots = (RespireConnectionMultiplexer?[])typeof(ClusterRouter).GetField("_slots", flags)!.GetValue(router)!;
        await Assert.That(ReferenceEquals(slots[0], redirected)).IsTrue();
        await Assert.That(ReferenceEquals(router.GetMultiplexer(redirectedEndpoint), redirected)).IsTrue();
        // The stale node ID must not be assigned to the redirected transport either.
        var identities = (ClusterNodeIdentityIndex)typeof(ClusterRouter).GetField("_identities", flags)!.GetValue(router)!;
        var ids = (Dictionary<RespireConnectionMultiplexer, string>)typeof(ClusterNodeIdentityIndex)
            .GetField("_nodeIds", flags)!.GetValue(identities)!;
        if (knownIdentity)
        {
            await Assert.That(ids[redirected]).IsEqualTo("redirect-id");
            var owners = (Dictionary<string, RespireConnectionMultiplexer>)typeof(ClusterNodeIdentityIndex)
                .GetField("_nodesById", flags)!.GetValue(identities)!;
            await Assert.That(ReferenceEquals(owners["redirect-id"], redirected)).IsTrue();
        }
        else
        {
            await Assert.That(ids.ContainsKey(redirected)).IsFalse();
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task StaleDiscoveryPreservesAskEndpointWithoutChangingSlotOwner(
        bool advertisedAsPreferred, bool dedicated)
    {
        await using var sourceServer = new FakeRespServer();
        await using var target = new FakeRespServer();
        await using var source = await RespireConnection.ConnectAsync("127.0.0.1", sourceServer.Port);
        await using var client = RespireClient.Create(Options(sourceServer.Port));
        var router = client.Core.Cluster!;
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var apply = typeof(ClusterRouter).GetMethod("ApplyTopology", flags)!;
        var version = typeof(ClusterRouter).GetField("_topologyVersion", flags)!;
        var originalEndpoint = new RespireEndpoint("127.0.0.1", sourceServer.Port);
        var redirectedEndpoint = new RespireEndpoint("127.0.0.1", target.Port);
        List<ClusterTopologyRange> initial = [new(0, 16383, originalEndpoint, "original-id", [])];
        apply.Invoke(router, [initial, 0L, 1L]);
        var capturedVersion = (long)version.GetValue(router)!;
        var error = new RespireServerException($"ASK 0 127.0.0.1:{target.Port}");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        if (dedicated)
        {
            await router.GetRedirectDedicatedPoolAsync(error, source, timeout.Token, commandSlot: null, discovery: null);
        }
        else
        {
            await router.GetRedirectConnectionAsync(error, source, timeout.Token, commandSlot: null, discovery: null);
        }
        var redirected = router.GetMultiplexer(redirectedEndpoint);
        var slots = (RespireConnectionMultiplexer?[])typeof(ClusterRouter).GetField("_slots", flags)!.GetValue(router)!;
        await Assert.That(ReferenceEquals(slots[0], client.Core.Multiplexer)).IsTrue();
        List<ClusterTopologyRange> stale = advertisedAsPreferred
            ? [new(0, 16383, redirectedEndpoint, "original-id", [])]
            : [new(0, 16383, originalEndpoint, "original-id", [redirectedEndpoint])];
        apply.Invoke(router, [stale, capturedVersion, 2L]);
        await Assert.That(ReferenceEquals(router.GetMultiplexer(redirectedEndpoint), redirected)).IsTrue();
        var next = await router.GetRedirectConnectionAsync(error, source, timeout.Token, commandSlot: null, discovery: null);
        await Assert.That(ReferenceEquals(next.Multiplexer, redirected)).IsTrue();

        // A discovery started after ASK is authoritative and may replace that endpoint's identity.
        List<ClusterTopologyRange> replacement = [new(0, 16383, redirectedEndpoint, "replacement-id", [])];
        var freshVersion = (long)version.GetValue(router)!;
        apply.Invoke(router, [replacement, freshVersion, 3L]);
        // First fresh discovery identifies the previously anonymous ASK transport.
        replacement = [new(0, 16383, redirectedEndpoint, "another-id", [])];
        apply.Invoke(router, [replacement, freshVersion, 4L]);
        await Assert.That(ReferenceEquals(router.GetMultiplexer(redirectedEndpoint), redirected)).IsFalse();
    }

    [Test]
    public async Task DiscardedSnapshotNodeDoesNotRetireOrFlushRepopulatedCache()
    {
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = { new RespireEndpoint("localhost") }, UseCluster = true, ClientSideCache = new(),
        });
        var router = client.Core.Cluster!;
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var apply = typeof(ClusterRouter).GetMethod("ApplyTopology", flags)!;
        router.SetSlotOwner(0, client.Core.Multiplexer);
        var capturedVersion = (long)typeof(ClusterRouter).GetField("_topologyVersion", flags)!.GetValue(router)!;
        var moved = router.GetMultiplexer(new RespireEndpoint("moved"));
        router.SetSlotOwner(0, moved);
        var retirements = 0;
        router.NodeRetired += _ => retirements++;
        var cache = client.Core.ClientCache!;
        RespireKey key = "cached";
        var token = cache.BeginRead(in key);
        var value = RespValue.BulkString("value"u8.ToArray());
        cache.CompleteRead(in token, in value, allowInsert: true);
        var flushes = cache.GetStatistics().ContinuityFlushes;
        List<ClusterTopologyRange> stale = [new(0, 0, new RespireEndpoint("staged"), "staged-id", [])];
        apply.Invoke(router, [stale, capturedVersion, 1L]);
        await Assert.That(retirements).IsEqualTo(0);
        await Assert.That(cache.Count).IsEqualTo(1);
        await Assert.That(cache.GetStatistics().ContinuityFlushes).IsEqualTo(flushes);
    }

    [Test]
    [Arguments(false, false, false)]
    [Arguments(false, true, false)]
    [Arguments(true, false, false)]
    [Arguments(true, true, false)]
    [Arguments(false, false, true)]
    [Arguments(false, true, true)]
    [Arguments(true, false, true)]
    [Arguments(true, true, true)]
    public async Task LaterDiscoveryWinsRegardlessOfCompletionOrder(
        bool movedBetweenRequests, bool laterCompletesFirst, bool movedAfterLaterRequest)
    {
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = { new RespireEndpoint("localhost") }, UseCluster = true,
        });
        var router = client.Core.Cluster!;
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var apply = typeof(ClusterRouter).GetMethod("ApplyTopology", flags)!;
        var version = typeof(ClusterRouter).GetField("_topologyVersion", flags)!;
        typeof(ClusterRouter).GetField("_seed", flags)!.SetValue(router, client.Core.Multiplexer);
        var endpoint = new RespireEndpoint("localhost");
        List<ClusterTopologyRange> initial = [new(0, 16383, endpoint, "initial", [])];
        List<ClusterTopologyRange> older = [new(0, 16383, endpoint, "older", [])];
        List<ClusterTopologyRange> later = [new(0, 16383, endpoint, "later", [])];
        apply.Invoke(router, [initial, 0L, 1L]);
        var olderVersion = (long)version.GetValue(router)!;
        if (movedBetweenRequests)
        {
            router.SetSlotOwner(0, router.GetMultiplexer(new RespireEndpoint("moved")));
        }
        var laterVersion = (long)version.GetValue(router)!;
        var newestRoute = router.GetMultiplexer(new RespireEndpoint("newest-route"));
        if (movedAfterLaterRequest)
        {
            router.SetSlotOwner(0, newestRoute);
        }
        if (laterCompletesFirst)
        {
            apply.Invoke(router, [later, laterVersion, 3L]);
            apply.Invoke(router, [older, olderVersion, 2L]);
        }
        else
        {
            apply.Invoke(router, [older, olderVersion, 2L]);
            apply.Invoke(router, [later, laterVersion, 3L]);
        }
        var current = router.GetMultiplexer(endpoint);
        var slots = (RespireConnectionMultiplexer?[])typeof(ClusterRouter).GetField("_slots", flags)!.GetValue(router)!;
        var identities = (ClusterNodeIdentityIndex)typeof(ClusterRouter).GetField("_identities", flags)!.GetValue(router)!;
        var ids = (Dictionary<string, RespireConnectionMultiplexer>)typeof(ClusterNodeIdentityIndex)
            .GetField("_nodesById", flags)!.GetValue(identities)!;
        await Assert.That(ids.ContainsKey("later")).IsTrue();
        await Assert.That(ReferenceEquals(ids["later"], current)).IsTrue();
        // A MOVED before the later request can be superseded; one after it remains protected.
        await Assert.That(ReferenceEquals(slots[0], movedAfterLaterRequest ? newestRoute : current)).IsTrue();
        await Assert.That(ReferenceEquals(slots[1], current)).IsTrue();
        await Assert.That(ReferenceEquals(typeof(ClusterRouter).GetField("_seed", flags)!.GetValue(router), current)).IsTrue();
    }

    [Test]
    [Arguments(false, RespireConnectionState.Reconnecting)]
    [Arguments(false, RespireConnectionState.Disconnected)]
    [Arguments(true, RespireConnectionState.Connected)]
    public async Task CacheFlushWaitsForMembershipAndHealthCriticalSection(bool retirement, RespireConnectionState state)
    {
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = { new RespireEndpoint("localhost") }, UseCluster = true, ClientSideCache = new(),
        });
        var core = client.Core;
        var router = core.Cluster!;
        var original = core.Multiplexer;
        var replacement = router.GetMultiplexer(new RespireEndpoint("replacement"));
        router.SetSlotOwner(0, original);
        if (retirement) router.SetSlotOwner(0, replacement);
        var cache = core.ClientCache!;
        CacheValue();
        var gate = typeof(ClientCore).GetField("_stateGate",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(core)!;
        using var started = new ManualResetEventSlim();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callback = new Thread(() =>
        {
            started.Set();
            try
            {
                if (retirement) core.NotifyCommandNodeRetired(original);
                else core.NotifyCommandStateChanged(original, 0, state);
                finished.SetResult();
            }
            catch (Exception exception) { finished.SetException(exception); }
        }) { IsBackground = true };
        int countWhileBlocked;
        lock (gate)
        {
            callback.Start();
            if (!started.Wait(TimeSpan.FromSeconds(5)) || !SpinWait.SpinUntil(
                    () => (callback.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0,
                    TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("The callback did not reach the held health gate.");
            }
            countWhileBlocked = cache.Count;
            // Flip membership and repopulate before the delayed callback can enter the gate.
            router.SetSlotOwner(0, retirement ? original : replacement);
            CacheValue();
        }
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(countWhileBlocked).IsEqualTo(1);
        await Assert.That(cache.Count).IsEqualTo(1);

        void CacheValue()
        {
            RespireKey key = "cached";
            var token = cache.BeginRead(in key);
            var value = RespValue.BulkString("value"u8.ToArray());
            cache.CompleteRead(in token, in value, allowInsert: true);
        }
    }

    [Test]
    [Arguments(RespireConnectionState.Reconnecting)]
    [Arguments(RespireConnectionState.Disconnected)]
    public async Task StaleHealthCallbacksPreserveRepopulatedCache(RespireConnectionState state)
    {
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = { new RespireEndpoint("localhost") }, UseCluster = true, ClientSideCache = new(),
        });
        var core = client.Core;
        var router = core.Cluster!;
        var original = core.Multiplexer;
        var replacement = router.GetMultiplexer(new RespireEndpoint("replacement"));
        router.SetSlotOwner(0, original);
        router.SetSlotOwner(0, replacement);
        var cache = core.ClientCache!;
        RespireKey key = "cached";
        var token = cache.BeginRead(in key);
        var value = RespValue.BulkString("value"u8.ToArray());
        cache.CompleteRead(in token, in value, allowInsert: true);
        var flushes = cache.GetStatistics().ContinuityFlushes;

        core.NotifyCommandStateChanged(original, 0, state);
        core.NotifyCommandNodeRetired(replacement);

        await Assert.That(cache.Count).IsEqualTo(1);
        await Assert.That(cache.GetStatistics().ContinuityFlushes).IsEqualTo(flushes);
        // A genuine active-transport failure must still invalidate tracked data.
        core.NotifyCommandStateChanged(replacement, 0, state);
        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(cache.GetStatistics().ContinuityFlushes).IsEqualTo(flushes + 1);
    }

    [Test]
    public async Task CorrectionPoolsShareTransportAndPeerAcrossConnections()
    {
        await using var server = new FakeRespServer(3, FakeRespServer.OkReply);
        var options = Options(server.Port);
        await using var primary = await RespireConnectionMultiplexer.CreateAsync(
            "127.0.0.1", server.Port, connectionCount: 2, options: options.ToConnectionOptions());
        await using var replacement = await RespireConnectionMultiplexer.CreateAsync(
            "127.0.0.1", server.Port, options: options.ToConnectionOptions());
        await using var router = new ClusterRouter(options, primary);
        var first = primary.GetConnection(0);
        var second = primary.GetConnection(1);
        await Assert.That(ReferenceEquals(first, second)).IsFalse();

        await using var firstLease = router.GetCorrectionLease(first);
        await using var secondLease = router.GetCorrectionLease(second);
        await using var replacementLease = router.GetCorrectionLease(replacement.GetConnection());
        await Assert.That(ReferenceEquals(firstLease.Pool, secondLease.Pool)).IsTrue();
        await Assert.That(ReferenceEquals(firstLease.Pool, replacementLease.Pool)).IsFalse();
    }

    [Test]
    public async Task CorrectionPoolsKeepDifferentNetworkPeersSeparate()
    {
        await using var oldPeer = new FakeRespServer();
        await using var newPeer = new FakeRespServer();
        var options = Options(oldPeer.Port);
        await using var primary = RespireConnectionMultiplexer.Create("redis.example", options: options.ToConnectionOptions());
        await using var router = new ClusterRouter(options, primary);
        await using var first = await RespireConnection.ConnectAsync("127.0.0.1", oldPeer.Port);
        await using var second = await RespireConnection.ConnectAsync("127.0.0.1", newPeer.Port);
        // Model successive physical connections resolving the same transport to different peers.
        first.Multiplexer = primary;
        second.Multiplexer = primary;

        await using var firstLease = router.GetCorrectionLease(first);
        await using var secondLease = router.GetCorrectionLease(second);
        await Assert.That(ReferenceEquals(firstLease.Pool, secondLease.Pool)).IsFalse();
    }

    [Test]
    public async Task TrackedCorrectionsRetainTransportAfterEndpointReassignment()
    {
        await using var target = new FakeRespServer(4,
            ":42\r\n"u8.ToArray(), ":0\r\n"u8.ToArray(),
            "$5\r\nvalue\r\n"u8.ToArray(), ":1\r\n"u8.ToArray());
        await using var seed = new FakeRespServer(
            Topology("localhost", target.Port, "old-id"), Topology("localhost", target.Port, "new-id"));
        await using var client = await RespireClient.ConnectAsync(Options(seed.Port));
        var script = RespireScript.Create("return redis.call('GET', KEYS[1])");
        var execution = await client.StartTrackedScriptExecutionAsync(script, ["key"], [], CancellationToken.None);
        using var result = await execution.Response;
        var original = execution.Connection;
        var router = client.Core.Cluster!;
        await router.GetMasterConnectionsAsync(CancellationToken.None, discovery: null);
        var replacement = await router.GetConnectionAsync(ClusterHash.GetSlot("key"), CancellationToken.None, discovery: null);
        await Assert.That(ReferenceEquals(original, replacement)).IsFalse();

        await client.ExecuteOnAllConnectionsAsync(script, ["key"], [], execution.ConnectionIdentity);

        var correctionIndex = target.ReceivedCommands.ToList().FindLastIndex(command => command.StartsWith("EVAL "));
        await Assert.That(target.ReceivedConnectionIds[correctionIndex]).IsNotEqualTo(0);
        await client.FenceCorrectionConnectionAsync(execution.ConnectionIdentity);
        await Assert.That(original.IsConnected).IsFalse();
        await Assert.That(replacement.IsConnected).IsTrue();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task AnnouncedAliasesShareMultiplexerAndDedicatedPool(bool includeNodeId)
    {
        await using var target = new FakeRespServer(FakeRespServer.PongReply);
        await using var seed = new FakeRespServer(Topology("localhost", target.Port, includeNodeId ? "node-1" : null));
        var options = Options(seed.Port);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", seed.Port, options: options.ToConnectionOptions());
        await using var router = new ClusterRouter(options, primary);
        await router.EnsureConnectedAsync(default, discovery: null);

        var hostname = new RespireEndpoint("localhost", target.Port);
        var address = new RespireEndpoint("127.0.0.1", target.Port);
        await Assert.That(ReferenceEquals(router.GetMultiplexer(hostname), router.GetMultiplexer(address))).IsTrue();
        await Assert.That(ReferenceEquals(router.GetMultiplexer(hostname),
            router.GetMultiplexer(new RespireEndpoint("LOCALHOST", target.Port)))).IsTrue();
        await Assert.That(ReferenceEquals(router.GetDedicatedPool(hostname), router.GetDedicatedPool(address))).IsTrue();
        await Assert.That(router.GetMultiplexer(address).Host).IsEqualTo("localhost");
    }

    [Test]
    public async Task ConfiguredSeedAliasRetainsExistingMultiplexer()
    {
        var commandReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var seed = new FakeRespServer
        {
            SuppressReply = _ => { commandReceived.TrySetResult(); return true; },
        };
        var options = Options(seed.Port);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", seed.Port, options: options.ToConnectionOptions());
        await using var router = new ClusterRouter(options, primary);
        var connect = router.EnsureConnectedAsync(default, discovery: null).AsTask();
        await commandReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await seed.SendRawAsync(Topology("localhost", seed.Port, "seed-node"));
        await connect;

        await Assert.That(ReferenceEquals(primary,
            router.GetMultiplexer(new RespireEndpoint("localhost", seed.Port)))).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SupersededSeedUsesReplacementForSlotlessCommands(bool movedHost)
    {
        var firstDiscovery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var discoveries = 0;
        // The same listener represents either a new node at the old address or a node whose
        // newly advertised hostname replaces its old address without an alias relationship.
        await using var seed = new FakeRespServer(2, FakeRespServer.PongReply)
        {
            SuppressReply = _ =>
            {
                if (Interlocked.Increment(ref discoveries) == 1)
                {
                    firstDiscovery.TrySetResult();
                }
                return true;
            },
        };
        var options = Options(seed.Port);
        await using var primary = RespireConnectionMultiplexer.Create(
            "127.0.0.1", seed.Port, options: options.ToConnectionOptions());
        await using var router = new ClusterRouter(options, primary);
        var connect = router.EnsureConnectedAsync(default, discovery: null).AsTask();
        await firstDiscovery.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var initial = Encoding.UTF8.GetBytes("*1\r\n" + Range(
            0, 16383, "127.0.0.1", seed.Port, "original", "", includeAliases: false));
        await seed.SendRawAsync(initial);
        await connect;

        var preferred = movedHost ? "localhost" : "127.0.0.1";
        var replacement = Encoding.UTF8.GetBytes("*1\r\n" + Range(
            0, 16383, preferred, seed.Port, movedHost ? "original" : "replacement", "", includeAliases: false));
        var refreshReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        seed.SuppressReply = _ => { refreshReceived.TrySetResult(); return true; };
        var refresh = router.GetMasterConnectionsAsync(default, discovery: null).AsTask();
        await refreshReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await seed.SendRawAsync(replacement);
        await refresh;

        var current = router.GetMultiplexer(new RespireEndpoint(preferred, seed.Port));
        await Assert.That(ReferenceEquals(current, primary)).IsFalse();
        await router.WaitForRetirementAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(primary.IsConnected).IsEqualTo(movedHost);
        var slotless = await router.GetConnectionAsync(null, default, discovery: null);
        await Assert.That(ReferenceEquals(slotless, current.GetConnection())).IsTrue();
        await Assert.That(router.SeedEndpoint.Host).IsEqualTo(preferred);
    }

    [Test]
    public async Task MissingNodeIdPrefersExistingPreferredTransportOverAlias()
    {
        await using var target = new FakeRespServer(FakeRespServer.PongReply);
        await using var seed = new FakeRespServer(Topology("127.0.0.1", target.Port, null));
        var options = Options(seed.Port);
        await using var primary = RespireConnectionMultiplexer.Create(
            "127.0.0.1", seed.Port, options: options.ToConnectionOptions());
        await using var router = new ClusterRouter(options, primary);
        var preferred = router.GetMultiplexer(new RespireEndpoint("127.0.0.1", target.Port));
        var alias = router.GetMultiplexer(new RespireEndpoint("localhost", target.Port));
        await router.EnsureConnectedAsync(default, discovery: null);

        await Assert.That(ReferenceEquals(preferred, alias)).IsFalse();
        await Assert.That(ReferenceEquals(preferred,
            router.GetMultiplexer(new RespireEndpoint("localhost", target.Port)))).IsTrue();
        await Assert.That(ReferenceEquals(preferred,
            router.GetMultiplexer(new RespireEndpoint("127.0.0.1", target.Port)))).IsTrue();
    }

    [Test]
    [Arguments(RespireConnectionState.Reconnecting)]
    [Arguments(RespireConnectionState.Disconnected)]
    public async Task DelayedRetirementDoesNotClearReactivatedNodeHealth(RespireConnectionState state)
    {
        await using var oldServer = new FakeRespServer();
        await using var newServer = new FakeRespServer();
        var slot = ClusterHash.GetSlot("key");
        await using var seed = new FakeRespServer(Encoding.UTF8.GetBytes("*1\r\n" + Range(
            slot, slot, "127.0.0.1", oldServer.Port, "original", "", includeAliases: false)));
        await using var client = await RespireClient.ConnectAsync(Options(seed.Port));
        var core = client.Core;
        var router = core.Cluster!;
        var original = router.GetMultiplexer(new RespireEndpoint("127.0.0.1", oldServer.Port));
        var replacement = router.GetMultiplexer(new RespireEndpoint("127.0.0.1", newServer.Port));
        router.SetSlotOwner(slot, replacement);
        router.SetSlotOwner(slot, original);
        var states = new List<RespireConnectionState>();
        core.ConnectionStateChanged += change => states.Add(change.State);
        core.NotifyCommandStateChanged(original, 0, state);

        // Model an earlier retirement callback delivered after the node was reactivated.
        core.NotifyCommandNodeRetired(original);

        await Assert.That(states).IsEquivalentTo([state]);
    }

    [Test]
    [Arguments(RespireConnectionState.Reconnecting)]
    [Arguments(RespireConnectionState.Disconnected)]
    public async Task RetiredNodeLateHealthNotificationIsIgnored(RespireConnectionState state)
    {
        await using var oldServer = new FakeRespServer();
        await using var newServer = new FakeRespServer();
        var slot = ClusterHash.GetSlot("key");
        await using var seed = new FakeRespServer(Encoding.UTF8.GetBytes("*1\r\n" + Range(
            slot, slot, "127.0.0.1", oldServer.Port, "original", "", includeAliases: false)));
        await using var client = await RespireClient.ConnectAsync(Options(seed.Port));
        var core = client.Core;
        var router = core.Cluster!;
        var original = router.GetMultiplexer(new RespireEndpoint("127.0.0.1", oldServer.Port));
        var replacement = router.GetMultiplexer(new RespireEndpoint("127.0.0.1", newServer.Port));
        router.SetSlotOwner(slot, replacement);
        var states = new List<RespireConnectionState>();
        core.ConnectionStateChanged += change => states.Add(change.State);

        // A transport can already have captured its event delegate before unsubscription.
        core.NotifyCommandStateChanged(original, 0, state);

        await Assert.That(states).IsEmpty();
    }

    [Test]
    public async Task DifferentNodeIdsDoNotMergeThroughConflictingMetadata()
    {
        await using var first = new FakeRespServer();
        var firstRange = Range(0, 8191, "127.0.0.1", first.Port, "first", "shared.invalid");
        var secondRange = Range(8192, 16383, "localhost", first.Port, "second", "shared.invalid");
        await using var seed = new FakeRespServer(Encoding.UTF8.GetBytes("*2\r\n" + firstRange + secondRange));
        var options = Options(seed.Port);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", seed.Port, options: options.ToConnectionOptions());
        await using var router = new ClusterRouter(options, primary);
        await router.EnsureConnectedAsync(default, discovery: null);

        await Assert.That(ReferenceEquals(
            router.GetMultiplexer(new RespireEndpoint("127.0.0.1", first.Port)),
            router.GetMultiplexer(new RespireEndpoint("localhost", first.Port)))).IsFalse();
    }

    [Test]
    public async Task NodeIdLinksPreferredFormsWithoutMetadataAliases()
    {
        await using var target = new FakeRespServer();
        var ranges = Range(0, 8191, "localhost", target.Port, "same-node", "", includeAliases: false)
            + Range(8192, 16383, "127.0.0.1", target.Port, "same-node", "", includeAliases: false);
        await using var seed = new FakeRespServer(Encoding.UTF8.GetBytes("*2\r\n" + ranges));
        var options = Options(seed.Port);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", seed.Port, options: options.ToConnectionOptions());
        await using var router = new ClusterRouter(options, primary);
        await router.EnsureConnectedAsync(default, discovery: null);

        await Assert.That(ReferenceEquals(
            router.GetMultiplexer(new RespireEndpoint("localhost", target.Port)),
            router.GetMultiplexer(new RespireEndpoint("127.0.0.1", target.Port)))).IsTrue();
    }

    [Test]
    public async Task EndpointReassignedToDifferentNodeIdGetsNewTransport()
    {
        await using var target = new FakeRespServer(FakeRespServer.PongReply);
        await using var seed = new FakeRespServer(
            Topology("localhost", target.Port, "old-node"),
            Topology("localhost", target.Port, "new-node"));
        var options = Options(seed.Port);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", seed.Port, options: options.ToConnectionOptions());
        await using var router = new ClusterRouter(options, primary);
        await router.EnsureConnectedAsync(default, discovery: null);
        var endpoint = new RespireEndpoint("localhost", target.Port);
        var old = router.GetMultiplexer(endpoint);
        await router.GetMasterConnectionsAsync(default, discovery: null);

        await Assert.That(ReferenceEquals(old, router.GetMultiplexer(endpoint))).IsFalse();
        await Assert.That(ReferenceEquals(router.GetMultiplexer(endpoint),
            router.GetMultiplexer(new RespireEndpoint("127.0.0.1", target.Port)))).IsTrue();
    }

    [Test]
    public async Task ChangedPortForKnownNodeIdCreatesTransportAtNewPort()
    {
        await using var original = new FakeRespServer(FakeRespServer.PongReply);
        await using var replacement = new FakeRespServer(FakeRespServer.PongReply);
        await using var seed = new FakeRespServer(
            Topology("localhost", original.Port, "same-node"),
            Topology("localhost", replacement.Port, "same-node"));
        var options = Options(seed.Port);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", seed.Port, options: options.ToConnectionOptions());
        await using var router = new ClusterRouter(options, primary);
        await router.EnsureConnectedAsync(default, discovery: null);
        var connections = await router.GetMasterConnectionsAsync(default, discovery: null);

        await Assert.That(connections).Count().IsEqualTo(1);
        await Assert.That(connections[0].Port).IsEqualTo(replacement.Port);
    }

    [Test]
    public async Task StableNodeIdMovingHostUsesNewTransport()
    {
        await using var target = new FakeRespServer(FakeRespServer.PongReply);
        var original = Encoding.UTF8.GetBytes("*1\r\n" + Range(0, 16383, "127.0.0.2", target.Port, "same-node", "", includeAliases: false));
        var replacement = Encoding.UTF8.GetBytes("*1\r\n" + Range(0, 16383, "127.0.0.1", target.Port, "same-node", "", includeAliases: false));
        await using var seed = new FakeRespServer(original, replacement);
        var options = Options(seed.Port);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", seed.Port, options: options.ToConnectionOptions());
        await using var router = new ClusterRouter(options, primary);
        await router.EnsureConnectedAsync(default, discovery: null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var connections = await router.GetMasterConnectionsAsync(timeout.Token, discovery: null);

        await Assert.That(connections[0].Host).IsEqualTo("127.0.0.1");
    }

    [Test]
    public async Task ReassignedAliasResolvesToItsNewNodeId()
    {
        await using var target = new FakeRespServer(FakeRespServer.PongReply);
        var original = Encoding.UTF8.GetBytes("*1\r\n" + Range(0, 16383, "old.invalid", target.Port, "old-node", "old.invalid"));
        await using var seed = new FakeRespServer(original, Topology("localhost", target.Port, "new-node"));
        var options = Options(seed.Port);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", seed.Port, options: options.ToConnectionOptions());
        await using var router = new ClusterRouter(options, primary);
        await router.EnsureConnectedAsync(default, discovery: null);
        var address = new RespireEndpoint("127.0.0.1", target.Port);
        var old = router.GetMultiplexer(address);
        await router.GetMasterConnectionsAsync(default, discovery: null);

        await Assert.That(ReferenceEquals(old, router.GetMultiplexer(address))).IsFalse();
        await Assert.That(ReferenceEquals(router.GetMultiplexer(address),
            router.GetMultiplexer(new RespireEndpoint("localhost", target.Port)))).IsTrue();
        await Assert.That(ReferenceEquals(router.GetDedicatedPool(address),
            router.GetDedicatedPool(new RespireEndpoint("localhost", target.Port)))).IsTrue();
    }

    internal static byte[] Topology(string preferred, int port, string? nodeId)
        => Encoding.UTF8.GetBytes("*1\r\n" + Range(0, 16383, preferred, port, nodeId, "localhost"));

    private static string Range(
        int start, int end, string preferred, int port, string? nodeId, string hostname, bool includeAliases = true)
        => $"*3\r\n:{start}\r\n:{end}\r\n*{(includeAliases ? 4 : 3)}\r\n{Bulk(preferred)}:{port}\r\n{(nodeId is null ? "$-1\r\n" : Bulk(nodeId))}"
            + (includeAliases ? $"%2\r\n+hostname\r\n{Bulk(hostname)}+ip\r\n{Bulk("127.0.0.1")}" : "");

    private static string Bulk(string value) => $"${Encoding.UTF8.GetByteCount(value)}\r\n{value}\r\n";

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(5, timeout.Token);
    }

    private static RespireOptions Options(int seedPort) => new()
    {
        Protocol = RespProtocol.Resp2,
        UseCluster = true,
        Endpoints = { new RespireEndpoint("127.0.0.1", seedPort) },
        Connections = 1,
    };

    private sealed class ThrowingErrorLogger : ILoggerFactory, ILogger
    {
        private int _errorCount;
        internal int ErrorCount => Volatile.Read(ref _errorCount);
        public ILogger CreateLogger(string categoryName) => this;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel < LogLevel.Error) return;
            Interlocked.Increment(ref _errorCount);
            throw new InvalidOperationException("logger failure");
        }
    }

    private sealed class ThrowingDebugLogger : ILoggerFactory, ILogger
    {
        private int _debugCount;
        internal int DebugCount => Volatile.Read(ref _debugCount);
        public ILogger CreateLogger(string categoryName) => this;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Debug;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Debug) return;
            Interlocked.Increment(ref _debugCount);
            throw new InvalidOperationException("debug logger failure");
        }
    }

    private sealed class WarningCaptureLogger : ILoggerFactory, ILogger
    {
        private int _warningCount;
        internal int WarningCount => Volatile.Read(ref _warningCount);
        internal TaskCompletionSource WarningReported { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string LastWarning { get; private set; } = "";
        public ILogger CreateLogger(string categoryName) => this;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel < LogLevel.Warning) return;
            LastWarning = formatter(state, exception);
            Interlocked.Increment(ref _warningCount);
            WarningReported.TrySetResult();
        }
    }

    private readonly struct BlockedWriteCommand(
        TaskCompletionSource enteredWrite,
        TaskCompletionSource releaseWrite) : IRespCommand
    {
        public ReadCommandKind ReadKind => ReadCommandKind.None;

        public void Write(ref RespWriter writer)
        {
            enteredWrite.TrySetResult();
            releaseWrite.Task.GetAwaiter().GetResult();
            writer.WriteRaw("*2\r\n$4\r\nECHO\r\n$4\r\nlate\r\n"u8);
        }
    }
}

