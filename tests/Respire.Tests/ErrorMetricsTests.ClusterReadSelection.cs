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
    public async Task ClusterReadSelectionRetainsRejectedCandidateOwner(
        [Matrix(RespireReadFrom.ReplicaPreferred, RespireReadFrom.AzAffinity,
            RespireReadFrom.AzAffinityReplicasAndPrimary)] RespireReadFrom policy,
        [Matrix("string", "stream", "cursor", "blocking", "hedged")] string shape,
        [Matrix("success", "rejection", "cancellation")] string outcome,
        [Matrix(false, true)] bool commandMetrics)
    {
        using var configuration = new MetricConfigurationScope(new()
        {
            Groups = RespireMetricGroups.Resiliency | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None),
        });
        using var unavailable = new ReservedUnavailablePort();
        await using var primary = new FakeRespServer(16, FakeRespServer.OkReply);
        var topology = Encoding.ASCII.GetBytes($"*1\r\n*4\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{primary.Port}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{unavailable.Port}\r\n");
        primary.ReplyOverride = (_, command) => command switch
        {
            "CLUSTER SLOTS" => topology,
            var read when IsSelectionRead(read) => outcome == "rejection" ? "-WRONGTYPE private-selection\r\n"u8.ToArray()
                : read.StartsWith("HSCAN ", StringComparison.Ordinal) ? "*2\r\n$1\r\n0\r\n*2\r\n$5\r\nfield\r\n$5\r\nvalue\r\n"u8.ToArray()
                : read.StartsWith("XREAD ", StringComparison.Ordinal) ? "*0\r\n"u8.ToArray()
                : "$2\r\nok\r\n"u8.ToArray(),
            _ => null,
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, Connections = 1, ThreadPoolMonitoring = false,
            ClusterTopologyRefreshInterval = null, Endpoints = [new("127.0.0.1", primary.Port)], ReadFrom = policy,
            ClientAvailabilityZone = ReadFallbackPolicy.UsesAvailabilityZone(policy) ? "local" : null,
            HedgedReads = shape == "hedged" ? new() { Delay = TimeSpan.FromSeconds(1), MaximumExtraLoadPercent = 100 } : null,
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (outcome == "cancellation") await cancellation.CancelAsync();
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: commandMetrics);
        if (outcome == "success") await ReadAsync();
        else if (outcome == "rejection")
            await Assert.That(ReadAsync).Throws<RespireServerException>();
        else
        {
            var error = await Assert.That(ReadAsync).Throws<OperationCanceledException>();
            await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        }
        var items = capture.Items.ToArray();
        var handled = items.Where(item => (bool)item.Tags["redis.client.errors.internal"]!).ToArray();
        var final = items.Where(item => !(bool)item.Tags["redis.client.errors.internal"]!).ToArray();
        await Assert.That(handled.Length).IsEqualTo(outcome == "cancellation" ? 0 : 1);
        // Pre-cancelled cursors submit no command but still report their caller-visible cancellation once.
        await Assert.That(final.Length).IsEqualTo(outcome == "success" ? 0 : 1);
        if (handled.Length != 0)
        {
            await Assert.That(handled[0].Tags["redis.client.errors.category"]).IsEqualTo("network");
            await Assert.That(handled[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        }
        if (final.Length != 0)
            await Assert.That(final[0].Tags["redis.client.operation.retry_attempts"])
                .IsEqualTo(outcome == "cancellation" ? 0 : 1);
        await Assert.That(primary.ReceivedCommands.Count(IsSelectionRead)).IsEqualTo(outcome == "cancellation" ? 0 : 1);
        await Assert.That(items.SelectMany(item => item.Tags.Values)
            .Any(value => value?.ToString()?.Contains("private-") == true)).IsFalse();

        async Task ReadAsync()
        {
            switch (shape)
            {
                case "string":
                case "hedged":
                    await Assert.That(await client.GetStringAsync("private-key", cancellation.Token)).IsEqualTo("ok");
                    break;
                case "stream":
                    await using (var stream = await client.Strings.GetStreamAsync("private-key", cancellation.Token))
                    {
                        using var copy = new MemoryStream();
                        await stream!.CopyToAsync(copy, cancellation.Token);
                        await Assert.That(Encoding.ASCII.GetString(copy.ToArray())).IsEqualTo("ok");
                    }
                    break;
                case "cursor":
                    await foreach (var entry in client.Hashes.ScanAsync("private-key", cancellationToken: cancellation.Token))
                        await Assert.That(entry.Key).IsEqualTo("field");
                    break;
                case "blocking":
                    await Assert.That(await client.Streams.ReadAsync("private-key", "0-0",
                        waitFor: TimeSpan.FromMilliseconds(1), cancellationToken: cancellation.Token)).IsEmpty();
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(shape));
            }
        }
    }

    private static bool IsSelectionRead(string command)
        => command.StartsWith("GET ", StringComparison.Ordinal) || command.StartsWith("HSCAN ", StringComparison.Ordinal)
            || command.StartsWith("XREAD ", StringComparison.Ordinal);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ClusterBatchSharesCandidateAttemptsWithEachDeferredOwner(bool rejection)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var unavailable = new ReservedUnavailablePort();
        await using var primary = new FakeRespServer(16, FakeRespServer.OkReply);
        var topology = Encoding.ASCII.GetBytes($"*1\r\n*4\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{primary.Port}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{unavailable.Port}\r\n");
        primary.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? topology
            : command.StartsWith("GET ", StringComparison.Ordinal) ? rejection
                ? "-WRONGTYPE private-selection\r\n"u8.ToArray() : "$2\r\nok\r\n"u8.ToArray() : null;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, Connections = 1, ThreadPoolMonitoring = false,
            ClusterTopologyRefreshInterval = null, Endpoints = [new("127.0.0.1", primary.Port)],
            ReadFrom = RespireReadFrom.ReplicaPreferred,
        });
        using var capture = new Capture(throwOnMeasurement: true);
        using var batch = client.CreateBatch();
        var first = batch.GetString("{private}:one");
        var second = batch.GetString("{private}:two");
        await batch.TryExecuteAsync();
        if (rejection)
        {
            await Assert.That(() => first.Result).Throws<RespireServerException>();
            await Assert.That(() => second.Result).Throws<RespireServerException>();
        }
        else
        {
            await Assert.That(first.Result).IsEqualTo("ok");
            await Assert.That(second.Result).IsEqualTo("ok");
        }
        var items = capture.Items.ToArray();
        await Assert.That(items.Count(item => (bool)item.Tags["redis.client.errors.internal"]!)).IsEqualTo(1);
        var final = items.Where(item => !(bool)item.Tags["redis.client.errors.internal"]!).ToArray();
        await Assert.That(final.Length).IsEqualTo(rejection ? 2 : 0);
        foreach (var item in final)
            await Assert.That(item.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(1);
        await Assert.That(primary.ReceivedCommands.Count(IsSelectionRead)).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ClusterAlternateReplicaPreservesLateActivationAndResetsNextOwner(bool lateActivation)
    {
        using var configuration = new MetricConfigurationScope(new()
            { Groups = lateActivation ? RespireMetricGroups.None : RespireMetricGroups.Resiliency });
        using var unavailable = new ReservedUnavailablePort();
        await using var replica = new FakeRespServer(16, FakeRespServer.OkReply);
        await using var primary = new FakeRespServer(16, FakeRespServer.OkReply);
        // The real range cursor starts at index one, so the refused endpoint precedes the healthy replica.
        var topology = Encoding.ASCII.GetBytes($"*1\r\n*5\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{primary.Port}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{replica.Port}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{unavailable.Port}\r\n");
        primary.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? topology : null;
        replica.ReplyOverride = (_, command) => command.StartsWith("GET ", StringComparison.Ordinal)
            ? "-WRONGTYPE private-selection\r\n"u8.ToArray() : null;
        var submitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = true;
        replica.SuppressReply = command =>
        {
            if (!command.StartsWith("GET ", StringComparison.Ordinal) || !Volatile.Read(ref hold)) return false;
            submitted.TrySetResult();
            return true;
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, Connections = 1, ThreadPoolMonitoring = false,
            ClusterTopologyRefreshInterval = null, Endpoints = [new("127.0.0.1", primary.Port)],
            ReadFrom = RespireReadFrom.Replica,
        });
        using var capture = new Capture(throwOnMeasurement: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = client.GetStringAsync("private-key", deadline.Token).AsTask();
        await submitted.Task.WaitAsync(deadline.Token);
        if (lateActivation) RespireMetrics.Configure(new() { Groups = RespireMetricGroups.Resiliency });
        Volatile.Write(ref hold, false);
        await replica.SendRawAsync("-WRONGTYPE private-selection\r\n"u8.ToArray());
        await Assert.That(async () => await pending).Throws<RespireServerException>();
        var first = capture.Items.ToArray();
        await Assert.That(first.Count(item => (bool)item.Tags["redis.client.errors.internal"]!)).IsEqualTo(lateActivation ? 0 : 1);
        var final = first.Single(item => !(bool)item.Tags["redis.client.errors.internal"]!);
        await Assert.That(final.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(1);
        await Assert.That(async () => await client.GetStringAsync("private-next", deadline.Token)).Throws<RespireServerException>();
        var next = capture.Items.Skip(first.Length).Single();
        await Assert.That((bool)next.Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(next.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        await Assert.That(primary.ReceivedCommands.Count(IsSelectionRead)).IsEqualTo(0);
        await Assert.That(replica.ReceivedCommands).Contains("READONLY");
        await Assert.That(replica.ReceivedCommands.Count(IsSelectionRead)).IsEqualTo(2);
    }
}
