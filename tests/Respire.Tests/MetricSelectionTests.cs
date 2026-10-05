using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

[NotInParallel]
public class MetricSelectionTests
{
    [Test]
    [Arguments("GET", "blocked")]
    [Arguments("GET", "excluded")]
    [Arguments("GET", "enabled")]
    [Arguments("HGET", "blocked")]
    [Arguments("HGET", "excluded")]
    [Arguments("HGET", "enabled")]
    [Arguments("DUMP", "blocked")]
    [Arguments("DUMP", "excluded")]
    [Arguments("DUMP", "enabled")]
    public async Task ByteResponseSelectionPreservesDirectOwnership(string operation, string mode)
    {
        foreach (var trace in new[] { false, true })
        {
            using var configuration = new MetricConfigurationScope(new()
            {
                Groups = RespireMetricGroups.Command,
                CommandAllowList = mode == "excluded" ? ["SET"] : [operation],
                CommandBlockList = mode == "blocked" ? [operation] : [],
            });
            await using var server = new FakeRespServer
                { SuppressReply = command => command.StartsWith(operation + " ", StringComparison.Ordinal) };
            await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
            using var capture = new Capture(trace);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var pending = operation switch
            {
                "GET" => client.GetBytesAsync("key", deadline.Token),
                "HGET" => client.Hashes.GetBytesAsync("key", "field", deadline.Token),
                _ => client.Keys.DumpAsync("key", deadline.Token),
            };
            while (!server.ReceivedCommands.Any(command => command.StartsWith(operation + " ", StringComparison.Ordinal)))
                await Task.Delay(1, deadline.Token);
            await Assert.That(Inflight(client.Core.Multiplexer.GetConnection()).TryPeek(out var head)).IsTrue();
            await Assert.That(head is BytesPendingResponseSource).IsEqualTo(!trace && mode != "enabled");
            byte[] expected = [0, 255, 97, 98];
            await server.SendRawAsync([.. "$4\r\n"u8, .. expected, 13, 10]);
            var bytes = await pending;
            await Assert.That(bytes!.AsSpan().SequenceEqual(expected)).IsTrue();
            await Assert.That(capture.Items.Count(item => item.Name == "db.client.operation.duration")).IsEqualTo(mode == "enabled" ? 1 : 0);
            await Assert.That(capture.Activities.Count).IsEqualTo(trace ? 1 : 0);
        }
    }

