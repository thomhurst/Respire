using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

[NotInParallel]
public class SortedSetAlgebraErrorMetricsTests
{
    [Test]
    [MatrixDataSource]
    public async Task ReportsOneFinalErrorAcrossValidationAndDispatch(
        [Matrix("ZINTER", "ZUNION", "ZDIFF", "ZINTERSTORE", "ZUNIONSTORE", "ZDIFFSTORE")] string operation,
        [Matrix("cross-slot", "server", "success")] string outcome,
        [Matrix(false, true)] bool enabled)
    {
        using var configuration = new MetricConfigurationScope(new()
        {
            Groups = enabled ? RespireMetricGroups.Resiliency : RespireMetricGroups.None,
        });
        await using var server = new FakeRespServer();
        server.ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
            ? Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{server.Port}\r\n")
            : outcome == "server" ? "-WRONGTYPE rejected\r\n"u8.ToArray()
            : operation.EndsWith("STORE", StringComparison.Ordinal) ? ":1\r\n"u8.ToArray()
            : "*1\r\n$3\r\none\r\n"u8.ToArray();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespireProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", server.Port)], Connections = 1,
        });
        var measurements = new ConcurrentQueue<(long Value, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "redis.client.errors")
                current.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            measurements.Enqueue((value, tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value))));
        listener.Start();
        RespireKey[] keys = ["{same}:one", outcome == "cross-slot" ? "{other}:two" : "{same}:two"];
        async Task Execute()
        {
            switch (operation)
            {
                case "ZINTER": await client.SortedSets.IntersectAsync(keys); break;
                case "ZUNION": await client.SortedSets.UnionAsync(keys); break;
                case "ZDIFF": await client.SortedSets.DifferenceAsync(keys); break;
                case "ZINTERSTORE": await client.SortedSets.IntersectStoreAsync("{same}:dest", keys); break;
                case "ZUNIONSTORE": await client.SortedSets.UnionStoreAsync("{same}:dest", keys); break;
                case "ZDIFFSTORE": await client.SortedSets.DifferenceStoreAsync("{same}:dest", keys); break;
            }
        }
        if (outcome == "cross-slot")
            await Assert.That(Execute).ThrowsExactly<RespireCrossSlotException>();
        else if (outcome == "server")
            await Assert.That(Execute).ThrowsExactly<RespireServerException>();
        else
            await Execute();

        await Assert.That(measurements.Count).IsEqualTo(enabled && outcome != "success" ? 1 : 0);
        if (enabled && outcome != "success")
        {
            var measurement = measurements.Single();
            await Assert.That(measurement.Value).IsEqualTo(1);
            await Assert.That((bool)measurement.Tags["redis.client.errors.internal"]!).IsFalse();
            await Assert.That(measurement.Tags["error.type"]).IsEqualTo(
                (outcome == "cross-slot" ? typeof(RespireCrossSlotException) : typeof(RespireServerException)).FullName);
            if (outcome == "cross-slot")
                await Assert.That(measurement.Tags["redis.client.errors.category"]).IsEqualTo("validation");
        }
        if (outcome == "cross-slot")
            await Assert.That(server.ReceivedCommands.Count).IsEqualTo(0);
        else
            await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith(operation + " ", StringComparison.Ordinal)))
                .IsEqualTo(1);
    }
}
