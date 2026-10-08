using System.Diagnostics;
using System.Reflection;
using Respire.Internal;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class ClientCacheEmptyInvalidationTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task EmptyInvalidationCompletesWhileSharedReadGateIsHeld(bool coalescing, bool flush)
    {
        var cache = new ClientSideCacheCoordinator(new() { CoalesceConcurrentMisses = coalescing });
        RespireKey key = "key";
        var token = cache.BeginRead(in key);
        using var value = RespValue.BulkString("old"u8.ToArray());
        cache.CompleteRead(in token, in value, allowInsert: true);
        using (var completed = await cache.CoalesceReadAsync(new ClientCacheCommandKey("GET", key.AsValue()), 0,
            static (_, _) => ValueTask.FromResult(RespValue.BulkString("primed"u8.ToArray())), default))
            await Assert.That(completed.AsString()).IsEqualTo("primed");
        await using var gate = await HeldGate.AcquireAsync(cache);
        Task invalidating = Task.CompletedTask;
        try
        {
            invalidating = Task.Run(() =>
            {
                if (flush) cache.FlushForContinuityLoss();
                else cache.Invalidate(in key);
            });
            // Gate release happens only after completion, so the write cannot pass by acquiring it.
            await invalidating.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(cache.Count).IsEqualTo(0);
        }
        finally
        {
            gate.Release();
            await invalidating.WaitAsync(Limit);
            cache.StopSharedReads();
        }
    }

    [Test]
    public async Task PendingReadAdmissionPreventsEmptyInvalidationFromSkippingTheGate()
    {
        var cache = new ClientSideCacheCoordinator(new() { CoalesceConcurrentMisses = true });
        RespireKey key = "key";
        var token = cache.BeginRead(in key);
        using var value = RespValue.BulkString("old"u8.ToArray());
        cache.CompleteRead(in token, in value, allowInsert: true);
        await using var gate = await HeldGate.AcquireAsync(cache);
        var reply = new TaskCompletionSource<RespValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<RespValue>? reading = null;
        Task invalidating = Task.CompletedTask;
        try
        {
            reading = Task.Run(async () => await cache.CoalesceReadAsync(
                new ClientCacheCommandKey("GET", key.AsValue()), reply,
                static (pending, cancellation) => new ValueTask<RespValue>(pending.Task.WaitAsync(cancellation)), default));
            await WaitUntilAsync(() => ReadCounter(cache, "_sharedReadAdmissions") == 1);
            invalidating = Task.Run(() => cache.Invalidate(in key));
            await WaitUntilAsync(() => ReadCounter(cache, "_sharedReadInvalidations") == 1);
            await Assert.That(invalidating.IsCompleted).IsFalse();
            await Assert.That(cache.Count).IsEqualTo(1);
            gate.Release();
            await invalidating.WaitAsync(Limit);
            reply.TrySetResult(RespValue.BulkString("value"u8.ToArray()));
            using var result = await reading.WaitAsync(Limit);
            reading = null;
            await Assert.That(result.AsString()).IsEqualTo("value");
            await Assert.That(ReadCounter(cache, "_sharedReadAdmissions")).IsEqualTo(0);
            await Assert.That(ReadCounter(cache, "_sharedReadInvalidations")).IsEqualTo(0);
            await Assert.That(ReadCounter(cache, "_joinableSharedReadCount")).IsEqualTo(0);
            await Assert.That(cache.ActiveSharedReadCount).IsEqualTo(0);
        }
        finally
        {
            gate.Release();
            reply.TrySetCanceled();
            await invalidating.WaitAsync(Limit);
            cache.StopSharedReads();
            if (reading is not null)
            {
                try { (await reading.WaitAsync(Limit)).Dispose(); }
                catch (OperationCanceledException) { }
            }
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RetiredReadDoesNotMakeLaterEmptyInvalidationTakeTheGate(bool flush)
    {
        var cache = new ClientSideCacheCoordinator(new() { CoalesceConcurrentMisses = true });
        RespireKey key = "key";
        var reply = new TaskCompletionSource<RespValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reading = cache.CoalesceReadAsync(new ClientCacheCommandKey("GET", key.AsValue()), reply,
            static (pending, cancellation) => new ValueTask<RespValue>(pending.Task.WaitAsync(cancellation)), default).AsTask();
        Task invalidating = Task.CompletedTask;
        try
        {
            cache.Invalidate(in key);
            await Assert.That(cache.ActiveSharedReadCount).IsEqualTo(1);
            await Assert.That(ReadCounter(cache, "_joinableSharedReadCount")).IsEqualTo(0);
            await using (var gate = await HeldGate.AcquireAsync(cache))
            {
                try
                {
                    invalidating = Task.Run(() =>
                    {
                        if (flush) cache.FlushForContinuityLoss();
                        else cache.Invalidate(in key);
                    });
                    await invalidating.WaitAsync(TimeSpan.FromSeconds(5));
                    await Assert.That(reading.IsCompleted).IsFalse();
                }
                finally
                {
                    gate.Release();
                    await invalidating.WaitAsync(Limit);
                }
            }
            reply.SetResult(RespValue.BulkString("value"u8.ToArray()));
            using var result = await reading.WaitAsync(Limit);
            await Assert.That(result.AsString()).IsEqualTo("value");
            await Assert.That(cache.ActiveSharedReadCount).IsEqualTo(0);
        }
        finally
        {
            reply.TrySetCanceled();
            cache.StopSharedReads();
            try { (await reading.WaitAsync(Limit)).Dispose(); }
            catch (OperationCanceledException) { }
        }
    }

    [Test]
    public async Task OverlappingInvalidationsKeepNewReadsUnjoinableUntilTheLastEnd()
    {
        var cache = new ClientSideCacheCoordinator(new() { CoalesceConcurrentMisses = true });
        var identity = new ClientCacheCommandKey("GET", (RespireValue)"key");
        var reply = new TaskCompletionSource<RespValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = new List<Task<RespValue>>();
        var calls = 0;
        var barriers = 0;
        try
        {
            reads.Add(Read());
            reads.Add(Read());
            await Assert.That(calls).IsEqualTo(1);
            Begin();
            Begin();
            reads.Add(Read());
            reads.Add(Read());
            await Assert.That(calls).IsEqualTo(3);
            End();
            reads.Add(Read());
            await Assert.That(calls).IsEqualTo(4);
            End();
            reads.Add(Read());
            reads.Add(Read());
            await Assert.That(calls).IsEqualTo(5);
            await Assert.That(ReadCounter(cache, "_joinableSharedReadCount")).IsEqualTo(1);
            reply.SetResult(RespValue.BulkString("value"u8.ToArray()));
            foreach (var read in reads)
            {
                using var result = await read.WaitAsync(Limit);
                await Assert.That(result.AsString()).IsEqualTo("value");
            }
            reads.Clear();
            await Assert.That(cache.ActiveSharedReadCount).IsEqualTo(0);
            await Assert.That(ReadCounter(cache, "_joinableSharedReadCount")).IsEqualTo(0);
            await Assert.That(ReadCounter(cache, "_sharedReadAdmissions")).IsEqualTo(0);
        }
        finally
        {
            while (barriers != 0) End();
            reply.TrySetCanceled();
            cache.StopSharedReads();
            foreach (var read in reads)
            {
                try { (await read.WaitAsync(Limit)).Dispose(); }
                catch (OperationCanceledException) { }
            }
        }

        Task<RespValue> Read() => cache.CoalesceReadAsync(identity, 0, (_, cancellation) =>
        {
            calls++;
            return new ValueTask<RespValue>(reply.Task.WaitAsync(cancellation));
        }, default).AsTask();

        void Begin() { InvokeBarrier(cache, "BeginSharedReadInvalidation"); barriers++; }
        void End() { InvokeBarrier(cache, "EndSharedReadInvalidation"); barriers--; }
    }

    private sealed class HeldGate : IAsyncDisposable
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _holder;

        private HeldGate(ClientSideCacheCoordinator cache, TaskCompletionSource held)
        {
            var gate = cache.InspectForTests().SharedReadGate;
            _holder = Task.Factory.StartNew(() =>
            {
                lock (gate)
                {
                    held.TrySetResult();
                    _release.Task.GetAwaiter().GetResult();
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        internal static async Task<HeldGate> AcquireAsync(ClientSideCacheCoordinator cache)
        {
            var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var gate = new HeldGate(cache, held);
            try
            {
                await held.Task.WaitAsync(Limit);
                return gate;
            }
            catch
            {
                await gate.DisposeAsync();
                throw;
            }
        }

        internal void Release() => _release.TrySetResult();

        public async ValueTask DisposeAsync()
        {
            Release();
            await _holder.WaitAsync(Limit);
        }
    }

    private static int ReadCounter(ClientSideCacheCoordinator cache, string name)
        => (int)typeof(ClientSideCacheCoordinator).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(cache)!;

    private static void InvokeBarrier(ClientSideCacheCoordinator cache, string name)
        => typeof(ClientSideCacheCoordinator).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(cache, null);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var started = Stopwatch.GetTimestamp();
        while (!condition())
        {
            if (Stopwatch.GetElapsedTime(started) > Limit) throw new TimeoutException("Cache barrier was not reached.");
            await Task.Yield();
        }
    }
}
