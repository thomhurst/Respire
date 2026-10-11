using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Respire.Internal;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

[NotInParallel]
public class CacheProducerOwnershipTests
{
    [Test]
    [Arguments(false, 3, 5)]
    [Arguments(true, 3, 5)]
    [Arguments(false, int.MaxValue - 1, int.MaxValue)]
    [Arguments(true, int.MaxValue - 1, int.MaxValue)]
    [Arguments(false, int.MaxValue, int.MaxValue)]
    [Arguments(true, int.MaxValue, int.MaxValue)]
    public async Task SharedFailureCombinesNestedRetriesWithoutOverflow(bool typed, int callerAttempts, int expectedAttempts)
    {
        using var configuration = new MetricConfigurationScope();
        using var capture = new Capture();
        using var producer = new Producer(typed);
        var first = producer.Read(callerAttempts);
        var second = producer.Read(7);
        producer.Observation.Handled(new IOException("nested retry"));
        producer.Observation.Handled(new IOException("nested retry"));
        var expected = new IOException("shared producer failure");
        producer.Reply.SetException(expected);

        var firstFailure = await Assert.That(async () => await first).Throws<IOException>();
        var secondFailure = await Assert.That(async () => await second).Throws<IOException>();
        await Assert.That(firstFailure).IsSameReferenceAs(expected);
        await Assert.That(secondFailure).IsSameReferenceAs(expected);
        await Assert.That(producer.Calls).IsEqualTo(1);
        await Assert.That(producer.Cache.ActiveSharedReadCount).IsEqualTo(0);
        await Assert.That(capture.Items.Where(item => item.Internal).Select(item => item.Attempts).ToArray())
            .IsEquivalentTo(new[] { 0, 1 });
        await Assert.That(capture.Items.Where(item => !item.Internal).Select(item => item.Attempts).ToArray())
            .IsEquivalentTo(new[] { expectedAttempts, 9 });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SharedProducerCancellationKeepsItsExceptionAndToken(bool typed)
    {
        using var configuration = new MetricConfigurationScope();
        using var capture = new Capture();
        using var producer = new Producer(typed);
        using var cancellation = new CancellationTokenSource();
        var first = producer.Read(3);
        var second = producer.Read(7);
        producer.Observation.Handled(new IOException("nested retry"));
        cancellation.Cancel();
        var expected = new OperationCanceledException("producer cancellation", cancellation.Token);
        producer.Reply.SetException(expected);

        var firstFailure = await Assert.That(async () => await first).Throws<OperationCanceledException>();
        var secondFailure = await Assert.That(async () => await second).Throws<OperationCanceledException>();
        await Assert.That(firstFailure).IsSameReferenceAs(expected);
        await Assert.That(secondFailure).IsSameReferenceAs(expected);
        await Assert.That(firstFailure!.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(secondFailure!.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(capture.Items.Where(item => !item.Internal).Select(item => item.Attempts).ToArray())
            .IsEquivalentTo(new[] { 4, 8 });
        await Assert.That(producer.Calls).IsEqualTo(1);
        await Assert.That(producer.Cache.ActiveSharedReadCount).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CanceledWaiterKeepsItsTokenAndActiveProducerCount(bool typed)
    {
        using var configuration = new MetricConfigurationScope();
        using var capture = new Capture();
        using var producer = new Producer(typed);
        using var cancellation = new CancellationTokenSource();
        var first = producer.Read(3, cancellation.Token);
        var second = producer.Read(7);
        producer.Observation.Handled(new IOException("first retry"));
        cancellation.Cancel();
        var canceled = await Assert.That(async () => await first).Throws<OperationCanceledException>();
        await Assert.That(canceled!.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(second.IsCompleted).IsFalse();
        // Reuse of the canceled caller's source must not affect the producer or remaining caller.
        var unrelated = DispatchResponseSource<string>.Start();
        unrelated.Observation.SetAttempts(99);
        unrelated.CompleteInternal();
        producer.Observation.Handled(new IOException("second retry"));
        var expected = new IOException("producer failure after independent cancellation");
        producer.Reply.SetException(expected);
        var failure = await Assert.That(async () => await second).Throws<IOException>();
        await Assert.That(failure).IsSameReferenceAs(expected);
        await Assert.That(capture.Items.Where(item => !item.Internal).Select(item => item.Attempts).ToArray())
            .IsEquivalentTo(new[] { 4, 9 });
        await Assert.That(producer.Calls).IsEqualTo(1);
        await Assert.That(producer.Cache.ActiveSharedReadCount).IsEqualTo(0);
    }

    [Test]
    [Arguments(false, 3, 5)]
    [Arguments(true, 3, 5)]
    [Arguments(false, int.MaxValue - 1, int.MaxValue)]
    [Arguments(true, int.MaxValue - 1, int.MaxValue)]
    [Arguments(false, int.MaxValue, int.MaxValue)]
    [Arguments(true, int.MaxValue, int.MaxValue)]
    public async Task ConversionFailureUsesCompletedProducerCountAndLeavesOtherCallerSuccessful(
        bool typed, int callerAttempts, int expectedAttempts)
    {
        using var configuration = new MetricConfigurationScope();
        using var capture = new Capture();
        using var producer = new Producer(typed);
        var expected = new FormatException("independent converter failure");
        var first = producer.Read(callerAttempts, converter: () =>
        {
            // Producer completion returns its source before either waiter converts.
            // Renting the same generic source exercises the completed-count snapshot.
            var unrelated = DispatchResponseSource<bool>.Start();
            unrelated.Observation.SetAttempts(99);
            unrelated.CompleteInternal();
            throw expected;
        });
        var second = producer.Read(7);
        producer.Observation.Handled(new IOException("first retry"));
        producer.Observation.Handled(new IOException("second retry"));
        producer.Reply.SetResult(RespValue.BulkString("value"u8.ToArray()));
        var failure = await Assert.That(async () => await first).Throws<FormatException>();
        await Assert.That(failure).IsSameReferenceAs(expected);
        await Assert.That(await second).IsEqualTo("value");
        await Assert.That(capture.Items.Where(item => !item.Internal).Select(item => item.Attempts).ToArray())
            .IsEquivalentTo(new[] { expectedAttempts });
        await Assert.That(producer.Cache.ActiveSharedReadCount).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SuccessfulProducerAndCallersRentNoErrorStorage(bool typed)
    {
        using var configuration = new MetricConfigurationScope();
        using var capture = new Capture();
        using var producer = new Producer(typed);
        var rentals = 0;
        ErrorObservation.RentalObserverForTests = () => Interlocked.Increment(ref rentals);
        RespireTelemetry.ErrorObservation.RentalObserverForTests = () => Interlocked.Increment(ref rentals);
        try
        {
            var first = producer.Read(0);
            var second = producer.Read(0);
            producer.Reply.SetResult(RespValue.BulkString("value"u8.ToArray()));
            await Assert.That(await first).IsEqualTo("value");
            await Assert.That(await second).IsEqualTo("value");
            await Assert.That(rentals).IsEqualTo(0);
            await Assert.That(capture.Items).IsEmpty();
        }
        finally
        {
            ErrorObservation.RentalObserverForTests = null;
            RespireTelemetry.ErrorObservation.RentalObserverForTests = null;
        }
    }

    private sealed class Producer(bool typed) : IDisposable
    {
        internal readonly ClientSideCacheCoordinator Cache = new(new() { CoalesceConcurrentMisses = true });
        internal readonly TaskCompletionSource<RespValue> Reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal RespireTelemetry.ErrorObservation Observation;
        internal int Calls;

        internal Task<string> Read(int attempts, CancellationToken token = default, Func<string>? converter = null)
            => DispatchResponseSource<string>.Run((Producer: this, Attempts: attempts, Token: token, Converter: converter),
                static (state, caller) =>
                {
                    caller.SetAttempts(state.Attempts);
                    var identity = new ClientCacheCommandKey("GET", "key");
                    return ReadCore(state.Producer, identity, state.Token, state.Converter, caller);
                }).AsTask();

        private static ValueTask<string> ReadCore(Producer producer, ClientCacheCommandKey identity,
            CancellationToken token, Func<string>? converter, RespireTelemetry.ErrorObservation caller)
            => producer.Typed
                ? producer.Cache.CoalesceGetReadAsync(identity, producer,
                    static async (state, _, observation) =>
                    {
                        state.Observation = observation;
                        state.Calls++;
                        var response = await state.Reply.Task.ConfigureAwait(false);
                        return new ClientSideCacheCoordinator.GetReadResult(response, "key", null, null, null);
                    }, converter, static (Func<string>? convert, in ClientSideCacheCoordinator.GetReadResult result)
                        => convert is null ? result.GetString()! : convert(), token, caller)
                : ConvertRawAsync(producer.Cache.CoalesceReadAsync(identity, producer,
                    static (state, _, observation) =>
                    {
                        state.Observation = observation;
                        state.Calls++;
                        return new ValueTask<RespValue>(state.Reply.Task);
                    }, token, caller), converter);

        private bool Typed => typed;

        private static async ValueTask<string> ConvertRawAsync(ValueTask<RespValue> reading, Func<string>? converter)
        {
            using var response = await reading.ConfigureAwait(false);
            return converter is null ? ResponseReader.StringOrNull(in response)! : converter();
        }

        public void Dispose()
        {
            Reply.TrySetException(new OperationCanceledException());
            Cache.StopSharedReads();
        }
    }

    private sealed class Capture : IDisposable
    {
        private readonly MeterListener _listener = new();
        internal readonly ConcurrentQueue<(bool Internal, int Attempts)> Items = new();

        internal Capture()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, RespireTelemetry.Meter) && instrument.Name == "redis.client.errors")
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                var internallyHandled = false;
                var attempts = 0;
                foreach (var tag in tags)
                {
                    if (tag.Key == "redis.client.errors.internal") internallyHandled = (bool)tag.Value!;
                    if (tag.Key == "redis.client.operation.retry_attempts") attempts = (int)tag.Value!;
                }
                Items.Enqueue((internallyHandled, attempts));
                throw new InvalidOperationException("Exporter failure must not change caller completion.");
            });
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }
}