    [Test]
    [Arguments("blocked")]
    [Arguments("excluded")]
    [Arguments("enabled")]
    public async Task PinnedResponseSelectionAvoidsUnneededTelemetryWrapper(string mode)
    {
        foreach (var trace in new[] { false, true })
        {
            using var configuration = new MetricConfigurationScope(new()
            {
                Groups = RespireMetricGroups.Command,
                CommandAllowList = mode == "excluded" ? ["SET"] : ["GET"],
                CommandBlockList = mode == "blocked" ? ["GET"] : [],
            });
            await using var server = new FakeRespServer
                { SuppressReply = command => command.StartsWith("GET ", StringComparison.Ordinal) };
            await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
            var connection = client.Core.Multiplexer.GetConnection();
            using var capture = new Capture(trace);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var pending = client.SendOnPinnedConnectionAsync("GET", connection, new Cmd1(Verbs.Get, "key"), deadline.Token);
            while (!server.ReceivedCommands.Contains("GET key")) await Task.Delay(1, deadline.Token);
            await Assert.That(Inflight(connection).TryPeek(out var head)).IsTrue();
            await Assert.That(ReferenceEquals(ResponseSource(ref pending), head)).IsEqualTo(!trace && mode != "enabled");
            await server.SendRawAsync("$5\r\nhello\r\n"u8.ToArray());
            using var value = await pending;
            await Assert.That(value.AsString()).IsEqualTo("hello");
            await Assert.That(capture.Items.Count(item => item.Name == "db.client.operation.duration")).IsEqualTo(mode == "enabled" ? 1 : 0);
            await Assert.That(capture.Activities.Count).IsEqualTo(trace ? 1 : 0);
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_inflight")]
    private static extern ref InflightRing Inflight(RespireConnection connection);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_obj")]
    private static extern ref object? ResponseSource(ref ValueTask<RespValue> value);

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
        await Assert.That(RespireTelemetry.CaptureOperationStart("GET").Timestamp).IsEqualTo(0L);
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
    [Arguments("GET")]
    [Arguments("get")]
    [Arguments("CLIENT LIST")]
    [Arguments("CUSTOM.COMMAND")]
    [Arguments("CUSTOM_COMMAND")]
    [Arguments("CUSTOM-COMMAND")]
    [Arguments("COMMAND42")]
    [Arguments("XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX")]
    public async Task AcceptedFiltersRetainCanonicalMetricLabels(string operation)
    {
        using var configuration = new MetricConfigurationScope(new()
            { Groups = RespireMetricGroups.Command, CommandAllowList = [operation] });
        using var capture = new Capture();
        var names = new MetricOperationNames();
        await Assert.That(RespireTelemetry.CaptureOperationStart(operation).MetricEnabled).IsTrue();
        await Assert.That(names.GetName(operation)).IsEqualTo(operation.ToUpperInvariant());
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
            var started = RespireTelemetry.CaptureBatchStart(prefix, commands, static command => command);
            await Assert.That(started.Timestamp == 0).IsEqualTo(commands.Contains("SET"));
            var scope = RespireTelemetry.StartBatchOperation(prefix, commands, static command => command, 0, out var operation, started);
            scope.Complete(operation, null, 6379, 0, batchSize: commands.Length == 1 ? null : commands.Length);
        }
        await Assert.That(capture.Items.Count(item => item.Name == "db.client.operation.duration")).IsEqualTo(3);
        RespireMetrics.Configure(new() { Groups = RespireMetricGroups.Command, CommandBlockList = [prefix] });
        await Assert.That(RespireTelemetry.CaptureBatchStart(prefix, Array.Empty<string>(), static command => command).Timestamp).IsEqualTo(0L);
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
    [Arguments("operation")]
    [Arguments("PIPELINE")]
    [Arguments("MULTI")]
    [Arguments("WAIT")]
    [Arguments("unrouted-operation")]
    [Arguments("unrouted-batch")]
    public async Task CapturedSelectionSurvivesConfigurationChangesBeforeScopeCreation(string kind)
    {
        foreach (var trace in new[] { false, true })
        foreach (var enabled in new[] { false, true })
        foreach (var filter in new[] { false, true })
        {
            using var configuration = new MetricConfigurationScope(new()
            {
                Groups = filter || enabled ? RespireMetricGroups.Command : RespireMetricGroups.None,
                CommandBlockList = filter && !enabled ? ["GET"] : [],
            });
            using var capture = new Capture(trace);
            string[] commands = ["GET", "GET"];
            var compound = kind is not ("operation" or "unrouted-operation");
            var prefix = kind == "unrouted-batch" ? "PIPELINE" : kind;
            var started = compound
                ? RespireTelemetry.CaptureBatchStart(prefix, commands, static command => command)
                : RespireTelemetry.CaptureOperationStart("GET");
            RespireMetrics.Configure(new()
            {
                Groups = filter || !enabled ? RespireMetricGroups.Command : RespireMetricGroups.None,
                CommandBlockList = filter && enabled ? ["GET"] : [],
            });
            if (kind == "unrouted-operation")
                RespireTelemetry.RecordUnroutedFailure("GET", 0, started, DisabledError);
            else if (kind == "unrouted-batch")
                RespireTelemetry.RecordUnroutedBatchFailure(prefix, commands, static command => command, 0, started, DisabledError);
            else if (compound)
                RespireTelemetry.StartBatchOperation(prefix, commands, static command => command, "metric.example", 6379, 0,
                    out var operation, started).Complete(operation, "metric.example", 6379, 0, batchSize: commands.Length);
            else
                RespireTelemetry.StartOperation("GET", "metric.example", 6379, 0, started: started)
                    .Complete("GET", "metric.example", 6379, 0);
            await Assert.That(capture.Items.Count(item => item.Name == "db.client.operation.duration")).IsEqualTo(enabled ? 1 : 0);
            await Assert.That(capture.Activities.Count).IsEqualTo(trace ? 1 : 0);
        }
    }

    [Test]
    [Arguments("blocking", false)]
    [Arguments("blocking", true)]
    [Arguments("stream-upload", false)]
    [Arguments("stream-upload", true)]
    [Arguments("durability", false)]
    [Arguments("durability", true)]
    public async Task DedicatedConnectionAcquisitionRetainsMetricSelection(string kind, bool failHandshake)
    {
        foreach (var enabled in new[] { false, true })
        {
            using var configuration = new MetricConfigurationScope(new()
            {
                Groups = enabled ? RespireMetricGroups.Command : RespireMetricGroups.None,
            });
            await using var server = new FakeRespServer(3, FakeRespServer.OkReply);
            await using var client = await RespireClient.ConnectAsync(new RespireOptions
            {
                Protocol = RespProtocol.Resp2,
                Endpoints = { new("127.0.0.1", server.Port) }, Connections = 1, ClientName = "metric-selection",
            });
            var acquisitions = 0;
            server.ReplyOverride = (id, command) =>
            {
                if (id > 0 && command == "CLIENT SETNAME metric-selection")
                {
                    Interlocked.Increment(ref acquisitions);
                    RespireMetrics.Configure(new() { Groups = enabled ? RespireMetricGroups.None : RespireMetricGroups.Command });
                    return failHandshake ? "-ERR acquisition failed\r\n"u8.ToArray() : FakeRespServer.OkReply;
                }
                return command.StartsWith("BLPOP ", StringComparison.Ordinal) ? "*-1\r\n"u8.ToArray()
                    : command.StartsWith("WAIT ", StringComparison.Ordinal) ? ":1\r\n"u8.ToArray() : FakeRespServer.OkReply;
            };
            using var capture = new Capture();
            if (failHandshake)
                await Assert.That(Execute).Throws<RespireConnectionException>();
            else
                await Execute();
            await Assert.That(acquisitions).IsEqualTo(1);
            await Assert.That(capture.Items.Count(item => item.Name == "db.client.operation.duration")).IsEqualTo(enabled ? 1 : 0);

            async Task Execute()
            {
                if (kind == "blocking")
                    await client.Lists.LeftPopAsync("private-key", waitFor: Timeout.InfiniteTimeSpan);
                else if (kind == "stream-upload")
                {
                    using var stream = new MemoryStream("private-payload"u8.ToArray());
                    await client.Strings.SetAsync("private-key", stream, stream.Length);
                }
                else
                {
                    using var batch = client.CreateBatch();
                    _ = batch.Set("private-key", "private-payload");
                    await batch.ExecuteAndWaitForReplicationAsync(1, TimeSpan.FromSeconds(1));
                }
            }
        }
    }

    [Test]
    [Arguments("blocking", false)]
    [Arguments("blocking", true)]
    [Arguments("stream-upload", false)]
    [Arguments("stream-upload", true)]
    [Arguments("script", false)]
    [Arguments("script", true)]
    [Arguments("tracked-script", false)]
    [Arguments("tracked-script", true)]
    public async Task ClusterAcquisitionRetainsMetricSelection(string kind, bool failHandshake)
    {
        foreach (var enabled in new[] { false, true })
        {
            using var configuration = new MetricConfigurationScope(new()
            {
                Groups = enabled ? RespireMetricGroups.Command : RespireMetricGroups.None,
            });
            var acquisitions = 0;
            await using var server = new FakeRespServer(3, FakeRespServer.OkReply)
            {
                ReplyOverride = (_, command) =>
                {
                    if (command == "CLIENT SETNAME cluster-metric-selection")
                    {
                        Interlocked.Increment(ref acquisitions);
                        RespireMetrics.Configure(new() { Groups = enabled ? RespireMetricGroups.None : RespireMetricGroups.Command });
                        return failHandshake ? "-ERR acquisition failed\r\n"u8.ToArray() : FakeRespServer.OkReply;
                    }
                    return command == "CLUSTER SLOTS" ? "*0\r\n"u8.ToArray()
                        : command.StartsWith("BLPOP ", StringComparison.Ordinal) ? "*-1\r\n"u8.ToArray()
                        : command.StartsWith("EVALSHA ", StringComparison.Ordinal) ? ":1\r\n"u8.ToArray() : FakeRespServer.OkReply;
                },
            };
            await using var client = RespireClient.Create(new RespireOptions
            {
                Protocol = RespProtocol.Resp2, UseCluster = true, Connections = 1,
                Endpoints = [new("127.0.0.1", server.Port)], ClientName = "cluster-metric-selection",
            });
            using var capture = new Capture();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            if (failHandshake) await Assert.That(Execute).Throws<RespireConnectionException>();
            else await Execute();
            await Assert.That(acquisitions > 0).IsTrue();
            var measurements = capture.Items.Where(item => item.Name == "db.client.operation.duration").ToArray();
            await Assert.That(measurements.Length).IsEqualTo(enabled ? 1 : 0);
            if (enabled)
                await Assert.That(measurements[0].Tags["db.operation.name"])
                    .IsEqualTo(kind switch { "blocking" => "BLPOP", "script" or "tracked-script" => "EVALSHA", _ => "SET" });

            async Task Execute()
            {
                if (kind == "blocking")
                    await client.Lists.LeftPopAsync("private-key", waitFor: Timeout.InfiniteTimeSpan, cancellationToken: deadline.Token);
                else if (kind == "stream-upload")
                {
                    using var stream = new MemoryStream("private-payload"u8.ToArray());
                    await client.Strings.SetAsync("private-key", stream, stream.Length, cancellationToken: deadline.Token);
                }
                else if (kind == "tracked-script")
                {
                    var execution = await client.StartTrackedScriptExecutionAsync(RespireScript.Create("return 1"),
                        [], [], deadline.Token, captureSendTimestampOnly: true);
                    using var result = await execution.Response;
                }
                else
                    using (await client.Scripts.ExecuteAsync(RespireScript.Create("return 1"), cancellationToken: deadline.Token)) { }
            }
        }
    }

    [Test]
    [Arguments(false, false, false)]
    [Arguments(false, true, false)]
    [Arguments(true, false, false)]
    [Arguments(true, true, false)]
    [Arguments(false, false, true)]
    [Arguments(false, true, true)]
    [Arguments(true, false, true)]
    [Arguments(true, true, true)]
    public async Task ClusterScriptFallbackRetainsOneLogicalMeasurement(bool readOnly, bool failFallback, bool tracked)
    {
        var script = RespireScript.Create("return 1", readOnly);
        foreach (var trace in new[] { false, true })
        foreach (var mode in new[] { "all", "allow-initial", "block-initial", "allow-fallback", "enable-between", "disable-between" })
        {
            using var configuration = new MetricConfigurationScope(new()
            {
                Groups = mode == "enable-between" ? RespireMetricGroups.None : RespireMetricGroups.Command,
                CommandAllowList = mode == "allow-initial" ? [script.EvalShaOperation]
                    : mode == "allow-fallback" ? [script.EvalOperation] : [],
                CommandBlockList = mode == "block-initial" ? [script.EvalShaOperation] : [],
            });
            await using var server = new FakeRespServer(3, FakeRespServer.OkReply)
            {
                ReplyOverride = (_, command) =>
                {
                    if (command.StartsWith(script.EvalShaOperation + " ", StringComparison.Ordinal))
                    {
                        if (mode is "enable-between" or "disable-between")
                            RespireMetrics.Configure(new() { Groups = mode == "enable-between" ? RespireMetricGroups.Command : RespireMetricGroups.None });
                        return "-NOSCRIPT No matching script\r\n"u8.ToArray();
                    }
                    return command == "CLUSTER SLOTS" ? "*0\r\n"u8.ToArray()
                        : command.StartsWith(script.EvalOperation + " ", StringComparison.Ordinal)
                            ? failFallback ? "-ERR script failed\r\n"u8.ToArray() : ":1\r\n"u8.ToArray()
                            : FakeRespServer.OkReply;
                },
            };
            await using var client = await RespireClient.ConnectAsync(new RespireOptions
            {
                Protocol = RespProtocol.Resp2, UseCluster = true, Connections = 1,
                Endpoints = [new("127.0.0.1", server.Port)],
            });
            using var capture = new Capture(trace);
            if (failFallback)
                await Assert.That(async () => { using var result = await Execute(); }).Throws<RespireServerException>();
            else
                using (var result = await Execute()) await Assert.That(result.AsInteger()).IsEqualTo(1L);
            var enabled = mode is "all" or "allow-initial" or "disable-between";
            var measurements = capture.Items.Where(item => item.Name == "db.client.operation.duration").ToArray();
            await Assert.That(measurements.Length).IsEqualTo(enabled ? 1 : 0);
            if (enabled)
            {
                await Assert.That(measurements[0].Tags["db.operation.name"]).IsEqualTo(script.EvalShaOperation);
                await Assert.That(measurements[0].Tags.ContainsKey("error.type")).IsEqualTo(failFallback);
            }
            await Assert.That(capture.Activities.Count).IsEqualTo(trace ? 1 : 0);
            if (trace)
            {
                var activity = capture.Activities.Single();
                await Assert.That(activity.GetTagItem("db.operation.name")).IsEqualTo(script.EvalShaOperation);
                await Assert.That(activity.Status == ActivityStatusCode.Error).IsEqualTo(failFallback);
            }
            await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith(script.EvalShaOperation + " ", StringComparison.Ordinal))).IsEqualTo(1);
            await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith(script.EvalOperation + " ", StringComparison.Ordinal))).IsEqualTo(1);

