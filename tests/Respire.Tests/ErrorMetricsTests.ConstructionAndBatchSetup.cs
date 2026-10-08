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
    public async Task MultiKeyConstructionReportsOnlyItsFinalPreflightError(
        [Matrix("SORT", "ZINTERCARD", "MSETNX")] string operation,
        [Matrix(false, true)] bool crossSlot, [Matrix(false, true)] bool enabled,
        [Matrix(false, true)] bool prefix)
    {
        using var configuration = new MetricConfigurationScope(new()
            { Groups = enabled ? RespireMetricGroups.Resiliency : RespireMetricGroups.None });
        await using var server = new FakeRespServer(8, FakeRespServer.OkReply);
        server.ReplyOverride = (_, command) => command switch
        {
            "CLUSTER SLOTS" => Encoding.ASCII.GetBytes(
                $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{server.Port}\r\n"),
            _ when command.StartsWith(operation + " ", StringComparison.Ordinal) => ":1\r\n"u8.ToArray(),
            _ => null,
        };
        await using var root = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        var client = prefix ? root.WithKeyPrefix("tenant:") : root;
        RespireKey first = "{same}:one";
        RespireKey second = crossSlot ? "{other}:two" : "{same}:two";
        using var capture = new Capture(throwOnMeasurement: true);
        async Task Execute()
        {
            switch (operation)
            {
                case "SORT": await client.Keys.SortStoreAsync(first, second); break;
                case "ZINTERCARD": await client.SortedSets.IntersectCountAsync([first, second]); break;
                default: await client.Strings.SetManyIfNotExistsAsync([(first, "one"), (second, "two")]); break;
            }
        }
        if (crossSlot)
        {
            var error = await Assert.That(Execute).Throws<RespireServerException>();
            await Assert.That(error!.Code).IsEqualTo("CROSSSLOT");
        }
        else await Execute();
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(enabled && crossSlot ? 1 : 0);
        if (enabled && crossSlot)
        {
            await Assert.That((bool)items[0].Tags["redis.client.errors.internal"]!).IsFalse();
            await Assert.That(items[0].Tags["error.type"]).IsEqualTo(typeof(RespireServerException).FullName);
            await Assert.That(items[0].Tags["db.response.status_code"]).IsEqualTo("CROSSSLOT");
            await Assert.That(items[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        }
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith(operation + " ", StringComparison.Ordinal)))
            .IsEqualTo(crossSlot ? 0 : 1);
    }

    [Test]
    [MatrixDataSource]
    public async Task BatchSetupReportsOneFinalErrorBeforePerCommandOwnership(
        [Matrix("disposed", "sent", "busy-import")] string failure,
        [Matrix(false, true)] bool enabled, [Matrix(false, true)] bool throwingExecute)
    {
        using var configuration = new MetricConfigurationScope(new()
            { Groups = enabled ? RespireMetricGroups.Resiliency : RespireMetricGroups.None });
        await using var server = new FakeRespServer(4, FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await using var session = failure == "busy-import" ? await client.Hashes.CreateImportSessionAsync() : null;
        using var batch = session is null ? client.CreateBatch() : session.CreateBatch();
        if (failure == "disposed") batch.Dispose();
        if (failure == "sent") await batch.TryExecuteAsync();
        using var busy = session?.EnterOperation();
        using var capture = new Capture(throwOnMeasurement: true);
        var error = await Assert.That(async () =>
        {
            if (throwingExecute) await batch.ExecuteAsync();
            else await batch.TryExecuteAsync();
        }).Throws<Exception>();
        await Assert.That(error!.GetType()).IsEqualTo(failure == "disposed"
            ? typeof(ObjectDisposedException) : typeof(InvalidOperationException));
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(enabled ? 1 : 0);
        if (enabled)
        {
            await Assert.That((bool)items[0].Tags["redis.client.errors.internal"]!).IsFalse();
            await Assert.That(items[0].Tags["error.type"]).IsEqualTo(error.GetType().FullName);
            await Assert.That(items[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        }
        await Assert.That(batch.Count).IsEqualTo(0);
        if (failure != "sent") await Assert.That(batch.IsSent).IsFalse();
    }
}
