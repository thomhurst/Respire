using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Text;
using System.Threading.Tasks.Sources;
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
public class DispatchResponseObservationTests
{
    private const string NativeProbe = "RESPIRE_NATIVE_DISPATCH_PROBE";

    [Test]
    [Arguments("raw", "success")]
    [Arguments("string", "success")]
    [Arguments("bytes", "success")]
    [Arguments("typed", "success")]
    [Arguments("raw", "error")]
    [Arguments("string", "error")]
    [Arguments("bytes", "error")]
    [Arguments("typed", "error")]
    [Arguments("raw", "cancelled")]
    [Arguments("string", "cancelled")]
    [Arguments("bytes", "cancelled")]
    [Arguments("typed", "cancelled")]
    [Arguments("raw", "pre-cancelled")]
    [Arguments("string", "pre-cancelled")]
    [Arguments("bytes", "pre-cancelled")]
    [Arguments("typed", "pre-cancelled")]
    public async Task ReadyStandaloneDispatchReturnsNativeSourceAndObservesLateEnabledErrors(string shape, string mode)
        => await RunNativeProbeAsync(shape + ":" + mode);

    // The test runner installs a global ActivityListener. Run before its startup so
    // the production no-tracing gate and the original native ValueTask can be tested.
    private static async Task RunNativeProbeAsync(string mode)
    {
        var start = AsyncFlushSignalTests.CreateProbeStartInfo(Environment.ProcessPath,
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH"), typeof(DispatchResponseObservationTests).Assembly.Location);
        start.Environment[NativeProbe] = mode;
        await AsyncFlushSignalTests.RunProbeAsync(start, TimeSpan.FromSeconds(30));
    }

    internal static int? RunIsolatedNativeDispatchProbe()
    {
        var mode = Environment.GetEnvironmentVariable(NativeProbe);
        if (mode is null) return null;
        try
        {
            if (mode == "converter") NativeConverterProbeAsync().GetAwaiter().GetResult();
            else if (mode == "capacity") NativeCapacityProbeAsync().GetAwaiter().GetResult();
            else if (mode.StartsWith("retirement:", StringComparison.Ordinal)) NativeRetirementProbeAsync(mode.Split(':')[1]).GetAwaiter().GetResult();
            else
            {
                var parts = mode.Split(':');
                NativeDispatchProbeAsync(parts[0], parts[1]).GetAwaiter().GetResult();
            }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static async Task NativeDispatchProbeAsync(string shape, string mode)
    {
        using var configuration = new MetricConfigurationScope();
        RespireMetrics.Configure(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            SuppressReply = command => command.StartsWith("GET ", StringComparison.Ordinal),
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var cancellation = new CancellationTokenSource();
        if (mode == "pre-cancelled") cancellation.Cancel();
        var command = new Cmd1(Verbs.Get, "held");
        switch (shape)
        {
            case "raw":
                await InspectNativeDispatch(server, client.SendAsync("GET", command, cancellation.Token),
                    typeof(PendingResponseSource), mode, cancellation);
                break;
            case "string":
                await InspectNativeDispatch(server, client.StringOrNullAsync("GET", command, cancellation.Token),
                    typeof(StringPendingResponseSource), mode, cancellation);
                break;
            case "bytes":
                await InspectNativeDispatch(server, client.BytesOrNullAsync("GET", command, cancellation.Token),
                    typeof(BytesPendingResponseSource), mode, cancellation);
                break;
            default:
                await InspectNativeDispatch(server, client.ConvertResponseAsync("GET", command, cancellation.Token, 0,
                    static (int _, in RespValue value) => ResponseReader.StringOrNull(in value)),
                    typeof(ConvertedPendingResponseSource<int, string?>), mode, cancellation);
                break;
        }
    }

    private static async Task InspectNativeDispatch<T>(FakeRespServer server, ValueTask<T> response,
        Type expectedSource, string mode, CancellationTokenSource cancellation)
    {
        var sourceField = typeof(ValueTask<T>).GetField("_obj", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await Assert.That(sourceField.GetValue(response)!.GetType()).IsEqualTo(expectedSource);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!server.ReceivedCommands.Contains("GET held")) await Task.Delay(1, deadline.Token);
        // Enable the listener after admission: native ownership must not depend on it
        // being enabled when the operation starts.
        using var capture = new Capture();
        if (mode is "cancelled" or "pre-cancelled") cancellation.Cancel();
        else await server.SendRawAsync(mode == "error" ? "-WRONGTYPE native error\r\n"u8.ToArray() : "$2\r\n42\r\n"u8.ToArray());
        Exception? failure = null;
        try
        {
            var result = await response.AsTask().WaitAsync(deadline.Token);
            if (result is RespValue raw)
            {
                await Assert.That(ResponseReader.StringOrNull(in raw)).IsEqualTo("42");
                raw.Dispose();
            }
            else if (result is byte[] bytes) await Assert.That(bytes).IsEquivalentTo("42"u8.ToArray());
            else await Assert.That(result).IsEqualTo((T)(object)"42");
        }
        catch (Exception error) { failure = error; }
        if (mode == "success")
        {
            await Assert.That(failure).IsNull();
            await Assert.That(capture.Items).IsEmpty();
        }
        else
        {
            if (mode is "cancelled" or "pre-cancelled")
            {
                await Assert.That(failure is OperationCanceledException).IsTrue();
                await Assert.That(((OperationCanceledException)failure!).CancellationToken).IsEqualTo(cancellation.Token);
                await server.SendRawAsync("$2\r\n42\r\n"u8.ToArray());
            }
            else await Assert.That(failure is RespireServerException { Code: "WRONGTYPE" }).IsTrue();
            await Assert.That(capture.Items.ToArray()).IsEquivalentTo(new[] { (false, 0) });
        }
    }

    [Test]
    public async Task DirectNativeConverterRunsAtConsumptionAndPreservesFailureThroughThrowingListener()
        => await RunNativeProbeAsync("converter");

    private static async Task NativeConverterProbeAsync()
    {
        using var configuration = new MetricConfigurationScope();
        RespireMetrics.Configure(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("GET ", StringComparison.Ordinal) ? "$2\r\n42\r\n"u8.ToArray() : null,
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var failure = new InvalidOperationException("native converter failure");
        var converted = false;
        var response = client.ConvertResponseAsync<Cmd1, int, int>("GET", new Cmd1(Verbs.Get, "held"), default, 0,
            (int _, in RespValue _) => { converted = true; throw failure; });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!response.IsCompleted) await Task.Delay(1, deadline.Token);
        await Assert.That(converted).IsFalse();
        using var listener = new MeterListener();
        var publications = 0;
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "redis.client.errors") owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => { publications++; throw new IOException("listener failure"); });
        listener.Start();
        Exception? actual = null;
        try { _ = await response; } catch (Exception error) { actual = error; }
        await Assert.That(ReferenceEquals(actual, failure)).IsTrue();
        await Assert.That(converted).IsTrue();
        await Assert.That(publications).IsEqualTo(1);
    }

    [Test]
    public async Task FullNativeRingTransfersCapacityWaitToDispatchOwner()
        => await RunNativeProbeAsync("capacity");

    private static async Task NativeCapacityProbeAsync()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            SuppressReply = command => command.StartsWith("GET ", StringComparison.Ordinal),
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, MaxInflightCommands = 1,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = client.GetStringAsync("first");
        while (!server.ReceivedCommands.Contains("GET first")) await Task.Delay(1, deadline.Token);
        var second = client.GetStringAsync("second");
        var source = typeof(ValueTask<string?>).GetField("_obj", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(second);
        await Assert.That(source is DispatchResponseSource<string?>).IsTrue();
        using var capture = new Capture();
        await server.SendRawAsync("$2\r\n42\r\n"u8.ToArray());
        await Assert.That(await first).IsEqualTo("42");
        while (!server.ReceivedCommands.Contains("GET second")) await Task.Delay(1, deadline.Token);
        await server.SendRawAsync("-WRONGTYPE capacity fallback\r\n"u8.ToArray());
        Exception? actual = null;
        try { _ = await second; } catch (Exception error) { actual = error; }
        await Assert.That(actual is RespireServerException { Code: "WRONGTYPE" }).IsTrue();
        await Assert.That(capture.Items.ToArray()).IsEquivalentTo(new[] { (false, 0) });
    }

    [Test]
    [Arguments("raw")]
    [Arguments("string")]
    [Arguments("bytes")]
    [Arguments("typed")]
    public async Task NativeRetirementRaceTransfersHandledRetryToDispatchOwner(string shape)
        => await RunNativeProbeAsync("retirement:" + shape);

    private static async Task NativeRetirementProbeAsync(string shape)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        static byte[]? Reply(int _, string command) => command switch
        {
            _ when command.StartsWith("HELLO", StringComparison.Ordinal) => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
            "CLIENT ID" => ":1\r\n"u8.ToArray(),
            "GET raced" => "-WRONGTYPE retirement fallback\r\n"u8.ToArray(),
            _ => null,
        };
        await using var origin = new FakeRespServer(FakeRespServer.OkReply) { ReplyOverride = Reply };
        await using var target = new FakeRespServer(FakeRespServer.OkReply) { ReplyOverride = Reply };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp3, Connections = 1, MaintenanceNotifications = RespireMaintenanceNotificationMode.Enabled,
            CommandTimeout = TimeSpan.FromSeconds(15), Endpoints = [new("127.0.0.1", origin.Port)],
        });
        var connection = client.Core.Multiplexer.GetConnection();
        using var entered = new ManualResetEventSlim();
        using var released = new ManualResetEventSlim();
        var first = 1;
        var command = new RetirementRaceCommand(() =>
        {
            if (Interlocked.Exchange(ref first, 0) != 1) return;
            entered.Set();
            if (!released.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Retirement race was not released.");
        });
        using var capture = new Capture();
        switch (shape)
        {
            case "raw": await Race(() => connection.SendNativeCheckedAsync(command, default, "GET")); break;
            case "string": await Race(() => connection.SendNativeStringAsync(command, default, "GET")); break;
            case "bytes": await Race(() => connection.SendNativeBytesAsync(command, default, "GET")); break;
            default: await Race(() => connection.SendNativeConvertedAsync(command, default, "GET", 0,
                static (int _, in RespValue response) => ResponseReader.StringOrNull(in response), false)); break;
        }
        await Assert.That(capture.Items.ToArray()).IsEquivalentTo(new[] { (true, 0), (false, 1) });

        async Task Race<T>(Func<ValueTask<T>> send)
        {
            var dispatch = Task.Run(send);
            try
            {
                if (!entered.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Native serialization did not start.");
                await origin.SendRawAsync(Encoding.UTF8.GetBytes($">4\r\n+MOVING\r\n:1\r\n:10\r\n+127.0.0.1:{target.Port}\r\n"));
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (connection.IsAcceptingCommands || ReferenceEquals(client.Core.Multiplexer.GetConnection(), connection))
                    await Task.Delay(1, deadline.Token);
            }
            finally { released.Set(); }
            var response = await dispatch;
            var source = typeof(ValueTask<T>).GetField("_obj", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(response);
            await Assert.That(source is DispatchResponseSource<T>).IsTrue();
            Exception? actual = null;
            try { _ = await response; } catch (Exception error) { actual = error; }
            await Assert.That(actual is RespireServerException { Code: "WRONGTYPE" }).IsTrue();
        }
    }

    private readonly struct RetirementRaceCommand(Action beforeWrite) : IRespCommand
    {
        public int GetWriteSizeHint() => 0;
        public ReadCommandKind ReadKind => ReadCommandKind.None;
        public void Write(ref RespWriter writer)
        {
            beforeWrite();
            writer.WriteRaw("*2\r\n$3\r\nGET\r\n$5\r\nraced\r\n"u8);
        }
    }

    [Test]
    [Arguments("synchronous")]
    [Arguments("pending")]
    [Arguments("cancelled")]
    public async Task ThrowingErrorListenerPreservesFailureAndReturnsDispatchSource(string mode)
    {
        using var configuration = new MetricConfigurationScope();
        using var listener = new MeterListener();
        var publications = 0;
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "redis.client.errors")
                owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) =>
        {
            publications++;
            throw new InvalidOperationException("error exporter failure");
        });
        listener.Start();

