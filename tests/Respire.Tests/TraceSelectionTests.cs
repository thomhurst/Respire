using System.Diagnostics;
using System.Diagnostics.Metrics;
using Respire.Internal;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

[NotInParallel]
public class TraceSelectionTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FilteredCommandStillExecutesAndRecordsDuration(bool throws)
    {
        using var configuration = new Configuration(name => throws ? throw new InvalidOperationException() : name != "PING");
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var capture = new Capture();
        await client.PingAsync();
        await Assert.That(server.ReceivedCommands).Contains("PING");
        await Assert.That(capture.Activities.Count).IsEqualTo(0);
        await Assert.That(capture.Durations).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FilterRunsBeforeSamplingAndPreservesFailureMetrics(bool throws)
    {
        using var configuration = new Configuration(name => throws ? throw new InvalidOperationException() : name == "GET");
        using var capture = new Capture();
        Emit("SET", new InvalidOperationException());
        await Assert.That(capture.SampleCalls).IsEqualTo(0);
        await Assert.That(capture.Activities.Count).IsEqualTo(0);
        await Assert.That(capture.Durations).IsEqualTo(1);
    }

    [Test]
    public async Task NamesIncludeSubcommandsAndExcludeScriptIdentifiers()
    {
        var names = new List<string>();
        using var configuration = new Configuration(name => { names.Add(name); return name is "CLIENT LIST" or "EVALSHA"; });
        using var capture = new Capture();
        Emit("CLIENT LIST");
        RespireTelemetry.StartOperation("EVALSHA", "localhost", 6379, 0, storedProcedureName: "digest")
            .Complete("EVALSHA", "localhost", 6379, 0, storedProcedureName: "digest");
        Emit("CUSTOM.COMMAND");
        await Assert.That(names.SequenceEqual(["CLIENT LIST", "EVALSHA", "CUSTOM.COMMAND"])).IsTrue();
        await Assert.That(capture.Activities.Select(activity => activity.DisplayName).SequenceEqual(["CLIENT LIST", "EVALSHA digest"])).IsTrue();
    }

    [Test]
    [Arguments("PIPELINE", false)]
    [Arguments("PIPELINE", true)]
    [Arguments("MULTI", false)]
    [Arguments("MULTI", true)]
    [Arguments("WAIT", false)]
    [Arguments("WAIT", true)]
    [Arguments("WAITAOF", false)]
    [Arguments("WAITAOF", true)]
    public async Task BatchRequiresEveryMemberIncludingDurabilityCommand(string prefix, bool endpoint)
    {
        var names = new List<string>();
        using var configuration = new Configuration(name => { names.Add(name); return name == "GET"; });
        using var capture = new Capture();
        Batch(prefix, ["GET", "SET"], endpoint);
        Batch(prefix, ["GET", "GET"], endpoint);
        await Assert.That(capture.Activities.Count).IsEqualTo(prefix is "WAIT" or "WAITAOF" ? 0 : 1);
        await Assert.That(capture.Durations).IsEqualTo(2);
        await Assert.That(names.Contains(prefix)).IsEqualTo(prefix is "WAIT" or "WAITAOF");
        await Assert.That(names.All(name => name is "GET" or "SET" or "WAIT" or "WAITAOF")).IsTrue();
    }

    [Test]
    public async Task EmptyAndSingleMemberBatchesUsePrefixAndMemberRespectively()
    {
        var names = new List<string>();
        using var configuration = new Configuration(name => { names.Add(name); return name == "GET"; });
        using var capture = new Capture();
        Batch("PIPELINE", []);
        Batch("MULTI", ["GET"], endpoint: true);
        await Assert.That(names.SequenceEqual(["PIPELINE", "GET"])).IsTrue();
        await Assert.That(capture.Activities.Single().DisplayName).IsEqualTo("GET");
        await Assert.That(capture.Durations).IsEqualTo(2);
    }

    [Test]
    public async Task FilteredPipelineAndTransactionStillExecute()
    {
        using var configuration = new Configuration(static name => name != "SET");
        await using var pipelineServer = new FakeRespServer(FakeRespServer.OkReply);
        await using var transactionServer = new FakeRespServer(
            FakeRespServer.OkReply, "+QUEUED\r\n"u8.ToArray(), "+QUEUED\r\n"u8.ToArray(),
            "*2\r\n+OK\r\n+OK\r\n"u8.ToArray());
        await using var pipelineClient = await FakeRespServer.ConnectClientAsync(pipelineServer.Port);
        await using var transactionClient = await FakeRespServer.ConnectClientAsync(transactionServer.Port);
        using var capture = new Capture();
        var batch = pipelineClient.CreateBatch();
        _ = batch.Set("one", "1");
        _ = batch.Set("two", "2");
        await batch.ExecuteAsync();
        var transaction = transactionClient.CreateTransaction();
        _ = transaction.Set("one", "1");
        _ = transaction.Set("two", "2");
        await transaction.CommitAsync();
        await Assert.That(pipelineServer.ReceivedCommands).Contains("SET one 1");
        await Assert.That(transactionServer.ReceivedCommands).Contains("EXEC");
        await Assert.That(capture.Activities.Count).IsEqualTo(0);
        await Assert.That(capture.Durations).IsEqualTo(2);
    }

    [Test]
    public async Task UnroutedCommandFailureIsFilteredWithoutLosingDuration()
    {
        using var configuration = new Configuration(static _ => false);
        using var capture = new Capture();
        var started = RespireTelemetry.CaptureOperationStart("GET");
        RespireTelemetry.RecordUnroutedFailure("GET", 0, started, new OperationCanceledException());
        await Assert.That(capture.Activities.Count).IsEqualTo(0);
        await Assert.That(capture.Durations).IsEqualTo(1);
    }

    [Test]
    public async Task UnroutedBatchFailureUsesMemberSelection()
    {
        using var configuration = new Configuration(static name => name != "SET");
        using var capture = new Capture();
        string[] commands = ["GET", "SET"];
        var started = RespireTelemetry.CaptureBatchStart("PIPELINE", commands, static name => name);
        RespireTelemetry.RecordUnroutedBatchFailure("PIPELINE", commands, static name => name, 0, started, new TimeoutException());
        await Assert.That(capture.Activities.Count).IsEqualTo(0);
        await Assert.That(capture.Durations).IsEqualTo(1);
    }

    [Test]
    public async Task NoListenerDoesNotCallPredicate()
    {
        var calls = 0;
        using var configuration = new Configuration(_ => { calls++; return true; });
        await Assert.That(RespireTelemetry.Source.HasListeners()).IsFalse();
        Emit("GET");
        Batch("PIPELINE", ["GET", "SET"]);
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task ConfigurationChangesAffectNewSpansAndBatchUsesOnePredicate()
    {
        using var configuration = new Configuration(_ => { RespireTracing.Configure(new() { CommandFilter = static _ => false }); return true; });
        using var capture = new Capture();
        Batch("PIPELINE", ["GET", "GET"]);
        Emit("GET");
        RespireTracing.Configure(new());
        Emit("GET");
        await Assert.That(capture.Activities.Count).IsEqualTo(2);
        await Assert.That(RespireTracing.Configuration.CommandFilter).IsNull();
    }

    [Test]
    public async Task TracingWorksWithMetricsDisabledAndPreservesExistingSpan()
    {
        using var configuration = new Configuration(null);
        RespireMetrics.Configure(new() { Groups = RespireMetricGroups.None });
        using var capture = new Capture();
        var operation = RespireTelemetry.StartOperation("GET", "localhost", 6379, 0);
        RespireTracing.Configure(new() { CommandFilter = static _ => false });
        operation.Complete("GET", "localhost", 6379, 0);
        Emit("SET");
        await Assert.That(capture.Activities.Count).IsEqualTo(1);
        await Assert.That(capture.Durations).IsEqualTo(0);
    }

    private static void Emit(string name, Exception? error = null)
        => RespireTelemetry.StartOperation(name, "localhost", 6379, 0).Complete(name, "localhost", 6379, 0, error: error);

    private static void Batch(string prefix, string[] commands, bool endpoint = false)
    {
        string name;
        var scope = endpoint
            ? RespireTelemetry.StartBatchOperation(prefix, commands, static name => name, new RespireEndpoint("localhost"), 0, out name)
            : RespireTelemetry.StartBatchOperation(prefix, commands, static name => name, 0, out name);
        scope.Complete(name, endpoint ? "localhost" : null, 6379, 0, batchSize: commands.Length);
    }

    private sealed class Configuration : IDisposable
    {
        private readonly RespireTracingOptions _tracing = RespireTracing.Configuration;
        private readonly RespireMetricsOptions _metrics = RespireMetrics.Configuration;

        public Configuration(Func<string, bool>? filter)
        {
            RespireTracing.Configure(new() { CommandFilter = filter });
            RespireMetrics.Configure(new() { Groups = RespireMetricGroups.Command });
        }

        public void Dispose()
        {
            RespireTracing.Configure(_tracing);
            RespireMetrics.Configure(_metrics);
        }
    }

    private sealed class Capture : IDisposable
    {
        private readonly ActivityListener _activities;
        private readonly MeterListener _metrics = new();
        public List<Activity> Activities { get; } = [];
        public int SampleCalls;
        public int Durations;

        public Capture()
        {
            _activities = new ActivityListener
            {
                ShouldListenTo = static source => source.Name == "Respire",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => { SampleCalls++; return ActivitySamplingResult.AllDataAndRecorded; },
                ActivityStopped = activity => Activities.Add(activity),
            };
            ActivitySource.AddActivityListener(_activities);
            _metrics.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Respire" && instrument.Name == "db.client.operation.duration")
                    listener.EnableMeasurementEvents(instrument);
            };
            _metrics.SetMeasurementEventCallback<double>((_, _, _, _) => Interlocked.Increment(ref Durations));
            _metrics.Start();
        }

        public void Dispose()
        {
            _activities.Dispose();
            _metrics.Dispose();
        }
    }
}
