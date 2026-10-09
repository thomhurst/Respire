using System.Runtime.CompilerServices;
using System.Text;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Collections.Concurrent;
using Respire.Commands;
using Respire.Infrastructure;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

[NotInParallel]
public sealed class SentinelCircuitDispatchTests
{
    [Test]
    public async Task ActiveReplicaHistoriesSurviveTheIdleEndpointLimit()
    {
        await using var primary = Primary();
        await using var replica = Primary();
        var ports = Enumerable.Range(20000, StandaloneCircuitRegistry.RetainedEndpointLimit - 1)
            .Prepend(replica.Port).ToArray();
        await using var sentinel = Sentinel(() => primary.Port, ports);
        await using var client = await Connect(sentinel);
        await client.Core.ReadRouter.RefreshNowAsync(default);
        var (circuit, _) = Prepare(client, replica);
        Open(client, replica);
        var registry = client.Core.Circuits!;
        registry.Acquire(new("127.0.0.1", primary.Port), default).Dispose();
        foreach (var port in ports.Skip(1))
            registry.Acquire(new("127.0.0.1", port), default).Dispose();
        for (var i = 0; i < 64; i++) registry.Acquire(new("history", 1000 + i), default).Dispose();

        await Assert.That(ReferenceEquals(registry.GetForTests(new("127.0.0.1", replica.Port)), circuit)).IsTrue();
        await Assert.That(() => registry.Acquire(new("127.0.0.1", replica.Port), default))
            .Throws<RespireCircuitOpenException>();
        await Assert.That(registry.CountForTests).IsEqualTo(ports.Length + 1);

        // Once discovery removes replicas, their idle histories become eligible for trimming.
        Array.Fill(ports, replica.Port);
        await client.Core.ReadRouter.RefreshNowAsync(default);
        registry.Acquire(new("history", 2000), default).Dispose();
        await Assert.That(registry.CountForTests).IsEqualTo(StandaloneCircuitRegistry.RetainedEndpointLimit);
        await Assert.That(ReferenceEquals(registry.GetForTests(new("127.0.0.1", replica.Port)), circuit)).IsTrue();
    }