        var failure = new IOException("original dispatch failure");
        using var cancellation = new CancellationTokenSource();
        var completion = new TaskCompletionSource<ThrowingListenerResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        RespireTelemetry.ErrorObservation original = default;
        Exception? actual = null;
        try
        {
            var response = DispatchResponseSource<ThrowingListenerResult>.Run(mode, (mode, observation) =>
            {
                original = observation;
                observation.Handled(new IOException("recovered dispatch failure"));
                if (mode == "synchronous") throw failure;
                return new(completion.Task);
            });
            var pending = response.AsTask();
            if (mode == "cancelled")
            {
                cancellation.Cancel();
                completion.SetCanceled(cancellation.Token);
            }
            else completion.SetException(failure);
            _ = await pending;
        }
        catch (Exception error) { actual = error; }

        if (mode == "cancelled")
        {
            await Assert.That(actual is OperationCanceledException).IsTrue();
            await Assert.That(((OperationCanceledException)actual!).CancellationToken).IsEqualTo(cancellation.Token);
        }
        else await Assert.That(ReferenceEquals(actual, failure)).IsTrue();
        await Assert.That(publications).IsEqualTo(2);

        // This private result type has only one outstanding source, so the next rent
        // must reuse it. A skipped Pool.Return cannot pass the identity assertion.
        var identity = original.InspectForTests().StorageIdentity;
        RespireTelemetry.ErrorObservation reused = default;
        var next = DispatchResponseSource<ThrowingListenerResult>.Run(0, (_, observation) =>
        {
            reused = observation;
            return new ValueTask<ThrowingListenerResult>(default(ThrowingListenerResult));
        });
        try
        {
            await Assert.That(ReferenceEquals(identity, reused.InspectForTests().StorageIdentity)).IsTrue();
            original.Handled(new IOException("stale retry after reuse"));
            await Assert.That(reused.Attempts).IsEqualTo(0);
            await Assert.That(publications).IsEqualTo(2);
        }
        finally { _ = await next; }
    }

    [Test]
    public async Task PendingStatusChecksAvoidRedundantNativeQueries()
    {
        var native = new CountedResponseSource();
        var response = DispatchResponseSource<int>.Run(native, static (source, _) => new(source, 0));
        await Assert.That(response.IsCompleted).IsFalse();
        await Assert.That(native.StatusCalls).IsEqualTo(2);
        native.StatusCalls = 0;
        native.Status = ValueTaskSourceStatus.Succeeded;
        await Assert.That(response.IsCompletedSuccessfully).IsTrue();
        await Assert.That(native.StatusCalls).IsEqualTo(1);
        await Assert.That(await response).IsEqualTo(42);
    }

    [Test]
    public async Task SuccessDuringStatusInspectionIsNotReportedAsFaulted()
    {
        var native = new CountedResponseSource { CompleteAfterFirstQuery = true };
        var response = DispatchResponseSource<int>.Run(native, static (source, _) => new(source, 0));
        await Assert.That(response.IsCompletedSuccessfully).IsTrue();
        await Assert.That(await response).IsEqualTo(42);
    }

    [Test]
    public async Task WarmConcurrentBurstRetainsEveryDispatchOwner()
    {
        var pending = new ValueTask<BurstResult>[10000];
        for (var index = 0; index < 4; index++) _ = MeasureBurst(pending, false);
        var measured = AllocationMeasurement.WithoutConcurrentGc(() =>
            (MeasureBurst(pending, false), MeasureBurst(pending, true)));
        await Assert.That(measured.Item1).IsEqualTo(0L);
        await Assert.That(measured.Item2).IsGreaterThan(0L);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureBurst(ValueTask<BurstResult>[] pending, bool control)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < pending.Length; index++)
            pending[index] = DispatchResponseSource<BurstResult>.Run(42, static (value, _) => new(new BurstResult(value)));
        for (var index = 0; index < pending.Length; index++)
        {
            if (pending[index].GetAwaiter().GetResult().Value != 42) throw new InvalidOperationException("Unexpected response.");
            if (control) GC.KeepAlive(new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private readonly record struct BurstResult(int Value);

    [Test]
    public async Task SynchronousThrowAfterBorrowRejectsLateRetryAfterReuse()
    {
        using var configuration = new MetricConfigurationScope();
        using var capture = new Capture();
        var state = new PendingState();
        var failure = new IOException("send failure after borrow");
        try
        {
            _ = DispatchResponseSource<int>.Run(state, (state, observation) =>
            {
                state.Observation = observation;
                observation.Handled(new IOException("retry"));
                throw failure;
            });
        }
        catch (IOException error)
        {
            await Assert.That(ReferenceEquals(error, failure)).IsTrue();
        }
        var old = state.Observation;
        var next = DispatchResponseSource<int>.Run(state, static (state, observation) =>
        {
            state.Observation = observation;
            return new ValueTask<int>(42);
        });
        old.Handled(new IOException("late retry"));
        await Assert.That(state.Observation.Attempts).IsEqualTo(0);
        await Assert.That(await next).IsEqualTo(42);
        await Assert.That(capture.Items.ToArray()).IsEquivalentTo(new[] { (true, 0), (false, 1) });
    }

    [Test]
    public async Task RetryBorrowersShareOneFinalOwnerUntilCallerInspection()
    {
        using var configuration = new MetricConfigurationScope();
        using var capture = new Capture();
        var failure = new IOException("final response");
        var state = new PendingState();
        var response = DispatchResponseSource<int>.Run(state, static (state, observation) =>
        {
            state.Observation = observation;
            observation.Handled(new IOException("retired connection"));
            return new(state.Completion.Task);
        });
        state.Observation.Handled(new IOException("capacity handoff"));
        state.Completion.SetException(failure);
        await Assert.That(capture.Items.Count).IsEqualTo(2);
        Exception? actual = null;
        try { _ = await response; } catch (Exception error) { actual = error; }
        await Assert.That(ReferenceEquals(actual, failure)).IsTrue();
        await Assert.That(capture.Items.Select(item => item.Attempts).ToArray()).IsEquivalentTo(new[] { 0, 1, 2 });
        await Assert.That(capture.Items.Last().Internal).IsFalse();
        state.Observation.Handled(new IOException("late borrower"));
        state.Observation.Final(failure);
        state.Observation.Dispose();
        await Assert.That(capture.Items.Count).IsEqualTo(3);
    }

    [Test]
    public async Task NativeConverterRunsOnlyWhenCallerConsumesAndFinishesAfterCleanup()
    {
        using var configuration = new MetricConfigurationScope();
        using var capture = new Capture();
        var state = new PendingState();
        var failure = new InvalidOperationException("converter failure");
        ConvertedPendingResponseSource<PendingState, int>? native = null;
        var response = DispatchResponseSource<int>.Run(state, (state, observation) =>
        {
            state.Observation = observation;
            observation.Handled(new IOException("retired connection"));
            native = ConvertedPendingResponseSource<PendingState, int>.Rent(state,
                (PendingState state, in RespValue _) => { state.Converted = true; throw failure; },
                false, "GET", observeErrors: false);
            return native.Task;
        });
        native!.TrySetResult(RespValue.Integer(42));
        native.ReleaseRef();
        await Assert.That(response.IsCompletedSuccessfully).IsTrue();
        await Assert.That(state.Converted).IsFalse();
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        await Assert.That(async () => _ = await response).Throws<InvalidOperationException>();
        await Assert.That(state.Converted).IsTrue();
        await Assert.That(capture.Items.Last().Attempts).IsEqualTo(1);
        await Assert.That(capture.Items.Last().Internal).IsFalse();
    }

    [Test]
    public async Task CancellationKeepsTaskStatusAndOriginalTokenAfterRetries()
    {
        using var configuration = new MetricConfigurationScope();
        using var capture = new Capture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var response = DispatchResponseSource<int>.Run(cancellation.Token, static (token, observation) =>
        {
            observation.Handled(new IOException("retired connection"));
            return ValueTask.FromCanceled<int>(token);
        });
        await Assert.That(response.IsCanceled).IsTrue();
        OperationCanceledException? actual = null;
        try { _ = await response; } catch (OperationCanceledException error) { actual = error; }
        await Assert.That(actual!.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(capture.Items.Last().Attempts).IsEqualTo(1);
        await Assert.That(capture.Items.Count).IsEqualTo(2);
    }

    [Test]
    [Arguments("string")]
    [Arguments("bytes")]
    [Arguments("integer")]
    public async Task CorePreCancelledDispatchHasOneFinalOwner(string shape)
    {
        using var configuration = new MetricConfigurationScope();
        await using var server = new FakeRespServer(1, FakeRespServer.OkReply);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        using var capture = new Capture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            if (shape == "string") _ = await client.GetStringAsync("key", cancellation.Token);
            else if (shape == "bytes") _ = await client.GetBytesAsync("key", cancellation.Token);
            else _ = await client.IntegerAsync("STRLEN", new Cmd1(Verbs.StrLen, "key"), cancellation.Token);
        }
        catch (OperationCanceledException error)
        {
            await Assert.That(error.CancellationToken).IsEqualTo(cancellation.Token);
        }
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        await Assert.That(capture.Items.Single().Attempts).IsEqualTo(0);
        await Assert.That(capture.Items.Single().Internal).IsFalse();
        // Existing native admission registers cancellation after publication. Its reply
        // must still drain before a subsequent command receives the next FIFO result.
        await client.OkAsync("PING", new RawCommand(FakeRespServer.PingFrame), default);
    }

    [Test]
    public async Task CancelledDiscardRetainsImmutableRetriesAfterDispatchOwnerReuse()
    {
        using var configuration = new MetricConfigurationScope();
        await using var server = new FakeRespServer(1, FakeRespServer.OkReply)
        {
            SuppressReply = command => command == "GET held",
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource();
        using var capture = new Capture();
        RespireTelemetry.ErrorObservation old = default;
        var pending = DispatchResponseSource<RespValue>.Run(0, (_, observation) =>
        {
            old = observation;
            observation.SetAttempts(2);
            return client.Core.Multiplexer.GetConnection().SendCheckedAsync(
                new Cmd1(Verbs.Get, "held"), cancellation.Token, "GET", observation: observation);
        });
        var identity = old.InspectForTests().StorageIdentity;
        while (!server.ReceivedCommands.Contains("GET held")) await Task.Delay(1, deadline.Token);
        cancellation.Cancel();
        OperationCanceledException? actual = null;
        try { _ = await pending; } catch (OperationCanceledException error) { actual = error; }
        await Assert.That(actual!.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(capture.Items.Single()).IsEqualTo((false, 2));

        var rentals = new List<(RawPendingState State, ValueTask<RespValue> Response)>();
        RawPendingState? reused = null;
        try
        {
            // Retain every rental so actual reuse is proved independently of pool ordering.
            for (var index = 0; index <= DispatchResponseSource<RespValue>.MaxPoolSize; index++)
            {
                var state = new RawPendingState();
                var response = DispatchResponseSource<RespValue>.Run(state, static (state, observation) =>
                {
                    state.Observation = observation;
                    return new(state.Completion.Task);
                });
                rentals.Add((state, response));
                if (!ReferenceEquals(identity, state.Observation.InspectForTests().StorageIdentity)) continue;
                reused = state;
                break;
            }
            await Assert.That(reused).IsNotNull();
            reused!.Observation.SetAttempts(9);
            old.Handled(new IOException("stale generation"));
            await Assert.That(reused.Observation.Attempts).IsEqualTo(9);
            await server.SendRawAsync("-NOPERM discarded\r\n"u8.ToArray());
            var discarded = await capture.InternalSeen.Task.WaitAsync(deadline.Token);
            await Assert.That(discarded).IsEqualTo((true, 2));
            await Assert.That(capture.Items.Count).IsEqualTo(2);
        }
        finally
        {
            foreach (var rental in rentals)
            {
                rental.State.Completion.TrySetResult(default);
                _ = await rental.Response;
            }
        }
    }

    [Test]
    public async Task WarmSuccessAllocatesNothingWithMetricsDisabledOrEnabled()
    {
        using var configuration = new MetricConfigurationScope();
        using var capture = new Capture();
        foreach (var groups in new[] { RespireMetricGroups.None, RespireMetricGroups.Resiliency })
        {
            RespireMetrics.Configure(new() { Groups = groups });
            for (var index = 0; index < 10; index++) _ = MeasureSuccess(false);
            var measured = AllocationMeasurement.WithoutConcurrentGc(() => (MeasureSuccess(false), MeasureSuccess(true)));
            await Assert.That(measured.Item1).IsEqualTo(0L);
            await Assert.That(measured.Item2).IsGreaterThan(0L);
        }
        await Assert.That(capture.Items).IsEmpty();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureSuccess(bool control)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1000; index++)
        {
            var result = DispatchResponseSource<int>.Run(42, static (value, _) => new ValueTask<int>(value)).GetAwaiter().GetResult();
            if (result != 42) throw new InvalidOperationException("Unexpected response.");
            if (control) GC.KeepAlive(new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private readonly struct ThrowingListenerResult;

    private sealed class PendingState
    {
        internal readonly TaskCompletionSource<int> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal RespireTelemetry.ErrorObservation Observation;
        internal bool Converted;
    }

    private sealed class CountedResponseSource : IValueTaskSource<int>
    {
        internal ValueTaskSourceStatus Status;
        internal int StatusCalls;
        internal bool CompleteAfterFirstQuery;
        public int GetResult(short token) => 42;
        public ValueTaskSourceStatus GetStatus(short token)
        {
            StatusCalls++;
            var status = Status;
            if (CompleteAfterFirstQuery) Status = ValueTaskSourceStatus.Succeeded;
            return status;
        }
        public void OnCompleted(Action<object?> continuation, object? state, short token,
            ValueTaskSourceOnCompletedFlags flags) => throw new InvalidOperationException("Unexpected continuation registration.");
    }

    private sealed class RawPendingState
    {
        internal readonly TaskCompletionSource<RespValue> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal RespireTelemetry.ErrorObservation Observation;
    }

    private sealed class Capture : IDisposable
    {
        private readonly MeterListener _listener = new();
        internal readonly ConcurrentQueue<(bool Internal, int Attempts)> Items = new();
        internal readonly TaskCompletionSource<(bool Internal, int Attempts)> InternalSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Capture()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Respire" && instrument.Name == "redis.client.errors")
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                var values = tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value);
                var item = ((bool)values["redis.client.errors.internal"]!,
                    (int)values["redis.client.operation.retry_attempts"]!);
                Items.Enqueue(item);
                if (item.Item1) InternalSeen.TrySetResult(item);
            });
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }
}
