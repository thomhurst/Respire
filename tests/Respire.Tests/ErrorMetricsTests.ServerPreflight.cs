using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public partial class ErrorMetricsTests
{
    [Test]
    public async Task StructuredConnectionValidationKeepsFaultedValueTask()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var capture = new Capture(throwOnMeasurement: true);
        var response = RespireClient.ConnectAsync((RespireOptions)null!);
        await Assert.That(response.IsFaulted).IsTrue();
        await Assert.That(async () => await response).Throws<ArgumentNullException>();
        await Assert.That(capture.Items.Count).IsEqualTo(1);
    }

    [Test]
    [MatrixDataSource]
    public async Task ServerPreflightOwnsValidationAndAdminFailures(
        [Matrix("kill-id", "slowlog", "latency", "memory", "config-admin", "flush-admin", "flush-mode",
            "clients-filter", "clients-fanout-filter", "kill-filter-admin", "hotkeys-fanout-admin",
            "client-info-admin", "client-filter", "client-kill-admin", "node-bits", "node-slots")]
        string route, [Matrix(false, true)] bool enabled)
    {
        using var configuration = new MetricConfigurationScope(new()
            { Groups = enabled ? RespireMetricGroups.Resiliency : RespireMetricGroups.None });
        await using var server = new FakeRespServer(4, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "CLIENT ID" ? ":1\r\n"u8.ToArray() : null,
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var handle = await client.Server.GetClientConnectionAsync();
        var node = client.Server.OnNode(new("127.0.0.1", server.Port));
        var sent = server.ReceivedCommands.Count;
        using var capture = new Capture(throwOnMeasurement: true);
        async Task Execute()
        {
            switch (route)
            {
                case "kill-id": await client.Server.KillClientAsync(0); break;
                case "slowlog": await client.Server.SlowLogAsync(-1); break;
                case "latency": await client.Server.ResetLatencyAsync(""); break;
                case "memory": await client.Server.MemoryUsageAsync("key", -1); break;
                case "config-admin": await client.Server.SetConfigAsync("name", "value"); break;
                case "flush-admin": await client.Server.FlushAllAsync(); break;
                case "flush-mode": await client.Server.FlushDatabaseAsync((ServerFlushMode)999, default); break;
                case "clients-filter": await client.Server.ClientsAsync(null!, default); break;
                case "clients-fanout-filter": await client.Server.ClientsOnAllNodesAsync(null!, default); break;
                case "kill-filter-admin": await client.Server.KillClientsAsync(new()); break;
                case "hotkeys-fanout-admin": await client.Server.StartHotKeysOnAllNodesAsync(new()); break;
                case "client-info-admin": await handle.SetInfoAsync(RespireClientInfoAttribute.LibraryName, "name"); break;
                case "client-filter": await handle.ClientsAsync(null!); break;
                case "client-kill-admin": await handle.KillClientsAsync(new()); break;
                case "node-bits": await node.AclGeneratePasswordAsync(0); break;
                default: await node.ClusterAddSlotsAsync([16384]); break;
            }
        }
        var error = await Assert.That(Execute).Throws<Exception>();
        await Assert.That(capture.Items.Count).IsEqualTo(enabled ? 1 : 0);
        if (enabled)
        {
            var item = capture.Items.Single();
            await Assert.That(item.Tags["error.type"]).IsEqualTo(error!.GetType().FullName);
            await Assert.That((bool)item.Tags["redis.client.errors.internal"]!).IsFalse();
            await Assert.That(item.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        }
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(sent);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ServerHandleAcquisitionPreservesCancellation(bool hotkeys)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var client = RespireClient.Create(new RespireOptions { Protocol = RespProtocol.Resp2, Endpoints = [new("127.0.0.1", 6379)] });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var capture = new Capture(throwOnMeasurement: true);
        async Task Execute()
        {
            if (hotkeys) await client.Server.GetHotKeysTrackerAsync(cancellation.Token);
            else await client.Server.GetClientConnectionAsync(cancellation.Token);
        }
        var error = await Assert.That(Execute).Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        await Assert.That(capture.Items.Single().Tags["redis.client.errors.category"]).IsEqualTo("cancelled");
    }
}
