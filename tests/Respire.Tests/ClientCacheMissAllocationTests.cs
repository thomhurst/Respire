using System.Reflection;
using System.Runtime.CompilerServices;
using Respire.Internal;
using Respire.Protocol;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class ClientCacheMissAllocationTests
{
    [Test]
    [NotInParallel]
    public async Task RacingStandaloneMissProducerAllocatesNothingWhenAnotherReadAlreadyFilledTheCache()
    {
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", 1)],
            ClientSideCache = new() { CoalesceConcurrentMisses = true },
        });
        var cache = client.Core.ClientCache!;
        var key = new RespireKey("key");
        var token = cache.BeginRead(in key);
        var response = RespValue.BulkString("value"u8.ToArray());
        cache.CompleteRead(in token, in response, allowInsert: true);
        // Bind once outside the measurement. This is the actual miss producer's
        // second lookup, after another read wins publication; no wire send starts.
        var producer = BindProducer(client);
        for (var index = 0; index < 32; index++)
        {
            Measure(producer, cache, key, false);
            Measure(producer, cache, key, true);
        }
        var measured = AllocationMeasurement.WithoutConcurrentGc(() => (
            Actual: Measure(producer, cache, key, false), Control: Measure(producer, cache, key, true)));
        await Assert.That(measured.Actual).IsEqualTo(0);
        await Assert.That(measured.Control).IsGreaterThanOrEqualTo(37_000);
    }

    private delegate ValueTask<RespValue> MissProducer(RespireKey key, ClientSideCacheCoordinator cache,
        CancellationToken cancellationToken, ResponseConverter<RespireClient, RespValue> converter, bool transferResponse,
        RespireTelemetry.ErrorObservation observation);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RacingProducerPreservesAsyncConverterFailureAndCancellation(bool canceled)
    {
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", 1)],
            ClientSideCache = new() { CoalesceConcurrentMisses = true },
        });
        var cache = client.Core.ClientCache!;
        var key = new RespireKey("key");
        var token = cache.BeginRead(in key);
        var response = RespValue.BulkString("value"u8.ToArray());
        cache.CompleteRead(in token, in response, allowInsert: true);
        var producer = BindProducer(client);
        using var cancellation = new CancellationTokenSource();
        Exception expected = canceled ? new OperationCanceledException(cancellation.Token) : new FormatException("converter");
        var operation = producer(key, cache, default, (RespireClient _, in RespValue _) => throw expected, true, default);
        await Assert.That(operation.IsCompleted).IsTrue();
        await Assert.That(operation.IsCanceled).IsEqualTo(canceled);
        await Assert.That(operation.IsFaulted).IsEqualTo(!canceled);
        Exception? actual = null;
        try { await operation; }
        catch (Exception error) { actual = error; }
        await Assert.That(ReferenceEquals(actual, expected)).IsTrue();
        await Assert.That(cancellation.IsCancellationRequested).IsFalse();
        cancellation.Cancel();
        var control = producer(key, cache, cancellation.Token,
            static (RespireClient _, in RespValue value) => value, true, default);
        await Assert.That(control.IsCompletedSuccessfully).IsTrue();
        await Assert.That(control.Result.AsString()).IsEqualTo("value");
        await Assert.That(client.IsConnected).IsFalse();
    }

    private static MissProducer BindProducer(RespireClient client)
        => typeof(RespireClient).GetMethod("FetchGetAndCacheAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .MakeGenericMethod(typeof(RespValue)).CreateDelegate<MissProducer>(client);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RacingProducerRestoresConverterChangesToCallerContext(bool throws)
    {
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", 1)],
            ClientSideCache = new() { CoalesceConcurrentMisses = true },
        });
        var cache = client.Core.ClientCache!;
        var key = new RespireKey("key");
        var token = cache.BeginRead(in key);
        var response = RespValue.BulkString("value"u8.ToArray());
        cache.CompleteRead(in token, in response, allowInsert: true);
        var producer = BindProducer(client);
        var local = new AsyncLocal<string> { Value = "caller" };
        var original = SynchronizationContext.Current;
        var caller = new SynchronizationContext();
        var changed = new SynchronizationContext();
        var expected = new FormatException("converter");
        string? valueAfter;
        SynchronizationContext? contextAfter;
        var sawCaller = false;
        ValueTask<RespValue> operation;
        try
        {
            SynchronizationContext.SetSynchronizationContext(caller);
            operation = producer(key, cache, default, (RespireClient _, in RespValue value) =>
            {
                sawCaller = ReferenceEquals(SynchronizationContext.Current, caller) && local.Value == "caller";
                local.Value = "converter";
                SynchronizationContext.SetSynchronizationContext(changed);
                if (throws) throw expected;
                return value;
            }, true, default);
            valueAfter = local.Value;
            contextAfter = SynchronizationContext.Current;
        }
        finally { SynchronizationContext.SetSynchronizationContext(original); }
        await Assert.That(operation.IsCompletedSuccessfully).IsEqualTo(!throws);
        if (throws)
        {
            Exception? actual = null;
            try { await operation; }
            catch (Exception error) { actual = error; }
            await Assert.That(ReferenceEquals(actual, expected)).IsTrue();
        }
        await Assert.That(sawCaller).IsTrue();
        await Assert.That(valueAfter).IsEqualTo("caller");
        await Assert.That(ReferenceEquals(contextAfter, caller)).IsTrue();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Measure(MissProducer producer, ClientSideCacheCoordinator cache, RespireKey key, bool control)
    {
        var start = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1000; index++)
        {
            var operation = producer(key, cache, default,
                static (RespireClient _, in RespValue value) => value, true, default);
            if (!operation.IsCompletedSuccessfully || !operation.Result.AsSpan().SequenceEqual("value"u8))
                throw new InvalidOperationException("Expected the racing producer to use the published cache entry.");
            if (control) GC.KeepAlive(new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - start;
    }
}
