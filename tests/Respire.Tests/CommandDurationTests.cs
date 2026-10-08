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
public class CommandDurationTests
{
    [Test]
    [Arguments("string")]
    [Arguments("bytes")]
    [Arguments("integer")]
    public async Task CapacityCancellationRecordsOnceWithoutUsingReclaimedSource(string shape)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.None });
        await using var server = new FakeRespServer { SuppressReply = command => command.StartsWith("GET ") || command.StartsWith("STRLEN ") };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, MaxInflightCommands = 1,
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
        });
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = client.GetStringAsync("first", limit.Token).AsTask();
        while (!server.ReceivedCommands.Contains("GET first")) await Task.Delay(1, limit.Token);
        RespireMetrics.Configure(new() { Groups = RespireMetricGroups.Command });
        using var capture = new DurationCapture(throwFromListener: true);
        using var cancellation = new CancellationTokenSource();
        var second = Send(client, shape, cancellation.Token);
        cancellation.Cancel();
        await Assert.That(async () => await second.WaitAsync(limit.Token)).ThrowsExactly<RespireCommandNotSubmittedException>();
        await Assert.That(capture.Items.Single().Tags.ContainsKey("error.type")).IsTrue();
        await Assert.That(server.ReceivedCommands.Any(command => command.EndsWith(" key"))).IsFalse();
        await server.SendRawAsync("$5\r\nfirst\r\n"u8.ToArray());
        await Assert.That(await first.WaitAsync(limit.Token)).IsEqualTo("first");
        await Assert.That(capture.Items.Count).IsEqualTo(1);
    }

    [Test]
    public async Task AdmissionFailureReturnsFaultedResultAndOneDuration()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Command });
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var capture = new DurationCapture();
        var expected = new InvalidOperationException("admission rejected");
        // Obtaining the ValueTask must retain the previous instrumented async boundary.
        var pending = client.ConvertResponseAsync<AdmissionFailureCommand, int, int>("GET",
            new(expected), default, 0, static (int state, in RespValue _) => state);
        var actual = await Assert.That(async () => await pending).ThrowsExactly<InvalidOperationException>();
        await Assert.That(actual).IsSameReferenceAs(expected);
        await Assert.That(capture.Items.Single().Tags.ContainsKey("error.type")).IsTrue();
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("GET "))).IsFalse();
    }

    private readonly struct AdmissionFailureCommand(Exception error) : IRespCommand
    {
        public ReadCommandKind ReadKind => ReadCommandKind.None;
        public void ValidateAdmission() => throw error;
        public void Write(ref RespWriter writer) => new Cmd1(Verbs.Get, "key").Write(ref writer);
    }

    [Test]
    [Arguments("string", "server")]
    [Arguments("bytes", "server")]
    [Arguments("integer", "server")]
    [Arguments("string", "cancel")]
    [Arguments("bytes", "cancel")]
    [Arguments("integer", "cancel")]
    [Arguments("string", "deadline")]
    [Arguments("bytes", "deadline")]
    [Arguments("integer", "deadline")]
    [Arguments("string", "dispose")]
    [Arguments("bytes", "dispose")]
    [Arguments("integer", "dispose")]
    public async Task FailedTypedResponseRecordsOnceAndKeepsOriginalError(string shape, string failure)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Command });
        await using var server = new FakeRespServer { SuppressReply = command => command.StartsWith("GET ") || command.StartsWith("STRLEN ") };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            CommandTimeout = failure == "deadline" ? TimeSpan.FromMilliseconds(250) : TimeSpan.FromSeconds(10),
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
        });
        using var capture = new DurationCapture(throwFromListener: true);
        using var cancellation = new CancellationTokenSource();
        var pending = Send(client, shape, cancellation.Token);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!server.ReceivedCommands.Any(command => command.StartsWith("GET ") || command.StartsWith("STRLEN ")))
            await Task.Delay(1, limit.Token);
        switch (failure)
        {
            case "server": await server.SendRawAsync("-ERR original server error\r\n"u8.ToArray()); break;
            case "cancel": cancellation.Cancel(); break;
            case "dispose": await client.DisposeAsync(); break;
        }
        Exception? actual = null;
        try { await pending.WaitAsync(limit.Token); }
        catch (Exception error) { actual = error; }
        switch (failure)
        {
            case "server": await Assert.That(actual is RespireServerException { Code: "ERR" }).IsTrue(); break;
            case "cancel": await Assert.That(actual is OperationCanceledException error && error.CancellationToken == cancellation.Token).IsTrue(); break;
            case "deadline": await Assert.That(actual is RespireTimeoutException).IsTrue(); break;
            default: await Assert.That(actual is RespireConnectionException or ObjectDisposedException).IsTrue(); break;
        }
        var measurement = capture.Items.Single();
        await Assert.That(measurement.Tags.ContainsKey("error.type")).IsTrue();
        await Assert.That(measurement.Tags["db.operation.name"]).IsEqualTo(shape == "integer" ? "STRLEN" : "GET");
        if (failure == "server") await Assert.That(measurement.Tags["db.response.status_code"]).IsEqualTo("ERR");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CustomConversionFailureKeepsSuccessfulDuration(bool throwFromListener)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Command });
        await using var server = new FakeRespServer(":42\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var capture = new DurationCapture(throwFromListener);
        var expected = new InvalidOperationException("custom conversion failed");
        var pending = client.ConvertResponseAsync<Respire.Commands.Cmd1, InvalidOperationException, int>("STRLEN",
            new Respire.Commands.Cmd1(Respire.Commands.Verbs.StrLen, "key"), default, expected,
            static (InvalidOperationException error, in RespValue _) => throw error);
        var actual = await Assert.That(async () => await pending).ThrowsExactly<InvalidOperationException>();
        await Assert.That(actual).IsSameReferenceAs(expected);
        await Assert.That(capture.Items.Single().Tags.ContainsKey("error.type")).IsFalse();
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task CompletedDurationTagsAndTypedSourcesAllocateNothing(int shape)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Command });
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Database = 7, Connections = 1,
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
        });
        using var listener = new MeterListener();
        listener.InstrumentPublished = static (instrument, owner) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "db.client.operation.duration")
                owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>(static (_, _, _, _) => { });
        listener.Start();
        var connection = client.Core.Multiplexer.GetConnection();
        for (var i = 0; i < 20; i++) MeasureCompletedSources(connection, shape);
        MeasurePositiveControl();
        var measured = AllocationMeasurement.WithoutConcurrentGc(() =>
            (MeasureCompletedSources(connection, shape), MeasurePositiveControl()));
        await Assert.That(measured.Item1).IsEqualTo(0L);
        await Assert.That(measured.Item2 > 0).IsTrue();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureCompletedSources(RespireConnection connection, int shape)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            var duration = new RespireTelemetry.DurationObservation(connection, RespireTelemetry.CaptureOperationStart("GET"));
            switch (shape)
            {
                case 0:
                    var text = StringPendingResponseSource.Rent("GET", duration);
                    text.TrySetResult(RespValue.Null); _ = text.Task.GetAwaiter().GetResult(); text.ReleaseRef(); break;
                case 1:
                    var bytes = BytesPendingResponseSource.Rent("GET", duration);
                    bytes.TrySetResult(RespValue.Null); _ = bytes.Task.GetAwaiter().GetResult(); bytes.ReleaseRef(); break;
                default:
                    var integer = ConvertedPendingResponseSource<int, int>.Rent(42,
                        static (int state, in RespValue _) => state, false, "GET", duration);
                    integer.TrySetResult(RespValue.Null); _ = integer.Task.GetAwaiter().GetResult(); integer.ReleaseRef(); break;
            }
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static object? _allocationAnchor;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasurePositiveControl()
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) _allocationAnchor = new byte[37];
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static Task Send(RespireClient client, string shape, CancellationToken token)
        => shape switch
        {
            "string" => client.GetStringAsync("key", token).AsTask(),
            "bytes" => client.GetBytesAsync("key", token).AsTask(),
            _ => client.Strings.LengthAsync("key", token).AsTask(),
        };

    [Test]
    [Arguments("string")]
    [Arguments("bytes")]
    [Arguments("integer")]
    public async Task DurationEndsAtSourceCompletionBeforeDelayedConsumption(string shape)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Command });
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var capture = new DurationCapture();
        var started = RespireTelemetry.CaptureOperationStart("GET");
        var duration = new RespireTelemetry.DurationObservation(client.Core.Multiplexer.GetConnection(), started);
        PendingResponse source;
        Action consume;
        switch (shape)
        {
            case "string":
                var text = StringPendingResponseSource.Rent("GET", duration);
                source = text; consume = () => text.Task.GetAwaiter().GetResult(); break;
            case "bytes":
                var bytes = BytesPendingResponseSource.Rent("GET", duration);
                source = bytes; consume = () => bytes.Task.GetAwaiter().GetResult(); break;
            default:
                var integer = ConvertedPendingResponseSource<int, int>.Rent(42,
                    static (int state, in RespValue _) => state, false, "GET", duration);
                source = integer; consume = () => integer.Task.GetAwaiter().GetResult(); break;
        }
        try
        {
            var beforeCompletion = Stopwatch.GetTimestamp();
            source.TrySetResult(RespValue.Null);
            var afterCompletion = Stopwatch.GetTimestamp();
            await Assert.That(capture.Items.IsEmpty).IsTrue();
            Thread.SpinWait(1000);
            consume();
            var measurement = capture.Items.Single();
            await Assert.That(measurement.Value >= Stopwatch.GetElapsedTime(started.Timestamp, beforeCompletion).TotalSeconds).IsTrue();
            await Assert.That(measurement.Value <= Stopwatch.GetElapsedTime(started.Timestamp, afterCompletion).TotalSeconds).IsTrue();
        }
        finally { source.ReleaseRef(); }
    }

    [Test]
    [Arguments("string")]
    [Arguments("bytes")]
    [Arguments("integer")]
    public async Task DurationListenerRetainsTypedSourceAndCapturedSelection(string shape)
    {
        var operation = shape == "integer" ? "STRLEN" : "GET";
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Command });
        await using var server = new FakeRespServer { SuppressReply = command => command.StartsWith(operation + " ") };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var capture = new DurationCapture();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task response;
        string expectedSource;
        switch (shape)
        {
            case "string": response = client.GetStringAsync("key", timeout.Token).AsTask(); expectedSource = nameof(StringPendingResponseSource); break;
            case "bytes": response = client.GetBytesAsync("key", timeout.Token).AsTask(); expectedSource = nameof(BytesPendingResponseSource); break;
            default: response = client.Strings.LengthAsync("key", timeout.Token).AsTask(); expectedSource = "ConvertedPendingResponseSource"; break;
        }
        while (!server.ReceivedCommands.Any(command => command.StartsWith(operation + " ")))
            await Task.Delay(1, timeout.Token);
        var connection = client.Core.Multiplexer.GetConnection();
        await Assert.That(connection.InspectForTests().Inflight.TryPeek(out var pending)).IsTrue();
        // The reply is parked, so the source cannot be hidden by synchronous completion.
        await Assert.That(pending!.GetType().Name.Split('`')[0]).IsEqualTo(expectedSource);
        RespireMetrics.Configure(new() { Groups = RespireMetricGroups.None });
        await server.SendRawAsync(shape == "integer" ? ":5\r\n"u8.ToArray() : "$5\r\nvalue\r\n"u8.ToArray());
        await response.WaitAsync(timeout.Token);
        var measurement = capture.Items.Single();
        await Assert.That(measurement.Value > 0).IsTrue();
        await Assert.That(measurement.Tags["db.operation.name"]).IsEqualTo(operation);
        await Assert.That(measurement.Tags["db.namespace"]).IsEqualTo("0");
        await Assert.That(measurement.Tags["server.port"]).IsEqualTo(server.Port);
        await Assert.That(measurement.Tags["network.peer.port"]).IsEqualTo(server.Port);
        await Assert.That(measurement.Tags.ContainsKey("error.type")).IsFalse();
    }

    private sealed record Measurement(double Value, Dictionary<string, object?> Tags);

    private sealed class DurationCapture : IDisposable
    {
        private readonly MeterListener _listener = new();
        internal ConcurrentQueue<Measurement> Items { get; } = new();

        internal DurationCapture(bool throwFromListener = false)
        {
            _listener.InstrumentPublished = static (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Respire" && instrument.Name == "db.client.operation.duration")
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
            {
                Items.Enqueue(new(value, tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value)));
                if (throwFromListener) throw new InvalidOperationException("metrics listener failed");
            });
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }
}
