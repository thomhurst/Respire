using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
using System.Runtime.CompilerServices;
using System.Diagnostics.Metrics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class ClientCacheMutationLifetimeTests
{
    [Test]
    public async Task BlockingGroupWaitDoesNotRetainIdleMutationWriterCapacity()
    {
        var cache = new ClientSideCacheCoordinator(new());
        var group = new CatalogCommand(RespireCommands.Stream.XREADGROUP,
            ["GROUP", "group", "consumer", "BLOCK", 0, "STREAMS", "events", ">"]);
        var waiting = cache.BeforeCommand("XREADGROUP", in group, blocking: true);
        try
        {
            var arguments = Enumerable.Range(0, 5000).Select(index => (RespireValue)("key:" + index)).ToArray();
            var command = new CatalogCommand(RespireCommands.Key.DEL, arguments);
            var mutation = cache.BeforeCommand("DEL", in command);
            cache.CompleteMutation(in mutation);
            await Assert.That(cache.InspectForTests().MutationWriterStorage.Keys).IsEqualTo(0);
            await Assert.That(cache.InspectForTests().MutationWriterStorage.Capacity).IsLessThanOrEqualTo(4096);
        }
        finally { cache.CompleteMutation(in waiting); }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task ParsedFinalReplyReleasesFenceBeforeInlineCallerReads(bool unknown, bool multiple)
    {
        var cache = new ClientSideCacheCoordinator(new());
        var key = new RespireKey("key");
        var command = new CatalogCommand(RespireCommands.String.SET, ["key", "new"]);
        var fence = unknown ? cache.BeginUnknownMutation() : cache.BeforeCommand("SET", in command);
        PendingResponse source;
        ValueTask<RespValue> response;
        if (multiple)
        {
            var transaction = MultiReplyPendingResponseSource.Rent(2, 0, "MULTI/EXEC");
            source = transaction;
            response = transaction.Task;
        }
        else
        {
            var single = new PendingResponsePool(1).Rent();
            source = single;
            response = single.Task;
        }
        source.BindMutationFence(in fence);
        var observed = -1;
        var awaiter = response.GetAwaiter();
        awaiter.UnsafeOnCompleted(() =>
        {
            using var result = awaiter.GetResult();
            cache.CompleteMutation(in fence, succeeded: true);
            using var fresh = RespValue.BulkString("new"u8.ToArray());
            var read = cache.BeginRead(in key);
            cache.CompleteRead(in read, in fresh, allowInsert: true);
            observed = cache.Count;
        });
        var scheduler = new CompletionScheduler();
        var reply = RespValue.Integer(1);
        if (multiple)
        {
            scheduler.Add(source, in reply);
            await Assert.That(cache.InspectForTests().ActiveMutationCount).IsEqualTo(1);
        }
        scheduler.Add(source, in reply);
        if (scheduler.FlushDeferred()) scheduler.Execute();
        await Assert.That(observed).IsEqualTo(1);
        await Assert.That(cache.InspectForTests().ActiveMutationCount).IsEqualTo(0);
    }

    [Test]
    public async Task MutationLeaseSupportsNativeOwnersBeyondTheFormerSeventeenBitLimit()
    {
        var cache = new ClientSideCacheCoordinator(new());
        var fence = cache.BeginUnknownMutation();
        var references = new ClientSideCacheCoordinator.MutationReference[131_071];
        var retained = 0;
        try
        {
            for (; retained < references.Length; retained++)
                references[retained] = fence.BindNative();
            cache.CompleteMutation(in fence, succeeded: true);
            await Assert.That(cache.InspectForTests().ActiveMutationCount).IsEqualTo(1);
        }
        finally
        {
            cache.CompleteMutation(in fence);
            for (var index = 0; index < retained; index++) references[index].Release();
        }
        await Assert.That(cache.InspectForTests().ActiveMutationCount).IsEqualTo(0);
    }

    [Test, NotInParallel]
    public async Task AdmissionMeterFailurePreservesOriginalErrorAndReleasesUnsubmittedFence()
    {
        using var metrics = new MetricConfigurationScope();
        var invalidations = RespireTelemetry.ClientCacheInvalidations;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, publishedListener) =>
        {
            if (ReferenceEquals(instrument, invalidations))
                publishedListener.EnableMeasurementEvents(instrument);
        };
        var original = new InvalidOperationException("admission observer failed");
        var observed = 0;
        listener.SetMeasurementEventCallback<long>((_, _, _, _) =>
        {
            if (++observed == 1) throw original;
            throw new InvalidOperationException("cleanup must not replace the admission error");
        });
        listener.Start();
        var cache = new ClientSideCacheCoordinator(new());
        var command = new CatalogCommand(RespireCommands.String.SET, ["key", "new"]);
        var error = await Assert.That(() => cache.BeforeCommand("SET", in command)).Throws<InvalidOperationException>();
        await Assert.That(error).IsSameReferenceAs(original);
        await Assert.That(observed).IsEqualTo(1);
        await Assert.That(cache.InspectForTests().ActiveMutationCount).IsEqualTo(0);
    }

    [Test]
    public async Task LargeMutationRetiresOwnedKeysAndBoundsIdleWriterStorage()
    {
        var cache = new ClientSideCacheCoordinator(new());
        var arguments = Enumerable.Range(0, 5000).Select(index => (RespireValue)("key:" + index)).ToArray();
        var command = new CatalogCommand(RespireCommands.Key.DEL, arguments);
        var fence = cache.BeforeCommand("DEL", in command);
        var active = cache.InspectForTests().MutationWriterStorage;
        await Assert.That(active.Keys).IsEqualTo(arguments.Length);
        await Assert.That(active.Capacity).IsGreaterThan(4096);
        cache.CompleteMutation(in fence);
        var idle = cache.InspectForTests().MutationWriterStorage;
        await Assert.That(idle.Keys).IsEqualTo(0);
        await Assert.That(idle.Capacity).IsLessThanOrEqualTo(4096);
        var next = cache.BeforeCommand("DEL", in command);
        cache.CompleteMutation(in next);
        await Assert.That(cache.InspectForTests().ActiveMutationCount).IsEqualTo(0);
        await Assert.That(cache.InspectForTests().MutationWriterStorage.Keys).IsEqualTo(0);
    }

    [Test, NotInParallel]
    public async Task CompletionMeterFailureSurfacesOnCallerWithoutBreakingNativeRetirement()
    {
        using var metrics = new MetricConfigurationScope();
        var invalidations = RespireTelemetry.ClientCacheInvalidations;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, publishedListener) =>
        {
            if (ReferenceEquals(instrument, invalidations))
                publishedListener.EnableMeasurementEvents(instrument);
        };
        var observed = 0;
        listener.SetMeasurementEventCallback<long>((_, _, _, _) =>
        {
            if (++observed == 2) throw new InvalidOperationException("completion observer failed");
        });
        listener.Start();
        var cache = new ClientSideCacheCoordinator(new());
        var command = new CatalogCommand(RespireCommands.String.SET, ["key", "new"]);
        var fence = cache.BeforeCommand("SET", in command);
        var source = new PendingResponsePool(1).Rent();
        source.BindMutationFence(in fence);
        var reply = RespValue.Integer(1);
        source.TrySetResult(in reply);
        using var received = await source.Task;
        await Assert.That(() => cache.CompleteMutation(in fence, succeeded: true))
            .Throws<InvalidOperationException>();
        source.ReleaseRef();
        await Assert.That(observed).IsEqualTo(2);
        await Assert.That(cache.InspectForTests().ActiveMutationCount).IsEqualTo(0);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task SharedReadAdmissionDuringMutationRespectsAllQueryDependencies(bool affected, bool unknown)
    {
        var cache = new ClientSideCacheCoordinator(new() { CoalesceConcurrentMisses = true });
        var command = new CatalogCommand(RespireCommands.String.SET, [affected ? "second" : "other", "new"]);
        var fence = unknown ? cache.BeginUnknownMutation() : cache.BeforeCommand("SET", in command);
        var identity = new ClientCacheCommandKey("LCS", new RespireValue[] { "first", "second", "LEN" });
        var release = new TaskCompletionSource<RespValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        ValueTask<RespValue> Read(int _, CancellationToken cancellation)
        {
            Interlocked.Increment(ref calls);
            return new(release.Task.WaitAsync(cancellation));
        }
        var first = cache.CoalesceReadAsync(identity, 0, Read, CancellationToken.None);
        var second = cache.CoalesceReadAsync(identity, 0, Read, CancellationToken.None);
        try { await Assert.That(calls).IsEqualTo(affected || unknown ? 2 : 1); }
        finally
        {
            cache.CompleteMutation(in fence);
            release.TrySetResult(RespValue.Integer(1));
            using var firstResult = await first;
            using var secondResult = await second;
        }
        await Assert.That(cache.ActiveSharedReadCount).IsEqualTo(0);
    }

    [Test]
    public async Task DiscardedReplyRetainsNativeFenceAfterWriteCompletion()
    {
        var cache = new ClientSideCacheCoordinator(new());
        var fence = cache.BeginUnknownMutation();
        var source = MutationDiscardPendingResponse.Rent("SCRIPT FLUSH");
        source.BindMutationFence(in fence);
        source.ReleaseRef();
        cache.CompleteMutation(in fence, succeeded: true);
        await Assert.That(cache.InspectForTests().ActiveMutationCount).IsEqualTo(1);
        var reply = RespValue.Integer(1);
        source.TrySetResult(in reply);
        source.ReleaseRef();
        await Assert.That(cache.InspectForTests().ActiveMutationCount).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task WriteWindowReadCannotPublishAfterNativeRetirement(bool query)
    {
        var cache = new ClientSideCacheCoordinator(new());
        var key = new RespireKey("key");
        var command = new CatalogCommand(RespireCommands.String.SET, ["key", "new"]);
        var fence = cache.BeforeCommand("SET", in command);
        var request = new ClientSideCacheCoordinator.QueryRequest(new ClientCacheCommandKey("STRLEN", "key"), key);
        var get = query ? default : cache.BeginRead(in key);
        var projection = query ? cache.BeginRead("STRLEN", in request) : default;
        cache.CompleteMutation(in fence);
        using var response = query ? RespValue.Integer(3) : RespValue.BulkString("old"u8.ToArray());
        if (query) cache.CompleteRead(in projection, in response, allowInsert: true);
        else cache.CompleteRead(in get, in response, allowInsert: true);
        await Assert.That(cache.Count).IsEqualTo(0);

        if (query)
        {
            var after = cache.BeginRead("STRLEN", in request);
            cache.CompleteRead(in after, in response, allowInsert: true);
        }
        else
        {
            var after = cache.BeginRead(in key);
            cache.CompleteRead(in after, in response, allowInsert: true);
        }
        await Assert.That(cache.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task OverlappingMutationCompletionCannotReleaseAnotherWriter(bool sameKey, bool unknown)
    {
        var cache = new ClientSideCacheCoordinator(new());
        var key = new RespireKey("key");
        var firstCommand = new CatalogCommand(RespireCommands.String.SET, ["key", "first"]);
        var secondCommand = new CatalogCommand(RespireCommands.String.SET, [sameKey ? "key" : "other", "second"]);
        var first = cache.BeforeCommand("SET", in firstCommand);
        var second = unknown ? cache.BeginUnknownMutation() : cache.BeforeCommand("SET", in secondCommand);
        cache.CompleteMutation(in first);
        try
        {
            cache.Clear();
            cache.FlushForContinuityLoss();
            using var response = RespValue.BulkString("stale"u8.ToArray());
            var during = cache.BeginRead(in key);
            cache.CompleteRead(in during, in response, allowInsert: true);
            await Assert.That(cache.Count).IsEqualTo(!sameKey && !unknown ? 1 : 0);
            await Assert.That(cache.InspectForTests().ActiveMutationCount).IsEqualTo(1);
        }
        finally { cache.CompleteMutation(in second); }
        await Assert.That(cache.InspectForTests().ActiveMutationCount).IsEqualTo(0);
    }

    [Test]
    public async Task RedirectRebasesSuppressedQueryAgainstCurrentWriterLifetime()
    {
        var cache = new ClientSideCacheCoordinator(new());
        var command = new CatalogCommand(RespireCommands.String.SET, ["key", "new"]);
        var fence = cache.BeforeCommand("SET", in command);
        var request = new ClientSideCacheCoordinator.QueryRequest(new ClientCacheCommandKey("STRLEN", "key"), "key");
        var original = cache.BeginRead("STRLEN", in request);
        var stillDuring = cache.RebaseRead(in original);
        await Assert.That(stillDuring.CanCache).IsFalse();
        cache.CompleteMutation(in fence);
        var redirected = cache.RebaseRead(in stillDuring);
        using var fresh = RespValue.Integer(3);
        cache.CompleteRead(in redirected, in fresh, allowInsert: true);
        await Assert.That(cache.TryGet(in request, out var cached)).IsTrue();
        cached.Dispose();
        await Assert.That(cache.InspectForTests().PendingQueryDependencyCount).IsEqualTo(0);
    }

    [Test, NotInParallel]
    public async Task WarmedKnownMutationAndNativeRetirementAllocateNothing()
    {
        var cache = new ClientSideCacheCoordinator(new());
        var command = new CatalogCommand(RespireCommands.String.SET, ["key", "new"]);
        var pool = new PendingResponsePool(1);
        _ = MeasureMutations(cache, command, pool, allocate: false);
        _ = MeasureMutations(cache, command, pool, allocate: true);
        var measured = AllocationMeasurement.WithoutConcurrentGc(() =>
            (Actual: MeasureMutations(cache, command, pool, allocate: false),
                Control: MeasureMutations(cache, command, pool, allocate: true)));
        await Assert.That(measured.Actual).IsEqualTo(0);
        await Assert.That(measured.Control).IsGreaterThanOrEqualTo(37_000);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureMutations(ClientSideCacheCoordinator cache, CatalogCommand command,
        PendingResponsePool pool, bool allocate)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1000; index++)
        {
            var fence = cache.BeforeCommand("SET", in command);
            var source = pool.Rent();
            source.BindMutationFence(in fence);
            var reply = RespValue.Integer(1);
            source.TrySetResult(in reply);
            var pending = source.Task;
            if (!pending.IsCompletedSuccessfully) throw new InvalidOperationException("The measured response must complete inline.");
            using var received = pending.Result;
            cache.CompleteMutation(in fence, succeeded: true);
            source.ReleaseRef();
            if (allocate) GC.KeepAlive(new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Test]
    [Arguments(false, 0)]
    [Arguments(false, 1)]
    [Arguments(false, 2)]
    [Arguments(true, 0)]
    [Arguments(true, 1)]
    [Arguments(true, 2)]
    public async Task ReadsBeginningDuringMutationCannotPublishEvenAcrossFlush(bool query, int flush)
    {
        var cache = new ClientSideCacheCoordinator(new());
        var key = new RespireKey("key");
        var command = new CatalogCommand(RespireCommands.String.SET, ["key", "new"]);
        var fence = cache.BeforeCommand("SET", in command);
        try
        {
            if (flush == 1) cache.Clear();
            if (flush == 2) cache.FlushForContinuityLoss();
            using var response = query ? RespValue.Integer(3) : RespValue.BulkString("old"u8.ToArray());
            if (query)
            {
                var request = new ClientSideCacheCoordinator.QueryRequest(new ClientCacheCommandKey("STRLEN", "key"), key);
                var during = cache.BeginRead("STRLEN", in request);
                cache.CompleteRead(in during, in response, allowInsert: true);
                await Assert.That(cache.TryGet(in request, out _)).IsFalse();
            }
            else
            {
                var during = cache.BeginRead(in key);
                cache.CompleteRead(in during, in response, allowInsert: true);
                await Assert.That(cache.TryGet(in key, out _)).IsFalse();
            }
        }
        finally { cache.CompleteMutation(in fence); }
        using var fresh = RespValue.BulkString("new"u8.ToArray());
        var after = cache.BeginRead(in key);
        cache.CompleteRead(in after, in fresh, allowInsert: true);
        await Assert.That(cache.TryGet(in key, out _)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CopiedCompletedFenceCannotInvalidateLaterReads(bool unknown)
    {
        var cache = new ClientSideCacheCoordinator(new());
        var key = new RespireKey("key");
        var command = new CatalogCommand(RespireCommands.String.SET, ["key", "new"]);
        var fence = unknown ? cache.BeginUnknownMutation() : cache.BeforeCommand("SET", in command);
        var copy = fence;
        cache.CompleteMutation(in fence);
        using var fresh = RespValue.BulkString("new"u8.ToArray());
        var after = cache.BeginRead(in key);
        cache.CompleteRead(in after, in fresh, allowInsert: true);
        cache.CompleteMutation(in copy);
        await Assert.That(cache.TryGet(in key, out _)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CallerCompletionRetainsFenceUntilNativeResponseRetires(bool success)
    {
        var cache = new ClientSideCacheCoordinator(new());
        var key = new RespireKey("key");
        var command = new CatalogCommand(RespireCommands.String.SET, ["key", "new"]);
        var fence = cache.BeforeCommand("SET", in command);
        var source = new PendingResponsePool(1).Rent();
        source.BindMutationFence(in fence);
        if (success)
        {
            using var reply = RespValue.Integer(1);
            source.TrySetResult(in reply);
            using var received = await source.Task;
        }
        else
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            source.TrySetCanceled(cancelled.Token);
            await Assert.That(async () => await source.Task).Throws<OperationCanceledException>();
        }
        cache.CompleteMutation(in fence, success);
        try
        {
            await Assert.That(cache.InspectForTests().ActiveMutationCount).IsEqualTo(1);
            using var stale = RespValue.BulkString("old"u8.ToArray());
            var during = cache.BeginRead(in key);
            cache.CompleteRead(in during, in stale, allowInsert: true);
            await Assert.That(cache.TryGet(in key, out _)).IsFalse();
        }
        finally { source.ReleaseRef(); }
        await Assert.That(cache.InspectForTests().ActiveMutationCount).IsEqualTo(0);
        using var fresh = RespValue.BulkString("new"u8.ToArray());
        var after = cache.BeginRead(in key);
        cache.CompleteRead(in after, in fresh, allowInsert: true);
        await Assert.That(cache.TryGet(in key, out _)).IsTrue();
    }

    [Test]
    public async Task AncientCopiedFenceCannotReleaseSubsequentRentals()
    {
        var cache = new ClientSideCacheCoordinator(new());
        var command = new CatalogCommand(RespireCommands.String.SET, ["key", "new"]);
        var ancient = cache.BeforeCommand("SET", in command);
        cache.CompleteMutation(in ancient);
        for (var index = 0; index < 1000; index++)
        {
            var current = cache.BeforeCommand("SET", in command);
            try
            {
                cache.CompleteMutation(in ancient);
                await Assert.That(cache.InspectForTests().ActiveMutationCount).IsEqualTo(1);
                await Assert.That(ancient.RetainNative()).IsFalse();
            }
            finally { cache.CompleteMutation(in current); }
        }
        await Assert.That(cache.InspectForTests().ActiveMutationCount).IsEqualTo(0);
    }
}
