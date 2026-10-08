using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public partial class ErrorMetricsTests
{
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
