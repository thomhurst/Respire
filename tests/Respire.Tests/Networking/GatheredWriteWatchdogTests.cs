using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public sealed class GatheredWriteWatchdogTests
{
    [Test]
    [Arguments(null)]
    [Arguments(120000)]
    public async Task IdleWriteSleepPreservesReceiveBudget(int? receiveMilliseconds)
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port, new()
        {
            ResponseTimeout = receiveMilliseconds is { } receive ? TimeSpan.FromMilliseconds(receive) : null,
            GatheredWriteStallTimeout = TimeSpan.FromMilliseconds(1),
        });
        var idleDelay = TimeSpan.FromMinutes(2);
        await Assert.That(connection.GetGatheredWriteWatchdogDelay(idleDelay)).IsEqualTo(idleDelay);
    }

    [Test]
    [Arguments(null)]
    [Arguments(120000)]
    public async Task ActiveWriteSleepUsesRemainingAllowance(int? receiveMilliseconds)
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port, new()
        {
            ResponseTimeout = receiveMilliseconds is { } receive ? TimeSpan.FromMilliseconds(receive) : null,
            GatheredWriteStallTimeout = TimeSpan.FromSeconds(60),
        });
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var progressField = typeof(RespireConnection).GetField("_flushProgress", flags)!;
        var progress = progressField.GetValue(connection)!;
        var timestampField = progress.GetType().GetField("GatheredWriteDeadlineTimestamp", flags)!;
        timestampField.SetValue(progress, Stopwatch.GetTimestamp() - 59 * Stopwatch.Frequency);
        progressField.SetValue(connection, progress);
        try
        {
            var delay = connection.GetGatheredWriteWatchdogDelay(TimeSpan.FromMinutes(2));
            await Assert.That(delay <= TimeSpan.FromSeconds(1)).IsTrue();
            await Assert.That(delay >= TimeSpan.Zero).IsTrue();
            await Assert.That(connection.GetGatheredWriteWatchdogDelay(TimeSpan.Zero)).IsEqualTo(TimeSpan.Zero);

            timestampField.SetValue(progress, Stopwatch.GetTimestamp() - 61 * Stopwatch.Frequency);
            progressField.SetValue(connection, progress);
            await Assert.That(connection.GetGatheredWriteWatchdogDelay(TimeSpan.FromMinutes(2))).IsEqualTo(TimeSpan.Zero);
        }
        finally
        {
            timestampField.SetValue(progress, 0L);
            progressField.SetValue(connection, progress);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [ParallelLimiter<TimingSensitive>]
    public async Task IndependentWriteBoundRelaxesAndRestoresDuringMaintenance(bool completeWindow)
    {
        var reads = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "HELLO 3" ? "%1\r\n+proto\r\n:3\r\n"u8.ToArray() : null,
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespireProtocol.Resp3, Connections = 1, ThreadPoolMonitoring = false,
            Endpoints = [new("127.0.0.1", server.Port)],
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Enabled,
            ConnectionIdleReadTimeout = null, CommandTimeout = null,
            GatheredWriteStallTimeout = TimeSpan.FromMilliseconds(500),
            MaintenanceRelaxedTimeout = TimeSpan.FromSeconds(3),
            MaintenanceWindowTimeout = TimeSpan.FromSeconds(10),
        });
        var connection = client.Core.Multiplexer.GetConnection();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await server.SendRawAsync(">3\r\n+FAILING_OVER\r\n:1\r\n:1\r\n"u8.ToArray());
        while (!connection.HasMaintenanceWindow) await Task.Delay(1, guard.Token);
        await Assert.That(connection.HasRelaxedTimeoutAllowance).IsTrue();
        server.ReadGate = reads.Task;
        var socket = (Socket)typeof(RespireConnection).GetField("_socket", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(connection)!;
        socket.SendBufferSize = 0;
        var payload = new byte[5 * 1024 * 1024];
        var pending = client.SetAsync("maintenance-stall", (RespireValue)payload, cancellationToken: guard.Token).AsTask();
        try
        {
            while (connection.CaptureTimeoutDiagnostics().InflightBytes == 0 || socket.Poll(0, SelectMode.SelectWrite))
            {
                if (pending.IsCompleted) await pending;
                await Task.Delay(1, guard.Token);
            }
            // Survive more than the normal write allowance with receive checks disabled.
            await Task.Delay(1000, guard.Token);
            await Assert.That(connection.IsConnected).IsTrue();
            await Assert.That(pending.IsCompleted).IsFalse();
            await Assert.That(connection.CaptureTimeoutDiagnostics().PendingWriteBytes > 0).IsTrue();
            if (completeWindow)
                await server.SendRawAsync(">2\r\n+FAILED_OVER\r\n:1\r\n"u8.ToArray());
            var error = await Assert.That(async () => await pending.WaitAsync(guard.Token))
                .ThrowsExactly<RespireConnectionException>();
            await Assert.That(error!.Message).Contains(completeWindow ? "00:00:00.5000000" : "00:00:03");
            await Assert.That(connection.IsConnected).IsFalse();
            // Public completion releases all kernel-send references before caller reuse.
            payload.AsSpan().Fill((byte)'b');
        }
        finally
        {
            await client.DisposeAsync();
            reads.TrySetResult();
            try { await pending; } catch (Exception) { }
        }
    }
}