    [Test]
    [Arguments("batch")]
    [Arguments("transaction")]
    public async Task RetiredQueueSelectionDoesNotRecordEndpointFailure(string shape)
    {
        await using var first = Primary();
        await using var second = Primary();
        var port = first.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref port));
        await using var client = await Connect(sentinel);
        var (circuit, _) = Prepare(client, first);
        var generation = client.Core.Sentinel!.Current!;
        Volatile.Write(ref port, second.Port);
        await Assert.That(generation.TryRetire()).IsTrue();

        // Reproduce selection after the ready snapshot, before a replacement is published.
        await Assert.That(() => SelectCircuitConnection(client, generation.Multiplexer, default))
            .Throws<RespireConnectionRetiredException>();
        await Assert.That(SelectReadyConnection(client, generation.Multiplexer, default)).IsNull();
        await Assert.That(circuit.Snapshot().FailureCount).IsEqualTo(0);
        await Send(client, shape, "healthy");
        await Assert.That(client.Core.Sentinel.Current!.Endpoint.Port).IsEqualTo(second.Port);
        await Assert.That(circuit.Snapshot().FailureCount).IsEqualTo(0);
    }

    [Test]
    [Arguments(RespireReadFrom.Replica, "string")]
    [Arguments(RespireReadFrom.Replica, "bytes")]
    [Arguments(RespireReadFrom.Replica, "converted")]
    [Arguments(RespireReadFrom.ReplicaPreferred, "string")]
    [Arguments(RespireReadFrom.ReplicaPreferred, "bytes")]
    [Arguments(RespireReadFrom.ReplicaPreferred, "converted")]
    public async Task PreparedReplicaReadsRespectAdmissionAndCompleteRecovery(RespireReadFrom policy, string shape)
    {
        if (Environment.GetEnvironmentVariable("TUNIT_DISABLE_HTML_REPORTER") == "true")
            await Assert.That(RespireTelemetry.IsOperationEnabled("GET")).IsFalse();
        await using var primary = Primary();
        await using var replica = Primary();
        var dataReply = replica.ReplyOverride!;
        replica.ReplyOverride = (id, command) => command == "ROLE"
            ? "*5\r\n+slave\r\n+127.0.0.1\r\n:6379\r\n+connected\r\n:0\r\n"u8.ToArray()
            : dataReply(id, command);
        await using var sentinel = Sentinel(() => primary.Port, replica.Port);
        await using var client = await Connect(sentinel);
        await client.Core.ReadRouter.RefreshNowAsync(default);
        var view = (RespireClient)client.WithReadFrom(policy);
        await Send(view, shape, "warm");
        await Assert.That(client.Core.ReadRouter.TryAcquireReadyConnection(policy, default)!.Port).IsEqualTo(replica.Port);
        var (circuit, clock) = Prepare(client, replica);
        Open(client, replica);

        var error = await Failure(() => Send(view, shape, "never"));
        await Assert.That(error).IsTypeOf<RespireCircuitOpenException>();
        await Assert.That(((RespireCircuitOpenException)error).Endpoint).IsEqualTo(new RespireEndpoint("127.0.0.1", replica.Port));
        await Assert.That(replica.ReceivedCommands.Contains("GET never")).IsFalse();
        await Assert.That(replica.ReceivedCommands.Contains("STRLEN never")).IsFalse();
        await Assert.That(primary.ReceivedCommands.Any(command => command.StartsWith("GET ") || command.StartsWith("STRLEN "))).IsFalse();

        clock.Advance();
        await Send(view, shape, "first");
        await Assert.That(circuit.Snapshot().SuccessfulProbes).IsEqualTo(1);
        await Send(view, shape, "second");
        await Assert.That(circuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Closed);
        await Assert.That(circuit.Snapshot().ActiveProbes).IsEqualTo(0);
    }

    [Test]
    [Arguments("raw")]
    [Arguments("typed")]
    [Arguments("queued")]
    [Arguments("ordered")]
    public async Task CursorRetirementAfterSelectionDoesNotReroute(string shape)
    {
        await using var first = Primary();
        await using var second = Primary();
        var port = first.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref port));
        await using var client = await Connect(sentinel);
        var view = (RespireClient)client.WithReadFrom(RespireReadFrom.PrimaryPreferred);
        await using var enumerator = view.Keys.ScanAsync().GetAsyncEnumerator();
        if (shape == "typed") await Assert.That(await enumerator.MoveNextAsync()).IsTrue();
        else { using var page = await view.ExecuteAsync(RespireCommands.Key.SCAN, ["0"]); }

        var issuingConnection = client.Core.Multiplexer.GetConnection();
        var (circuit, _) = Prepare(client, first);
        using var clock = new AdmissionGateClock();
        CircuitClock(circuit) = clock;
        clock.Arm();
        var pending = Task.Run(async () =>
        {
            if (shape == "typed") await enumerator.MoveNextAsync();
            else if (shape is "queued" or "ordered")
            {
                var command = new CatalogCommand(RespireCommands.Key.SCAN, ["7"]);
                var response = shape == "ordered"
                    ? await QueuedCircuitDispatch.EnqueueAsync(client.Core.Circuits!, issuingConnection,
                        command, "SCAN", default, client: client)
                    : QueuedCircuitDispatch.SendAsync(client.Core.Circuits!, issuingConnection,
                        command, "SCAN", default, default, client: client);
                using var page = await response;
            }
            else { using var page = await view.ExecuteAsync(RespireCommands.Key.SCAN, ["7"]); }
        });
        try
        {
            // Pause admission after selection of the cursor's issuing connection.
            await clock.Selected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Volatile.Write(ref port, second.Port);
            await client.Core.Sentinel!.GetGenerationAsync(default, forceDiscovery: true);
        }
        finally { clock.Release(); }

        await Assert.That(await Failure(async () => await pending)).IsTypeOf<RespireConnectionRetiredException>();
        await Assert.That(first.ReceivedCommands.Any(command => command.StartsWith("SCAN 7"))).IsFalse();
        await Assert.That(second.ReceivedCommands.Any(command => command.StartsWith("SCAN "))).IsFalse();
        await Assert.That(circuit.Snapshot().FailureCount).IsEqualTo(0);
        await Assert.That(circuit.Snapshot().ActiveProbes).IsEqualTo(0);
    }

    [Test]
    [Arguments("string")]
    [Arguments("bytes")]
    [Arguments("converted")]
    [Arguments("raw")]
    [Arguments("fire-and-forget")]
    [Arguments("batch")]
    [Arguments("transaction")]
    public async Task OpenDataEndpointRejectsEveryDispatchShape(string shape)
    {
        await using var primary = Primary();
        await using var sentinel = Sentinel(() => primary.Port);
        await using var client = await Connect(sentinel);
        var (circuit, _) = Prepare(client, primary);
        Open(client, primary);
        var error = await Failure(() => Send(client, shape, "never"));
        await Assert.That(error).IsTypeOf<RespireCircuitOpenException>();
        await Assert.That(((RespireCircuitOpenException)error).Endpoint).IsEqualTo(new RespireEndpoint("127.0.0.1", primary.Port));
        await Assert.That(((RespireCircuitOpenException)error).RetryAfter > TimeSpan.Zero).IsTrue();
        await Assert.That(primary.ReceivedCommands.Contains("GET never")).IsFalse();
        await Assert.That(primary.ReceivedCommands.Contains("STRLEN never")).IsFalse();
        await Assert.That(primary.ReceivedCommands.Contains("MULTI")).IsFalse();
        await Assert.That(circuit.Snapshot().ActiveProbes).IsEqualTo(0);
    }

    [Test]
    [Arguments("string")]
    [Arguments("bytes")]
    [Arguments("converted")]
    [Arguments("raw")]
    [Arguments("fire-and-forget")]
    [Arguments("batch")]
    [Arguments("transaction")]
    public async Task HealthyPrimaryDoesNotInheritOldEndpointFailures(string shape)
    {
        await using var first = Primary();
        await using var second = Primary();
        var port = first.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref port));
        await using var client = await Connect(sentinel);
        var (oldCircuit, _) = Prepare(client, first);
        Open(client, first);
        var old = client.Core.Multiplexer.GetConnection();
        port = second.Port;
        await client.Core.Sentinel!.GetGenerationAsync(default, forceDiscovery: true);
        await Send(client, shape, "healthy");
        var applicationCommand = shape == "converted" ? "STRLEN healthy" : "GET healthy";
        await Received(second, applicationCommand);
        await Assert.That(first.ReceivedCommands.Contains("GET healthy")).IsFalse();
        await Assert.That(second.ReceivedCommands.Count(command => command == applicationCommand)).IsEqualTo(1);
        await Assert.That(oldCircuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Open);
        await Assert.That(client.Core.Circuits!.GetForTests(new("127.0.0.1", second.Port)).Snapshot().State)
            .IsEqualTo(EndpointCircuitState.Closed);
        // Even an explicitly retained old connection must reject retirement before consulting its open circuit.
        var command = new CatalogCommand(RespireCommands.String.GET, ["retired"]);
        using var result = await client.SendOnConnectionAsync("GET", old, command, default);
        await Assert.That(ResponseReader.String(in result)).IsEqualTo("retired");
        await Assert.That(first.ReceivedCommands.Contains("GET retired")).IsFalse();
        await Assert.That(second.ReceivedCommands.Count(value => value == "GET retired")).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RetiredBatchConnectionUsesNewEndpointAdmission(bool orderedAdmission)
    {
        await using var first = Primary();
        await using var second = Primary();
        var port = first.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref port));
        await using var client = await Connect(sentinel);
        Prepare(client, first);
        Open(client, first);
        var old = client.Core.Multiplexer.GetConnection();
        port = second.Port;
        await client.Core.Sentinel!.GetGenerationAsync(default, forceDiscovery: true);
        var command = new CatalogCommand(RespireCommands.String.GET, ["queued"]);
        var response = orderedAdmission
            ? await QueuedCircuitDispatch.EnqueueAsync(client.Core.Circuits!, old, command, "GET", default, client: client)
            : QueuedCircuitDispatch.SendAsync(client.Core.Circuits!, old, command, "GET", default, default, client: client);
        using var reply = await response;
        await Assert.That(ResponseReader.String(in reply)).IsEqualTo("queued");
        await Assert.That(first.ReceivedCommands.Contains("GET queued")).IsFalse();
        await Assert.That(second.ReceivedCommands.Count(value => value == "GET queued")).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RetiredQueueKeepsOneTelemetryScopeOnReplacement(bool transactional)
    {
        using var configuration = new MetricConfigurationScope();
        await using var first = Primary();
        await using var second = Primary();
        var port = first.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref port));
        await using var client = await Connect(sentinel);
        var (circuit, _) = Prepare(client, first);
        using var clock = new AdmissionGateClock();
        CircuitClock(circuit) = clock;
        var observed = new AsyncLocal<bool>();
        var started = new ConcurrentQueue<Activity>();
        var stopped = new ConcurrentQueue<Activity>();
        var measurements = new ConcurrentQueue<Dictionary<string, object?>>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Respire",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity => { if (observed.Value) started.Enqueue(activity); },
            ActivityStopped = activity => { if (observed.Value) stopped.Enqueue(activity); },
        };
        ActivitySource.AddActivityListener(listener);
        using var meter = new MeterListener();
        meter.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "db.client.operation.duration")
                owner.EnableMeasurementEvents(instrument);
        };
        meter.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            if (observed.Value) measurements.Enqueue(tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value));
        });
        meter.Start();
        using var batch = client.CreateBatch();
        await using var transaction = client.CreateTransaction();
        IRespireCommandQueue queue = transactional ? transaction : batch;
        _ = queue.Strings.GetString("healthy");
        clock.Arm();
        var execution = Task.Run(async () =>
        {
            observed.Value = true;
            if (transactional) await transaction.CommitAsync();
            else await batch.ExecuteAsync();
            await Assert.That(Activity.Current).IsNull();
            observed.Value = false;
        });
        try
        {
            await clock.Selected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Volatile.Write(ref port, second.Port);
            await client.Core.Sentinel!.GetGenerationAsync(default, forceDiscovery: true);
        }
        finally { clock.Release(); }
        await execution.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(started.Count).IsEqualTo(1);
        await Assert.That(stopped.Count).IsEqualTo(1);
        var activity = stopped.Single();
        await Assert.That(activity.GetTagItem("server.port")).IsEqualTo(second.Port);
        await Assert.That(measurements.Count).IsEqualTo(1);
        await Assert.That(measurements.Single()["server.port"]).IsEqualTo(second.Port);
        await Assert.That(second.ReceivedCommands.Contains("GET healthy")).IsTrue();
        await Assert.That(first.ReceivedCommands.Contains("GET healthy")).IsFalse();
    }

    [Test]
    [Arguments(1, false)]
    [Arguments(2, false)]
    [Arguments(2, true)]
    public async Task BatchAdmissionRaceRetainsOrderOnReplacementPrimary(int connections, bool includeCursor)
    {
        await using var first = Primary();
        await using var second = Primary();
        var port = first.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref port));
        await using var client = await Connect(sentinel, connections: connections);
        var (circuit, _) = Prepare(client, first);
        using var clock = new AdmissionGateClock();
        CircuitClock(circuit) = clock;
        using var batch = client.CreateBatch();
        var one = batch.GetString("first");
        var two = batch.GetString("second");
        var cursor = includeCursor ? ((IPendingSink)batch).Add("SCAN",
            new CatalogCommand(RespireCommands.Key.SCAN, ["7"]), static (_, _) => true) : null;
        clock.Arm();
        var execution = Task.Run(async () => await batch.TryExecuteAsync());
        try
        {
            await clock.Selected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Volatile.Write(ref port, second.Port);
            await client.Core.Sentinel!.GetGenerationAsync(default, forceDiscovery: true);
        }
        finally { clock.Release(); }
        await execution.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(await one).IsEqualTo("first");
        await Assert.That(await two).IsEqualTo("second");
        await Assert.That(first.ReceivedCommands.Any(command => command.StartsWith("GET "))).IsFalse();
        await Assert.That(second.ReceivedCommands.Where(command => command.StartsWith("GET ")).ToArray())
            .IsEquivalentTo(new[] { "GET first", "GET second" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        var commands = second.ReceivedCommands;
        var connectionIds = second.ReceivedConnectionIds;
        await Assert.That(commands.Select((command, index) => (command, index))
            .Where(entry => entry.command.StartsWith("GET "))
            .Select(entry => connectionIds[entry.index]).Distinct().Count()).IsEqualTo(1);
        await Assert.That(circuit.Snapshot().FailureCount).IsEqualTo(0);
        await Assert.That(circuit.Snapshot().ActiveProbes).IsEqualTo(0);
        if (cursor is not null)
        {
            await Assert.That(cursor.Error).IsTypeOf<RespireConnectionRetiredException>();
            await Assert.That(second.ReceivedCommands.Any(command => command.StartsWith("SCAN "))).IsFalse();
        }
    }

    [Test]
    public async Task RetiredUnrelatedSocketDoesNotBorrowThePrimaryRoute()
    {
        await using var primary = Primary();
        await using var unrelated = Primary();
        await using var sentinel = Sentinel(() => primary.Port);
        await using var client = await Connect(sentinel);
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", unrelated.Port,
            new RespireConnectionOptions { Protocol = RespProtocol.Resp2 });
        connection.StopAcceptingCommands();
        var command = new CatalogCommand(RespireCommands.String.GET, ["never"]);
        await Assert.That(await Failure(async () =>
        {
            using var reply = await client.SendOnConnectionAsync("GET", connection, command, default);
        })).IsTypeOf<RespireConnectionRetiredException>();
        await Assert.That(primary.ReceivedCommands.Contains("GET never")).IsFalse();
        await Assert.That(unrelated.ReceivedCommands.Contains("GET never")).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CanceledRecoveryQueueReleasesCapacityAndKeepsFifo(bool transactional)
    {
        await using var primary = Primary();
        await using var sentinel = Sentinel(() => primary.Port);
        await using var client = await Connect(sentinel);
        var (circuit, clock) = Prepare(client, primary);
        Open(client, primary);
        clock.Advance();
        primary.SuppressReply = command => command is "GET canceled" or "MULTI" or "EXEC";
        using var cancellation = new CancellationTokenSource();
        using var batch = client.CreateBatch();
        await using var transaction = client.CreateTransaction();
        IRespireCommandQueue queue = transactional ? transaction : batch;
        var pending = queue.Strings.GetString("canceled");
        var execution = transactional ? transaction.CommitAsync(cancellation.Token).AsTask()
            : batch.ExecuteAsync(cancellation.Token).AsTask();
        await Received(primary, transactional ? "EXEC" : "GET canceled");
        cancellation.Cancel();
        await Assert.That(await Failure(async () => await execution)).IsTypeOf<OperationCanceledException>();
        await Assert.That(pending.Error).IsTypeOf<OperationCanceledException>();
        await Assert.That(circuit.Snapshot().ActiveProbes).IsEqualTo(0);
        await Assert.That(circuit.Snapshot().SuccessfulProbes).IsEqualTo(0);
        primary.SuppressReply = null;
        await primary.SendRawAsync(transactional
            ? "+OK\r\n+QUEUED\r\n*1\r\n$4\r\nlate\r\n"u8.ToArray() : "$4\r\nlate\r\n"u8.ToArray());
        using var recovery = client.CreateBatch();
        var one = recovery.Strings.GetString("first");
        var two = recovery.Strings.GetString("second");
        await recovery.ExecuteAsync();
        await Assert.That(await one).IsEqualTo("first");
        await Assert.That(await two).IsEqualTo("second");
        await Assert.That(circuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Closed);
        await Assert.That(primary.ReceivedCommands.Count(command => command == "GET canceled")).IsEqualTo(1);
    }

    [Test]
    public async Task FailedRecoveryDispatchReleasesPermitWithoutOpeningCircuit()
    {
        await using var primary = Primary();
        await using var sentinel = Sentinel(() => primary.Port);
        await using var client = await Connect(sentinel);
        var (circuit, clock) = Prepare(client, primary);
        Open(client, primary);
        clock.Advance();
        using var failed = client.CreateBatch();
        var pending = ((IPendingSink)failed).Add<ThrowingCommand, bool>("GET", new(), static (_, _) => true);
        await Assert.That(await Failure(async () => await failed.ExecuteAsync())).IsTypeOf<IOException>();
        await Assert.That(pending.Error).IsTypeOf<IOException>();
        await Assert.That(circuit.Snapshot().ActiveProbes).IsEqualTo(0);
        using var recovery = client.CreateBatch();
        var one = recovery.Strings.GetString("first");
        var two = recovery.Strings.GetString("second");
        await recovery.ExecuteAsync();
        await Assert.That(await one).IsEqualTo("first");
        await Assert.That(await two).IsEqualTo("second");
        await Assert.That(circuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Closed);
    }

    [Test]
    public async Task TimedOutRecoveryReopensAndAllowsLaterFullRecoveryBatch()
    {
        await using var primary = Primary();
        await using var sentinel = Sentinel(() => primary.Port);
        await using var client = await Connect(sentinel, TimeSpan.FromMilliseconds(200));
        var (circuit, clock) = Prepare(client, primary);
        for (var failure = 0; failure < 2; failure++)
        {
            primary.SuppressReply = command => command == "GET timeout";
            await Assert.That(await Failure(() => Send(client, "raw", "timeout"))).IsTypeOf<RespireTimeoutException>();
            await Assert.That(circuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Open);
            await Assert.That(circuit.Snapshot().ActiveProbes).IsEqualTo(0);
            await Assert.That(await Failure(() => Send(client, "raw", "never"))).IsTypeOf<RespireCircuitOpenException>();
            primary.SuppressReply = null;
            await primary.SendRawAsync("$4\r\nlate\r\n"u8.ToArray());
            clock.Advance();
        }
        using var recovery = client.CreateBatch();
        var first = recovery.Strings.GetString("first");
        var second = recovery.Strings.GetString("second");
        await recovery.ExecuteAsync();
        await Assert.That(await first).IsEqualTo("first");
        await Assert.That(await second).IsEqualTo("second");
        await Assert.That(circuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Closed);
        await Assert.That(primary.ReceivedCommands.Contains("GET never")).IsFalse();
        await Assert.That(primary.ReceivedCommands.Count(command => command == "GET timeout")).IsEqualTo(2);
    }

    private static async Task<RespireClient> Connect(FakeRespServer sentinel, TimeSpan? commandTimeout = null, int connections = 1)
    {
        var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", sentinel.Port)], SentinelPrimaryName = "mymaster",
            Connections = connections, Protocol = RespProtocol.Resp2, ThreadPoolMonitoring = false,
            ConnectTimeout = TimeSpan.FromSeconds(5), CommandTimeout = commandTimeout ?? TimeSpan.FromSeconds(5),
            CircuitBreaker = new() { MinimumFailureCount = 1, FailureRateThreshold = 0.25,
                HalfOpenProbeCount = 2, OpenDuration = TimeSpan.FromMinutes(1) },
        });
        await SentinelTestSetup.WaitForStartupAsync(client);
        return client;
    }

    private static (EndpointCircuitBreaker Circuit, Clock Clock) Prepare(RespireClient client, FakeRespServer primary)
    {
        var admission = client.Core.Circuits!.Acquire(new("127.0.0.1", primary.Port), default);
        admission.Dispose();
        var circuit = client.Core.Circuits.GetForTests(new("127.0.0.1", primary.Port));
        var clock = new Clock();
        CircuitClock(circuit) = clock;
        return (circuit, clock);
    }

    private static void Open(RespireClient client, FakeRespServer primary)
    {
        var admission = client.Core.Circuits!.Acquire(new("127.0.0.1", primary.Port), default);
        try { admission.Failed(new RespireConnectionException("Injected endpoint failure"), default); }
        finally { admission.Dispose(); }
    }

    private static async Task Send(RespireClient client, string shape, string key)
    {
        switch (shape)
        {
            case "string": await client.GetStringAsync(key); break;
            case "bytes": await client.GetBytesAsync(key); break;
            case "converted": await client.Strings.LengthAsync(key); break;
            case "raw": using (await client.ExecuteAsync("GET", [key])) { } break;
            case "fire-and-forget": await client.ExecuteFireAndForgetAsync("GET", [key]); break;
            case "batch":
                using (var batch = client.CreateBatch())
                {
                    var pending = batch.Strings.GetString(key);
                    await batch.ExecuteAsync();
                    await Assert.That(await pending).IsEqualTo(key);
                }
                break;
            case "transaction":
                await using (var transaction = client.CreateTransaction())
                {
                    var pending = transaction.Strings.GetString(key);
                    await transaction.CommitAsync();
                    await Assert.That(await pending).IsEqualTo("healthy");
                }
                break;
        }
    }

    private static FakeRespServer Primary() => new(32, FakeRespServer.OkReply)
    {
        ReplyOverride = (_, command) => command switch
        {
            "ROLE" => "*3\r\n+master\r\n:0\r\n*0\r\n"u8.ToArray(),
            "GET canceled" => "+QUEUED\r\n"u8.ToArray(),
            "GET healthy" => "$7\r\nhealthy\r\n"u8.ToArray(),
            "EXEC" => "*1\r\n$7\r\nhealthy\r\n"u8.ToArray(),
            _ when command.StartsWith("SCAN 0") => "*2\r\n$1\r\n7\r\n*1\r\n$5\r\nfirst\r\n"u8.ToArray(),
            _ when command.StartsWith("SCAN 7") => "*2\r\n$1\r\n0\r\n*1\r\n$6\r\nsecond\r\n"u8.ToArray(),
            _ when command.StartsWith("GET ") => Bulk(command[4..]),
            _ when command.StartsWith("STRLEN ") => ":5\r\n"u8.ToArray(),
            _ => null,
        },
    };

    private static FakeRespServer Sentinel(Func<int> port, params int[] replicaPorts)
    {
        var epochs = new Dictionary<int, int>();
        byte[] Configuration()
        {
            var current = port();
            int epoch;
            lock (epochs)
                if (!epochs.TryGetValue(current, out epoch)) epochs[current] = epoch = epochs.Count + 1;
            return Encoding.ASCII.GetBytes($"*8\r\n+ip\r\n+127.0.0.1\r\n+port\r\n+{current}\r\n+config-epoch\r\n+{epoch}\r\n+flags\r\n+master\r\n");
        }
        return new(64, "*0\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command switch
            {
                "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster" => Encoding.ASCII.GetBytes($"*2\r\n+127.0.0.1\r\n+{port()}\r\n"),
                "SENTINEL MASTER mymaster" => Configuration(),
                "SENTINEL REPLICAS mymaster" => Encoding.ASCII.GetBytes(
                    $"*{replicaPorts.Length}\r\n" + string.Concat(replicaPorts.Select(replica =>
                        $"*6\r\n+ip\r\n+127.0.0.1\r\n+port\r\n+{replica}\r\n+flags\r\n+slave\r\n"))),
                "SUBSCRIBE +switch-master" => "*3\r\n+subscribe\r\n+switch-master\r\n:1\r\n"u8.ToArray(),
                "SUBSCRIBE +sdown" => "*3\r\n+subscribe\r\n+sdown\r\n:2\r\n"u8.ToArray(),
                "SUBSCRIBE +odown" => "*3\r\n+subscribe\r\n+odown\r\n:3\r\n"u8.ToArray(),
                _ => null,
            },
        };
    }

    private static byte[] Bulk(string value) => Encoding.UTF8.GetBytes($"${value.Length}\r\n{value}\r\n");
    private static async Task Received(FakeRespServer server, string command)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!server.ReceivedCommands.Contains(command)) await Task.Delay(1, limit.Token);
    }
    private static async Task<Exception> Failure(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) { return error; }
        throw new InvalidOperationException("Expected failure");
    }
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_clock")]
    private static extern ref TimeProvider CircuitClock(EndpointCircuitBreaker circuit);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "GetCircuitConnectionSlow")]
    private static extern RespireConnection SelectCircuitConnection(RespireClient client,
        RespireConnectionMultiplexer multiplexer, CancellationToken cancellationToken);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "TryAcquireReadyConnection")]
    private static extern RespireConnection? SelectReadyConnection(RespireClient client,
        RespireConnectionMultiplexer multiplexer, CancellationToken cancellationToken);
    private sealed class Clock : TimeProvider
    {
        private long _timestamp = TimeProvider.System.GetTimestamp();
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public override long TimestampFrequency => TimeProvider.System.TimestampFrequency;
        internal void Advance() => Interlocked.Add(ref _timestamp, 60 * TimestampFrequency);
    }
    private sealed class AdmissionGateClock : TimeProvider, IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private int _armed;
        internal TaskCompletionSource Selected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Arm() => Volatile.Write(ref _armed, 1);
        internal void Release() => _release.Set();
        public override long GetTimestamp()
        {
            if (Interlocked.Exchange(ref _armed, 0) == 1)
            {
                Selected.TrySetResult();
                if (!_release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Cursor admission gate was not released.");
            }
            return TimeProvider.System.GetTimestamp();
        }
        public override long TimestampFrequency => TimeProvider.System.TimestampFrequency;
        public void Dispose() => _release.Dispose();
    }
    private readonly struct ThrowingCommand : IRespCommand
    {
        public ReadCommandKind ReadKind => ReadCommandKind.None;
        public void Write(ref RespWriter writer) => throw new IOException("Writer failed before dispatch");
    }
}
