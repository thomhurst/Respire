using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using Respire.Internal;
using Respire.Serialization;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class TelemetryTests
{
    [Test]
    public async Task Command_EmitsRedisSpanAndStableDurationMetric()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply, FakeRespServer.PongReply);
        using var capture = new TelemetryCapture();
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            Database = 4,
        });

        await client.PingAsync();

        var activity = capture.SingleActivity("PING", server.Port);
        var tags = Tags(activity);
        await Assert.That(activity.Kind).IsEqualTo(ActivityKind.Client);
        await Assert.That(activity.Status).IsEqualTo(ActivityStatusCode.Unset);
        await Assert.That(tags["db.system.name"]).IsEqualTo("redis");
        await Assert.That(tags["db.namespace"]).IsEqualTo("4");
        await Assert.That(tags["db.operation.name"]).IsEqualTo("PING");
        await Assert.That(tags["server.address"]).IsEqualTo("127.0.0.1");
        await Assert.That(tags["server.port"]).IsEqualTo(server.Port);
        await Assert.That(tags["network.peer.address"]).IsEqualTo("127.0.0.1");
        await Assert.That(tags["network.peer.port"]).IsEqualTo(server.Port);
        await Assert.That(tags.ContainsKey("error.type")).IsFalse();

        var measurement = capture.SingleMeasurement("PING", server.Port);
        await Assert.That(measurement.InstrumentName).IsEqualTo("db.client.operation.duration");
        await Assert.That(measurement.Unit).IsEqualTo("s");
        await Assert.That(measurement.Value > 0).IsTrue();
        await Assert.That(measurement.Tags["db.system.name"]).IsEqualTo("redis");
        await Assert.That(measurement.Tags["db.namespace"]).IsEqualTo("4");
        await Assert.That(measurement.Tags.ContainsKey("error.type")).IsFalse();
        await Assert.That(capture.HistogramBucketBoundaries!.SequenceEqual(
            new[] { 0.001, 0.005, 0.01, 0.05, 0.1, 0.5, 1.0, 5.0, 10.0 })).IsTrue();

        var creationTags = capture.CreationTags.Single(tags => tags.GetValueOrDefault("server.port") as int? == server.Port);
        await Assert.That(creationTags["db.system.name"]).IsEqualTo("redis");
        await Assert.That(creationTags["db.namespace"]).IsEqualTo("4");
        await Assert.That(creationTags["db.operation.name"]).IsEqualTo("PING");
        await Assert.That(creationTags["server.address"]).IsEqualTo("127.0.0.1");
    }

    [Test]
    public async Task FireAndForget_EmitsCommandTelemetry()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        using var capture = new TelemetryCapture();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await client.ExecuteFireAndForgetAsync("SET", "key", "value");

        var activity = capture.SingleActivity("SET", server.Port);
        await Assert.That(activity.Status).IsEqualTo(ActivityStatusCode.Unset);
        _ = capture.SingleMeasurement("SET", server.Port);
    }

    [Test]
    public async Task FireAndForget_CapturesStoredProcedureNames()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply, FakeRespServer.OkReply);
        using var capture = new TelemetryCapture();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        const string sha1 = "0123456789abcdef0123456789abcdef01234567";
        const string function = "library.function";

        await client.ExecuteFireAndForgetAsync("EVALSHA", sha1, 0);
        await client.ExecuteFireAndForgetAsync(RespireCommands.Scripting.FCALL, function, 0);

        var evalActivity = capture.SingleActivity("EVALSHA", server.Port);
        await Assert.That(evalActivity.OperationName).IsEqualTo($"EVALSHA {sha1}");
        await Assert.That(Tag(evalActivity, "db.stored_procedure.name")).IsEqualTo(sha1);
        var evalMeasurement = capture.SingleMeasurement("EVALSHA", server.Port);
        await Assert.That(evalMeasurement.Tags["db.stored_procedure.name"]).IsEqualTo(sha1);

        var functionActivity = capture.SingleActivity("FCALL", server.Port);
        await Assert.That(functionActivity.OperationName).IsEqualTo($"FCALL {function}");
        await Assert.That(Tag(functionActivity, "db.stored_procedure.name")).IsEqualTo(function);
        var functionMeasurement = capture.SingleMeasurement("FCALL", server.Port);
        await Assert.That(functionMeasurement.Tags["db.stored_procedure.name"]).IsEqualTo(function);
    }

    [Test]
    public async Task ClusterFireAndForget_EmitsOneTargetNodeOperation()
    {
        await using var target = new FakeRespServer(FakeRespServer.OkReply);
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{target.Port}\r\n");
        await using var seed = new FakeRespServer(topology);
        using var capture = new TelemetryCapture();
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Cluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });

        await client.ExecuteFireAndForgetAsync("SET", "key", "value");

        var activity = capture.SingleActivity("SET", target.Port);
        await Assert.That(activity.Status).IsEqualTo(ActivityStatusCode.Unset);
        await Assert.That(capture.Activities.Count(item =>
            Tag(item, "db.operation.name") as string == "SET" &&
            Tag(item, "server.port") as int? == target.Port)).IsEqualTo(1);
        await Assert.That(capture.Activities.Any(item =>
            Tag(item, "db.operation.name") as string == "SET" &&
            Tag(item, "server.port") as int? == seed.Port)).IsFalse();
        _ = capture.SingleMeasurement("SET", target.Port);
    }

    [Test]
    public async Task ClusterWideFireAndForget_EmitsOneOperationPerMaster()
    {
        await using var firstNode = new FakeRespServer(FakeRespServer.OkReply);
        await using var secondNode = new FakeRespServer(FakeRespServer.OkReply);
        var topology = Encoding.ASCII.GetBytes(
            $"*2\r\n" +
            $"*3\r\n:0\r\n:8191\r\n*2\r\n$9\r\n127.0.0.1\r\n:{firstNode.Port}\r\n" +
            $"*3\r\n:8192\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{secondNode.Port}\r\n");
        await using var seed = new FakeRespServer(topology);
        using var capture = new TelemetryCapture();
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Cluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });

        await client.ExecuteFireAndForgetAsync(
            RespireCommands.Scripting.FUNCTION_LOAD, "#!lua name=library");

        var expectedPorts = new int?[] { firstNode.Port, secondNode.Port };
        var ports = capture.Activities
            .Where(item =>
                Tag(item, "db.operation.name") as string == "FUNCTION LOAD" &&
                expectedPorts.Contains(Tag(item, "server.port") as int?))
            .Select(item => Tag(item, "server.port") as int?)
            .ToArray();
        await Assert.That(ports).IsEquivalentTo(expectedPorts);
        await Assert.That(capture.Measurements.Count(measurement =>
            measurement.Tags.GetValueOrDefault("db.operation.name") as string == "FUNCTION LOAD" &&
            expectedPorts.Contains(measurement.Tags.GetValueOrDefault("server.port") as int?)))
            .IsEqualTo(2);
    }

    [Test]
    public async Task DefaultRedisPort_IsOmitted()
    {
        using var capture = new TelemetryCapture();

        var telemetry = RespireTelemetry.StartOperation("PING", "semconv-default.example", 6379, 0);
        telemetry.Complete("PING", "semconv-default.example", 6379, 0);

        var activity = capture.Activities.Single(activity =>
            Tag(activity, "server.address") as string == "semconv-default.example");
        await Assert.That(Tags(activity).ContainsKey("server.port")).IsFalse();
        var measurement = capture.Measurements.Single(measurement =>
            measurement.Tags.GetValueOrDefault("server.address") as string == "semconv-default.example");
        await Assert.That(measurement.Tags.ContainsKey("server.port")).IsFalse();
    }

    [Test]
    public async Task RedisError_EmitsStatusCodeAndMatchingErrorType()
    {
        await using var server = new FakeRespServer("-WRONGTYPE rejected my-secret\r\n"u8.ToArray());
        using var capture = new TelemetryCapture();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await Assert.That(async () => await client.GetStringAsync("key"))
            .ThrowsExactly<RespireServerException>();

        var activity = capture.SingleActivity("GET", server.Port);
        var tags = Tags(activity);
        await Assert.That(activity.Status).IsEqualTo(ActivityStatusCode.Error);
        await Assert.That(activity.StatusDescription).IsEqualTo("WRONGTYPE");
        await Assert.That(activity.StatusDescription!.Contains("my-secret", StringComparison.Ordinal)).IsFalse();
        await Assert.That(tags["error.type"]).IsEqualTo("WRONGTYPE");
        await Assert.That(tags["db.response.status_code"]).IsEqualTo("WRONGTYPE");

        var measurement = capture.SingleMeasurement("GET", server.Port);
        await Assert.That(measurement.Tags["error.type"]).IsEqualTo("WRONGTYPE");
        await Assert.That(measurement.Tags["db.response.status_code"]).IsEqualTo("WRONGTYPE");
    }

    [Test]
    public async Task Pipeline_EmitsOneBatchOperation()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        using var capture = new TelemetryCapture();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var batch = client.CreateBatch();
        _ = batch.Set("one", "1");
        _ = batch.Set("two", "2");

        await batch.ExecuteAsync();

        var activities = capture.Activities.Where(activity =>
            Tag(activity, "server.port") as int? == server.Port).ToArray();
        await Assert.That(activities.Length).IsEqualTo(1);
        await Assert.That(activities[0].OperationName).IsEqualTo("PIPELINE SET");
        await Assert.That(Tag(activities[0], "db.operation.name")).IsEqualTo("PIPELINE SET");
        await Assert.That(Tag(activities[0], "db.operation.batch.size")).IsEqualTo(2);
        var measurement = capture.SingleMeasurement("PIPELINE SET", server.Port);
        await Assert.That(measurement.Tags["db.operation.batch.size"]).IsEqualTo(2);
    }

    [Test]
    public async Task EmptyPipeline_EmitsBatchSizeZeroWithoutConnecting()
    {
        using var capture = new TelemetryCapture();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("empty-pipeline.example") },
        });

        await client.CreateBatch().ExecuteAsync();

        var activity = capture.Activities.Single(activity =>
            Tag(activity, "server.address") as string == "empty-pipeline.example");
        await Assert.That(activity.OperationName).IsEqualTo("PIPELINE");
        await Assert.That(Tag(activity, "db.operation.batch.size")).IsEqualTo(0);
        var measurement = capture.Measurements.Single(measurement =>
            measurement.Tags.GetValueOrDefault("server.address") as string == "empty-pipeline.example");
        await Assert.That(measurement.Tags["db.operation.batch.size"]).IsEqualTo(0);
    }

    [Test]
    public async Task Transaction_EmitsOneBatchOperation()
    {
        await using var server = new FakeRespServer(
            FakeRespServer.OkReply,
            "+QUEUED\r\n"u8.ToArray(),
            "+QUEUED\r\n"u8.ToArray(),
            "*2\r\n+OK\r\n+OK\r\n"u8.ToArray());
        using var capture = new TelemetryCapture();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var transaction = client.CreateTransaction();
        _ = transaction.Set("one", "1");
        _ = transaction.Set("two", "2");

        await transaction.CommitAsync();

        var activity = capture.SingleActivity("MULTI SET", server.Port);
        await Assert.That(Tag(activity, "db.operation.batch.size")).IsEqualTo(2);
        var measurement = capture.SingleMeasurement("MULTI SET", server.Port);
        await Assert.That(measurement.Tags["db.operation.batch.size"]).IsEqualTo(2);
    }

    [Test]
    public async Task Transaction_ConversionFailureMarksBatchTelemetryAsError()
    {
        await using var server = new FakeRespServer(
            FakeRespServer.OkReply,
            "+QUEUED\r\n"u8.ToArray(),
            "*1\r\n$3\r\nbad\r\n"u8.ToArray());
        using var capture = new TelemetryCapture();
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            Connections = 1,
            Serializer = new FailingDeserializer(),
        });
        var transaction = client.CreateTransaction();
        var pending = transaction.Get<Payload>("key");

        await transaction.CommitAsync();

        await Assert.That(async () => await pending).ThrowsExactly<InvalidOperationException>();
        var activity = capture.SingleActivity("GET", server.Port);
        await Assert.That(activity.Status).IsEqualTo(ActivityStatusCode.Error);
        await Assert.That(Tag(activity, "error.type")).IsEqualTo(typeof(InvalidOperationException).FullName);
    }

    [Test]
    public async Task EmptyTransaction_EmitsBatchSizeZeroWithoutConnecting()
    {
        using var capture = new TelemetryCapture();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("empty-transaction.example") },
        });
        await using var transaction = client.CreateTransaction();

        await transaction.CommitAsync();

        var activity = capture.Activities.Single(activity =>
            Tag(activity, "server.address") as string == "empty-transaction.example");
        await Assert.That(activity.OperationName).IsEqualTo("MULTI");
        await Assert.That(Tag(activity, "db.operation.batch.size")).IsEqualTo(0);
        var measurement = capture.Measurements.Single(measurement =>
            measurement.Tags.GetValueOrDefault("server.address") as string == "empty-transaction.example");
        await Assert.That(measurement.Tags["db.operation.batch.size"]).IsEqualTo(0);
    }

    [Test]
    public async Task ScriptFallback_EmitsOneSuccessfulLogicalOperation()
    {
        await using var server = new FakeRespServer(
            "-NOSCRIPT No matching script. Please use EVAL.\r\n"u8.ToArray(),
            ":1\r\n"u8.ToArray());
        using var capture = new TelemetryCapture();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var script = RespireScript.Create("return 1");

        using var result = await client.Scripts.ExecuteAsync(script);

        var activities = capture.Activities.Where(activity =>
            Tag(activity, "server.port") as int? == server.Port).ToArray();
        await Assert.That(activities.Length).IsEqualTo(1);
        await Assert.That(activities[0].OperationName).IsEqualTo($"EVALSHA {script.Sha1}");
        await Assert.That(activities[0].Status).IsEqualTo(ActivityStatusCode.Unset);
        await Assert.That(Tag(activities[0], "db.stored_procedure.name")).IsEqualTo(script.Sha1);
        await Assert.That(Tags(activities[0]).ContainsKey("error.type")).IsFalse();
        await Assert.That(capture.Measurements.Count(measurement =>
            measurement.Tags.GetValueOrDefault("server.port") as int? == server.Port)).IsEqualTo(1);
    }

    [Test]
    public async Task RawCompoundCommand_PreservesApplicationCommandName()
    {
        await using var server = new FakeRespServer("*0\r\n"u8.ToArray());
        using var capture = new TelemetryCapture();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        using var result = await client.ExecuteAsync("CONFIG GET", "maxmemory");

        var activity = capture.SingleActivity("CONFIG GET", server.Port);
        await Assert.That(Tag(activity, "db.operation.name")).IsEqualTo("CONFIG GET");
    }

    [Test]
    public async Task RawCommand_ExcludesInlineArgumentsFromTelemetry()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        using var capture = new TelemetryCapture();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        using var result = await client.ExecuteAsync("AUTH my-secret");

        var activity = capture.SingleActivity("AUTH", server.Port);
        await Assert.That(Tag(activity, "db.operation.name")).IsEqualTo("AUTH");
        var measurement = capture.SingleMeasurement("AUTH", server.Port);
        await Assert.That(measurement.Tags["db.operation.name"]).IsEqualTo("AUTH");
        await Assert.That(activity.OperationName.Contains("my-secret", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task InterpolatedCommand_NormalizesTelemetryWithoutChangingWireToken()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        using var capture = new TelemetryCapture();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var key = new RespireKey("key");

        using var result = await client.ExecuteAsync($"get {key}");

        var activity = capture.SingleActivity("GET", server.Port);
        await Assert.That(Tag(activity, "db.operation.name")).IsEqualTo("GET");
        await Assert.That(server.ReceivedCommands.Single()).IsEqualTo("get key");
    }

    [Test]
    public async Task RawEvalSha_CapturesStoredProcedureDigest()
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray());
        using var capture = new TelemetryCapture();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        const string sha1 = "0123456789abcdef0123456789abcdef01234567";

        using var result = await client.ExecuteAsync("EVALSHA", sha1, 0);

        var activity = capture.SingleActivity("EVALSHA", server.Port);
        await Assert.That(activity.OperationName).IsEqualTo($"EVALSHA {sha1}");
        await Assert.That(Tag(activity, "db.stored_procedure.name")).IsEqualTo(sha1);
    }

    private static object? Tag(Activity activity, string name)
        => activity.TagObjects.FirstOrDefault(tag => tag.Key == name).Value;

    private static Dictionary<string, object?> Tags(Activity activity)
        => activity.TagObjects.ToDictionary(static tag => tag.Key, static tag => tag.Value);

    private sealed class TelemetryCapture : IDisposable
    {
        private readonly ActivityListener _activityListener;
        private readonly MeterListener _meterListener;

        public ConcurrentQueue<Activity> Activities { get; } = new();
        public ConcurrentQueue<Dictionary<string, object?>> CreationTags { get; } = new();
        public ConcurrentQueue<Measurement> Measurements { get; } = new();
        public IReadOnlyList<double>? HistogramBucketBoundaries { get; private set; }

        public TelemetryCapture()
        {
            _activityListener = new ActivityListener
            {
                ShouldListenTo = static source => source.Name == "Respire",
                Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                {
                    CreationTags.Enqueue(options.Tags?.ToDictionary(
                        static tag => tag.Key, static tag => tag.Value) ?? []);
                    return ActivitySamplingResult.AllDataAndRecorded;
                },
                SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = Activities.Enqueue,
            };
            ActivitySource.AddActivityListener(_activityListener);

            _meterListener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == "Respire" && instrument.Name == "db.client.operation.duration")
                    {
                        HistogramBucketBoundaries = ((Histogram<double>)instrument).Advice?.HistogramBucketBoundaries;
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };
            _meterListener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
                Measurements.Enqueue(new Measurement(
                    instrument.Name,
                    instrument.Unit,
                    value,
                    tags.ToArray().ToDictionary(static tag => tag.Key, static tag => tag.Value))));
            _meterListener.Start();
        }

        public Activity SingleActivity(string operation, int port)
            => Activities.Single(activity =>
                Tag(activity, "db.operation.name") as string == operation &&
                Tag(activity, "server.port") as int? == port);

        public Measurement SingleMeasurement(string operation, int port)
            => Measurements.Single(measurement =>
                measurement.Tags.GetValueOrDefault("db.operation.name") as string == operation &&
                measurement.Tags.GetValueOrDefault("server.port") as int? == port);

        public void Dispose()
        {
            _activityListener.Dispose();
            _meterListener.Dispose();
        }
    }

    private sealed record Measurement(
        string InstrumentName,
        string? Unit,
        double Value,
        Dictionary<string, object?> Tags);

    private sealed record Payload;

    private sealed class FailingDeserializer : IRespireSerializer
    {
        public void Serialize<T>(IBufferWriter<byte> destination, T value)
            => throw new NotSupportedException();

        public T? Deserialize<T>(ReadOnlySpan<byte> payload)
            => throw new InvalidOperationException("Conversion failed.");
    }
}
