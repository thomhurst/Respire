using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Respire.Protocol;

namespace Respire.Tests.Networking;

public class MaintenanceNotificationTests
{
    [Test]
    public async Task AutoModeFallsBackToResp2WithoutMaintenanceCommand()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            Protocol = RespProtocol.Resp2,
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Auto,
        });

        await client.PingAsync();

        await Assert.That(server.ReceivedCommands).Contains("PING");
        await Assert.That(server.ReceivedCommands).DoesNotContain("CLIENT MAINT_NOTIFICATIONS ON");
    }

    [Test]
    public async Task EnabledModeRejectsResp2()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        await Assert.That(async () => await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            Protocol = RespProtocol.Resp2,
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Enabled,
        })).ThrowsExactly<RespireConfigurationException>();
    }

    [Test]
    public async Task AutoModeContinuesWhenResp3ServerRejectsCapability()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        server.ReplyOverride = (_, command) => command switch
        {
            "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
            "CLIENT MAINT_NOTIFICATIONS ON" => "-ERR unknown command 'CLIENT'\r\n"u8.ToArray(),
            _ => null,
        };

        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            Protocol = RespProtocol.Resp3,
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Auto,
        });

        await client.PingAsync();
        await Assert.That(server.ReceivedCommands).Contains("PING");
    }

    [Test]
    public async Task EnabledModeRejectsUnsupportedResp3Capability()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        server.ReplyOverride = (_, command) => command switch
        {
            "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
            "CLIENT MAINT_NOTIFICATIONS ON" => "-ERR unsupported\r\n"u8.ToArray(),
            _ => null,
        };

        await Assert.That(async () => await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            Protocol = RespProtocol.Resp3,
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Enabled,
        })).Throws<RespireConnectionException>();
    }

    [Test]
    public async Task EnabledModeNegotiatesAndDeliversPushOffReceiveLoop()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        server.ReplyOverride = (_, command) => command switch
        {
            "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
            "CLIENT MAINT_NOTIFICATIONS ON" => FakeRespServer.OkReply,
            _ => null,
        };

        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            Protocol = RespProtocol.Resp3,
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Enabled,
        });

        var received = new TaskCompletionSource<RespireMaintenanceNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        var nestedPingCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.MaintenanceNotificationReceived += notification =>
        {
            try
            {
                var ping = client.PingAsync().AsTask();
                nestedPingCompleted.TrySetResult(ping.Wait(TimeSpan.FromSeconds(3)));
                received.TrySetResult(notification);
            }
            catch (Exception error)
            {
                received.TrySetException(error);
            }
        };

        await server.SendRawAsync(
            ">4\r\n$9\r\nMIGRATING\r\n:17\r\n$1\r\n0\r\n*1\r\n$7\r\nshard-1\r\n"u8.ToArray());
        var actual = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(nestedPingCompleted.Task.Result).IsTrue();
        await Assert.That(actual.Type).IsEqualTo(RespireMaintenanceNotificationType.Migrating);
        await Assert.That(actual.SequenceId).IsEqualTo(17);
        await Assert.That(actual.Details).IsEquivalentTo(["0", "shard-1"]);
        await Assert.That(server.ReceivedCommands).Contains("CLIENT MAINT_NOTIFICATIONS ON");
    }

    [Test]
    public async Task MaintenancePushExtendsTimeoutsForInflightCommand()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        server.ReplyOverride = (_, command) => command switch
        {
            "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
            "CLIENT MAINT_NOTIFICATIONS ON" => FakeRespServer.OkReply,
            _ => null,
        };
        server.DelayReply(2, 250);

        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            Protocol = RespProtocol.Resp3,
            CommandTimeout = TimeSpan.FromMilliseconds(100),
            ConnectionIdleReadTimeout = TimeSpan.FromMilliseconds(100),
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Enabled,
            MaintenanceTimeoutExtension = TimeSpan.FromMilliseconds(500),
        });

        var ping = client.PingAsync().AsTask();
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!server.ReceivedCommands.Contains("PING"))
            await Task.Delay(1, wait.Token);

        await server.SendRawAsync(
            ">4\r\n$9\r\nMIGRATING\r\n:21\r\n$1\r\n0\r\n*1\r\n$7\r\nshard-1\r\n"u8.ToArray());
        await ping.WaitAsync(wait.Token);

        await Assert.That(ping.IsCompletedSuccessfully).IsTrue();
    }

    [Test]
    public async Task MaintenanceTimeoutExtensionExpiresAfterConfiguredBound()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        server.ReplyOverride = (_, command) => command switch
        {
            "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
            "CLIENT MAINT_NOTIFICATIONS ON" => FakeRespServer.OkReply,
            _ => null,
        };
        server.DelayReply(2, 250);
        server.DelayReply(3, 250);

        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            Protocol = RespProtocol.Resp3,
            CommandTimeout = TimeSpan.FromMilliseconds(100),
            ConnectionIdleReadTimeout = TimeSpan.FromMilliseconds(100),
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Enabled,
            MaintenanceTimeoutExtension = TimeSpan.FromMilliseconds(500),
        });

        var extendedPing = client.PingAsync().AsTask();
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!server.ReceivedCommands.Contains("PING"))
            await Task.Delay(1, wait.Token);
        await server.SendRawAsync(
            ">4\r\n$9\r\nMIGRATING\r\n:22\r\n$1\r\n0\r\n*1\r\n$7\r\nshard-1\r\n"u8.ToArray());
        await extendedPing.WaitAsync(wait.Token);

        await Task.Delay(600, wait.Token);
        await Assert.That(async () => await client.PingAsync()).Throws<RespireTimeoutException>();
    }

    [Test]
    public async Task CallerCancellationStillCancelsDuringMaintenance()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        server.ReplyOverride = (_, command) => command switch
        {
            "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
            "CLIENT MAINT_NOTIFICATIONS ON" => FakeRespServer.OkReply,
            _ => null,
        };
        server.SuppressReply = command => command == "PING";

        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            Protocol = RespProtocol.Resp3,
            CommandTimeout = TimeSpan.FromSeconds(5),
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Enabled,
            MaintenanceTimeoutExtension = TimeSpan.FromSeconds(5),
        });

        await server.SendRawAsync(
            ">4\r\n$9\r\nMIGRATING\r\n:23\r\n$1\r\n0\r\n*1\r\n$7\r\nshard-1\r\n"u8.ToArray());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.That(async () => await client.PingAsync(cancellation.Token)).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task TryCreate_CopiesEveryValidNotificationShape()
    {
        var frames = new[]
        {
            (RespireMaintenanceNotificationType.Moving, RespValue.Push([RespValue.BulkString("MOVING"), RespValue.Integer(1), RespValue.BulkString("10"), RespValue.BulkString("redis.example:6379")])),
            (RespireMaintenanceNotificationType.Migrating, RespValue.Push([RespValue.BulkString("MIGRATING"), RespValue.Integer(2), RespValue.BulkString("3"), RespValue.Array([RespValue.BulkString("shard-1"), RespValue.BulkString("shard-2")])])),
            (RespireMaintenanceNotificationType.Migrated, RespValue.Push([RespValue.BulkString("MIGRATED"), RespValue.Integer(2), RespValue.Array([RespValue.BulkString("shard-1")])])),
            (RespireMaintenanceNotificationType.FailingOver, RespValue.Push([RespValue.BulkString("FAILING_OVER"), RespValue.Integer(3), RespValue.BulkString("1"), RespValue.Array([RespValue.BulkString("shard-1")])])),
            (RespireMaintenanceNotificationType.FailedOver, RespValue.Push([RespValue.BulkString("FAILED_OVER"), RespValue.Integer(3), RespValue.Array([RespValue.BulkString("shard-1")])])),
            (RespireMaintenanceNotificationType.Smigrating, RespValue.Push([RespValue.BulkString("SMIGRATING"), RespValue.Integer(4), RespValue.BulkString("0-10"), RespValue.BulkString("12")])),
            (RespireMaintenanceNotificationType.Smigrated, RespValue.Push([RespValue.BulkString("SMIGRATED"), RespValue.Integer(4), RespValue.BulkString("source.example:6379"), RespValue.BulkString("target.example:6379"), RespValue.BulkString("0-10"), RespValue.BulkString("12")])),
        };

        foreach (var (expectedType, frame) in frames)
        {
            using (frame)
            {
                await Assert.That(RespireMaintenanceNotification.TryCreate(in frame, out var notification)).IsTrue();
                await Assert.That(notification!.Type).IsEqualTo(expectedType);
                await Assert.That(notification.SequenceId).IsGreaterThan(0);
                await Assert.That(notification.Details).IsNotEmpty();
            }
        }
    }

    [Test]
    public async Task TryCreate_RejectsMalformedAndUnknownFrames()
    {
        var frames = new[]
        {
            RespValue.Push([RespValue.BulkString("UNKNOWN"), RespValue.Integer(1), RespValue.BulkString("x")]),
            RespValue.Push([RespValue.BulkString("MOVING"), RespValue.Integer(-1), RespValue.BulkString("10"), RespValue.BulkString("host:6379")]),
            RespValue.Push([RespValue.BulkString("MOVING"), RespValue.Integer(1), RespValue.BulkString("soon"), RespValue.BulkString("host:6379")]),
            RespValue.Push([RespValue.BulkString("SMIGRATING"), RespValue.Integer(2), RespValue.BulkString("-1-20")]),
            RespValue.Push([RespValue.BulkString("SMIGRATED"), RespValue.Integer(3), RespValue.BulkString("source:6379"), RespValue.BulkString("target:6379")]),
            RespValue.Array([RespValue.BulkString("MIGRATED"), RespValue.Integer(4), RespValue.BulkString("shard")]),
        };

        foreach (var frame in frames)
        {
            using (frame)
            {
                await Assert.That(RespireMaintenanceNotification.TryCreate(in frame, out var notification)).IsFalse();
                await Assert.That(notification).IsNull();
            }
        }
    }
}
