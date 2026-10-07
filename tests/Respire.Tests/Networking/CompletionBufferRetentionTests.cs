using System.Reflection;
using System.Runtime.CompilerServices;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class CompletionBufferRetentionTests
{
    private static readonly FieldInfo Spares = typeof(CompletionScheduler).GetField("_spares", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Test, NotInParallel]
    public async Task WarmDeepDrainsReuseBuffersWithoutAllocations()
    {
        var scheduler = new CompletionScheduler();
        var sources = Enumerable.Range(0, 12).Select(_ => new PendingResponseSource()).ToArray();
        _ = MeasureDrains(scheduler, sources, false);
        _ = MeasureDrains(scheduler, sources, true);
        var measured = AllocationMeasurement.WithoutConcurrentGc(() => MeasureDrains(scheduler, sources, false));
        var control = AllocationMeasurement.WithoutConcurrentGc(() => MeasureDrains(scheduler, sources, true));
        await Assert.That(measured.Bytes).IsEqualTo(0L);
        await Assert.That(measured.Sum).IsEqualTo(32L * 66);
        await Assert.That(control.Bytes).IsGreaterThanOrEqualTo(32L * 37);
        await Assert.That(control.Sum).IsEqualTo(measured.Sum);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (long Bytes, long Sum) MeasureDrains(CompletionScheduler scheduler, PendingResponseSource[] sources, bool allocate)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        long sum = 0;
        for (var drain = 0; drain < 32; drain++)
        {
            for (var index = 0; index < sources.Length; index++)
            {
                sources[index].PrepareForUse();
                scheduler.Add(sources[index], RespValue.Integer(index));
                _ = scheduler.FlushDeferred(); // The test owns the runner; no pool dispatch.
            }
            scheduler.Execute();
            foreach (var source in sources)
            {
                using var reply = source.Task.GetAwaiter().GetResult();
                sum += reply.AsInteger();
            }
            if (allocate) GC.KeepAlive(new byte[37]);
        }
        return (GC.GetAllocatedBytesForCurrentThread() - before, sum);
    }

    [Test]
    [Arguments(1, 32)]
    [Arguments(64, 32)]
    [Arguments(255, 8)] // Uses a 256-entry buffer without queuing the automatic runner.
    public async Task DeepBurstsBoundRetentionAndClearEveryEntry(int repliesPerBatch, int batches)
    {
        var scheduler = new CompletionScheduler();
        for (var cycle = 0; cycle < 2; cycle++)
        {
            var sources = new PendingResponseSource[repliesPerBatch * batches];
            for (var index = 0; index < sources.Length; index++)
            {
                var source = sources[index] = new PendingResponseSource();
                source.PrepareForUse();
                scheduler.Add(source, RespValue.Integer(index));
                if ((index + 1) % repliesPerBatch == 0) _ = scheduler.FlushDeferred();
            }
            await Assert.That(scheduler.WaitingReplyCount).IsEqualTo(sources.Length);
            scheduler.Execute();
            for (var index = 0; index < sources.Length; index++)
            {
                using var reply = sources[index].Task.GetAwaiter().GetResult();
                await Assert.That(reply.AsInteger()).IsEqualTo((long)index);
            }
            await Assert.That(scheduler.WaitForIdleAsync().IsCompletedSuccessfully).IsTrue();
            await AssertClearedAndBoundedAsync(scheduler);
        }
    }

    private static async Task AssertClearedAndBoundedAsync(CompletionScheduler scheduler)
    {
        var buffers = ((Array)Spares.GetValue(scheduler)!).Cast<Array?>().Where(buffer => buffer is not null).ToArray();
        await Assert.That(buffers.Length).IsLessThanOrEqualTo(16);
        var slots = buffers.Sum(buffer => buffer!.Length);
        await Assert.That(slots).IsLessThanOrEqualTo(1024);
        var entryType = typeof(CompletionScheduler).GetNestedType("Entry", BindingFlags.NonPublic)!;
        var entrySize = (int)typeof(Unsafe).GetMethod(nameof(Unsafe.SizeOf))!.MakeGenericMethod(entryType).Invoke(null, null)!;
        Console.WriteLine($"Completion spares: {buffers.Length} arrays, {slots} entry slots, {slots * entrySize} entry bytes (array headers excluded).");
        foreach (var buffer in buffers)
        {
            var source = entryType.GetField("Source")!;
            var value = entryType.GetField("Value")!;
            await Assert.That(buffer!.Length).IsLessThanOrEqualTo(256);
            foreach (var entry in buffer)
            {
                await Assert.That(source.GetValue(entry)).IsNull();
                await Assert.That(value.GetValue(entry)!.Equals(default(RespValue))).IsTrue();
            }
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DeepDrainsReleaseCancelledAndFailedSources(bool cancel)
    {
        var scheduler = new CompletionScheduler();
        var sources = Enumerable.Range(0, 24).Select(_ => new PendingResponsePool(1).Rent()).ToArray();
        var replies = sources.Select(source => source.Task.AsTask()).ToArray();
        for (var index = 0; index < sources.Length; index++)
        {
            if (index % 2 == 0)
            {
                if (cancel) _ = sources[index].TrySetCanceled(new CancellationToken(true));
                else _ = sources[index].TrySetException(new InvalidOperationException("connection failure"));
            }
            scheduler.Add(sources[index], RespValue.Integer(index));
            _ = scheduler.FlushDeferred();
        }
        scheduler.Execute();
        for (var index = 0; index < sources.Length; index++)
        {
            if (index % 2 == 0)
            {
                if (cancel) await Assert.That(async () => await replies[index]).Throws<OperationCanceledException>();
                else await Assert.That(async () => await replies[index]).Throws<InvalidOperationException>();
            }
            else
            {
                using var value = await replies[index];
                await Assert.That(value.AsInteger()).IsEqualTo((long)index);
            }
            await Assert.That(sources[index].State).IsEqualTo(2);
        }
        await AssertClearedAndBoundedAsync(scheduler);
    }

    [Test, NotInParallel]
    public async Task RescuedTailRemainsOwnedUntilDeliveryAndGrowsWithinBatchLimit()
    {
        var scheduler = new CompletionScheduler();
        var sources = Enumerable.Range(0, 128).Select(_ => new PendingResponsePool(1).Rent()).ToArray();
        var first = sources[0].Task.ConfigureAwait(false).GetAwaiter();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        first.UnsafeOnCompleted(() =>
        {
            try
            {
                using var value = first.GetResult();
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Old runner was not released.");
            }
            catch (Exception error) { finished.TrySetException(error); }
        });
        for (var index = 0; index < sources.Length; index++) scheduler.Add(sources[index], RespValue.Integer(index));
        await Assert.That(scheduler.FlushDeferred()).IsTrue();
        var runner = new Thread(() =>
        {
            try { scheduler.Execute(); finished.TrySetResult(); }
            catch (Exception error) { finished.TrySetException(error); }
        }) { IsBackground = true };
        runner.Start();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(scheduler.RescueStalledRunner(0, 500)).IsFalse();
            await Assert.That(scheduler.RescueStalledRunner(500, 500)).IsTrue();
            for (var index = 1; index < sources.Length; index++)
            {
                using var value = await sources[index].Task;
                await Assert.That(value.AsInteger()).IsEqualTo((long)index);
            }
            await scheduler.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await AssertClearedAndBoundedAsync(scheduler);
            var seed = new PendingResponsePool(1).Rent();
            scheduler.Add(seed, RespValue.Integer(1));
            _ = scheduler.FlushDeferred(); // Pops the cleared 127-entry rescued tail.
            var growth = Enumerable.Range(0, 255).Select(_ => new PendingResponsePool(1).Rent()).ToArray();
            foreach (var source in growth) scheduler.Add(source, RespValue.Integer(2));
            _ = scheduler.FlushDeferred();
            scheduler.Execute();
            using var seedValue = await seed.Task;
            foreach (var source in growth) { using var value = await source.Task; }
            await AssertClearedAndBoundedAsync(scheduler);
        }
        finally
        {
            release.Set();
            await Assert.That(runner.Join(TimeSpan.FromSeconds(5))).IsTrue();
        }
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await AssertClearedAndBoundedAsync(scheduler);
    }
}
