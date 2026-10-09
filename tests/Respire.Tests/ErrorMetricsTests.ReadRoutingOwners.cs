using System.Net.Sockets;
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
    public async Task SharedPhysicalReadFailureKeepsEachCallerFinalOwner()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var primary = new FakeRespServer(16, FakeRespServer.OkReply);
        await using var replica = new FakeRespServer(16, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "ROLE"
                ? "*5\r\n$5\r\nslave\r\n$9\r\n127.0.0.1\r\n:6379\r\n$9\r\nconnected\r\n:0\r\n"u8.ToArray()
                : command == "GET warm" ? "$2\r\nok\r\n"u8.ToArray() : null,
            SuppressReply = command => command.StartsWith("GET failed", StringComparison.Ordinal),
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, ThreadPoolMonitoring = false,
            Endpoints = [new("127.0.0.1", primary.Port)], ReplicaEndpoints = [new("127.0.0.1", replica.Port)],
            ReadFrom = RespireReadFrom.Replica,
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.That(await client.GetStringAsync("warm", deadline.Token)).IsEqualTo("ok");
        using var capture = new Capture(throwOnMeasurement: true);
        var first = client.GetStringAsync("failed-1", deadline.Token).AsTask();
        var second = client.GetStringAsync("failed-2", deadline.Token).AsTask();
        while (replica.ReceivedCommands.Count(command => command.StartsWith("GET failed", StringComparison.Ordinal)) != 2)
            await Task.Delay(1, deadline.Token);
        await replica.DisposeAsync();
        var firstError = await Assert.That(async () => await first).Throws<RespireConnectionException>();
        var secondError = await Assert.That(async () => await second).Throws<RespireConnectionException>();
        await Assert.That(ReferenceEquals(firstError, secondError)).IsTrue();
        var items = capture.Items.ToArray();
        await Assert.That(items.Count(item => (bool)item.Tags["redis.client.errors.internal"]!)).IsEqualTo(1);
        await Assert.That(items.Count(item => !(bool)item.Tags["redis.client.errors.internal"]!)).IsEqualTo(2);
        await Assert.That(items.All(item => Equals(item.Tags["redis.client.operation.retry_attempts"], 0))).IsTrue();
    }

    [Test]
    [MatrixDataSource]
    public async Task StandaloneReadFallbackRetainsRetriesThroughFinalConversion(
        [Matrix("string", "stream", "cursor", "blocking", "hedged")] string shape,
        [Matrix(false, true)] bool roleFailure,
        [Matrix(false, true)] bool commandMetrics,
        [Matrix(false, true)] bool lateActivation)
    {
        using var configuration = new MetricConfigurationScope(new()
        {
            Groups = lateActivation ? RespireMetricGroups.None : RespireMetricGroups.Resiliency
                | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None),
        });
        await using var primary = new FakeRespServer(16, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => IsSelectionRead(command) ? "-WRONGTYPE private-read\r\n"u8.ToArray() : null,
        };
        await using var replica = new FakeRespServer(16, FakeRespServer.OkReply);
        var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new SocketException((int)SocketError.ConnectionRefused);
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, ThreadPoolMonitoring = false,
            Endpoints = [new("127.0.0.1", primary.Port)], ReplicaEndpoints = [new("127.0.0.1", replica.Port)],
            ReadFrom = RespireReadFrom.ReplicaPreferred,
            HedgedReads = shape == "hedged" ? new() { Delay = TimeSpan.FromSeconds(1), MaximumExtraLoadPercent = 100 } : null,
            TestingStreamFactory = async (host, port, cancellationToken) =>
            {
                if (port == replica.Port)
                {
                    attempted.TrySetResult();
                    await release.Task.WaitAsync(cancellationToken);
                    if (!roleFailure) throw expected;
                }
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(host, port, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            },
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: commandMetrics);
        try
        {
            var read = ReadAsync();
            await attempted.Task.WaitAsync(deadline.Token);
            if (lateActivation) RespireMetrics.Configure(new()
            {
                Groups = RespireMetricGroups.Resiliency | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None),
            });
            release.TrySetResult();
            await Assert.That(async () => await read).Throws<RespireServerException>();
            var items = capture.Items.ToArray();
            await Assert.That(items.Length).IsEqualTo(2);
            var handled = items.Single(item => (bool)item.Tags["redis.client.errors.internal"]!);
            var final = items.Single(item => !(bool)item.Tags["redis.client.errors.internal"]!);
            await Assert.That(handled.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
            await Assert.That(final.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(1);
            await Assert.That(final.Tags["db.response.status_code"]).IsEqualTo("WRONGTYPE");
            await Assert.That(replica.ReceivedCommands.Count(IsSelectionRead)).IsEqualTo(0);
            await Assert.That(primary.ReceivedCommands.Count(IsSelectionRead)).IsEqualTo(1);
        }
        finally { release.TrySetResult(); }

        async Task ReadAsync()
        {
            switch (shape)
            {
                case "stream":
                    await using (await client.Strings.GetStreamAsync("key", deadline.Token)) { }
                    break;
                case "cursor":
                    await foreach (var _ in client.Hashes.ScanAsync("key", cancellationToken: deadline.Token)) { }
                    break;
                case "blocking":
                    _ = await client.Streams.ReadAsync("key", "0-0",
                        waitFor: TimeSpan.FromMilliseconds(1), cancellationToken: deadline.Token);
                    break;
                default:
                    _ = await client.GetStringAsync("key", deadline.Token);
                    break;
            }
        }
    }
}
