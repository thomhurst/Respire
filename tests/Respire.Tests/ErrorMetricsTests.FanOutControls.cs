using System.Diagnostics;
using System.Text;
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
    public async Task JoinedFanOutKeepsBothTargetRetriesUntilFinalFailure(
        [Matrix("function", "script")] string route, [Matrix(false, true)] bool listenerThrows)
    {
        using var configuration = new MetricConfigurationScope(new()
            { Groups = RespireMetricGroups.Resiliency | RespireMetricGroups.Command });
        await using var first = new FakeRespServer(16, "-NOPERM first target\r\n"u8.ToArray());
        await using var second = new FakeRespServer(16, "-WRONGTYPE second target\r\n"u8.ToArray());
        var topology = Encoding.ASCII.GetBytes(
            $"*2\r\n*3\r\n:0\r\n:8191\r\n*2\r\n$9\r\n127.0.0.1\r\n:{first.Port}\r\n" +
            $"*3\r\n:8192\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{second.Port}\r\n");
        await using var seed = new FakeRespServer(16, topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespireProtocol.Resp2, Connections = 2, UseCluster = true,
            ClusterTopologyRefreshInterval = null, Endpoints = [new("127.0.0.1", seed.Port)],
        });
        var connections = await client.Core.Cluster!.GetMasterConnectionsAsync(default, discovery: null, observation: default);
        foreach (var connection in connections) _ = connection.Multiplexer!.GetConnection();
        var operation = route == "function" ? "FUNCTION FLUSH" : "SCRIPT FLUSH";
        var retired = new HashSet<int>();
        var retirements = new List<Task>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Respire",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity =>
            {
                if (activity.GetTagItem("db.operation.name")?.ToString() != operation) return;
                var port = (int)activity.GetTagItem("server.port")!;
                if (retired.Add(port)) retirements.Add(connections.Single(connection => connection.Port == port).RetireAsync());
            },
        };
        ActivitySource.AddActivityListener(listener);
        using var capture = new Capture(throwOnMeasurement: listenerThrows);
        Exception? failure = null;
        try
        {
            if (route == "function") await client.Functions.FlushAsync();
            else await client.Scripts.FlushAsync();
        }
        catch (Exception error) { failure = error; }
        await Task.WhenAll(retirements);
        await Assert.That(retired.Count).IsEqualTo(2);
        await Assert.That(failure is RespireServerException).IsTrue();
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(3);
        await Assert.That(items.Count(item => (bool)item.Tags["redis.client.errors.internal"]!)).IsEqualTo(2);
        var final = items.Single(item => !(bool)item.Tags["redis.client.errors.internal"]!);
        await Assert.That(final.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(2);
        await Assert.That(final.Tags["error.type"]).IsEqualTo(failure!.GetType().FullName);
        await Assert.That(first.ReceivedCommands.Count(command => command.StartsWith(operation, StringComparison.Ordinal))).IsEqualTo(1);
        await Assert.That(second.ReceivedCommands.Count(command => command.StartsWith(operation, StringComparison.Ordinal))).IsEqualTo(1);
    }

    [Test]
    public async Task ConcurrentNodeFanOutCallersKeepIndependentFailures()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer(16, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "HOTKEYS GET" ? "-NOPERM node failure\r\n"u8.ToArray() : null,
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var capture = new Capture(throwOnMeasurement: true);
        var results = await Task.WhenAll(client.Server.GetHotKeysOnAllNodesAsync().AsTask(),
            client.Server.GetHotKeysOnAllNodesAsync().AsTask());
        await Assert.That(results.All(result => result.Length == 1 && !result[0].IsSuccess)).IsTrue();
        await Assert.That(ReferenceEquals(results[0][0].Error, results[1][0].Error)).IsFalse();
        await Assert.That(capture.Items.Count).IsEqualTo(2);
        await Assert.That(capture.Items.All(item => !(bool)item.Tags["redis.client.errors.internal"]!
            && (int)item.Tags["redis.client.operation.retry_attempts"]! == 0)).IsTrue();
        // Inspecting a returned error result has no new dispatch or publication lifetime.
        await Assert.That(() => _ = results[0][0].Value).Throws<InvalidOperationException>();
        await Assert.That(capture.Items.Count).IsEqualTo(2);
    }
}
