using System.Text;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public partial class ErrorMetricsTests
{
    [Test]
    [MatrixDataSource]
    public async Task ClientHandlePreflightHasOneFinalOwner(
        [Matrix("info-admin", "evict", "touch", "pause-admin", "unpause", "unblock-admin",
            "info-null", "info-attribute", "pause-negative", "pause-fraction", "pause-mode", "unblock-id", "unblock-mode")] string failure,
        [Matrix(false, true)] bool enabled)
    {
        using var configuration = new MetricConfigurationScope(new()
            { Groups = enabled ? RespireMetricGroups.Resiliency : RespireMetricGroups.None });
        await using var server = new FakeRespServer(4, ":123\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1,
            AllowAdmin = failure is not ("info-admin" or "evict" or "touch" or "pause-admin" or "unpause" or "unblock-admin"),
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        var handle = await client.Server.GetClientConnectionAsync();
        var commandsBefore = server.CommandsSeen;
        using var capture = new Capture(throwOnMeasurement: true);
        var error = await Assert.That(async () =>
        {
            switch (failure)
            {
                case "info-admin": await handle.SetInfoAsync(RespireClientInfoAttribute.LibraryName, "name"); break;
                case "evict": await handle.SetNoEvictAsync(true); break;
                case "touch": await handle.SetNoTouchAsync(true); break;
                case "pause-admin": await handle.PauseClientsAsync(TimeSpan.Zero); break;
                case "unpause": await handle.UnpauseClientsAsync(); break;
                case "unblock-admin": await handle.UnblockClientAsync(123); break;
                case "info-null": await handle.SetInfoAsync(RespireClientInfoAttribute.LibraryName, null!); break;
                case "info-attribute": await handle.SetInfoAsync((RespireClientInfoAttribute)99, "name"); break;
                case "pause-negative": await handle.PauseClientsAsync(TimeSpan.FromMilliseconds(-1)); break;
                case "pause-fraction": await handle.PauseClientsAsync(TimeSpan.FromTicks(1)); break;
                case "pause-mode": await handle.PauseClientsAsync(TimeSpan.Zero, (RespireClientPauseMode)99); break;
                case "unblock-id": await handle.UnblockClientAsync(0); break;
                default: await handle.UnblockClientAsync(123, (RespireClientUnblockMode)99); break;
            }
        }).Throws<Exception>();
        await Assert.That(server.CommandsSeen).IsEqualTo(commandsBefore);
        await Assert.That(capture.Items.Count).IsEqualTo(enabled ? 1 : 0);
        if (enabled)
        {
            var final = capture.Items.Single();
            await Assert.That((bool)final.Tags["redis.client.errors.internal"]!).IsFalse();
            await Assert.That(final.Tags["error.type"]).IsEqualTo(error!.GetType().FullName);
            await Assert.That(final.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        }
    }

    [Test]
    [MatrixDataSource]
    public async Task ClientHandleConversionKeepsTransferredOwner(
        [Matrix("info", "evict", "touch", "pause", "unpause", "unblock")] string operation,
        [Matrix("success", "server", "shape")] string response)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer(4, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "CLIENT ID" ? ":123\r\n"u8.ToArray()
                : response == "server" ? "-NOPERM denied\r\n"u8.ToArray()
                : response == "shape" ? "$3\r\nbad\r\n"u8.ToArray()
                : operation == "unblock" ? ":1\r\n"u8.ToArray() : null,
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, AllowAdmin = true,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        var handle = await client.Server.GetClientConnectionAsync();
        using var capture = new Capture(throwOnMeasurement: true);
        async Task Execute()
        {
            switch (operation)
            {
                case "info": await handle.SetInfoAsync(RespireClientInfoAttribute.LibraryName, "name"); break;
                case "evict": await handle.SetNoEvictAsync(true); break;
                case "touch": await handle.SetNoTouchAsync(true); break;
                case "pause": await handle.PauseClientsAsync(TimeSpan.Zero); break;
                case "unpause": await handle.UnpauseClientsAsync(); break;
                default: await Assert.That(await handle.UnblockClientAsync(123)).IsTrue(); break;
            }
        }
        if (response == "success") await Execute();
        else
        {
            var error = await Assert.That(Execute).Throws<Exception>();
            var final = capture.Items.Single();
            await Assert.That((bool)final.Tags["redis.client.errors.internal"]!).IsFalse();
            await Assert.That(final.Tags["error.type"]).IsEqualTo(error!.GetType().FullName);
            await Assert.That(final.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        }
        await Assert.That(capture.Items.Count).IsEqualTo(response == "success" ? 0 : 1);
    }

    [Test]
    [Arguments("admin")]
    [Arguments("id")]
    [Arguments("mode")]
    public async Task UnblockPreflightPreservesAsynchronousFailure(string failure)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer(4, ":123\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, AllowAdmin = failure != "admin",
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        var handle = await client.Server.GetClientConnectionAsync();
        using var capture = new Capture(throwOnMeasurement: true);
        // This call must return a faulted ValueTask rather than throw during invocation.
        var pending = handle.UnblockClientAsync(failure == "id" ? 0 : 123,
            failure == "mode" ? (RespireClientUnblockMode)99 : RespireClientUnblockMode.Timeout);
        var error = await Assert.That(async () => await pending).Throws<Exception>();
        await Assert.That(error!.GetType()).IsEqualTo(failure == "admin"
            ? typeof(NotSupportedException) : typeof(ArgumentOutOfRangeException));
        await Assert.That(capture.Items.Count).IsEqualTo(1);
    }

    [Test]
    [MatrixDataSource]
    public async Task BatchFinalErrorFollowsCompletedCacheFence(
        [Matrix(false, true)] bool throwingExecute, [Matrix(false, true)] bool cluster)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "HELLO 3" ? "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray()
                : command.StartsWith("SET ", StringComparison.Ordinal) ? "-NOPERM denied\r\n"u8.ToArray() : null,
        };
        var replyOverride = server.ReplyOverride!;
        server.ReplyOverride = (connectionId, command) => command == "CLUSTER SLOTS"
            ? NotificationTopology(server.Port) : replyOverride(connectionId, command);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp3, Connections = 1, ClientSideCache = new(),
            UseCluster = cluster, ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        var mutationsAtFinal = -1;
        using var capture = new Capture(throwOnMeasurement: true, onMeasurement: item =>
        {
            if (!(bool)item.Tags["redis.client.errors.internal"]!)
                mutationsAtFinal = client.Core.ClientCache!.InspectForTests().ActiveMutationCount;
        });
        using var batch = client.CreateBatch();
        var pending = batch.Strings.Set("key", "value");
        if (throwingExecute) await Assert.That(async () => await batch.ExecuteAsync()).Throws<RespireServerException>();
        else await Assert.That((await batch.TryExecuteAsync()).Failures.Count).IsEqualTo(1);
        await Assert.That(pending.Error).IsTypeOf<RespireServerException>();
        await Assert.That(mutationsAtFinal).IsEqualTo(0);
        await Assert.That(capture.Items.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    public async Task BackgroundNotificationRejectionHasIndependentHandledOwner(int attempts)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var first = new FakeRespServer(20, FakeRespServer.OkReply);
        await using var second = new FakeRespServer(20, FakeRespServer.OkReply);
        var topology = NotificationTopology(first.Port);
        var descriptor = RespireChannel.KeyEvent(RespireKeyNotificationType.Set, 0);
        foreach (var server in new[] { first, second })
            server.ReplyOverride = (_, command) => command switch
            {
                "CLUSTER SLOTS" => Volatile.Read(ref topology),
                _ when command.StartsWith("SUBSCRIBE ", StringComparison.Ordinal) => server == second
                    ? "-NOPERM denied\r\n"u8.ToArray()
                    : Encoding.ASCII.GetBytes($"*3\r\n$9\r\nsubscribe\r\n${descriptor.ToString().Length}\r\n{descriptor}\r\n:1\r\n"),
                _ when command.StartsWith("UNSUBSCRIBE ", StringComparison.Ordinal) => Encoding.ASCII.GetBytes(
                    $"*3\r\n$11\r\nunsubscribe\r\n${descriptor.ToString().Length}\r\n{descriptor}\r\n:0\r\n"),
                _ => null,
            };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, UseCluster = true, ClusterTopologyRefreshInterval = null,
            ReconnectPolicy = new()
            {
                MaxAttempts = attempts, InitialDelay = TimeSpan.FromMilliseconds(1),
                MaxDelay = TimeSpan.FromMilliseconds(1), JitterRatio = 0,
            }, Endpoints = [new("127.0.0.1", first.Port)],
        });
        await using var subscription = await client.SubscribeAsync(descriptor).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        using var capture = new Capture(throwOnMeasurement: true);
        Volatile.Write(ref topology, NotificationTopology(second.Port));
        _ = await client.Core.Cluster!.GetMasterConnectionsAsync(default, discovery: null);
        await subscription.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var rejection = capture.Items.Where(item => item.Tags.GetValueOrDefault("db.response.status_code") is "NOPERM").ToArray();
        await Assert.That(rejection.Length).IsEqualTo(attempts);
        foreach (var item in rejection)
        {
            await Assert.That((bool)item.Tags["redis.client.errors.internal"]!).IsTrue();
            await Assert.That(item.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        }
    }

    private static byte[] NotificationTopology(int port) => Encoding.ASCII.GetBytes(
        $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{port}\r\n");
}
