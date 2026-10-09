using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

[NotInParallel]
public class ErrorObservationTests
{
    [Test]
    public async Task AlreadyObservedRetriesRemainAtomicWithoutDuplicateEvents()
    {
        using var configuration = new MetricConfigurationScope();
        using var capture = new Capture();
        var owner = ErrorObservation.StartFailure();
        try
        {
            Parallel.For(0, 128, _ => owner.RecordRetry());
            await Assert.That(owner.RetryAttempts).IsEqualTo(128);
            await Assert.That(capture.Items.Count).IsEqualTo(0);
            owner.PublishFinal(new IOException());
            await Assert.That(capture.Items.Single().RetryAttempts).IsEqualTo(128);
            await Assert.That(capture.Items.Single().Internal).IsFalse();
            await Assert.That(owner.RecordRetry()).IsFalse();
        }
        finally { owner.Complete(); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BorrowAfterFinalPublicationOrCompletionRejectsRetryHistory(bool publishFinal)
    {
        using var configuration = new MetricConfigurationScope();
        using var capture = new Capture();
        var owner = ErrorObservation.StartFailure();
        var error = new IOException();
        owner.RecordHandled(error);
        if (publishFinal) owner.PublishFinal(error);
        else owner.Complete();
        var borrower = owner.Borrow();
        try
        {
            await Assert.That(borrower.RecordHandled(error)).IsFalse();
            await Assert.That(borrower.Borrow().RecordHandled(error)).IsFalse();
            await Assert.That(capture.Items.Count).IsEqualTo(publishFinal ? 2 : 1);
        }
        finally { borrower.Complete(); owner.Complete(); }
    }

    [Test]
    public async Task CopiesPublishAndCompleteOnlyOnce()
    {
        using var configuration = new MetricConfigurationScope();
        using var capture = new Capture();
        var owner = ErrorObservation.StartFailure();
        var copy = owner;
        var borrower = owner.Borrow();
        var borrowerCopy = borrower;
        var error = new IOException();
        await Assert.That(owner.PublishFinal(error)).IsTrue();
        await Assert.That(copy.PublishFinal(error)).IsFalse();
        owner.Complete();
        copy.Complete();
        await Assert.That(owner.PublishFinal(error)).IsFalse();
        borrower.Complete();
        borrowerCopy.Complete();
        await Assert.That(owner.PublishFinal(error)).IsFalse();
        await Assert.That(capture.Items.Count).IsEqualTo(1);
    }

    [Test]
    public async Task NestedBorrowersShareRetriesButIndependentCallersDoNotShareFailures()
    {
        using var configuration = new MetricConfigurationScope();
        using var capture = new Capture();
        var error = new IOException("shared failure");
        var first = ErrorObservation.StartFailure();
        var second = ErrorObservation.StartFailure();
        var borrower = first.Borrow();
        var nested = borrower.Borrow();
        try
        {
            first.RecordHandled(error);
            borrower.RecordHandled(error);
            nested.RecordHandled(error);
            nested.Complete();
            borrower.Complete();
            await Assert.That(first.PublishFinal(error)).IsTrue();
            await Assert.That(second.PublishFinal(error)).IsTrue();
            var items = capture.Items.ToArray();
            await Assert.That(items.Select(item => item.RetryAttempts).ToArray()).IsEquivalentTo(new[] { 0, 1, 2, 3, 0 });
            await Assert.That(items.Count(item => item.Internal)).IsEqualTo(3);
            await Assert.That(items.Count(item => !item.Internal)).IsEqualTo(2);
        }
        finally { first.Complete(); second.Complete(); nested.Complete(); borrower.Complete(); }
    }

    [Test]
    public async Task OwnerCompletionKeepsBorrowersAliveUntilTheyComplete()
    {
        using var configuration = new MetricConfigurationScope();
        using var capture = new Capture();
        var owner = ErrorObservation.StartFailure();
        var borrower = owner.Borrow();
        owner.Complete();
        var other = ErrorObservation.StartFailure();
        try
        {
            borrower.RecordHandled(new IOException());
            var nested = borrower.Borrow();
            nested.Complete();
            await Assert.That(capture.Items.Single().RetryAttempts).IsEqualTo(0);
        }
        finally { borrower.Complete(); other.Complete(); }
    }

    [Test]
    public async Task ReusedGenerationRejectsStaleOwnerAndBorrowerInsteadOfDuplicateInspection()
    {
        using var configuration = new MetricConfigurationScope();
        using var capture = new Capture();
        var old = ErrorObservation.StartFailure();
        var borrower = old.Borrow();
        var error = new IOException();
        old.RecordHandled(error);
        old.PublishFinal(error);
        borrower.Complete();
        old.Complete();
        await Assert.That(old.PublishFinal(error)).IsFalse();
        // Retain rentals to exhaust the bounded pool, independently of pool ordering.
        var rentals = new List<ErrorObservation.FinalOwner>();
        try
        {
            for (var i = 0; i < 33; i++) rentals.Add(ErrorObservation.StartFailure());
            await Assert.That(old.PublishFinal(error)).IsFalse();
            await Assert.That(borrower.RecordHandled(error)).IsFalse();
            await Assert.That(old.Borrow().RecordHandled(error)).IsFalse();
            await Assert.That(borrower.Borrow().RecordHandled(error)).IsFalse();
            old.Complete();
            borrower.Complete();
            rentals[0].PublishFinal(error);
            await Assert.That(capture.Items.Last().RetryAttempts).IsEqualTo(0);
        }
        finally { foreach (var rental in rentals) rental.Complete(); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RepeatedCompletionAfterReusePreservesOriginalException(bool cancelled)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Exception expected = cancelled ? new OperationCanceledException(cancellation.Token) : new IOException();
        Exception? actual = null;
        try { CompleteBeforeFinally(expected); } catch (Exception error) { actual = error; }
        await Assert.That(ReferenceEquals(actual, expected)).IsTrue();
        if (cancelled) await Assert.That(((OperationCanceledException)actual!).CancellationToken).IsEqualTo(cancellation.Token);
    }

    private static void CompleteBeforeFinally(Exception error)
    {
        var owner = ErrorObservation.StartFailure();
        var ownerCopy = owner;
        var borrower = owner.Borrow();
        var borrowerCopy = borrower;
        borrower.Complete();
        owner.Complete();
        var rentals = new List<ErrorObservation.FinalOwner>();
        try
        {
            for (var i = 0; i < 33; i++) rentals.Add(ErrorObservation.StartFailure());
            try { throw error; }
            finally
            {
                ownerCopy.Complete();
                borrowerCopy.Complete();
            }
        }
        finally { foreach (var rental in rentals) rental.Complete(); }
    }

    [Test]
    public async Task ConcurrentFinalInspectionsAndCopiedCompletionsAreIdempotent()
    {
        using var configuration = new MetricConfigurationScope();
        using var capture = new Capture();
        var owner = ErrorObservation.StartFailure();
        var borrower = owner.Borrow();
        var error = new IOException();
        var published = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => owner.PublishFinal(error))));
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => owner.Complete())));
        await Assert.That(published.Count(value => value)).IsEqualTo(1);
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        borrower.Complete();
    }

    [Test]
    public async Task ConcurrentBorrowersCountEveryHandledRetry()
    {
        using var configuration = new MetricConfigurationScope();
        using var capture = new Capture();
        var owner = ErrorObservation.StartFailure();
        var borrowers = Enumerable.Range(0, 32).Select(_ => owner.Borrow()).ToArray();
        var error = new IOException();
        try
        {
            await Task.WhenAll(borrowers.Select(borrower => Task.Run(() => { borrower.RecordHandled(error); borrower.Complete(); })));
            owner.PublishFinal(error);
            await Assert.That(capture.Items.Count).IsEqualTo(33);
            await Assert.That(capture.Items.Last().RetryAttempts).IsEqualTo(32);
            await Assert.That(capture.Items.Where(item => item.Internal).Select(item => item.RetryAttempts).Distinct().Count()).IsEqualTo(32);
        }
        finally { owner.Complete(); foreach (var borrower in borrowers) borrower.Complete(); }
    }

    [Test]
    public async Task CompletedLeasesRejectFurtherWorkAndBorrowersHaveNoFinalPublication()
    {
        var owner = ErrorObservation.StartFailure();
        var borrower = owner.Borrow();
        borrower.Complete();
        await Assert.That(borrower.RecordHandled(new IOException())).IsFalse();
        await Assert.That(borrower.Borrow().RecordHandled(new IOException())).IsFalse();
        await Assert.That(typeof(ErrorObservation.Borrower).GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Any(method => method.Name == "PublishFinal")).IsFalse();
        owner.Complete();
    }

    [Test]
    public async Task FinalPublicationClosesRetryReportingAndNewBorrowing()
    {
        using var configuration = new MetricConfigurationScope();
        using var capture = new Capture();
        var owner = ErrorObservation.StartFailure();
        var borrower = owner.Borrow();
        var error = new IOException();
        try
        {
            owner.PublishFinal(error);
            await Assert.That(borrower.RecordHandled(error)).IsFalse();
            await Assert.That(owner.RecordHandled(error)).IsFalse();
            await Assert.That(owner.Borrow().RecordHandled(error)).IsFalse();
            await Assert.That(borrower.Borrow().RecordHandled(error)).IsFalse();
            await Assert.That(capture.Items.Count).IsEqualTo(1);
        }
        finally { borrower.Complete(); owner.Complete(); }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task RejectedBookkeepingPreservesOriginalException(bool reuse, bool cancelled)
    {
        using var configuration = new MetricConfigurationScope();
        using var capture = new Capture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Exception expected = cancelled ? new OperationCanceledException(cancellation.Token) : new IOException();
        var owner = ErrorObservation.StartFailure();
        var borrower = owner.Borrow();
        owner.PublishFinal(expected);
        var rentals = new List<ErrorObservation.FinalOwner>();
        Exception? actual = null;
        try
        {
            if (reuse)
            {
                borrower.Complete();
                owner.Complete();
                for (var i = 0; i < 33; i++) rentals.Add(ErrorObservation.StartFailure());
            }
            try { throw expected; }
            catch (Exception error)
            {
                owner.RecordHandled(error);
                borrower.RecordHandled(error);
                owner.Borrow().RecordHandled(error);
                borrower.Borrow().RecordHandled(error);
                owner.PublishFinal(error);
                throw;
            }
            finally { borrower.Complete(); owner.Complete(); }
        }
        catch (Exception error) { actual = error; }
        finally { foreach (var rental in rentals) rental.Complete(); }
        await Assert.That(ReferenceEquals(actual, expected)).IsTrue();
        if (cancelled) await Assert.That(((OperationCanceledException)actual!).CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(capture.Items.Count).IsEqualTo(1);
    }

    [Test]
    public async Task DefaultLeasesRejectBookkeepingWithoutMetrics()
    {
        using var configuration = new MetricConfigurationScope();
        using var capture = new Capture();
        var owner = default(ErrorObservation.FinalOwner);
        var borrower = default(ErrorObservation.Borrower);
        var error = new IOException();
        await Assert.That(owner.RecordHandled(error)).IsFalse();
        await Assert.That(owner.PublishFinal(error)).IsFalse();
        await Assert.That(owner.Borrow().RecordHandled(error)).IsFalse();
        await Assert.That(borrower.RecordHandled(error)).IsFalse();
        await Assert.That(borrower.Borrow().RecordHandled(error)).IsFalse();
        owner.Complete();
        borrower.Complete();
        await Assert.That(capture.Items.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GenerationMismatchCannotChangeLiveObservation()
    {
        using var configuration = new MetricConfigurationScope();
        using var capture = new Capture();
        var observation = new ErrorObservation.Observation { Generation = 2, References = 1, RetryAttempts = 7 };
        var stale = new ErrorObservation.FinalOwner(new ErrorObservation.Lease(observation, 1));
        var current = new ErrorObservation.FinalOwner(new ErrorObservation.Lease(observation, 2));
        var error = new IOException();
        try
        {
            await Assert.That(stale.RecordHandled(error)).IsFalse();
            await Assert.That(stale.Borrow().RecordHandled(error)).IsFalse();
            await Assert.That(stale.PublishFinal(error)).IsFalse();
            stale.Complete();
            await Assert.That(observation.References).IsEqualTo(1);
            await Assert.That(current.PublishFinal(error)).IsTrue();
            await Assert.That(capture.Items.Single().RetryAttempts).IsEqualTo(7);
        }
        finally { stale.Complete(); current.Complete(); }
    }

    [Test]
    public async Task RetryAndReferenceLimitsDoNotThrowOrOverflow()
    {
        using var configuration = new MetricConfigurationScope();
        using var capture = new Capture();
        var observation = new ErrorObservation.Observation { Generation = 1, References = int.MaxValue, RetryAttempts = int.MaxValue };
        var owner = new ErrorObservation.FinalOwner(new ErrorObservation.Lease(observation, 1));
        var error = new IOException();
        try
        {
            await Assert.That(owner.Borrow().RecordHandled(error)).IsFalse();
            await Assert.That(observation.References).IsEqualTo(int.MaxValue);
            await Assert.That(owner.RecordHandled(error)).IsTrue();
            await Assert.That(owner.PublishFinal(error)).IsTrue();
            await Assert.That(capture.Items.Count).IsEqualTo(2);
            await Assert.That(capture.Items.All(item => item.RetryAttempts == int.MaxValue)).IsTrue();
        }
        finally { observation.References = 1; owner.Complete(); }
    }

    [Test]
    public async Task FinalInspectionRacingRetriesRejectsLateWorkWithoutThrowing()
    {
        using var configuration = new MetricConfigurationScope();
        using var capture = new Capture();
        var owner = ErrorObservation.StartFailure();
        var borrowers = Enumerable.Range(0, 32).Select(_ => owner.Borrow()).ToArray();
        var error = new IOException();
        try
        {
            var final = Task.Run(() => owner.PublishFinal(error));
            var retries = await Task.WhenAll(borrowers.Select(borrower => Task.Run(() => borrower.RecordHandled(error))));
            await Assert.That(await final).IsTrue();
            await Assert.That(capture.Items.Count(item => item.Internal)).IsEqualTo(retries.Count(accepted => accepted));
            await Assert.That(capture.Items.Single(item => !item.Internal).RetryAttempts).IsEqualTo(retries.Count(accepted => accepted));
            await Assert.That(borrowers[0].RecordHandled(error)).IsFalse();
        }
        finally { foreach (var borrower in borrowers) borrower.Complete(); owner.Complete(); }
    }

    [Test]
    public async Task SlowExporterDoesNotHoldOwnershipGate()
    {
        using var configuration = new MetricConfigurationScope();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var capture = new Capture(onMeasurement: () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Exporter was not released.");
        });
        var owner = ErrorObservation.StartFailure();
        var borrower = owner.Borrow();
        var publication = Task.Run(() => owner.PublishFinal(new IOException()));
        try
        {
            await Assert.That(entered.Wait(TimeSpan.FromSeconds(5))).IsTrue();
            await Task.Run(() => borrower.Complete()).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.Set();
            await publication;
            borrower.Complete();
            owner.Complete();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FailurePublicationPreservesOriginalExceptionAndCancellationToken(bool cancelled)
    {
        using var configuration = new MetricConfigurationScope();
        using var capture = new Capture(throwOnMeasurement: true);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Exception expected = cancelled ? new OperationCanceledException(cancellation.Token) : new IOException();
        Exception? actual = null;
        try { await ObserveWithOwnerAsync(ValueTask.FromException<int>(expected)); } catch (Exception error) { actual = error; }
        await Assert.That(ReferenceEquals(actual, expected)).IsTrue();
        if (cancelled) await Assert.That(((OperationCanceledException)actual!).CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(capture.Items.Count).IsEqualTo(1);
    }

    private static async ValueTask<int> ObserveWithOwnerAsync(ValueTask<int> operation)
    {
        var owner = default(ErrorObservation.FinalOwner);
        try { return await operation.ConfigureAwait(false); }
        catch (Exception failure)
        {
            owner = ErrorObservation.StartFailure();
            owner.PublishFinal(failure);
            throw;
        }
        finally { owner.Complete(); }
    }

    [Test]
    public async Task SuccessUsesNoObservationStorageOrAllocations()
    {
        using var configuration = new MetricConfigurationScope();
        using var capture = new Capture();
        _ = Measure(false);
        _ = Measure(true);
        var measured = AllocationMeasurement.WithoutConcurrentGc(() => Measure(false));
        var control = AllocationMeasurement.WithoutConcurrentGc(() => Measure(true));
        await Assert.That(measured).IsEqualTo(0L);
        await Assert.That(control).IsGreaterThanOrEqualTo(37_000L);
        await Assert.That(capture.Items.Count).IsEqualTo(0);
    }

    private static object? _allocationAnchor;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Measure(bool allocate)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
        {
            var result = ObserveWithOwnerAsync(new ValueTask<int>(42)).GetAwaiter().GetResult();
            if (result != 42) throw new InvalidOperationException();
            if (allocate) Volatile.Write(ref _allocationAnchor, new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private sealed class Capture : IDisposable
    {
        private readonly MeterListener _listener = new();
        internal readonly ConcurrentQueue<(bool Internal, int RetryAttempts)> Items = new();
        internal Capture(bool throwOnMeasurement = false, Action? onMeasurement = null)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, RespireTelemetry.Meter) && instrument.Name == "redis.client.errors")
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                var internallyHandled = false;
                var retryAttempts = 0;
                foreach (var tag in tags)
                {
                    if (tag.Key == "redis.client.errors.internal") internallyHandled = (bool)tag.Value!;
                    if (tag.Key == "redis.client.operation.retry_attempts") retryAttempts = (int)tag.Value!;
                }
                Items.Enqueue((internallyHandled, retryAttempts));
                onMeasurement?.Invoke();
                if (throwOnMeasurement) throw new InvalidOperationException("Exporter failure");
            });
            _listener.Start();
        }
        public void Dispose() => _listener.Dispose();
    }
}
