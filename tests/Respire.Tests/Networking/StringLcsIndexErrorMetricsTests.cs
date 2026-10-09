using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Respire.Protocol;
using TUnit.Core;

namespace Respire.Tests.Networking;

[NotInParallel]
public class StringLcsIndexErrorMetricsTests
{
    [Test]
    [MatrixDataSource]
    public async Task ConstructionFailure_IsObservedOnceBeforeDispatch(
        [Matrix("immediate", "batch", "transaction")] string mode, [Matrix(false, true)] bool crossSlot)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer("*0\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = crossSlot, Connections = 1,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        using var capture = new ErrorCapture();
        var options = crossSlot ? null : new RespireLcsOptions { MinimumMatchLength = -1 };
        async Task Invoke()
        {
            if (mode == "immediate") await client.Strings.LcsIndexAsync("{a}:first", "{b}:second", options);
            else if (mode == "batch")
            {
                using var batch = client.CreateBatch();
                _ = batch.Strings.LcsIndex("{a}:first", "{b}:second", options);
            }
            else
            {
                await using var transaction = client.CreateTransaction();
                _ = transaction.Strings.LcsIndex("{a}:first", "{b}:second", options);
            }
        }
        if (crossSlot) await Assert.That(Invoke).ThrowsExactly<RespireServerException>();
        else await Assert.That(Invoke).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        var tags = capture.Items.Single();
        await Assert.That(tags["error.type"]).IsEqualTo(
            crossSlot ? typeof(RespireServerException).FullName : typeof(ArgumentOutOfRangeException).FullName);
        await Assert.That((bool)tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        if (crossSlot) await Assert.That(tags["db.response.status_code"]).IsEqualTo("CROSSSLOT");
        await Assert.That(server.ReceivedCommands.Where(command => command != "CLUSTER SLOTS")).IsEmpty();
    }

    [Test]
    [Arguments("cancelled")]
    [Arguments("server")]
    [Arguments("protocol")]
    [Arguments("success")]
    public async Task ImmediateBoundary_ObservesEachFailureOnce(string outcome)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        var reply = outcome switch
        {
            "server" => "-WRONGTYPE rejected\r\n"u8.ToArray(),
            "protocol" => "+invalid\r\n"u8.ToArray(),
            _ => "*4\r\n$7\r\nmatches\r\n*0\r\n$3\r\nlen\r\n:0\r\n"u8.ToArray(),
        };
        await using var server = new FakeRespServer(reply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var capture = new ErrorCapture();
        using var cancellation = new CancellationTokenSource();
        if (outcome == "cancelled") cancellation.Cancel();
        async Task Invoke() => _ = await client.Strings.LcsIndexAsync("first", "second", cancellationToken: cancellation.Token);
        if (outcome == "cancelled") await Assert.That(Invoke).ThrowsExactly<OperationCanceledException>();
        else if (outcome == "server") await Assert.That(Invoke).ThrowsExactly<RespireServerException>();
        else if (outcome == "protocol") await Assert.That(Invoke).ThrowsExactly<RespireProtocolException>();
        else await Invoke();
        await Assert.That(capture.Items.Count).IsEqualTo(outcome == "success" ? 0 : 1);
        if (outcome == "cancelled") await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    private sealed class ErrorCapture : IDisposable
    {
        private readonly MeterListener _listener = new();
        internal ConcurrentQueue<Dictionary<string, object?>> Items { get; } = new();

        internal ErrorCapture()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Respire" && instrument.Name == "redis.client.errors")
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
                Items.Enqueue(tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value)));
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }
}
