using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

[NotInParallel]
public class NativeResponseErrorTests
{
    [Test]
    [Arguments("raw")]
    [Arguments("converted")]
    [Arguments("string")]
    [Arguments("bytes")]
    public async Task CopiedRetryAttemptsSurviveCallerReleaseAndSourceReuse(string shape)
    {
        using var configuration = new MetricConfigurationScope();
        using var capture = new Capture();
        var (source, consume) = Rent(shape, true);
        source.ErrorAttempts = 3;
        var expected = new IOException("transport failure");
        source.TrySetException(expected);
        source.ReleaseRef();
        Exception? actual = null;
        try { await consume(); }
        catch (Exception error) { actual = error; }
        await Assert.That(ReferenceEquals(actual, expected)).IsTrue();
        await Assert.That(capture.Items.Single().Attempts).IsEqualTo(3);
        var (reused, reuseConsume) = Rent(shape, true);
        reused.TrySetResult(RespValue.Integer(1));
        reused.ReleaseRef();
        try { await reuseConsume(); }
        catch (RespireProtocolException) { }
        await Assert.That(reused.ErrorAttempts).IsEqualTo(0);
    }

    [Test]
    public async Task NativeConversionCountsOriginalFailureAfterCallerCleanup()
    {
        using var configuration = new MetricConfigurationScope();
        var expected = new RespireProtocolException("conversion failure");
        var source = ConvertedPendingResponseSource<Exception, int>.Rent(expected,
            static (Exception error, in RespValue _) => throw error, false, "GET", observeErrors: true);
        using var capture = new Capture(() =>
        {
            if (source.InspectForTests().ReferenceCount != 1)
                throw new InvalidOperationException("Caller still owns the source.");
        });
        var pending = source.Task;
        source.TrySetResult(RespValue.Integer(1));
        Exception? actual = null;
        try { _ = await pending; }
        catch (Exception error) { actual = error; }
        finally { source.ReleaseRef(); }
        await Assert.That(ReferenceEquals(actual, expected)).IsTrue();
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        await Assert.That(capture.CallbackFailure).IsNull();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PooledConversionCountsOriginalFailure(bool asynchronous)
    {
        using var configuration = new MetricConfigurationScope();
        using var capture = new Capture();
        var expected = new RespireProtocolException("conversion failure");
        var completion = new TaskCompletionSource<RespValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? actual = null;
        try
        {
            var result = PooledResponseSource<Exception, int>.Create(
                asynchronous ? new(completion.Task) : new(RespValue.Integer(1)), expected,
                static (Exception error, in RespValue _) => throw error);
            if (asynchronous) completion.SetResult(RespValue.Integer(1));
            _ = await result;
        }
        catch (Exception error) { actual = error; }
        await Assert.That(ReferenceEquals(actual, expected)).IsTrue();
        await Assert.That(capture.Items.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments(false, "conversion")]
    [Arguments(true, "conversion")]
    [Arguments(false, "cancel")]
    [Arguments(true, "cancel")]
    [Arguments(false, "cleanup")]
    [Arguments(true, "cleanup")]
    public async Task PooledConversionPreservesFirstFailureWhenCleanupThrows(bool asynchronous, string outcome)
    {
        using var configuration = new MetricConfigurationScope();
        using var capture = new Capture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Exception? expected = outcome switch
        {
            "conversion" => new RespireProtocolException("conversion failure"),
            "cancel" => new OperationCanceledException(cancellation.Token),
            _ => null,
        };
        // An invalid element count injects a deterministic disposal failure without renting storage.
        var response = RespValue.PooledAggregate(RespDataType.Array, new RespValue[1], 2);
        var owner = ErrorObservation.StartFailure();
        var completion = new TaskCompletionSource<RespValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? actual = null;
        Task<int>? task = null;
        try
        {
            var converted = PooledResponseSource<Exception?, int>.Create(
                asynchronous ? new(completion.Task) : new(response), expected,
                static (Exception? error, in RespValue _) => error is null ? 1 : throw error,
                observation: owner);
            task = converted.AsTask();
            if (asynchronous) completion.SetResult(response);
            _ = await task;
        }
        catch (Exception error) { actual = error; }
        if (expected is null) await Assert.That(actual).IsTypeOf<IndexOutOfRangeException>();
        else await Assert.That(ReferenceEquals(actual, expected)).IsTrue();
        if (outcome == "cancel")
        {
            await Assert.That(((OperationCanceledException)actual!).CancellationToken).IsEqualTo(cancellation.Token);
            if (asynchronous) await Assert.That(task!.IsCanceled).IsTrue();
        }
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        if (outcome == "cancel") await Assert.That(capture.Items.Single().Category).IsEqualTo("cancelled");
        await Assert.That(owner.PublishFinal(new IOException())).IsFalse();
    }

    [Test]
    [Arguments("raw", "transport")]
    [Arguments("raw", "server")]
    [Arguments("raw", "cancel")]
    [Arguments("converted", "transport")]
    [Arguments("converted", "server")]
    [Arguments("converted", "cancel")]
    [Arguments("string", "transport")]
    [Arguments("string", "server")]
    [Arguments("string", "cancel")]
    [Arguments("bytes", "transport")]
    [Arguments("bytes", "server")]
    [Arguments("bytes", "cancel")]
    public async Task NativeFailuresPreserveExceptionAndCancellationAfterCleanup(string shape, string outcome)
    {
        using var configuration = new MetricConfigurationScope();
        var (source, consume) = Rent(shape, true);
        using var capture = new Capture(() =>
        {
            if (source.InspectForTests().ReferenceCount != 1)
                throw new InvalidOperationException("Caller reference was not released.");
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var expected = new IOException("transport failure");
        if (outcome == "server") source.TrySetResult(RespValue.Error("WRONGTYPE private data"));
        else if (outcome == "cancel") source.TrySetCanceled(cancellation.Token);
        else source.TrySetException(expected);
        var task = consume();
        Exception? actual = null;
        try { await task; }
        catch (Exception error) { actual = error; }
        await Assert.That(source.CommandName).IsEqualTo("GET");
        source.ReleaseRef();
        if (outcome == "transport") await Assert.That(ReferenceEquals(actual, expected)).IsTrue();
        else if (outcome == "server") await Assert.That(actual).IsTypeOf<RespireServerException>();
        else
        {
            await Assert.That(task.IsCanceled).IsTrue();
            await Assert.That(((OperationCanceledException)actual!).CancellationToken).IsEqualTo(cancellation.Token);
        }
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        await Assert.That(capture.Items.Single().Internal).IsFalse();
        await Assert.That(capture.Items.Single().Category).IsEqualTo(outcome switch
        {
            "transport" => "network", "cancel" => "cancelled", _ => "server",
        });
        await Assert.That(capture.CallbackFailure).IsNull();
    }

    [Test]
    [Arguments("raw")]
    [Arguments("converted")]
    [Arguments("string")]
    [Arguments("bytes")]
    public async Task BorrowedNativeInspectionDoesNotPublishFinalFailure(string shape)
    {
        using var configuration = new MetricConfigurationScope();
        using var capture = new Capture();
        var (source, consume) = Rent(shape, false);
        source.TrySetException(new IOException());
        try { await consume(); }
        catch (IOException) { }
        finally { source.ReleaseRef(); }
        await Assert.That(capture.Items.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("raw")]
    [Arguments("converted")]
    [Arguments("string")]
    [Arguments("bytes")]
    public async Task NativeOwnersRetainRetriesWhenReceiveReferenceReturnsSourceFirst(string shape)
    {
        using var configuration = new MetricConfigurationScope();
        using var capture = new Capture();
        var owner = ErrorObservation.StartFailure();
        owner.RecordHandled(new IOException());
        var (source, consume) = Rent(shape, true, owner);
        source.TrySetException(new IOException());
        source.ReleaseRef();
        try { await consume(); }
        catch (IOException) { }
        await Assert.That(capture.Items.Count).IsEqualTo(2);
        await Assert.That(capture.Items.Last().Attempts).IsEqualTo(1);
        await Assert.That(owner.PublishFinal(new IOException())).IsFalse();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task PooledConversionRetainsSuppliedRetryOwner(bool asynchronous, bool inputFails)
    {
        using var configuration = new MetricConfigurationScope();
        using var capture = new Capture();
        var owner = ErrorObservation.StartFailure();
        owner.RecordHandled(new IOException());
        var expected = new RespireProtocolException("conversion or input failure");
        var completion = new TaskCompletionSource<RespValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? actual = null;
        try
        {
            ValueTask<RespValue> input = asynchronous ? new(completion.Task)
                : inputFails ? ValueTask.FromException<RespValue>(expected) : new(RespValue.Integer(1));
            var result = PooledResponseSource<Exception, int>.Create(input, expected,
                static (Exception error, in RespValue _) => throw error, observation: owner);
            if (asynchronous)
            {
                if (inputFails) completion.SetException(expected);
                else completion.SetResult(RespValue.Integer(1));
            }
            _ = await result;
        }
        catch (Exception error) { actual = error; }
        await Assert.That(ReferenceEquals(actual, expected)).IsTrue();
        await Assert.That(capture.Items.Count).IsEqualTo(2);
        await Assert.That(capture.Items.Last().Attempts).IsEqualTo(1);
        await Assert.That(owner.PublishFinal(expected)).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OrdinaryPooledConversionDoesNotRecountInputFailures(bool asynchronous)
    {
        using var configuration = new MetricConfigurationScope();
        using var capture = new Capture();
        var expected = new IOException();
        var source = new PendingResponsePool(1).Rent(observeErrors: true);
        if (!asynchronous) source.TrySetException(expected);
        var converted = PooledResponseSource<int, long>.Create(source.Task, 0,
            static (int _, in RespValue value) => value.AsInteger());
        if (asynchronous) source.TrySetException(expected);
        try { _ = await converted; }
        catch (IOException error) { await Assert.That(ReferenceEquals(error, expected)).IsTrue(); }
        finally { source.ReleaseRef(); }
        await Assert.That(capture.Items.Count).IsEqualTo(1);
    }

    [Test]
    public async Task SuccessfulNativeAndPooledInspectionAddsNoAllocations()
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

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task PooledResponseOwnershipSurvivesObservation(bool asynchronous, bool transfer)
    {
        using var configuration = new MetricConfigurationScope();
        using var capture = new Capture();
        var elements = RespirePools.ValueArrays.Rent(1);
        elements[0] = RespValue.Integer(42);
        var response = RespValue.PooledAggregate(RespDataType.Array, elements, 1);
        var completion = new TaskCompletionSource<RespValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        var converted = PooledResponseSource<int, RespValue>.Create(
            asynchronous ? new(completion.Task) : new(response), 0,
            static (int _, in RespValue value) => value, transferOwnership: transfer);
        if (asynchronous) completion.SetResult(response);
        var result = await converted;
        await Assert.That(elements[0].Type == RespDataType.Integer).IsEqualTo(transfer);
        if (transfer) result.Dispose();
        await Assert.That(capture.Items.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PooledConversionPublishesAfterResponseDisposal(bool asynchronous)
    {
        using var configuration = new MetricConfigurationScope();
        var elements = RespirePools.ValueArrays.Rent(1);
        elements[0] = RespValue.Integer(42);
        var response = RespValue.PooledAggregate(RespDataType.Array, elements, 1);
        using var capture = new Capture(() =>
        {
            if (elements[0].Type == RespDataType.Integer)
                throw new InvalidOperationException("Response still owns its elements.");
        });
        var completion = new TaskCompletionSource<RespValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var converted = PooledResponseSource<int, int>.Create(
                asynchronous ? new(completion.Task) : new(response), 0,
                static (int _, in RespValue value) => throw new RespireProtocolException("conversion failure"),
                transferOwnership: true);
            if (asynchronous) completion.SetResult(response);
            _ = await converted;
        }
        catch (RespireProtocolException) { }
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        await Assert.That(capture.CallbackFailure).IsNull();
    }

    private static object? _allocationAnchor;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Measure(bool allocate)
    {
        var pool = new PendingResponsePool(1);
        CompletePooledConversion(pool);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
        {
            var native = ConvertedPendingResponseSource<int, long>.Rent(0,
                static (int _, in RespValue value) => value.AsInteger(), false, "GET", observeErrors: true);
            var task = native.Task;
            native.TrySetResult(RespValue.Integer(42));
            native.ReleaseRef();
            if (task.GetAwaiter().GetResult() != 42) throw new InvalidOperationException();
            var text = StringPendingResponseSource.Rent("GET", observeErrors: true);
            var textTask = text.Task;
            text.SetDirectResult("value");
            text.TrySetResult(default);
            text.ReleaseRef();
            if (textTask.GetAwaiter().GetResult() != "value") throw new InvalidOperationException();
            var bytes = BytesPendingResponseSource.Rent("GET", observeErrors: true);
            var bytesTask = bytes.Task;
            bytes.SetDirectResult(null);
            bytes.TrySetResult(default);
            bytes.ReleaseRef();
            if (bytesTask.GetAwaiter().GetResult() is not null) throw new InvalidOperationException();
            CompletePooledConversion(pool);
            var result = PooledResponseSource<int, long>.Create(new(RespValue.Integer(42)), 0,
                static (int _, in RespValue value) => value.AsInteger()).GetAwaiter().GetResult();
            if (result != 42) throw new InvalidOperationException();
            if (allocate) Volatile.Write(ref _allocationAnchor, new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static void CompletePooledConversion(PendingResponsePool pool)
    {
        var raw = pool.Rent(observeErrors: true);
        var converted = PooledResponseSource<int, long>.Create(raw.Task, 0,
            static (int _, in RespValue value) => value.AsInteger());
        raw.TrySetResult(RespValue.Integer(42));
        raw.ReleaseRef();
        if (converted.GetAwaiter().GetResult() != 42) throw new InvalidOperationException();
    }

    private static (PendingResponse Source, Func<Task> Consume) Rent(string shape, bool observeErrors,
        ErrorObservation.FinalOwner observation = default)
    {
        if (shape == "raw")
        {
            var source = new PendingResponsePool(1).Rent(true, "GET", observeErrors, observation);
            var task = source.Task;
            return (source, async () => { using var value = await task; });
        }
        if (shape == "converted")
        {
            var source = ConvertedPendingResponseSource<int, long>.Rent(0,
                static (int _, in RespValue value) => value.AsInteger(), false, "GET",
                observeErrors: observeErrors, observation: observation);
            var task = source.Task;
            return (source, async () => { _ = await task; });
        }
        if (shape == "string")
        {
            var source = StringPendingResponseSource.Rent("GET", observeErrors: observeErrors, observation: observation);
            var task = source.Task;
            return (source, async () => { _ = await task; });
        }
        else
        {
            var source = BytesPendingResponseSource.Rent("GET", observeErrors: observeErrors, observation: observation);
            var task = source.Task;
            return (source, async () => { _ = await task; });
        }
    }

    private sealed class Capture : IDisposable
    {
        private readonly MeterListener _listener = new();
        internal readonly ConcurrentQueue<(bool Internal, int Attempts, string Category)> Items = new();
        internal Exception? CallbackFailure;
        internal Capture(Action? onMeasurement = null)
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
                var category = "";
                foreach (var tag in tags)
                {
                    if (tag.Key == "redis.client.errors.internal") internallyHandled = (bool)tag.Value!;
                    if (tag.Key == "redis.client.operation.retry_attempts") attempts = (int)tag.Value!;
                    if (tag.Key == "redis.client.errors.category") category = (string)tag.Value!;
                }
                Items.Enqueue((internallyHandled, attempts, category));
                try { onMeasurement?.Invoke(); }
                catch (Exception error) { CallbackFailure = error; }
            });
            _listener.Start();
        }
        public void Dispose() => _listener.Dispose();
    }
}
