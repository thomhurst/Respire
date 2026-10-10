using Respire.Tests.Networking;
using Respire.Internal;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public partial class ErrorMetricsTests
{
    [Test]
    [MatrixDataSource]
    public async Task ScanPreflightCountsOneFinalError(
        [Matrix("keys", "hash", "fields", "set", "sorted", "fields-page", "valkey", "resumable")] string route,
        [Matrix("count", "cancelled", "disposed")] string failure,
        [Matrix(false, true)] bool commandMetrics)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency
            | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None) });
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var cancellation = new CancellationTokenSource();
        if (failure == "cancelled") cancellation.Cancel();
        if (failure == "disposed") await client.DisposeAsync();
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: commandMetrics);
        var count = failure == "count" ? 0 : 250;
        var pageCancelled = false;
        async Task Execute()
        {
            switch (route)
            {
                case "keys": await First(client.Keys.ScanAsync(countHint: count, cancellationToken: cancellation.Token)); break;
                case "hash": await First(client.Hashes.ScanAsync("key", countHint: count, cancellationToken: cancellation.Token)); break;
                case "fields": await First(client.Hashes.ScanFieldsAsync("key", countHint: count, cancellationToken: cancellation.Token)); break;
                case "set": await First(client.Sets.ScanAsync("key", countHint: count, cancellationToken: cancellation.Token)); break;
                case "sorted": await First(client.SortedSets.ScanAsync("key", countHint: count, cancellationToken: cancellation.Token)); break;
                case "fields-page":
                    var fieldsPage = client.Hashes.ScanFieldsPageAsync("key", countHint: count, cancellationToken: cancellation.Token);
                    pageCancelled = fieldsPage.IsCanceled;
                    await fieldsPage;
                    break;
                case "valkey":
                    var valkeyPage = client.Keys.ScanValkeyClusterPageAsync(countHint: count, cancellationToken: cancellation.Token);
                    pageCancelled = valkeyPage.IsCanceled;
                    await valkeyPage;
                    break;
                default:
                    var clusterPage = client.Keys.ScanClusterPageAsync(RespireClusterScanCursor.Start, countHint: count, cancellationToken: cancellation.Token);
                    pageCancelled = clusterPage.IsCanceled;
                    await clusterPage;
                    break;
            }
        }
        var error = await Assert.That(Execute).Throws<Exception>();
        if (failure == "cancelled")
        {
            await Assert.That(error is OperationCanceledException).IsTrue();
            await Assert.That(((OperationCanceledException)error!).CancellationToken).IsEqualTo(cancellation.Token);
            if (route is "fields-page" or "valkey" or "resumable") await Assert.That(pageCancelled).IsTrue();
        }
        else if (failure == "disposed") await Assert.That(error).IsTypeOf<ObjectDisposedException>();
        else await Assert.That(error).IsTypeOf<ArgumentOutOfRangeException>();
        var final = capture.Items.Single();
        await Assert.That(final.Tags["error.type"]).IsEqualTo(error!.GetType().FullName);
        await Assert.That((bool)final.Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(final.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("HSCAN ", StringComparison.Ordinal)
            || command.StartsWith("SSCAN ", StringComparison.Ordinal) || command.StartsWith("ZSCAN ", StringComparison.Ordinal)
            || command.StartsWith("SCAN ", StringComparison.Ordinal) || command.StartsWith("CLUSTERSCAN ", StringComparison.Ordinal))).IsFalse();

        static async Task First<T>(IAsyncEnumerable<T> source)
        {
            await using var enumerator = source.GetAsyncEnumerator();
            _ = await enumerator.MoveNextAsync();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CollectionScanParserPreservesOriginalFailure(bool commandMetrics)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency
            | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None) });
        await using var server = new FakeRespServer("*2\r\n$1\r\n0\r\n*0\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: commandMetrics);
        var original = new InvalidOperationException("caller parser failure");
        var entries = CollectionScan.EnumerateAsync(client, "SSCAN", RespireCommands.Set.SSCAN.Verb,
            "key", null, 250, (in RespValue _) => ThrowPage(), default);
        await using var enumerator = entries.GetAsyncEnumerator();
        var error = await Assert.That(async () => _ = await enumerator.MoveNextAsync()).Throws<InvalidOperationException>();
        await Assert.That(ReferenceEquals(error, original)).IsTrue();
        var final = capture.Items.Single();
        await Assert.That((bool)final.Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(final.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        // Reply cleanup leaves the connection usable after the parser fails.
        server.ReplyOverride = (_, command) => command == "PING" ? FakeRespServer.PongReply : null;
        using var ping = await client.ExecuteAsync("PING");

        string[] ThrowPage() => throw original;
    }

    [Test]
    [MatrixDataSource]
    public async Task CollectionScanCancellationBetweenItemsCountsOnce(
        [Matrix(false, true)] bool abandon, [Matrix("keys", "set")] string route)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer("*2\r\n$1\r\n0\r\n*2\r\n$3\r\none\r\n$3\r\ntwo\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var capture = new Capture(throwOnMeasurement: true);
        using var cancellation = new CancellationTokenSource();
        var entries = route == "keys" ? client.Keys.ScanAsync(cancellationToken: cancellation.Token)
            : client.Sets.ScanAsync("key", cancellationToken: cancellation.Token);
        var enumerator = entries.GetAsyncEnumerator();
        try
        {
            await Assert.That(await enumerator.MoveNextAsync()).IsTrue();
            cancellation.Cancel();
            if (!abandon)
            {
                var error = await Assert.That(async () => _ = await enumerator.MoveNextAsync()).Throws<OperationCanceledException>();
                await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
            }
        }
        finally { await enumerator.DisposeAsync(); }
        await Assert.That(capture.Items.Count).IsEqualTo(abandon ? 0 : 1);
        var verb = route == "keys" ? "SCAN " : "SSCAN ";
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith(verb, StringComparison.Ordinal))).IsEqualTo(1);
    }
}
