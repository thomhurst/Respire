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
    [Test, NotInParallel]
    public async Task CompletionMeterFailureSurfacesOnCallerWithoutBreakingNativeRetirement()
    {
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, publishedListener) =>
        {
            if (ReferenceEquals(instrument, RespireTelemetry.ClientCacheInvalidations))
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