            async ValueTask<RespireResult> Execute()
            {
                if (!tracked) return await client.Scripts.ExecuteAsync(script);
                var execution = await client.StartTrackedScriptExecutionAsync(script, [], [], default, captureSendTimestampOnly: true);
                return await execution.Response;
            }
        }
    }

    [Test]
    [Arguments("PIPELINE")]
    [Arguments("MULTI")]
    [Arguments("WAIT")]
    [Arguments("WAITAOF")]
    public async Task UnfilteredBatchSelectionDoesNotReadMemberNames(string prefix)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Command });
        using var capture = new Capture();
        var reads = 0;
        var started = RespireTelemetry.CaptureBatchStart(prefix, new[] { "GET", "SET" }, command =>
        {
            reads++;
            return command;
        });
        await Assert.That(started.MetricEnabled).IsTrue();
        await Assert.That(reads).IsEqualTo(0);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task ClusterScriptSamplingUsesTheAcquiredEndpoint(bool tracked, bool failHandshake)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.None });
        await using var server = new FakeRespServer(3, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "CLIENT SETNAME script-sampling" when failHandshake => "-ERR handshake failed\r\n"u8.ToArray(),
                "CLUSTER SLOTS" => "*0\r\n"u8.ToArray(),
                _ when command.StartsWith("EVALSHA ", StringComparison.Ordinal) => "-NOSCRIPT absent\r\n"u8.ToArray(),
                _ when command.StartsWith("EVAL ", StringComparison.Ordinal) => ":1\r\n"u8.ToArray(),
                _ => FakeRespServer.OkReply,
            },
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, Connections = 1,
            Endpoints = [new("127.0.0.1", server.Port)], ClientName = "script-sampling",
        });
        var sampled = new ConcurrentQueue<Dictionary<string, object?>>();
        var completed = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "Respire",
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
            {
                sampled.Enqueue(options.Tags!.ToDictionary(pair => pair.Key, pair => pair.Value));
                return ActivitySamplingResult.AllDataAndRecorded;
            },
            ActivityStopped = completed.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (failHandshake) await Assert.That(Execute).Throws<RespireConnectionException>();
        else await Execute();
        await Assert.That(sampled.Count).IsEqualTo(1);
        await Assert.That(completed.Count).IsEqualTo(1);
        var tags = sampled.Single();
        await Assert.That(tags["db.operation.name"]).IsEqualTo("EVALSHA");
        await Assert.That(tags.ContainsKey("server.address")).IsEqualTo(!failHandshake);
        await Assert.That(tags.ContainsKey("server.port")).IsEqualTo(!failHandshake);
        if (!failHandshake)
        {
            await Assert.That(tags["server.address"]).IsEqualTo("127.0.0.1");
            await Assert.That(tags["server.port"]).IsEqualTo(server.Port);
        }
        await Assert.That(completed.Single().Status == ActivityStatusCode.Error).IsEqualTo(failHandshake);

        async Task Execute()
        {
            var script = RespireScript.Create("return 1");
            if (tracked)
            {
                var execution = await client.StartTrackedScriptExecutionAsync(script, [], [], deadline.Token,
                    captureSendTimestampOnly: true);
                using var result = await execution.Response;
            }
            else
                using (await client.Scripts.ExecuteAsync(script, cancellationToken: deadline.Token)) { }
        }
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
            var started = RespireTelemetry.CaptureOperationStart("GET");
            RespireTelemetry.StartOperation("GET", "metric.example", 6379, 0, started: started)
                .Complete("GET", "metric.example", 6379, 0);
            var batchStarted = RespireTelemetry.CaptureBatchStart("PIPELINE", commands, static command => command);
            RespireTelemetry.StartBatchOperation("PIPELINE", commands, static command => command, 0, out var operation, batchStarted)
                .Complete(operation, null, 6379, 0);
            RespireTelemetry.RecordUnroutedBatchFailure("MULTI", commands, static command => command, 0, new(1, false), DisabledError);
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
