using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

[NotInParallel]
public class PendingObservationBookkeepingTests
{
    [Test]
    [MatrixDataSource]
    public async Task AddedRetriesSaturateWithOrWithoutFailureStorage(
        [Matrix(false, true)] bool enabled, [Matrix(false, true)] bool handled)
    {
        using var configuration = new MetricConfigurationScope(new()
            { Groups = enabled ? RespireMetricGroups.Resiliency : RespireMetricGroups.None });
        using var capture = new Capture();
        var pending = new RespirePending<int>();
        var observation = pending.Observation;
        if (handled) observation.Handled(new IOException("recovered"));
        observation.SetAttempts(int.MaxValue - 2);
        pending.AddErrorAttempts(3);
        await Assert.That(observation.Attempts).IsEqualTo(int.MaxValue);
        pending.AddErrorAttempts(int.MaxValue);
        pending.AdvanceErrorAttempt();
        observation.Retry();
        pending.AddErrorAttempts(-1);
        pending.AddErrorAttempts(int.MinValue);
        await Assert.That(observation.Attempts).IsEqualTo(int.MaxValue);
        pending.Fail(new IOException("terminal"));
        pending.ReportError();
        await Assert.That(observation.Attempts).IsEqualTo(int.MaxValue);
        await Assert.That(capture.Items.Where(item => !item.Internal).Select(item => item.Attempts).ToArray())
            .IsEquivalentTo(enabled ? new[] { int.MaxValue } : Array.Empty<int>());
    }

    [Test]
    [MatrixDataSource]
    public async Task NegativeAddedRetriesDoNotReduceCompletedAttempts([Matrix(false, true)] bool handled)
    {
        var pending = new RespirePending<int>();
        var observation = pending.Observation;
        if (handled) observation.Handled(new IOException());
        observation.SetAttempts(7);
        pending.AddErrorAttempts(-3);
        pending.AddErrorAttempts(int.MinValue);
        pending.AddErrorAttempts(0);
        await Assert.That(observation.Attempts).IsEqualTo(7);
        pending.Succeed(42);
        pending.ReportError();
    }

    [Test]
    [MatrixDataSource]
    public async Task CopiedNestedRetriesKeepIndependentFinalErrorsAndOriginalCancellation(
        [Matrix(false, true)] bool enabled, [Matrix(false, true)] bool cancelled)
    {
        using var configuration = new MetricConfigurationScope(new()
            { Groups = enabled ? RespireMetricGroups.Resiliency : RespireMetricGroups.None });
        using var capture = new Capture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Exception error = cancelled ? new OperationCanceledException(cancellation.Token) : new IOException("original");
        var first = new RespirePending<int>();
        var second = new RespirePending<int>();
        var outer = first.Observation;
        var nested = outer;
        outer.Retry();
        nested.Handled(new IOException("recovered"));
        first.AddErrorAttempts(3);
        nested.Retry();
        first.Fail(error);
        second.Fail(error);
        first.AllowErrorInspection();
        second.AllowErrorInspection();
        first.ReportError();
        second.ReportError();
        outer.SetAttempts(99);
        nested.Retry();
        first.AddErrorAttempts(int.MaxValue);
        await Assert.That(nested.TryHandled(error)).IsFalse();
        first.ReportError();
        second.ReportError();
        for (var index = 0; index < 2; index++)
        {
            await Assert.That(ReadError(() => first.Result)).IsSameReferenceAs(error);
            await Assert.That(ReadError(() => first.GetAwaiter().GetResult())).IsSameReferenceAs(error);
            await Assert.That(ReadError(() => second.Result)).IsSameReferenceAs(error);
        }
        await Assert.That(outer.Attempts).IsEqualTo(6);
        await Assert.That(second.Observation.Attempts).IsEqualTo(0);
        await Assert.That(first.Error).IsSameReferenceAs(error);
        if (cancelled) await Assert.That(((OperationCanceledException)first.Error!).CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(capture.Items.ToArray()).IsEquivalentTo(enabled
            ? new[] { (true, 1), (false, 6), (false, 0) }
            : Array.Empty<(bool, int)>());
    }

    [Test]
    public async Task SuccessfulDeferredRetriesUseNoFailureStorageOrInspectionAllocation()
    {
        var pending = new RespirePending<int>();
        var rentals = 0;
        ErrorObservation.RentalObserverForTests = () => rentals++;
        try
        {
            pending.Observation.SetAttempts(2);
            pending.AddErrorAttempts(3);
            pending.Observation.Retry();
            pending.Succeed(42);
            pending.ReportError();
            await Assert.That(pending.Observation.Attempts).IsEqualTo(6);
            await Assert.That(rentals).IsEqualTo(0);
            _ = MeasureInspection(pending, false);
            _ = MeasureInspection(pending, true);
            var allocated = AllocationMeasurement.WithoutConcurrentGc(() => MeasureInspection(pending, false));
            var control = AllocationMeasurement.WithoutConcurrentGc(() => MeasureInspection(pending, true));
            await Assert.That(allocated).IsEqualTo(0L);
            await Assert.That(control).IsGreaterThanOrEqualTo(37_000L);
            var owner = ErrorObservation.StartFailure();
            owner.Complete();
            await Assert.That(rentals).IsEqualTo(1);
        }
        finally { ErrorObservation.RentalObserverForTests = null; }
    }

    private static Exception ReadError(Func<int> read)
    {
        try { _ = read(); }
        catch (Exception error) { return error; }
        throw new InvalidOperationException("Expected the original pending failure.");
    }

    private static object? _allocationAnchor;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureInspection(RespirePending<int> pending, bool allocate)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1_000; index++)
        {
            if (pending.Result != 42 || pending.ReportError()) throw new InvalidOperationException();
            if (allocate) Volatile.Write(ref _allocationAnchor, new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
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
            });
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }
}
