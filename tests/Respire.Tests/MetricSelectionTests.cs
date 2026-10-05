using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Respire.Internal;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

[NotInParallel]
public class MetricSelectionTests
{
    [Test]
    public async Task DefaultsSelectOnlyBasicConnectionsAndResiliency()
    {
        using var configuration = new MetricConfigurationScope(new());
        using var capture = new Capture();
        await Assert.That(RespireMetrics.Configuration.Groups).IsEqualTo(RespireMetricGroups.Default);
        EmitCounters();
        RespireTelemetry.StartOperation("GET", "metric.example", 6379, 0).Complete("GET", "metric.example", 6379, 0);
        RespireTelemetry.RecordSubscriptionGap(SubscriptionKind.Sharded, RespireSubscriptionGapReason.Reconnect);
        await Assert.That(capture.Items.Count(item => item.Name.StartsWith("redis.", StringComparison.Ordinal))).IsEqualTo(2);
        await Assert.That(capture.Items.Any(item => item.Name == "db.client.operation.duration")).IsFalse();
        await Assert.That(capture.Items.Any(item => item.Name == "respire.pubsub.delivery.gaps")).IsTrue();
        await Assert.That(RespireTelemetry.CaptureStartTimestamp("GET")).IsEqualTo(0L);
    }

    [Test]
    [Arguments(RespireMetricGroups.Resiliency)]
    [Arguments(RespireMetricGroups.ConnectionBasic)]
    [Arguments(RespireMetricGroups.ConnectionAdvanced)]
    [Arguments(RespireMetricGroups.Command)]
    [Arguments(RespireMetricGroups.ClientSideCaching)]
    [Arguments(RespireMetricGroups.PubSub)]
    [Arguments(RespireMetricGroups.Streaming)]
    [Arguments(RespireMetricGroups.None)]
    [Arguments(RespireMetricGroups.All)]
    public async Task EachGroupSelectsOnlyItsImplementedMeasurements(RespireMetricGroups groups)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = groups });
        using var capture = new Capture();
        foreach (var group in new[] { RespireMetricGroups.Resiliency, RespireMetricGroups.ConnectionBasic,
                     RespireMetricGroups.ConnectionAdvanced, RespireMetricGroups.Command,
                     RespireMetricGroups.ClientSideCaching, RespireMetricGroups.PubSub, RespireMetricGroups.Streaming })
            await Assert.That(RespireTelemetry.IsMetricEnabled(group, RespireTelemetry.OperationDuration)).IsEqualTo((groups & group) == group);
        EmitCounters();
        RespireTelemetry.StartOperation("GET", "metric.example", 6379, 0).Complete("GET", "metric.example", 6379, 0);
        await Assert.That(capture.Items.Count(item => item.Name.StartsWith("redis.client.csc.", StringComparison.Ordinal)))
            .IsEqualTo((groups & RespireMetricGroups.ClientSideCaching) != 0 ? 2 : 0);
        await Assert.That(capture.Items.Count(item => item.Name is "redis.client.maintenance.notifications" or "redis.client.geofailover.failovers"))
            .IsEqualTo((groups & RespireMetricGroups.Resiliency) != 0 ? 2 : 0);
        await Assert.That(capture.Items.Count(item => item.Name == "db.client.operation.duration"))
            .IsEqualTo((groups & RespireMetricGroups.Command) != 0 ? 1 : 0);
        // Group selection never suppresses existing Respire-only instruments.
        await Assert.That(capture.Items.Any(item => item.Name == "respire.failover.endpoint.switches")).IsTrue();
    }

    [Test]
    public async Task ConfigurationOwnsListsAndBlockListWins()
    {
        var allow = new List<string> { "get", "SET", "CLIENT LIST" };
        var block = new[] { "set" };
        using var configuration = new MetricConfigurationScope(new()
        {
            Groups = RespireMetricGroups.Command, CommandAllowList = allow, CommandBlockList = block,
        });
        allow.Clear();
        block[0] = "GET";
        var returned = (string[])RespireMetrics.Configuration.CommandAllowList;
        Array.Fill(returned, "PING");
        using var capture = new Capture();
        foreach (var operation in new[] { "GET", "get", "SET", "PING", "CLIENT LIST" })
            RespireTelemetry.StartOperation(operation, "metric.example", 6379, 0).Complete(operation, "metric.example", 6379, 0);
        var names = capture.Items.Where(item => item.Name == "db.client.operation.duration")
            .Select(item => (string)item.Tags["db.operation.name"]!).ToArray();
        await Assert.That(names).IsEquivalentTo(new[] { "GET", "GET", "CLIENT LIST" });
    }

    [Test]
    [Arguments("")]
    [Arguments(" GET")]
    [Arguments("GET ")]
    [Arguments("CLIENT  LIST")]
    [Arguments("GET\nSET")]
    [Arguments("GET\tSET")]
    [Arguments("GÉT")]
    [Arguments("GET*")]
    [Arguments("GET{key}")]
    public async Task InvalidNamesDoNotReplaceConfiguration(string name)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Command });
        foreach (var allowList in new[] { true, false })
        {
            var options = new RespireMetricsOptions
            {
                CommandAllowList = allowList ? [name] : [], CommandBlockList = allowList ? [] : [name],
            };
            await Assert.That(() => RespireMetrics.Configure(options)).Throws<ArgumentException>();
            await Assert.That(RespireMetrics.Configuration.Groups).IsEqualTo(RespireMetricGroups.Command);
        }
    }

    [Test]
    public async Task NullListsUnknownGroupsAndLongNamesAreRejected()
    {
        using var configuration = new MetricConfigurationScope(new());
        await Assert.That(() => RespireMetrics.Configure(null!)).Throws<ArgumentNullException>();
        await Assert.That(() => RespireMetrics.Configure(new() { CommandAllowList = null! })).Throws<ArgumentNullException>();
        await Assert.That(() => RespireMetrics.Configure(new() { CommandBlockList = [null!] })).Throws<ArgumentException>();
        await Assert.That(() => RespireMetrics.Configure(new() { Groups = (RespireMetricGroups)128 })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RespireMetrics.Configure(new() { CommandAllowList = [new string('A', 65)] })).Throws<ArgumentException>();
    }

    [Test]
    [Arguments("immediate")]
    [Arguments("raw")]
    [Arguments("batch")]
    [Arguments("transaction")]
    [Arguments("blocking")]
    [Arguments("script")]
    [Arguments("fire-and-forget")]
    [Arguments("stream-upload")]
    [Arguments("stream-read")]
    public async Task CommandSelectionIsIndependentOfTracingOnEveryExecutionPath(string kind)
    {
        var command = kind switch { "blocking" => "BLPOP", "script" => "EVALSHA", "stream-read" => "GET", _ => "SET" };
        var operation = kind switch { "batch" => "PIPELINE SET", "transaction" => "MULTI SET", _ => command };
        foreach (var trace in new[] { false, true })
        foreach (var mode in new[] { "disabled", "blocked", "enabled" })
        {
            using var configuration = new MetricConfigurationScope(new()
            {
                Groups = mode == "disabled" ? RespireMetricGroups.Default : RespireMetricGroups.Command,
                CommandAllowList = [command], CommandBlockList = mode == "blocked" ? [command] : [],
            });
            var replies = kind switch
            {
                "transaction" => new[] { FakeRespServer.OkReply, "+QUEUED\r\n"u8.ToArray(), "+QUEUED\r\n"u8.ToArray(), "*2\r\n+OK\r\n+OK\r\n"u8.ToArray() },
                "script" => new[] { "-NOSCRIPT No matching script\r\n"u8.ToArray(), ":1\r\n"u8.ToArray() },
                "blocking" => new[] { "*-1\r\n"u8.ToArray() },
                "stream-read" => new[] { "$3\r\none\r\n"u8.ToArray() },
                _ => new[] { FakeRespServer.OkReply },
            };
            await using var server = new FakeRespServer(3, replies);
            await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
            using var capture = new Capture(trace);
            switch (kind)
            {
                case "immediate": await client.SetAsync("private-key", "private-payload"); break;
                case "raw": using (await client.ExecuteAsync("SET", "private-key", "private-payload")) { } break;
                case "fire-and-forget": await client.ExecuteFireAndForgetAsync("SET", "private-key", "private-payload"); break;
                case "batch":
                    var batch = client.CreateBatch();
                    _ = batch.Set("one", "1"); _ = batch.Set("two", "2");
                    await batch.ExecuteAsync(); break;
                case "transaction":
                    await using (var transaction = client.CreateTransaction())
                    {
                        _ = transaction.Set("one", "1"); _ = transaction.Set("two", "2");
                        await transaction.CommitAsync();
                    }
                    break;
                case "blocking": await client.Lists.LeftPopAsync("private-key", waitFor: Timeout.InfiniteTimeSpan); break;
                case "script": using (await client.Scripts.ExecuteAsync(RespireScript.Create("return 1"))) { } break;
                case "stream-upload":
                    using (var stream = new MemoryStream("private-payload"u8.ToArray()))
                        await client.Strings.SetAsync("private-key", stream, stream.Length);
                    break;
                case "stream-read":
                    await using (var stream = await client.Strings.GetStreamAsync("private-key"))
                        await stream!.CopyToAsync(Stream.Null);
                    break;
            }
            // Streaming EOF wakes the reader before the receive loop invokes its diagnostic callback.
            if (mode == "enabled") await capture.DurationRecorded.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (trace) await capture.ActivityStopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var measurements = capture.Items.Where(item => item.Name == "db.client.operation.duration"
                && Equals(item.Tags.GetValueOrDefault("server.port"), server.Port)).ToArray();
            await Assert.That(measurements.Length).IsEqualTo(mode == "enabled" ? 1 : 0);
            await Assert.That(capture.Activities.Count(item => Equals(item.GetTagItem("server.port"), server.Port))).IsEqualTo(trace ? 1 : 0);
            if (measurements.Length != 0)
            {
                await Assert.That(measurements[0].Tags["db.operation.name"]).IsEqualTo(operation);
                await Assert.That(measurements[0].Tags.ContainsKey("db.stored_procedure.name")).IsFalse();
                await Assert.That(measurements[0].Tags.Values.Any(value => value is string text && text.Contains("private-", StringComparison.Ordinal))).IsFalse();
            }
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DurabilityFiltersIncludeTheAcknowledgement(bool aof)
    {
        var acknowledgement = aof ? "WAITAOF" : "WAIT";
        using var configuration = new MetricConfigurationScope(new()
        {
            Groups = RespireMetricGroups.Command, CommandAllowList = ["SET", acknowledgement], CommandBlockList = [acknowledgement],
        });
        await using var server = new FakeRespServer(3, FakeRespServer.OkReply,
            aof ? "*2\r\n:1\r\n:1\r\n"u8.ToArray() : ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var capture = new Capture(trace: true);
        using var batch = client.CreateBatch();
        _ = batch.Set("one", "1");
        if (aof) await batch.ExecuteAndWaitForAofAsync(true, 1, TimeSpan.FromSeconds(1));
        else await batch.ExecuteAndWaitForReplicationAsync(1, TimeSpan.FromSeconds(1));
        await Assert.That(capture.Items.Any(item => item.Name == "db.client.operation.duration")).IsFalse();
        await Assert.That(capture.Activities.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments("PIPELINE")]
    [Arguments("MULTI")]
    public async Task CompoundFilteringChecksEveryMemberAndEmptyOperation(string prefix)
    {
        using var configuration = new MetricConfigurationScope(new()
        {
            Groups = RespireMetricGroups.Command, CommandAllowList = ["GET", "SET", prefix], CommandBlockList = ["SET"],
        });
        using var capture = new Capture();
        foreach (var commands in new[] { new[] { "GET" }, new[] { "GET", "GET" }, new[] { "GET", "SET" }, Array.Empty<string>() })
        {
            var started = RespireTelemetry.CaptureBatchStartTimestamp(prefix, commands, static command => command);
            await Assert.That(started == 0).IsEqualTo(commands.Contains("SET"));
            var scope = RespireTelemetry.StartBatchOperation(prefix, commands, static command => command, 0, out var operation, started);
            scope.Complete(operation, null, 6379, 0, batchSize: commands.Length == 1 ? null : commands.Length);
        }
        await Assert.That(capture.Items.Count(item => item.Name == "db.client.operation.duration")).IsEqualTo(3);
        RespireMetrics.Configure(new() { Groups = RespireMetricGroups.Command, CommandBlockList = [prefix] });
        await Assert.That(RespireTelemetry.CaptureBatchStartTimestamp(prefix, Array.Empty<string>(), static command => command)).IsEqualTo(0L);
    }

    [Test]
    public async Task InFlightScopesRetainSelectionAndNewScopesUseReplacement()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Command });
        using var capture = new Capture();
        var selected = RespireTelemetry.StartOperation("GET", "metric.example", 6379, 0);
        RespireMetrics.Configure(new() { Groups = RespireMetricGroups.None });
        selected.Complete("GET", "metric.example", 6379, 0);
        RespireTelemetry.StartOperation("GET", "metric.example", 6379, 0).Complete("GET", "metric.example", 6379, 0);
        await Assert.That(capture.Items.Count(item => item.Name == "db.client.operation.duration")).IsEqualTo(1);
    }

    [Test]
    public async Task RawCommandLabelBudgetIsBoundedWithoutChangingTraces()
    {
        var names = new MetricOperationNames(2);
        await Assert.That(names.GetName("get")).IsEqualTo("GET");
        await Assert.That(names.GetName("GET")).IsEqualTo("GET");
        await Assert.That(names.GetName("SET")).IsEqualTo("SET");
        await Assert.That(names.GetName("custom")).IsEqualTo("OTHER");
        await Assert.That(names.GetName(new string('X', 81))).IsEqualTo("OTHER");
        await Assert.That(names.GetName("bad\nname")).IsEqualTo("OTHER");
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Command });
        using var capture = new Capture(trace: true);
        var operation = new string('X', 81);
        RespireTelemetry.StartOperation(operation, "metric.example", 6379, 0, storedProcedureName: "private-script")
            .Complete(operation, "metric.example", 6379, 0, storedProcedureName: "private-script");
        await Assert.That(capture.Activities.Single().GetTagItem("db.operation.name")).IsEqualTo(operation);
        await Assert.That(capture.Items.Single().Tags["db.operation.name"]).IsEqualTo("OTHER");
    }

    [Test]
    [Arguments("disabled")]
    [Arguments("blocked")]
    [Arguments("no-listener")]
    public async Task DisabledMetricsAllocateNothing(string mode)
    {
        using var configuration = new MetricConfigurationScope(new()
        {
            Groups = mode switch { "blocked" => RespireMetricGroups.Command, "no-listener" => RespireMetricGroups.All, _ => RespireMetricGroups.None },
            CommandBlockList = mode == "blocked" ? ["GET"] : [],
        });
        using var capture = mode == "no-listener" ? null : new Capture();
        string[] commands = ["GET", "GET"];
        for (var i = 0; i < 20; i++) MeasureDisabled(commands);
        MeasurePositiveControl();
        var bytes = AllocationMeasurement.WithoutConcurrentGc(() => (MeasureDisabled(commands), MeasurePositiveControl()));
        await Assert.That(bytes.Item1).IsEqualTo(0L);
        await Assert.That(bytes.Item2 > 0).IsTrue();
        if (capture is not null) await Assert.That(capture.Items.IsEmpty).IsTrue();
        await Assert.That(RespireTelemetry.IsOperationEnabled("GET")).IsFalse();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureDisabled(string[] commands)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            RespireTelemetry.RecordCacheRequest(true);
            RespireTelemetry.RecordCacheEvictions(1, "ttl");
            RespireTelemetry.RecordMaintenanceNotification("metric.example", 6379, "MOVING");
            var started = RespireTelemetry.CaptureStartTimestamp("GET");
            RespireTelemetry.StartOperation("GET", "metric.example", 6379, 0, started: started)
                .Complete("GET", "metric.example", 6379, 0);
            var batchStarted = RespireTelemetry.CaptureBatchStartTimestamp("PIPELINE", commands, static command => command);
            RespireTelemetry.StartBatchOperation("PIPELINE", commands, static command => command, 0, out var operation, batchStarted)
                .Complete(operation, null, 6379, 0);
            RespireTelemetry.RecordUnroutedBatchFailure("MULTI", commands, static command => command, 0, 1, DisabledError);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static readonly Exception DisabledError = new InvalidOperationException();
    private static object? _allocationAnchor;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasurePositiveControl()
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) _allocationAnchor = new byte[37];
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static void EmitCounters()
    {
        RespireTelemetry.RecordCacheRequest(true);
        RespireTelemetry.RecordCacheEvictions(1, "ttl");
        RespireTelemetry.RecordMaintenanceNotification("metric.example", 6379, "MOVING");
        RespireTelemetry.RecordFailoverSwitch(new("first"), new("second"), RespireFailoverSwitchReasons.ActiveEndpointUnhealthy);
    }

    private sealed record Item(string Name, Dictionary<string, object?> Tags);

    private sealed class Capture : IDisposable
    {
        private readonly MeterListener _meter = new();
        private readonly ActivityListener? _activity;
        internal ConcurrentQueue<Item> Items { get; } = new();
        internal ConcurrentQueue<Activity> Activities { get; } = new();
        internal TaskCompletionSource DurationRecorded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ActivityStopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Capture(bool trace = false)
        {
            _meter.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Respire") listener.EnableMeasurementEvents(instrument);
            };
            _meter.SetMeasurementEventCallback<long>((instrument, _, tags, _) => Add(instrument, tags));
            _meter.SetMeasurementEventCallback<double>((instrument, _, tags, _) => Add(instrument, tags));
            _meter.Start();
            if (trace)
            {
                _activity = new()
                {
                    ShouldListenTo = static source => source.Name == "Respire",
                    Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                    ActivityStopped = activity =>
                    {
                        Activities.Enqueue(activity);
                        ActivityStopped.TrySetResult();
                    },
                };
                ActivitySource.AddActivityListener(_activity);
            }
        }

        private void Add(Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            Items.Enqueue(new(instrument.Name, tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value)));
            if (instrument.Name == "db.client.operation.duration") DurationRecorded.TrySetResult();
        }

        public void Dispose()
        {
            _activity?.Dispose();
            _meter.Dispose();
        }
    }
}
