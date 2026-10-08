using System.Net.Sockets;
using System.Text;
using Respire.Commands;
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
    public async Task OptionalClusterHedgeSelectionHasIndependentOwner(
        [Matrix(false, true)] bool commandMetrics, [Matrix(false, true)] bool lateActivation)
    {
        using var configuration = new MetricConfigurationScope(new()
        {
            Groups = lateActivation ? RespireMetricGroups.None : RespireMetricGroups.Resiliency
                | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None),
        });
        using var unavailable = new ReservedUnavailablePort();
        await using var primary = new FakeRespServer(16, FakeRespServer.OkReply);
        await using var replica = new FakeRespServer(16, FakeRespServer.OkReply)
        {
            SuppressReply = command => command == "GET key",
        };
        // Initial selection starts at index one, the healthy replica. Optional selection
        // excludes it and reaches the gated refused endpoint at index zero.
        var topology = Encoding.ASCII.GetBytes($"*1\r\n*5\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{primary.Port}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{unavailable.Port}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{replica.Port}\r\n");
        primary.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? topology : null;
        var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new SocketException((int)SocketError.ConnectionRefused);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, Connections = 1,
            Endpoints = [new("127.0.0.1", primary.Port)], ClusterTopologyRefreshInterval = null,
            TestingStreamFactory = async (host, port, cancellationToken) =>
            {
                if (port == unavailable.Port)
                {
                    attempted.TrySetResult();
                    await release.Task.WaitAsync(cancellationToken);
                    throw expected;
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
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var slot = ClusterHash.GetSlot("key");
        var router = client.Core.Cluster!;
        var original = await router.GetReadConnectionAsync(slot, RespireReadFrom.Replica, deadline.Token);
        var response = original.SendCheckedAsync(new Cmd1(Verbs.Get, "key"), deadline.Token).AsTask();
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: commandMetrics);
        try
        {
            var selection = router.GetHedgeConnectionAsync(slot, RespireReadFrom.Replica, original, deadline.Token).AsTask();
            await attempted.Task.WaitAsync(deadline.Token);
            if (lateActivation) RespireMetrics.Configure(new()
            {
                Groups = RespireMetricGroups.Resiliency | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None),
            });
            release.TrySetResult();
            await Assert.That(await selection.WaitAsync(deadline.Token)).IsNull();
            await replica.SendRawAsync("$2\r\nok\r\n"u8.ToArray());
            using var result = await response.WaitAsync(deadline.Token);
            await Assert.That(result.AsString()).IsEqualTo("ok");
            await Assert.That(capture.Items.Count).IsEqualTo(1);
            var item = capture.Items.Single();
            await Assert.That((bool)item.Tags["redis.client.errors.internal"]!).IsTrue();
            await Assert.That(item.Tags["redis.client.errors.category"]).IsEqualTo("network");
            await Assert.That(item.Tags["error.type"]).IsEqualTo(typeof(SocketException).FullName);
            await Assert.That(item.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
            await Assert.That(primary.ReceivedCommands.Contains("GET key")).IsFalse();
        }
        finally { release.TrySetResult(); }
    }

    [Test]
    [MatrixDataSource]
    public async Task ContinuousStreamBatchValidationHasOneFinalOwner(
        [Matrix(false, true)] bool single, [Matrix(-1, 0)] int batchSize,
        [Matrix(false, true)] bool commandMetrics)
    {
        using var configuration = new MetricConfigurationScope(new()
        {
            Groups = RespireMetricGroups.Resiliency | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None),
        });
        using var unavailable = new ReservedUnavailablePort();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Endpoints = [new("127.0.0.1", unavailable.Port)],
        });
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: commandMetrics);
        var error = await Assert.That(() =>
        {
            if (single) _ = client.Streams.ReadAllAsync("events", batchSize: batchSize);
            else _ = client.Streams.ReadAllAsync([("events", new RespireStreamId("0-0"))], batchSize: batchSize);
        }).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(error!.ParamName).IsEqualTo("batchSize");
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        var item = capture.Items.Single();
        await Assert.That((bool)item.Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(item.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
    }
}
