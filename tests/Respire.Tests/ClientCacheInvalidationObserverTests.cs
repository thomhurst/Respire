using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class ClientCacheInvalidationObserverTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Test]
    public async Task ExistingCacheImplementationsRemainCompatible()
    {
        IRespireClientSideCache cache = new ExternalCache();
        await Assert.That(() => cache.SubscribeInvalidations("key", _ => { })).ThrowsExactly<NotSupportedException>();
        cache.Clear();
        await Assert.That(cache.Count).IsEqualTo(0);
    }

    private sealed class ExternalCache : IRespireClientSideCache
    {
        public int Count => 0;
        public long SizeBytes => 0;
        public RespireClientSideCacheStatistics GetStatistics() => default;
        public void Clear() { }
    }

    [Test]
    public async Task BinaryKeysAreOwnedAndOnlyMatchingObserversReceiveThePush()
    {
        var cache = new ClientSideCacheCoordinator(new());
        byte[] bytes = [0, 255, 1];
        var expected = new RespireKey(bytes.ToArray());
        var matching = Channel.CreateUnbounded<RespireClientCacheInvalidation>();
        var other = Channel.CreateUnbounded<RespireClientCacheInvalidation>();
        using var subscription = cache.SubscribeInvalidations(bytes, change => matching.Writer.TryWrite(change));
        using var unrelated = cache.SubscribeInvalidations("unrelated", change => other.Writer.TryWrite(change));
        Array.Fill(bytes, (byte)42);
        Insert(cache, expected);
        byte[] wireKey = [0, 255, 1];
        using var push = Push(RespValue.Array([RespValue.BulkString(wireKey)]));
        cache.HandlePush(in push);
        Array.Fill(wireKey, (byte)99);
        var change = await matching.Reader.ReadAsync().AsTask().WaitAsync(Limit);
        await Assert.That(change.Key).IsEqualTo(expected);
        await Assert.That(subscription.Key).IsEqualTo(expected);
        await Assert.That(change.Reasons).IsEqualTo(RespireClientCacheInvalidationReason.ServerInvalidation);
        await Assert.That(cache.Count).IsEqualTo(0);
        cache.Clear(); // A delivery barrier for the unrelated observer, without sleeping.
        var cleared = await other.Reader.ReadAsync().AsTask().WaitAsync(Limit);
        await Assert.That(cleared.Reasons).IsEqualTo(RespireClientCacheInvalidationReason.ExplicitClear);
    }

    [Test]
    public async Task SlowObserverKeepsOnePendingWakeUpWithoutDelayingEvictionOrOtherObservers()
    {
        var cache = new ClientSideCacheCoordinator(new());
        RespireKey key = "key";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<RespireClientCacheInvalidation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fast = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var slow = cache.SubscribeInvalidations(key, change =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            }
            else pending.TrySetResult(change);
        });
        using var other = cache.SubscribeInvalidations(key, _ => fast.TrySetResult());
        try
        {
            cache.Invalidate(in key);
            await entered.Task.WaitAsync(Limit);
            await fast.Task.WaitAsync(Limit);
            for (var index = 0; index < 1_000; index++)
            {
                Insert(cache, key);
                cache.Invalidate(in key, RespireClientCacheInvalidationReason.ServerInvalidation);
            }
            cache.Clear();
            cache.FlushForContinuityLoss();
            await Assert.That(cache.Count).IsEqualTo(0);
            await Assert.That(calls).IsEqualTo(1);
        }
        finally { release.TrySetResult(); }
        var coalesced = await pending.Task.WaitAsync(Limit);
        await Assert.That(coalesced.Reasons).IsEqualTo(RespireClientCacheInvalidationReason.ServerInvalidation
            | RespireClientCacheInvalidationReason.ExplicitClear | RespireClientCacheInvalidationReason.ContinuityLost);
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    public async Task ThrowingObserverIsIsolatedAndKeepsReceiving()
    {
        var cache = new ClientSideCacheCoordinator(new());
        RespireKey key = "key";
        var failure = new InvalidOperationException("observer failed");
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        using var subscription = cache.SubscribeInvalidations(key, _ =>
        {
            if (Interlocked.Increment(ref count) == 1)
            {
                first.TrySetResult();
                throw failure;
            }
            second.TrySetResult();
        });
        Insert(cache, key);
        cache.Invalidate(in key);
        await first.Task.WaitAsync(Limit);
        cache.Invalidate(in key);
        await second.Task.WaitAsync(Limit);
        await Assert.That(subscription.LastObserverException).IsSameReferenceAs(failure);
        await Assert.That(cache.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CancellationAndDisposalDiscardPendingWakeUps(bool cancel)
    {
        var cache = new ClientSideCacheCoordinator(new());
        RespireKey key = "key";
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var subscription = cache.SubscribeInvalidations(key, _ =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            returned.TrySetResult();
        }, cancellation.Token);
        try
        {
            cache.Invalidate(in key);
            await entered.Task.WaitAsync(Limit);
            cache.Invalidate(in key);
            if (cancel) cancellation.Cancel();
            else subscription.Dispose();
            await Assert.That(subscription.IsDisposed).IsTrue();
            cache.Clear();
        }
        finally { release.TrySetResult(); }
        await returned.Task.WaitAsync(Limit);
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    [Arguments(RespireClientCacheInvalidationReason.ContinuityLost)]
    [Arguments(RespireClientCacheInvalidationReason.ExplicitClear)]
    [Arguments(RespireClientCacheInvalidationReason.LocalMutation)]
    [Arguments(RespireClientCacheInvalidationReason.ServerInvalidation)]
    public async Task EveryGlobalInvalidationWakesObserversAndRejectsOlderReads(RespireClientCacheInvalidationReason reason)
    {
        var cache = new ClientSideCacheCoordinator(new());
        RespireKey key = "key";
        var received = new TaskCompletionSource<RespireClientCacheInvalidation>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = cache.SubscribeInvalidations(key, change => received.TrySetResult(change));
        Insert(cache, key);
        var token = cache.BeginRead(in key);
        switch (reason)
        {
            case RespireClientCacheInvalidationReason.ContinuityLost: cache.FlushForContinuityLossWithoutMetrics(); break;
            case RespireClientCacheInvalidationReason.ExplicitClear: cache.Clear(); break;
            case RespireClientCacheInvalidationReason.LocalMutation: cache.FlushForUnknownCommand(); break;
            default:
                using (var push = Push(RespValue.Null)) cache.HandlePush(in push);
                break;
        }
        var value = RespValue.BulkString("old"u8.ToArray());
        cache.CompleteRead(in token, in value, allowInsert: true);
        var change = await received.Task.WaitAsync(Limit);
        await Assert.That(change.Key).IsEqualTo(key);
        await Assert.That(change.Reasons).IsEqualTo(reason);
        await Assert.That(cache.Count).IsEqualTo(0);
    }

    [Test]
    public async Task EmptyKeyRetainsItsIdentityAndSelfDisposalStopsObservation()
    {
        var cache = new ClientSideCacheCoordinator(new());
        RespireKey key = RespireKey.Empty;
        var completed = new TaskCompletionSource<RespireClientCacheInvalidation>(TaskCreationOptions.RunContinuationsAsynchronously);
        RespireClientCacheInvalidationSubscription? subscription = null;
        subscription = cache.SubscribeInvalidations(key, change =>
        {
            subscription!.Dispose();
            completed.TrySetResult(change);
        });
        using (subscription)
        {
            Insert(cache, key);
            cache.Invalidate(in key);
            var change = await completed.Task.WaitAsync(Limit);
            await Assert.That(change.Key).IsEqualTo(RespireKey.Empty);
            await Assert.That(subscription.IsDisposed).IsTrue();
            await Assert.That(cache.Count).IsEqualTo(0);
        }
    }

    [Test]
    public async Task RegistrationChecksCoverageCancellationAndLifetime()
    {
        var cache = new ClientSideCacheCoordinator(new()
        {
            TrackingMode = RespireClientTrackingMode.Broadcast, BroadcastPrefixes = ["tenant:"],
        });
        await Assert.That(() => cache.SubscribeInvalidations("other:key", _ => { })).ThrowsExactly<ArgumentException>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(() => cache.SubscribeInvalidations("tenant:key", _ => { }, cancellation.Token))
            .ThrowsExactly<OperationCanceledException>();
        using var subscription = cache.SubscribeInvalidations("tenant:key", _ => { });
        cache.StopInvalidationObservers();
        await Assert.That(subscription.IsDisposed).IsTrue();
        await Assert.That(() => cache.SubscribeInvalidations("tenant:key", _ => { })).ThrowsExactly<ObjectDisposedException>();
    }

    [Test]
    [NotInParallel]
    public async Task UnobservedInvalidationsAllocateNothingIncludingAfterUnsubscribe()
    {
        var cache = new ClientSideCacheCoordinator(new());
        _ = Measure(cache, allocate: false);
        var disabled = AllocationMeasurement.WithoutConcurrentGc(() => Measure(cache, allocate: false));
        using (cache.SubscribeInvalidations("key", _ => { })) { }
        var removed = AllocationMeasurement.WithoutConcurrentGc(() => Measure(cache, allocate: false));
        var control = AllocationMeasurement.WithoutConcurrentGc(() => Measure(cache, allocate: true));
        await Assert.That(disabled).IsEqualTo(0L);
        await Assert.That(removed).IsEqualTo(0L);
        await Assert.That(control).IsGreaterThanOrEqualTo(37_000L);
    }

    private static object? _escape;
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Measure(ClientSideCacheCoordinator cache, bool allocate)
    {
        RespireKey key = "key";
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1_000; index++)
        {
            cache.Invalidate(in key);
            if (allocate) Volatile.Write(ref _escape, new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static RespValue Push(RespValue keys) => RespValue.Array([RespValue.BulkString("invalidate"u8.ToArray()), keys]);

    private static void Insert(ClientSideCacheCoordinator cache, RespireKey key)
    {
        var token = cache.BeginRead(in key);
        var value = RespValue.BulkString("value"u8.ToArray());
        cache.CompleteRead(in token, in value, allowInsert: true);
    }
}
