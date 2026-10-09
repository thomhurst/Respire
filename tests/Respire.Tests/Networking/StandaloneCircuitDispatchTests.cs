using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Text;
using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

[NotInParallel]
public class StandaloneCircuitDispatchTests
{
    [Test]
    [Arguments(false, "string")]
    [Arguments(true, "string")]
    [Arguments(false, "bytes")]
    [Arguments(true, "bytes")]
    [Arguments(false, "converted")]
    [Arguments(true, "converted")]
    public async Task SuccessfulTypedMutationDoesNotReinvalidateAfterItsReply(bool enabled, string shape)
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        {
            Protocol = RespProtocol.Resp3, ClientSideCache = new(),
            CircuitBreaker = enabled ? Options(server).CircuitBreaker : null,
        });
        await client.GetStringAsync("warm");
        var cache = client.Core.ClientCache!;
        RespireKey key = "key";
        var read = cache.BeginRead(in key);
        try
        {
            var command = new CatalogCommand(RespireCommands.String.SET, ["key", "new"]);
            if (shape == "string")
                await Assert.That(await client.StringOrNullAsync("SET", in command, default)).IsEqualTo("OK");
            else if (shape == "bytes")
                await Assert.That((await client.BytesOrNullAsync("SET", in command, default))!).IsEquivalentTo("OK"u8.ToArray());
            else
                await Assert.That(await client.OkResultAsync("SET", in command, default)).IsTrue();
            // Keep an in-flight read generation alive across the mutation. Admission
            // invalidates it once; a successful reply must not invalidate it again.
            await Assert.That(read.State.Generation).IsEqualTo(read.Generation + 1);
            await Assert.That(cache.InspectForTests().ActiveMutationCount).IsEqualTo(0);
            await Assert.That(server.ReceivedCommands.Count(command => command == "SET key new")).IsEqualTo(1);
        }
        finally { cache.CompleteRead(in read, default, allowInsert: false); }
    }

    [Test]
    [Arguments("redis")]
    [Arguments("converter")]
    [Arguments("rejection")]
    public async Task FailedTypedMutationStillReinvalidatesAndReleasesItsFence(string failure)
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        {
            Protocol = RespProtocol.Resp3, ClientSideCache = new(),
        });
        await client.GetStringAsync("warm");
        if (failure == "redis")
            server.ReplyOverride = (_, command) => command == "SET key new" ? "-ERR rejected\r\n"u8.ToArray() : null;
        else if (failure == "rejection")
        {
            var admission = client.Core.Circuits!.Acquire(new("127.0.0.1", server.Port), default);
            admission.Failed(new RespireConnectionException("unavailable"), default);
            admission.Dispose();
        }
        var cache = client.Core.ClientCache!;
        RespireKey key = "key";
        var read = cache.BeginRead(in key);
        try
        {
            var command = new CatalogCommand(RespireCommands.String.SET, ["key", "new"]);
            var conversionError = new InvalidOperationException("conversion failed");
            var error = await Failure(async () =>
            {
                if (failure == "converter")
                    await client.ConvertResponseAsync<CatalogCommand, Exception, bool>("SET", in command, default,
                        conversionError, static (Exception error, in RespValue _) => throw error);
                else await client.OkResultAsync("SET", in command, default);
            });
            if (failure == "rejection") await Assert.That(error).IsTypeOf<RespireCircuitOpenException>();
            else if (failure == "redis") await Assert.That(error).IsTypeOf<RespireServerException>();
            else await Assert.That(error).IsSameReferenceAs(conversionError);
            await Assert.That(read.State.Generation).IsEqualTo(read.Generation + 2);
            await Assert.That(cache.InspectForTests().ActiveMutationCount).IsEqualTo(0);
            await Assert.That(server.ReceivedCommands.Count(command => command == "SET key new"))
                .IsEqualTo(failure == "rejection" ? 0 : 1);
        }
        finally { cache.CompleteRead(in read, default, allowInsert: false); }
    }

    [Test]
    public async Task SelectionFailureOpensCircuitWithoutDispatchingACommand()
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        {
            ReconnectPolicy = new() { InitialDelay = TimeSpan.FromMinutes(1), MaxDelay = TimeSpan.FromMinutes(1) },
        });
        var (circuit, clock) = await Prepare(client, server);
        var connection = client.Core.Multiplexer.GetConnection();
        server.CloseConnections();
        await connection.Closed.WaitAsync(TimeSpan.FromSeconds(5));
        var commands = server.CommandsSeen;
        await Assert.That(await Failure(() => Send(client, "string", "unavailable"))).IsTypeOf<RespireConnectionException>();
        await Assert.That(circuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Open);
        await Assert.That(await Failure(() => Send(client, "raw", "rejected"))).IsTypeOf<RespireCircuitOpenException>();
        clock.Advance(TimeSpan.FromMinutes(1));
        await Assert.That(await Failure(() => Send(client, "bytes", "failed-probe"))).IsTypeOf<RespireConnectionException>();
        await Assert.That(circuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Open);
        await Assert.That(circuit.Snapshot().ActiveProbes).IsEqualTo(0);
        await Assert.That(server.CommandsSeen).IsEqualTo(commands);
    }

    [Test]
    [Arguments("raw", false, false)]
    [Arguments("raw", false, true)]
    [Arguments("raw", true, false)]
    [Arguments("raw", true, true)]
    [Arguments("string", false, false)]
    [Arguments("string", false, true)]
    [Arguments("string", true, false)]
    [Arguments("string", true, true)]
    public async Task SelectionFailureDuringPublicationRetriesWithoutChargingReplacement(
        string shape, bool sourceOpen, bool targetOpen)
    {
        await using var target = Server();
        await using var source = Server();
        await using var client = await RespireClient.ConnectAsync(Options(source) with
        {
            Protocol = RespProtocol.Resp3, MaintenanceNotifications = RespireMaintenanceNotificationMode.Enabled,
            ReconnectPolicy = new() { InitialDelay = TimeSpan.FromMinutes(1), MaxDelay = TimeSpan.FromMinutes(1) },
        });
        var (sourceCircuit, _) = await Prepare(client, source);
        if (sourceOpen) await Trip(client, source);
        var multiplexer = client.Core.Multiplexer;
        var original = multiplexer.GetConnection();
        var endpoint = new RespireEndpoint("127.0.0.1", target.Port);
        var targetAdmission = client.Core.Circuits!.Acquire(endpoint, default);
        if (targetOpen) targetAdmission.Failed(new RespireConnectionException("target unavailable"), default);
        targetAdmission.Dispose();
        var targetCircuit = client.Core.Circuits.GetForTests(endpoint);
        var announcement = multiplexer.CaptureMovingAnnouncement(0, original,
            new MaintenanceNotification("MOVING", 1, 10, endpoint));
        source.CloseConnections();
        await original.Closed.WaitAsync(TimeSpan.FromSeconds(5));
        var commands = source.CommandsSeen;
        using var published = new ManualResetEventSlim();
        var handoffs = 0;
        multiplexer.MovingHandoffPublished += published.Set;
        multiplexer.StateChanged += change =>
        {
            if (change.State != RespireConnectionState.Reconnecting || Interlocked.Increment(ref handoffs) != 1) return;
            // Selection already observed the dead source. Publish its captured MOVING before
            // GetConnection throws, so the catch must not charge or reject the new endpoint.
            multiplexer.QueueDedicatedMovingHandoff(original, announcement, static () => true);
            published.Wait(TimeSpan.FromSeconds(5));
        };

        var pending = Task.Run(() => Send(client, shape, "routed"));
        if (targetOpen)
        {
            var error = await Failure(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
            await Assert.That(error).IsTypeOf<RespireCircuitOpenException>();
            await Assert.That(((RespireCircuitOpenException)error).Endpoint).IsEqualTo(endpoint);
        }
        else
        {
            await pending.WaitAsync(TimeSpan.FromSeconds(10));
            await Send(client, shape, "next");
        }
        await Assert.That(published.IsSet).IsTrue();
        await Assert.That(handoffs).IsEqualTo(1);
        await Assert.That(multiplexer.ActiveConnectionEndpoint).IsEqualTo(endpoint);
        await Assert.That(sourceCircuit.Snapshot().State).IsEqualTo(sourceOpen ? EndpointCircuitState.Open : EndpointCircuitState.Closed);
        await Assert.That(sourceCircuit.Snapshot().ActiveProbes).IsEqualTo(0);
        await Assert.That(targetCircuit.Snapshot().State).IsEqualTo(targetOpen ? EndpointCircuitState.Open : EndpointCircuitState.Closed);
        await Assert.That(targetCircuit.Snapshot().ActiveProbes).IsEqualTo(0);
        await Assert.That(source.CommandsSeen).IsEqualTo(commands);
        await Assert.That(target.ReceivedCommands.Count(command => command == "GET routed")).IsEqualTo(targetOpen ? 0 : 1);
        await Assert.That(target.ReceivedCommands.Count(command => command == "GET next")).IsEqualTo(targetOpen ? 0 : 1);
    }

    [Test]
    [Arguments("array")]
    [Arguments("span")]
    [Arguments("async-array")]
    [Arguments("async-memory")]
    public async Task ValidatedStreamEofCompletesRecoveryBeforeTransportCallback(string shape)
    {
        var endpoint = new RespireEndpoint("localhost");
        var registry = new StandaloneCircuitRegistry(new() { MinimumFailureCount = 1, HalfOpenProbeCount = 1 });
        var failed = registry.Acquire(endpoint, default);
        failed.Failed(new RespireConnectionException("lost"), default);
        failed.Dispose();
        var circuit = registry.GetForTests(endpoint);
        var clock = new Clock();
        CircuitClock(circuit) = clock;
        clock.Advance(TimeSpan.FromMinutes(1));
        var completion = new CircuitStreamCompletion(registry.Acquire(endpoint, default), default);
        await using var stream = new CircuitCompletionStream(new MemoryStream([42]), completion);
        var buffer = new byte[1];
        await Assert.That(await Read(0)).IsEqualTo(0);
        await Assert.That(circuit.Snapshot().ActiveProbes).IsEqualTo(1);
        await Assert.That(await Read(1)).IsEqualTo(1);
        await Assert.That(await Read(1)).IsEqualTo(0);
        await stream.DisposeAsync();
        await Assert.That(circuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Closed);
        completion.Complete(new RespireConnectionException("late callback"));
        await Assert.That(circuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Closed);

        ValueTask<int> Read(int length) => shape switch
        {
            "array" => ValueTask.FromResult(stream.Read(buffer, 0, length)),
            "span" => ValueTask.FromResult(stream.Read(buffer.AsSpan(0, length))),
            "async-array" => new(stream.ReadAsync(buffer, 0, length)),
            _ => stream.ReadAsync(buffer.AsMemory(0, length)),
        };
    }

    [Test]
    [Arguments("raw", false, false)]
    [Arguments("string", false, false)]
    [Arguments("bytes", false, false)]
    [Arguments("integer", false, false)]
    [Arguments("fire-and-forget", false, false)]
    [Arguments("stream", false, false)]
    [Arguments("cache", false, false)]
    [Arguments("raw", true, false)]
    [Arguments("string", true, false)]
    [Arguments("bytes", true, false)]
    [Arguments("integer", true, false)]
    [Arguments("fire-and-forget", true, false)]
    [Arguments("stream", true, false)]
    [Arguments("cache", true, false)]
    [Arguments("string", false, true)]
    [Arguments("bytes", false, true)]
    [Arguments("integer", false, true)]
    [Arguments("string", true, true)]
    [Arguments("bytes", true, true)]
    [Arguments("integer", true, true)]
    public async Task MaintenanceHandoffReacquiresAdmissionForActualEndpoint(string shape, bool targetOpen, bool tracingEnabled)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Command | RespireMetricGroups.Resiliency });
        var durations = new ConcurrentQueue<Dictionary<string, object?>>();
        var errors = new ConcurrentQueue<Dictionary<string, object?>>();
        var activities = new ConcurrentQueue<Activity>();
        using var tracing = new ActivityListener
        {
            ShouldListenTo = source => source.Name == RespireTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.OperationName is "GET" or "STRLEN") activities.Enqueue(activity);
            },
        };
        // Traced typed commands use the general response path. Its circuit retry must
        // retain ownership of both the activity and duration through the handoff.
        if (tracingEnabled) ActivitySource.AddActivityListener(tracing);
        using var listener = new MeterListener();
        listener.InstrumentPublished = static (instrument, meter) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name is "db.client.operation.duration" or "redis.client.errors")
                meter.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
            durations.Enqueue(tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value)));
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            errors.Enqueue(tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value)));
        listener.Start();
        await using var target = Server();
        await using var source = Server();
        await using var client = await RespireClient.ConnectAsync(Options(source) with
        {
            Protocol = RespProtocol.Resp3, MaintenanceNotifications = RespireMaintenanceNotificationMode.Enabled,
            CommandTimeout = TimeSpan.FromSeconds(5), MaxInflightCommands = 2,
            ClientSideCache = shape == "cache" ? new() : null,
        });
        var (sourceCircuit, clock) = await Prepare(client, source);
        await Trip(client, source);
        clock.Advance(TimeSpan.FromMinutes(1));
        var original = client.Core.Multiplexer.GetConnection();
        source.SuppressReply = command => command == "GET parked";
        var first = original.SendWithoutResponseTimeoutAsync(new Cmd1(Verbs.Get, "parked")).AsTask();
        var second = original.SendWithoutResponseTimeoutAsync(new Cmd1(Verbs.Get, "parked")).AsTask();
        await Received(source, "GET parked", 2);
        var endpoint = new RespireEndpoint("127.0.0.1", target.Port);
        var targetAdmission = client.Core.Circuits!.Acquire(endpoint, default);
        if (targetOpen) targetAdmission.Failed(new RespireConnectionException("target unavailable"), default);
        targetAdmission.Dispose();
        var targetCircuit = client.Core.Circuits.GetForTests(endpoint);
        durations.Clear();
        errors.Clear();
        activities.Clear();
        await Assert.That(RespireTelemetry.Source.HasListeners()).IsEqualTo(tracingEnabled);
        var pending = Dispatch();
        await Assert.That(pending.IsCompleted).IsFalse();
        await source.SendRawAsync(Encoding.UTF8.GetBytes($">4\r\n+MOVING\r\n:1\r\n:10\r\n+127.0.0.1:{target.Port}\r\n"));
        try
        {
            if (targetOpen)
                await Assert.That(await Failure(() => pending)).IsTypeOf<RespireCircuitOpenException>();
            else
            {
                await pending.WaitAsync(TimeSpan.FromSeconds(5));
                await Received(target, shape == "integer" ? "STRLEN routed" : "GET routed");
            }
            await Assert.That(sourceCircuit.Snapshot().ActiveProbes).IsEqualTo(0);
            await Assert.That(sourceCircuit.Snapshot().State).IsEqualTo(EndpointCircuitState.HalfOpen);
            await Assert.That(targetCircuit.Snapshot().State).IsEqualTo(targetOpen ? EndpointCircuitState.Open : EndpointCircuitState.Closed);
            await Assert.That(source.ReceivedCommands.Any(command => command.Contains("routed", StringComparison.Ordinal))).IsFalse();
            await Assert.That(target.ReceivedCommands.Count(command => command.Contains("routed", StringComparison.Ordinal))).IsEqualTo(targetOpen ? 0 : 1);
            // The circuit retry owns the handled retirement; its transport attempt must
            // not report it as a final error or lose the logical retry count.
            var handled = errors.Where(item => (bool)item["redis.client.errors.internal"]!).ToArray();
            var final = errors.Where(item => !(bool)item["redis.client.errors.internal"]!).ToArray();
            await Assert.That(handled.Length).IsEqualTo(1);
            await Assert.That(handled[0]["error.type"]).IsEqualTo(typeof(RespireConnectionRetiredException).FullName);
            await Assert.That(handled[0]["redis.client.operation.retry_attempts"]).IsEqualTo(0);
            await Assert.That(final.Length).IsEqualTo(targetOpen ? 1 : 0);
            if (targetOpen)
            {
                await Assert.That(final[0]["error.type"]).IsEqualTo(typeof(RespireCircuitOpenException).FullName);
                await Assert.That(final[0]["redis.client.operation.retry_attempts"]).IsEqualTo(1);
            }
            if (shape is "string" or "bytes" or "integer")
            {
                await Assert.That(durations.Count).IsEqualTo(1);
                var duration = durations.Single();
                await Assert.That(duration["server.port"]).IsEqualTo(target.Port);
                await Assert.That(duration.ContainsKey("error.type")).IsEqualTo(targetOpen);
                if (targetOpen)
                    await Assert.That(duration["error.type"]).IsEqualTo(typeof(RespireCircuitOpenException).FullName);
                await Assert.That(activities.Count).IsEqualTo(tracingEnabled ? 1 : 0);
                if (tracingEnabled)
                {
                    var activity = activities.Single();
                    await Assert.That(activity.GetTagItem("server.port")).IsEqualTo(target.Port);
                    await Assert.That(activity.Status).IsEqualTo(targetOpen ? ActivityStatusCode.Error : ActivityStatusCode.Unset);
                }
            }
        }
        finally
        {
            var wireId = source.ReceivedConnectionIds[^1];
            await source.SendRawAsync("$6\r\nparked\r\n$6\r\nparked\r\n"u8.ToArray(), wireId);
            (await first.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
            (await second.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        }

        async Task Dispatch()
        {
            if (shape == "stream")
            {
                await using var stream = await client.Strings.GetStreamAsync("routed");
                using var reader = new StreamReader(stream!);
                await Assert.That(await reader.ReadToEndAsync()).IsEqualTo("routed");
            }
            else await Send(client, shape == "cache" ? "string" : shape, "routed");
        }
    }

    [Test]
    [Arguments("activity", false)]
    [Arguments("activity", true)]
    [Arguments("duration", false)]
    [Arguments("duration", true)]
    public async Task ThrowingTelemetryListenerCannotUndoSuccessfulRecoveryProbe(string callback, bool connectionFailure)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Command });
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        {
            CircuitBreaker = Options(server).CircuitBreaker! with { HalfOpenProbeCount = 1 },
        });
        var (circuit, clock) = await Prepare(client, server);
        await Trip(client, server);
        clock.Advance(TimeSpan.FromMinutes(1));
        Exception listenerError = connectionFailure
            ? new RespireConnectionException("Telemetry listener failed.")
            : new InvalidOperationException("Telemetry listener failed.");
        EndpointCircuitState? callbackState = null;
        int? callbackProbes = null;
        var callbacks = 0;
        using var tracing = new ActivityListener
        {
            ShouldListenTo = source => source.Name == RespireTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (callback == "activity" && activity.OperationName == "GET") ThrowFromListener();
            },
        };
        ActivitySource.AddActivityListener(tracing);
        using var listener = new MeterListener();
        listener.InstrumentPublished = static (instrument, meter) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "db.client.operation.duration")
                meter.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, _, _, _) =>
        {
            if (callback == "duration") ThrowFromListener();
        });
        listener.Start();

        var error = await Failure(() => Send(client, "string", "recovery"));
        await Assert.That(error).IsSameReferenceAs(listenerError);
        await Assert.That(callbacks).IsEqualTo(1);
        await Assert.That(callbackState).IsEqualTo(EndpointCircuitState.Closed);
        await Assert.That(callbackProbes).IsEqualTo(0);
        await Assert.That(circuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Closed);
        await Assert.That(circuit.Snapshot().ActiveProbes).IsEqualTo(0);
        await Assert.That(server.ReceivedCommands.Count(command => command == "GET recovery")).IsEqualTo(1);

        void ThrowFromListener()
        {
            callbacks++;
            var snapshot = circuit.Snapshot();
            callbackState = snapshot.State;
            callbackProbes = snapshot.ActiveProbes;
            throw listenerError;
        }
    }

    [Test]
    public async Task RetiredOpenEndpointCannotRejectHealthyMaintenanceReplacement()
    {
        await using var target = Server();
        await using var source = Server();
        await using var client = await RespireClient.ConnectAsync(Options(source) with
        {
            Protocol = RespProtocol.Resp3, MaintenanceNotifications = RespireMaintenanceNotificationMode.Enabled,
        });
        await Prepare(client, source);
        var original = client.Core.Multiplexer.GetConnection();
        await Trip(client, source);
        await source.SendRawAsync(Encoding.UTF8.GetBytes($">4\r\n+MOVING\r\n:1\r\n:10\r\n+127.0.0.1:{target.Port}\r\n"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (client.Core.Multiplexer.ActiveConnectionEndpoint.Port != target.Port) await Task.Delay(1, deadline.Token);
        using var reply = await client.SendOnConnectionAsync("GET", original, new Cmd1(Verbs.Get, "routed"), deadline.Token);
        await Assert.That(reply.AsString()).IsEqualTo("routed");
        await Assert.That(target.ReceivedCommands.Count(command => command == "GET routed")).IsEqualTo(1);
    }

    [Test]
    public async Task CacheHitsBypassAdmissionAndRejectedMissSendsNoTrackingPrelude()
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        {
            Protocol = RespProtocol.Resp3, ClientSideCache = new(),
        });
        var (circuit, clock) = await Prepare(client, server);
        await client.GetStringAsync("cached");
        await Trip(client, server);
        var commands = server.CommandsSeen;
        await Assert.That(await client.GetStringAsync("cached")).IsEqualTo("cached");
        await Assert.That(await Failure(() => Send(client, "string", "missing"))).IsTypeOf<RespireCircuitOpenException>();
        await Assert.That(server.CommandsSeen).IsEqualTo(commands);
        clock.Advance(TimeSpan.FromMinutes(1));
        await Task.WhenAll(Send(client, "string", "full-1"), Send(client, "string", "full-2"));
        await Assert.That(circuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Closed);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task WireTransportFailuresOpenCircuitAndFailedProbeStartsNewDelay(bool protocol)
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var (circuit, clock) = await Prepare(client, server);
        if (!protocol) server.CloseConnectionAfterCommand = server.CommandsSeen + 1;
        var error = await Failure(() => Send(client, "string", protocol ? "malformed" : "disconnect"));
        await Assert.That(error is RespireConnectionException or RespireProtocolException).IsTrue();
        await Assert.That(circuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Open);
        await Assert.That(await Failure(() => Send(client, "raw", "rejected"))).IsTypeOf<RespireCircuitOpenException>();
        // Recovery is demand-driven and independent of circuit timing; no application
        // command retries while the replacement socket is being established.
        client.Core.Multiplexer.ScheduleReconnect(0);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!client.Core.Multiplexer.IsConnected) await Task.Delay(1, deadline.Token);
        clock.Advance(TimeSpan.FromMinutes(1));
        await Trip(client, server);
        await Assert.That(circuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Open);
        await Assert.That(await Failure(() => Send(client, "string", "rejected-again"))).IsTypeOf<RespireCircuitOpenException>();
        clock.Advance(TimeSpan.FromMinutes(1));
        await Task.WhenAll(Send(client, "string", "full-1"), Send(client, "raw", "full-2"));
        await Assert.That(circuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Closed);
    }

    [Test]
    [Arguments("string")]
    [Arguments("bytes")]
    [Arguments("integer")]
    [Arguments("raw")]
    [Arguments("fire-and-forget")]
    public async Task OpenEndpointRejectsWithoutWriting(string shape)
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var (circuit, clock) = await Prepare(client, server);
        await Trip(client, server);
        var commands = server.CommandsSeen;
        var error = await Failure(() => Send(client.WithKeyPrefix("view:"), shape, "rejected"));
        await Assert.That(error).IsTypeOf<RespireCircuitOpenException>();
        var rejected = (RespireCircuitOpenException)error;
        await Assert.That(rejected.Endpoint).IsEqualTo(new RespireEndpoint("127.0.0.1", server.Port));
        await Assert.That(rejected.RetryAfter).IsEqualTo(TimeSpan.FromMinutes(1));
        await Assert.That(server.CommandsSeen).IsEqualTo(commands);
        clock.Advance(TimeSpan.FromMinutes(1));
        await Task.WhenAll(Send(client, shape, "recovery"), Send(client, "string", "recovery-2"));
        // Discarded fire-and-forget replies do not close a recovery probe.
        if (shape == "fire-and-forget") await Send(client, "string", "recovery-3");
        await Assert.That(circuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Closed);
        await Assert.That(server.ReceivedCommands.Any(command => command.Contains("rejected", StringComparison.Ordinal))).IsFalse();
        await Assert.That(server.ReceivedCommands.Count(command => command == "GET fail")).IsEqualTo(1);
    }

    [Test]
    public async Task ConcurrentHalfOpenProbesRejectOverflowAndRecoverInFifoOrder()
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var (circuit, clock) = await Prepare(client, server);
        await Trip(client, server);
        clock.Advance(TimeSpan.FromMinutes(1));
        server.SuppressReply = command => command.StartsWith("GET probe", StringComparison.Ordinal);
        var first = client.GetStringAsync("probe-1").AsTask();
        var second = client.ExecuteAsync("GET", "probe-2").AsTask();
        await Received(server, "GET probe-2");
        await Assert.That(circuit.Snapshot().ActiveProbes).IsEqualTo(2);
        await Assert.That(await Failure(() => Send(client, "string", "overflow"))).IsTypeOf<RespireCircuitOpenException>();
        await server.SendRawAsync("$3\r\none\r\n$3\r\ntwo\r\n"u8.ToArray());
        await Assert.That(await first).IsEqualTo("one");
        using (var reply = await second) await Assert.That(reply.AsString()).IsEqualTo("two");
        await Assert.That(circuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Closed);
        server.SuppressReply = null;
        await Assert.That(await client.GetStringAsync("after")).IsEqualTo("after");
        await Assert.That(server.ReceivedCommands.Any(command => command.Contains("overflow", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    [Arguments("cancel")]
    [Arguments("pre-cancel")]
    [Arguments("writer")]
    [Arguments("capacity-timeout")]
    public async Task IgnoredExitPathsReleaseRecoveryCapacity(string exit)
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server) with { MaxInflightCommands = 1 });
        var (circuit, clock) = await Prepare(client, server);
        await Trip(client, server);
        clock.Advance(TimeSpan.FromMinutes(1));
        server.SuppressReply = command => command == "GET parked";
        using var cancellation = new CancellationTokenSource();
        Task? parked = null;
        if (exit is "cancel" or "capacity-timeout")
        {
            parked = exit == "capacity-timeout"
                ? HoldCapacity()
                : Send(client, "string", "parked", cancellation.Token);
            await Received(server, "GET parked");
        }
        async Task HoldCapacity()
        {
            using var reply = await client.Core.Multiplexer.GetConnection().SendWithoutResponseTimeoutAsync(
                new Cmd1(Verbs.Get, "parked"), cancellation.Token);
        }
        switch (exit)
        {
            case "cancel":
                cancellation.Cancel();
                await Assert.That(await Failure(() => parked!)).IsTypeOf<OperationCanceledException>();
                break;
            case "pre-cancel":
                cancellation.Cancel();
                await Assert.That(await Failure(() => Send(client, "string", "never", cancellation.Token))).IsTypeOf<OperationCanceledException>();
                break;
            case "writer":
                await Assert.That(await Failure(async () => { using var reply = await client.SendAsync("GET", new ThrowingCommand(), default); }))
                    .IsTypeOf<IOException>();
                break;
            case "capacity-timeout":
                await Assert.That(await Failure(() => Send(client, "raw", "never"))).IsTypeOf<RespireTimeoutException>();
                cancellation.Cancel();
                await Failure(() => parked!);
                break;
        }
        await Assert.That(circuit.Snapshot().ActiveProbes).IsEqualTo(0);
        if (parked is not null) await server.SendRawAsync("$4\r\nlate\r\n"u8.ToArray());
        server.SuppressReply = null;
        // Every ignored path leaves enough capacity for the entire later probe batch.
        await Assert.That(await client.GetStringAsync("full-1")).IsEqualTo("full-1");
        await Assert.That(await client.GetStringAsync("full-2")).IsEqualTo("full-2");
        await Assert.That(circuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Closed);
        await Assert.That(server.ReceivedCommands.Any(command => command.Contains("never", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task RedisErrorsAreHealthyOutcomesAndValidationDoesNotEnterCircuit()
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var (circuit, clock) = await Prepare(client, server);
        await Assert.That(await Failure(() => Send(client, "string", "wrongtype"))).IsTypeOf<RespireServerException>();
        await Assert.That(circuit.Snapshot().FailureCount).IsEqualTo(0);
        await Trip(client, server);
        clock.Advance(TimeSpan.FromMinutes(1));
        await Assert.That(await Failure(async () => await client.ExecuteFireAndForgetAsync("BLPOP", "x", "1"))).IsTypeOf<NotSupportedException>();
        await Assert.That(circuit.Snapshot().ActiveProbes).IsEqualTo(0);
        await Assert.That(await Failure(() => Send(client, "string", "wrongtype"))).IsTypeOf<RespireServerException>();
        await Send(client, "raw", "healthy");
        await Assert.That(circuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Closed);
    }

    [Test]
    public async Task RejectedDispatchDoesNotChangeBatchOrTransactionAdmission()
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var (circuit, _) = await Prepare(client, server);
        await Trip(client, server);
        using var batch = client.CreateBatch();
        var pending = batch.Strings.GetString("batch");
        await batch.ExecuteAsync();
        await Assert.That(await pending).IsEqualTo("batch");
        await using var transaction = client.CreateTransaction();
        var transactional = transaction.Strings.GetString("transaction");
        await transaction.CommitAsync();
        await Assert.That(await transactional).IsEqualTo("transaction");
        await Assert.That(circuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Open);
    }

    [Test]
    [Arguments("blocking")]
    [Arguments("upload")]
    public async Task RejectedDedicatedCommandsReturnHealthyLeaseWithoutNewHandshakes(string shape)
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server));
        await Prepare(client, server);
        var pool = await client.Core.GetDedicatedPoolAsync(default);
        var lease = await pool.RentAsync(default,
            kind: shape == "upload" ? DedicatedLeaseKind.Streaming : DedicatedLeaseKind.Ordinary);
        pool.Return(lease);
        await Trip(client, server);
        var commands = server.CommandsSeen;
        for (var i = 0; i < 8; i++)
        {
            using var payload = new MemoryStream("value"u8.ToArray());
            var error = await Failure(async () =>
            {
                if (shape == "upload")
                {
                    await client.Strings.SetAsync("rejected", payload, 5);
                }
                else using (var response = await client.ExecuteAsync("BLPOP", "rejected", "1")) { }
            });
            await Assert.That(error).IsTypeOf<RespireCircuitOpenException>();
            await Assert.That(payload.Position).IsEqualTo(0);
            var reused = await pool.RentAsync(default,
                kind: shape == "upload" ? DedicatedLeaseKind.Streaming : DedicatedLeaseKind.Ordinary);
            try { await Assert.That(ReferenceEquals(reused, lease)).IsTrue(); }
            finally { pool.Return(reused); }
            await Assert.That(pool.CaptureRetirementState().Borrowed).IsEqualTo(0);
        }
        await Assert.That(server.CommandsSeen).IsEqualTo(commands);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task UploadHandoffBeforeAdmissionUsesReplacementCircuit(bool targetOpen, bool retireSocket)
    {
        await using var target = Server();
        await using var source = Server();
        await using var client = await RespireClient.ConnectAsync(Options(source) with
        {
            Protocol = RespProtocol.Resp3, MaintenanceNotifications = RespireMaintenanceNotificationMode.Enabled,
            CommandTimeout = TimeSpan.FromSeconds(10),
        });
        var (sourceCircuit, _) = await Prepare(client, source);
        var multiplexer = client.Core.Multiplexer;
        var original = multiplexer.GetConnection();
        var pool = await client.Core.GetDedicatedPoolAsync(default);
        var lease = await pool.RentAsync(default, kind: DedicatedLeaseKind.Streaming);
        pool.Return(lease);
        var failed = client.Core.Circuits!.Acquire(new("127.0.0.1", source.Port), default);
        failed.Failed(new RespireConnectionException("source unavailable"), default);
        failed.Dispose();
        var endpoint = new RespireEndpoint("127.0.0.1", target.Port);
        var targetAdmission = client.Core.Circuits.Acquire(endpoint, default);
        if (targetOpen) targetAdmission.Failed(new RespireConnectionException("target unavailable"), default);
        targetAdmission.Dispose();
        var targetCircuit = client.Core.Circuits.GetForTests(endpoint);
        var announcement = multiplexer.CaptureMovingAnnouncement(0, original,
            new MaintenanceNotification("MOVING", 1, 10, endpoint));
        var commands = source.CommandsSeen;
        using var published = new ManualResetEventSlim();
        multiplexer.MovingHandoffPublished += published.Set;
        var handoffs = 0;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Respire",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity =>
            {
                if (activity.OperationName != "SET" || !Equals(activity.GetTagItem("server.port"), source.Port)
                    || Interlocked.Increment(ref handoffs) != 1) return;
                // The upload has rented the warmed lease, but has not acquired circuit admission.
                // Publish the real maintenance replacement before allowing admission to continue.
                multiplexer.QueueDedicatedMovingHandoff(original, announcement, static () => true);
                if (!published.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Upload handoff did not publish.");
                if (retireSocket) _ = lease.RetireAsync();
            },
        };
        ActivitySource.AddActivityListener(listener);
        using var payload = new MemoryStream("value"u8.ToArray());
        if (targetOpen)
        {
            var error = await Failure(async () => await client.Strings.SetAsync("routed", payload, 5));
            await Assert.That(error).IsTypeOf<RespireCircuitOpenException>();
            await Assert.That(((RespireCircuitOpenException)error).Endpoint).IsEqualTo(endpoint);
            await Assert.That(payload.Position).IsEqualTo(0);
        }
        else
        {
            await client.Strings.SetAsync("routed", payload, 5);
            await Assert.That(payload.Position).IsEqualTo(5);
        }
        await Assert.That(handoffs).IsEqualTo(1);
        await Assert.That(multiplexer.ActiveConnectionEndpoint).IsEqualTo(endpoint);
        await Assert.That(source.CommandsSeen).IsEqualTo(commands);
        await Assert.That(target.ReceivedCommands.Count(command => command == "SET routed value")).IsEqualTo(targetOpen ? 0 : 1);
        await Assert.That(sourceCircuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Open);
        await Assert.That(sourceCircuit.Snapshot().ActiveProbes).IsEqualTo(0);
        await Assert.That(targetCircuit.Snapshot().State).IsEqualTo(targetOpen ? EndpointCircuitState.Open : EndpointCircuitState.Closed);
        await Assert.That(targetCircuit.Snapshot().ActiveProbes).IsEqualTo(0);
        await Assert.That(pool.CaptureRetirementState().Borrowed).IsEqualTo(0);
        var replacementPool = await client.Core.GetDedicatedPoolAsync(default);
        await Assert.That(replacementPool.CaptureRetirementState().Borrowed).IsEqualTo(0);
    }

    [Test]
    [Arguments("stream")]
    [Arguments("upload")]
    [Arguments("blocking")]
    public async Task DedicatedCommandsRejectBeforeTheirApplicationFrame(string shape)
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server));
        await Prepare(client, server);
        await Trip(client, server);
        var error = await Failure(async () =>
        {
            if (shape == "stream") await using (var stream = await client.Strings.GetStreamAsync("rejected")) { }
            else if (shape == "upload")
            {
                using var payload = new MemoryStream("value"u8.ToArray());
                await client.Strings.SetAsync("rejected", payload, 5);
            }
            else using (var result = await client.ExecuteAsync("BLPOP", "rejected", "1")) { }
        });
        await Assert.That(error).IsTypeOf<RespireCircuitOpenException>();
        await Assert.That(server.ReceivedCommands.Any(command => command.Contains("rejected", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    [Arguments("cancel")]
    [Arguments("dispose")]
    [Arguments("read-cancel")]
    public async Task StreamCancellationOrDisposalReleasesIncompleteFrameProbe(string exit)
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var (circuit, clock) = await Prepare(client, server);
        await Trip(client, server);
        clock.Advance(TimeSpan.FromMinutes(1));
        server.SuppressReply = command => command == "GET stream";
        using var cancellation = new CancellationTokenSource();
        var pending = client.Strings.GetStreamAsync("stream", cancellation.Token).AsTask();
        await Received(server, "GET stream");
        await server.SendRawAsync("$100\r\nx"u8.ToArray());
        await using var stream = await pending;
        await Assert.That(circuit.Snapshot().ActiveProbes).IsEqualTo(1);
        if (exit == "cancel") cancellation.Cancel();
        else if (exit == "dispose") await stream!.DisposeAsync();
        else
        {
            var buffer = new byte[100];
            await Assert.That(await stream!.ReadAsync(buffer)).IsEqualTo(1);
            using var readCancellation = new CancellationTokenSource();
            var read = stream.ReadAsync(buffer, readCancellation.Token);
            readCancellation.Cancel();
            await Assert.That(await Failure(async () => await read)).IsTypeOf<OperationCanceledException>();
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (circuit.Snapshot().ActiveProbes != 0) await Task.Delay(1, deadline.Token);
        // Complete the discarded frame before later replies: the same connection keeps FIFO.
        await server.SendRawAsync(Encoding.UTF8.GetBytes(new string('x', 99) + "\r\n"));
        server.SuppressReply = null;
        await Send(client, "string", "full-1");
        await Send(client, "string", "full-2");
        await Assert.That(circuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Closed);
    }

    [Test]
    public async Task StreamFrameCompletionRacesCancellationAndDisposalWithoutCompletingReplacementProbes()
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var (circuit, clock) = await Prepare(client, server);
        for (var i = 0; i < 8; i++)
        {
            await Trip(client, server);
            clock.Advance(TimeSpan.FromMinutes(1));
            server.SuppressReply = command => command == "GET stream";
            using var cancellation = new CancellationTokenSource();
            var pending = client.Strings.GetStreamAsync("stream", cancellation.Token).AsTask();
            await Received(server, "GET stream", i + 1);
            await server.SendRawAsync("$2\r\nx"u8.ToArray());
            await using var stream = await pending;
            await Task.WhenAll(server.SendRawAsync("y\r\n"u8.ToArray()), Task.Run(async () =>
            {
                cancellation.Cancel();
                await stream!.DisposeAsync();
            }));
            await Assert.That(circuit.Snapshot().ActiveProbes).IsEqualTo(0);
            server.SuppressReply = null;
            // If frame completion wins, it has already satisfied one probe. At most
            // one more concurrent admission is allowed until the circuit closes.
            await Send(client, "string", "race-full-1");
            await Send(client, "raw", "race-full-2");
            await Assert.That(circuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Closed);
        }
    }

    [Test]
    public async Task TopologyConfigurationsRejectCircuitOption()
    {
        foreach (var options in new[]
        {
            new RespireOptions { Endpoints = [new("localhost")], UseCluster = true },
            new RespireOptions { Endpoints = [new("localhost")], SentinelPrimaryName = "primary" },
            new RespireOptions { Endpoints = [new("localhost")], ReplicaEndpoints = [new("replica")] },
        })
            await Assert.That(() => RespireClient.Create(options with { CircuitBreaker = new() }))
                .Throws<RespireConfigurationException>();
        await Assert.That(() => RespireClient.Create(new RespireOptions { Endpoints = [new("localhost")], CircuitBreaker = new() { HalfOpenProbeCount = 0 } }))
            .Throws<ArgumentOutOfRangeException>();
    }

    private static FakeRespServer Server() => new(32, FakeRespServer.OkReply)
    {
        ReplyOverride = (_, command) => command switch
        {
            _ when command.StartsWith("HELLO", StringComparison.Ordinal) => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
            "CLIENT ID" => ":1\r\n"u8.ToArray(),
            "GET wrongtype" => "-WRONGTYPE expected string\r\n"u8.ToArray(),
            "GET malformed" => ":invalid\r\n"u8.ToArray(),
            "GET transaction" => "+QUEUED\r\n"u8.ToArray(),
            _ when command.StartsWith("GET ", StringComparison.Ordinal) => Bulk(command[4..]),
            _ when command.StartsWith("STRLEN ", StringComparison.Ordinal) => ":5\r\n"u8.ToArray(),
            "MULTI" => FakeRespServer.OkReply,
            "EXEC" => "*1\r\n$11\r\ntransaction\r\n"u8.ToArray(),
            _ => null,
        },
    };

    private static byte[] Bulk(string value) => Encoding.UTF8.GetBytes($"${Encoding.UTF8.GetByteCount(value)}\r\n{value}\r\n");

    private static RespireOptions Options(FakeRespServer server) => new()
    {
        Endpoints = [new("127.0.0.1", server.Port)], Connections = 1, Protocol = RespProtocol.Resp2,
        ThreadPoolMonitoring = false, CommandTimeout = TimeSpan.FromMilliseconds(200),
        CircuitBreaker = new() { MinimumFailureCount = 1, FailureRateThreshold = 0.25, HalfOpenProbeCount = 2, OpenDuration = TimeSpan.FromMinutes(1) },
    };

    private static async Task<(EndpointCircuitBreaker Circuit, Clock Clock)> Prepare(RespireClient client, FakeRespServer server)
    {
        await client.GetStringAsync("warm");
        var circuit = client.Core.Circuits!.GetForTests(new("127.0.0.1", server.Port));
        var clock = new Clock();
        CircuitClock(circuit) = clock;
        return (circuit, clock);
    }

    private static async Task Trip(RespireClient client, FakeRespServer server)
    {
        server.SuppressReply = command => command == "GET fail";
        await Assert.That(await Failure(() => Send(client, "raw", "fail"))).IsTypeOf<RespireTimeoutException>();
        await server.SendRawAsync("$4\r\nlate\r\n"u8.ToArray(), server.ReceivedConnectionIds[^1]);
        server.SuppressReply = null;
    }

    private static async Task Send(IRespireClient client, string shape, string key, CancellationToken cancellationToken = default)
    {
        switch (shape)
        {
            case "string": await client.GetStringAsync(key, cancellationToken); break;
            case "bytes": await client.GetBytesAsync(key, cancellationToken); break;
            case "integer": await client.Strings.LengthAsync(key, cancellationToken); break;
            case "raw": using (var result = await client.ExecuteAsync("GET", [key], cancellationToken: cancellationToken)) { } break;
            case "fire-and-forget": await client.ExecuteFireAndForgetAsync("GET", [key], cancellationToken: cancellationToken); break;
        }
    }

    private static async Task<Exception> Failure(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception error) { return error; }
        throw new InvalidOperationException("Expected dispatch failure.");
    }

    private static async Task Received(FakeRespServer server, string command, int occurrences = 1)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (server.ReceivedCommands.Count(received => received == command) < occurrences) await Task.Delay(1, deadline.Token);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_clock")]
    private static extern ref TimeProvider CircuitClock(EndpointCircuitBreaker circuit);

    private sealed class Clock : TimeProvider
    {
        private long _timestamp = TimeProvider.System.GetTimestamp();
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public override long TimestampFrequency => TimeProvider.System.TimestampFrequency;
        internal void Advance(TimeSpan duration) => Interlocked.Add(ref _timestamp, (long)(duration.TotalSeconds * TimestampFrequency));
    }

    private readonly struct ThrowingCommand : IRespCommand
    {
        public ReadCommandKind ReadKind => ReadCommandKind.None;
        public void Write(ref RespWriter writer) => throw new IOException("Application command writer failed before dispatch.");
    }
}
