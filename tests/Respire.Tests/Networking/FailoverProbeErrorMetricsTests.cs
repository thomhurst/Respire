using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

[NotInParallel]
public class FailoverProbeErrorMetricsTests
{
    [Test]
    [MatrixDataSource]
    public async Task HandledProbeFailuresNeverReportFinalCallerErrors(
        [Matrix("ping", "cluster-error", "cluster-malformed", "cluster-state", "sentinel-ping")] string shape,
        [Matrix(false, true)] bool enabled, [Matrix(false, true)] bool throwingListener)
    {
        using var configuration = new MetricConfigurationScope(new()
        {
            Groups = enabled ? RespireMetricGroups.Resiliency : RespireMetricGroups.None,
        });
        await using var failed = new FakeRespServer(32, FakeRespServer.PongReply);
        await using var healthy = new FakeRespServer(32, FakeRespServer.PongReply);
        await using var sentinel = new FakeRespServer(32, FakeRespServer.PongReply);
        var slots = Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{failed.Port}\r\n");
        failed.ReplyOverride = (_, command) => command switch
        {
            "CLUSTER SLOTS" => slots,
            "ROLE" => "*3\r\n$6\r\nmaster\r\n:0\r\n*0\r\n"u8.ToArray(),
            "PING" => "-NOPERM probe rejected\r\n"u8.ToArray(),
            "CLUSTER INFO" when shape == "cluster-malformed" => ":1\r\n"u8.ToArray(),
            "CLUSTER INFO" when shape == "cluster-state" => "$20\r\ncluster_state:fail\r\n\r\n"u8.ToArray(),
            "CLUSTER INFO" => "-NOPERM probe rejected\r\n"u8.ToArray(),
            _ => null,
        };
        sentinel.ReplyOverride = (_, command) => command switch
        {
            "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster" => Encoding.ASCII.GetBytes(
                $"*2\r\n$9\r\n127.0.0.1\r\n${failed.Port.ToString().Length}\r\n{failed.Port}\r\n"),
            "SENTINEL SENTINELS mymaster" => "*0\r\n"u8.ToArray(),
            _ => null,
        };
        var options = Options(shape == "sentinel-ping" ? sentinel.Port : failed.Port) with
        {
            UseCluster = shape.StartsWith("cluster", StringComparison.Ordinal),
            SentinelPrimaryName = shape == "sentinel-ping" ? "mymaster" : null,
            ClusterTopologyRefreshInterval = null,
        };
        using var capture = new Capture(throwingListener);
        await using var group = await RespireFailoverGroup.ConnectAsync(
            [new(options), new(Options(healthy.Port), Priority: 1)], ProbeOptions());
        await Assert.That(group.ActiveClient.Endpoint.Port).IsEqualTo(healthy.Port);
        var failedStatus = group.GetEndpointStatuses()[0];
        await Assert.That(failedStatus.IsHealthy).IsFalse();
        await Assert.That(failedStatus.ConsecutiveFailures).IsEqualTo(1);
        await Assert.That(failedStatus.LastErrorType).IsNotNull();
        await Assert.That(capture.Items.Count).IsEqualTo(enabled ? 1 : 0);
        if (enabled)
        {
            var item = capture.Items.Single();
            await Assert.That((bool)item["redis.client.errors.internal"]!).IsTrue();
            await Assert.That(item["redis.client.operation.retry_attempts"]).IsEqualTo((object)0);
            if (shape is "ping" or "cluster-error" or "sentinel-ping")
                await Assert.That(item["db.response.status_code"]).IsEqualTo((object)"NOPERM");
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ActiveClientPublicPingFailureRemainsFinal(bool throwingListener)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer(32, FakeRespServer.PongReply);
        await using var group = await RespireFailoverGroup.ConnectAsync([new(Options(server.Port))], ProbeOptions());
        server.ReplyOverride = (_, command) => command == "PING" ? "-NOPERM caller rejected\r\n"u8.ToArray() : null;
        using var capture = new Capture(throwingListener);
        await Assert.That(async () => await group.ActiveClient.PingAsync()).Throws<RespireServerException>();
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        await Assert.That((bool)capture.Items.Single()["redis.client.errors.internal"]!).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExpectedStartupCancellationDoesNotReportProbeError(bool throwingListener)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer(32, FakeRespServer.PongReply)
        {
            SuppressReply = static command => command == "PING",
        };
        using var cancellation = new CancellationTokenSource();
        using var capture = new Capture(throwingListener);
        var connecting = RespireFailoverGroup.ConnectAsync([new(Options(server.Port))], ProbeOptions(), cancellation.Token);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!server.ReceivedCommands.Contains("PING")) await Task.Delay(1, deadline.Token);
        cancellation.Cancel();
        await Assert.That(async () => await connecting).Throws<OperationCanceledException>();
        await Assert.That(capture.Items.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ProbeBudgetCancellationIsHandledInternally(bool throwingListener)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var stalled = new FakeRespServer(32, FakeRespServer.PongReply)
        {
            SuppressReply = static command => command == "PING",
        };
        await using var healthy = new FakeRespServer(32, FakeRespServer.PongReply);
        using var capture = new Capture(throwingListener);
        await using var group = await RespireFailoverGroup.ConnectAsync(
            [new(Options(stalled.Port)), new(Options(healthy.Port), Priority: 1)],
            ProbeOptions() with { ProbeTimeout = TimeSpan.FromMilliseconds(250) });
        await Assert.That(group.ActiveClient.Endpoint.Port).IsEqualTo(healthy.Port);
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        await Assert.That((bool)capture.Items.Single()["redis.client.errors.internal"]!).IsTrue();
    }

    private static RespireOptions Options(int port) => new()
    {
        Protocol = RespProtocol.Resp2, Connections = 1, Endpoints = [new("127.0.0.1", port)],
        CommandTimeout = TimeSpan.FromSeconds(2),
    };

    private static RespireFailoverGroupOptions ProbeOptions() => new()
    {
        ProbeInterval = TimeSpan.FromHours(1), ProbeTimeout = TimeSpan.FromSeconds(5),
        FailureThreshold = 1, CircuitOpenDuration = TimeSpan.FromHours(1),
    };

    private sealed class Capture : IDisposable
    {
        private readonly MeterListener _listener = new();
        internal ConcurrentQueue<Dictionary<string, object?>> Items { get; } = new();
        internal Capture(bool throwing)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Respire" && instrument.Name == "redis.client.errors")
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                Items.Enqueue(tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value));
                if (throwing) throw new InvalidOperationException("Listener failures must remain isolated.");
            });
            _listener.Start();
        }
        public void Dispose() => _listener.Dispose();
    }
}
