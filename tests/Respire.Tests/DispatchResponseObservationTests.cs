using System.Collections.Concurrent;
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
public class DispatchResponseObservationTests
{
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
            for (var index = 0; index <= 4096; index++)
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

    private sealed class PendingState
    {
        internal readonly TaskCompletionSource<int> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal RespireTelemetry.ErrorObservation Observation;
        internal bool Converted;
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
