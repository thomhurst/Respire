using Respire.Internal;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public partial class ErrorMetricsTests
{
    [Test]
    [MatrixDataSource]
    public async Task GroupReadValidationHasOneFinalOwner(
        [Matrix("page", "options-iterator")] string route,
        [Matrix(false, true)] bool nullConsumer)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer(4, FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var sent = server.ReceivedCommands.Count;
        var group = nullConsumer ? "group" : null!;
        var consumer = nullConsumer ? null! : "consumer";
        using var capture = new Capture(throwOnMeasurement: true);
        async Task Execute()
        {
            if (route == "page") await client.Streams.ReadGroupOnceAsync("events", group, consumer);
            else
            {
                await foreach (var _ in client.Streams.ReadGroupAsync(new StreamReadOptions(), "events", group, consumer)) { }
            }
        }
        var error = await Assert.That(Execute).ThrowsExactly<ArgumentNullException>();
        await Assert.That(error!.ParamName).IsEqualTo(nullConsumer ? "consumer" : "group");
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(1);
        await Assert.That((bool)items[0].Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(items[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(sent);
    }

    [Test]
    public async Task GroupIteratorOptionsValidationHasOneFinalOwner()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var client = RespireClient.Create(new RespireOptions { Endpoints = [new("127.0.0.1", 6379)] });
        using var capture = new Capture(throwOnMeasurement: true);
        await using var reader = client.Streams.ReadGroupAsync(new StreamReadOptions { Count = 0 },
            "events", "group", "consumer").GetAsyncEnumerator();
        await Assert.That(async () => await reader.MoveNextAsync()).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(capture.Items.Count).IsEqualTo(1);
    }

    [Test]
    [MatrixDataSource]
    public async Task GroupIteratorKeepsOwnerAfterFirstPage(
        [Matrix("options", "live", "replay")] string route, [Matrix(false, true)] bool disposeEarly)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        var reads = 0;
        await using var server = new FakeRespServer(4, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("XREADGROUP ", StringComparison.Ordinal)
                ? Interlocked.Increment(ref reads) == 1
                    ? StreamReadTests.Reply(false, ("events", ["1-0"])) : "-NOPERM denied\r\n"u8.ToArray()
                : null,
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var capture = new Capture(throwOnMeasurement: true);
        var entries = route switch
        {
            "options" => client.Streams.ReadGroupAsync(new StreamReadOptions(), "events", "group", "consumer"),
            "live" => client.Streams.ReadGroupAsync("events", "group", "consumer"),
            _ => client.Streams.ReadGroupAsync("events", "group", "consumer", RespireStreamId.Beginning),
        };
        await using (var reader = entries.GetAsyncEnumerator())
        {
            await Assert.That(await reader.MoveNextAsync()).IsTrue();
            await Assert.That(reader.Current.Id).IsEqualTo((RespireStreamId)"1-0");
            if (!disposeEarly)
                await Assert.That(async () => await reader.MoveNextAsync()).ThrowsExactly<RespireServerException>();
        }
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(disposeEarly ? 0 : 1);
        if (!disposeEarly)
        {
            await Assert.That(items[0].Tags["db.response.status_code"]).IsEqualTo("NOPERM");
            await Assert.That((bool)items[0].Tags["redis.client.errors.internal"]!).IsFalse();
        }
        await Assert.That(reads).IsEqualTo(disposeEarly ? 1 : 2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task StreamPageCancellationPreservesCallerTokenAndStatus(bool group)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer(4, FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var capture = new Capture(throwOnMeasurement: true);
        var pending = (group
            ? client.Streams.ReadGroupOnceAsync("events", "group", "consumer", cancellationToken: cancellation.Token)
            : client.Streams.ReadAsync("events", cancellationToken: cancellation.Token)).AsTask();
        var error = await Assert.That(async () => await pending).Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(pending.IsCanceled).IsTrue();
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        await Assert.That(capture.Items.Single().Tags["redis.client.errors.category"]).IsEqualTo("cancelled");
    }
}
