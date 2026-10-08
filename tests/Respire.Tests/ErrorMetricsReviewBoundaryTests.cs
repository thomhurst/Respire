using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

[NotInParallel]
public class ErrorMetricsReviewBoundaryTests
{
    [Test]
    [MatrixDataSource]
    public async Task TypedFailureKeepsBothMetricOwners(
        [Matrix("string", "bytes", "integer")] string shape, [Matrix(false, true)] bool cluster)
    {
        using var configuration = new MetricConfigurationScope(new()
        {
            Groups = RespireMetricGroups.Resiliency | RespireMetricGroups.Command,
        });
        await using var target = new FakeRespServer(8, "$-1\r\n"u8.ToArray());
        var slot = ClusterHash.GetSlot("key");
        await using var seed = new FakeRespServer(Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:{slot}\r\n:{slot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{target.Port}\r\n"));
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = cluster, Connections = 1,
            ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", cluster ? seed.Port : target.Port)],
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = await client.Strings.GetStringAsync("key", deadline.Token);
        target.ReplyOverride = (_, command) => command.StartsWith("GET ", StringComparison.Ordinal)
            || command.StartsWith("STRLEN ", StringComparison.Ordinal) ? "-WRONGTYPE rejected\r\n"u8.ToArray() : null;
        using var errors = new ErrorCapture();
        var durations = new ConcurrentQueue<double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "db.client.operation.duration")
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, value, _, _) =>
        {
            durations.Enqueue(value);
            throw new InvalidOperationException("duration listener failure");
        });
        listener.Start();
        await Assert.That(async () =>
        {
            switch (shape)
            {
                case "string": await client.Strings.GetStringAsync("key", deadline.Token); break;
                case "bytes": await client.Strings.GetBytesAsync("key", deadline.Token); break;
                default: await client.Strings.LengthAsync("key", deadline.Token); break;
            }
        }).ThrowsExactly<RespireServerException>();
        await Assert.That(errors.Items.Count).IsEqualTo(1);
        await Assert.That((bool)errors.Items.Single()["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(errors.Items.Single()["db.response.status_code"]).IsEqualTo("WRONGTYPE");
        await Assert.That(durations.Count).IsEqualTo(1);
        await Assert.That(durations.Single()).IsGreaterThan(0);
    }

    [Test]
    [Arguments(false, 0)]
    [Arguments(false, 3)]
    [Arguments(true, 0)]
    [Arguments(true, 3)]
    public async Task StreamingPrefixAndCommandErrorsHaveSeparateOwners(bool deferred, int attempts)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var capture = new ErrorCapture();
        var source = new BulkStreamPendingResponseSource("GET", true, null) { ErrorAttempts = attempts };
        using var observation = RespireTelemetry.ErrorObservation.Rent(force: true);
        observation.SetAttempts(attempts);
        var pending = RespireTelemetry.ObserveFinalError(source.Task, observation).AsTask();
        var scheduler = new CompletionScheduler();
        foreach (var code in new[] { "WRONGTYPE", "NOPERM" })
        {
            var reply = RespValue.Error(Encoding.ASCII.GetBytes(code + " rejected"));
            if (code == "WRONGTYPE") source.ObservePrefix(in reply);
            if (deferred)
            {
                scheduler.Add(source, in reply);
                scheduler.Flush();
                await scheduler.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            else { source.TrySetResult(in reply); source.ReleaseRef(); }
        }
        await Assert.That(async () => await pending).ThrowsExactly<RespireServerException>();
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(2);
        await Assert.That(items.Single(item => !(bool)item["redis.client.errors.internal"]!)["db.response.status_code"])
            .IsEqualTo("WRONGTYPE");
        await Assert.That(items.Single(item => (bool)item["redis.client.errors.internal"]!)["db.response.status_code"])
            .IsEqualTo("NOPERM");
        await Assert.That(items.All(item => Equals(item["redis.client.operation.retry_attempts"], attempts))).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BlockingAskErrorsRetainCompletedRedirectCount(bool largeKnownFrame)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var target = new FakeRespServer(8, "-WRONGTYPE rejected\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command == "ASKING" ? "-NOPERM asking rejected\r\n"u8.ToArray() : null,
        };
        var key = largeKnownFrame ? "{key}" + new string('x', 70_000) : "key";
        var slot = ClusterHash.GetSlot(key);
        await using var source = new FakeRespServer(8, Encoding.ASCII.GetBytes($"-ASK {slot} 127.0.0.1:{target.Port}\r\n"));
        await using var seed = new FakeRespServer(Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:{slot}\r\n:{slot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{source.Port}\r\n"));
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", seed.Port)],
        });
        using var capture = new ErrorCapture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.That(async () =>
        {
            if (largeKnownFrame)
                await client.Lists.LeftPopAsync(key, TimeSpan.FromSeconds(1), deadline.Token);
            else
            {
                using var result = await client.ExecuteAsync("BLPOP", [key, 0], cancellationToken: deadline.Token);
            }
        }).ThrowsExactly<RespireServerException>();
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(3);
        await Assert.That(items.Single(item => Equals(item["db.response.status_code"], "ASK"))["redis.client.operation.retry_attempts"])
            .IsEqualTo(0);
        await Assert.That(items.Single(item => Equals(item["db.response.status_code"], "NOPERM"))["redis.client.operation.retry_attempts"])
            .IsEqualTo(1);
        await Assert.That(items.Single(item => !(bool)item["redis.client.errors.internal"]!)["redis.client.operation.retry_attempts"])
            .IsEqualTo(1);
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
            {
                Items.Enqueue(tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value));
                throw new InvalidOperationException("listener failure");
            });
            _listener.Start();
        }
        public void Dispose() => _listener.Dispose();
    }
}
