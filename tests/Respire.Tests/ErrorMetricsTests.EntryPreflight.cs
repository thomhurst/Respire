using System.Diagnostics.Metrics;
using Respire.Internal;
using Respire.Protocol;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public partial class ErrorMetricsTests
{
    [Test]
    [MatrixDataSource]
    public async Task TypedMutationReportsFinalErrorAfterThrowingCompletion(
        [Matrix("success", "error", "canceled")] string response, [Matrix(false, true)] bool throwOnCompletion)
    {
        using var configuration = new MetricConfigurationScope(new()
            { Groups = RespireMetricGroups.Resiliency | RespireMetricGroups.ClientSideCaching });
        await using var server = new FakeRespServer(4, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "HELLO 3" ? "%1\r\n+proto\r\n:3\r\n"u8.ToArray()
                : response == "error" && command.StartsWith("SET ", StringComparison.Ordinal)
                    ? "-NOPERM denied\r\n"u8.ToArray() : null,
            SuppressReply = command => response == "canceled" && command.StartsWith("SET ", StringComparison.Ordinal),
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp3, Connections = 1, ClientSideCache = new(),
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, publishedListener) =>
        {
            if (ReferenceEquals(instrument, RespireTelemetry.ClientCacheInvalidations))
                publishedListener.EnableMeasurementEvents(instrument);
        };
        var observed = 0;
        listener.SetMeasurementEventCallback<long>((_, _, _, _) =>
        {
            if (++observed == 2 && throwOnCompletion)
                throw new InvalidOperationException("completion observer failed");
        });
        listener.Start();
        var observationsAtFinal = -1;
        using var capture = new Capture(throwOnMeasurement: true, onMeasurement: _ => observationsAtFinal = observed);
        using var cancellation = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = client.Strings.SetAsync("key", "value", cancellationToken: cancellation.Token).AsTask();
        if (response == "canceled")
        {
            while (!server.ReceivedCommands.Contains("SET key value")) await Task.Delay(1, deadline.Token);
            cancellation.Cancel();
        }
        if (response == "success" && !throwOnCompletion)
            await Assert.That(await pending.WaitAsync(deadline.Token)).IsTrue();
        else
        {
            var error = await Assert.That(async () => await pending.WaitAsync(deadline.Token)).Throws<Exception>();
            var expectedType = response == "error" ? typeof(RespireServerException)
                : response == "canceled" ? typeof(OperationCanceledException) : typeof(InvalidOperationException);
            await Assert.That(expectedType.IsInstanceOfType(error)).IsTrue();
            await Assert.That(capture.Items.Count).IsEqualTo(1);
            var item = capture.Items.Single();
            await Assert.That(item.Tags["error.type"]).IsEqualTo(error!.GetType().FullName);
            await Assert.That((bool)item.Tags["redis.client.errors.internal"]!).IsFalse();
            await Assert.That(observationsAtFinal).IsEqualTo(2);
        }
        await Assert.That(pending.IsCanceled).IsEqualTo(response == "canceled");
        await Assert.That(observed).IsEqualTo(2);
        if (response == "success" && !throwOnCompletion)
            await Assert.That(capture.Items.Count).IsEqualTo(0);
        if (response == "canceled")
        {
            var index = server.ReceivedCommands.ToList().IndexOf("SET key value");
            await server.SendRawAsync(FakeRespServer.OkReply, server.ReceivedConnectionIds[index]);
            await client.PingAsync(cancellationToken: deadline.Token);
        }
        await Assert.That(client.Core.ClientCache!.InspectForTests().ActiveMutationCount).IsEqualTo(0);
    }

    /// <summary>Checks public entry points report validation failures that precede their send owner exactly once.</summary>
    [Test]
    [MatrixDataSource]
    public async Task EntryPointPreflightFailuresReportOneFinalError(
        [Matrix("script-integer", "script-span", "clusterscan", "hotkeys-stop", "hotkeys-start", "node-genpass",
            "node-migrate", "node-cluster-forget", "raw-flags", "xreadgroup", "xreadgroup-replay", "subscribe-null",
            "subscribe-mixed")] string route,
        [Matrix(false, true)] bool enabled)
    {
        using var configuration = new MetricConfigurationScope(new()
            { Groups = enabled ? RespireMetricGroups.Resiliency : RespireMetricGroups.None });
        await using var server = new FakeRespServer(4, FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var tracker = route.StartsWith("hotkeys", StringComparison.Ordinal)
            ? await client.Server.GetHotKeysTrackerAsync() : null;
        var node = client.Server.OnNode(new("127.0.0.1", server.Port));
        var sent = server.ReceivedCommands.Count;
        using var capture = new Capture(throwOnMeasurement: true);
        async Task Execute()
        {
            switch (route)
            {
                case "script-integer": await client.Scripts.ExecuteIntegerAsync(null!); break;
                case "script-span": await client.Scripts.ExecuteSpanAsync(null!, [], []); break;
                case "clusterscan": await client.Keys.ScanValkeyClusterPageAsync(); break;
                case "hotkeys-stop": await tracker!.StopAsync(); break;
                case "hotkeys-start": await tracker!.StartAsync(null!); break;
                case "node-genpass": await node.AclGeneratePasswordAsync(0); break;
                case "node-migrate": await node.MigrateAsync(new("destination", 6379), [], 0, TimeSpan.FromSeconds(1)); break;
                case "node-cluster-forget": await node.ClusterForgetAsync(""); break;
                case "raw-flags": await client.ExecuteAsync($"GET {"key"}", (RespireCommandFlags)0x4000); break;
                case "xreadgroup":
                    await foreach (var _ in client.Streams.ReadGroupAsync("stream", "group", "consumer", 0)) { }
                    break;
                case "xreadgroup-replay":
                    await foreach (var _ in client.Streams.ReadGroupAsync("stream", "group", "consumer", RespireStreamId.Beginning, 0)) { }
                    break;
                case "subscribe-null": await client.SubscribeAsync((string)null!); break;
                default:
                    RespireChannel[] mixed = [new("one"), new RespireChannel("two").WithKind(SubscriptionKind.Pattern)];
                    await client.SubscribeAsync(mixed, CancellationToken.None);
                    break;
            }
        }
        var error = await Assert.That(Execute).Throws<Exception>();
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(enabled ? 1 : 0);
        if (enabled)
        {
            await Assert.That((bool)items[0].Tags["redis.client.errors.internal"]!).IsFalse();
            await Assert.That(items[0].Tags["error.type"]).IsEqualTo(error!.GetType().FullName);
            await Assert.That(items[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        }
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(sent);
    }
}
