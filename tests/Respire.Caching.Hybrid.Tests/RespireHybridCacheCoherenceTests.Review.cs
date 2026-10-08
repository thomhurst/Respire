using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Caching.Hybrid.Tests;

public partial class RespireHybridCacheCoherenceTests
{
    [Test]
    public async Task ThrowingInvalidationCleanupDoesNotStopTrackingPump()
    {
        using var memory = new ProbingMemoryCache();
        await using var writer = BuildProvider(false);
        await using var reader = BuildProvider(true, memory: memory);
        var writeCache = writer.GetRequiredService<HybridCache>();
        var readCache = reader.GetRequiredService<HybridCache>();
        var coherent = Coherent(reader);
        var key = NewKey();
        await writeCache.SetAsync(key, "old", LongLived);
        await Assert.That(await ReadAsync(readCache, key)).IsEqualTo("old");
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        memory.OnRemove = () =>
        {
            cleanup.TrySetResult();
            throw new InvalidOperationException("invalidation memory cleanup failed");
        };
        try
        {
            await writeCache.SetAsync(key, "new", LongLived);
            await cleanup.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await UntilAsync(() => coherent.ObservationCount == 0);
            memory.OnRemove = null;
            await Assert.That(coherent.TrackingClient.IsConnected).IsTrue();
            await coherent.TrackingClient.PingAsync();
            await Assert.That(await ReadAsync(readCache, key)).IsEqualTo("new");
            await writeCache.SetAsync(key, "later", LongLived);
            await UntilAsync(() => coherent.ObservationCount == 0);
            await Assert.That(await ReadAsync(readCache, key)).IsEqualTo("later");
        }
        finally { memory.OnRemove = null; }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task ReentrantAsyncDisposalRejectsJoiningItsOwnDrain(bool nested)
        => AssertReentrantDisposalAsync(asynchronous: true, nested);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task ReentrantSyncDisposalRejectsJoiningItsOwnDrain(bool nested)
        => AssertReentrantDisposalAsync(asynchronous: false, nested);

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public Task ThreadHoppedDisposalRejectsJoiningItsOwnDrain(bool asynchronous, bool nested)
        => AssertReentrantDisposalAsync(asynchronous, nested, threadHop: true);

    private async Task AssertReentrantDisposalAsync(bool asynchronous, bool nested, bool threadHop = false)
    {
        using var memory = new ProbingMemoryCache();
        using var otherMemory = new ProbingMemoryCache();
        await using var provider = BuildProvider(true, memory: memory);
        await using var otherProvider = BuildProvider(true, memory: otherMemory);
        var coherent = Coherent(provider);
        var other = Coherent(otherProvider);
        var key = NewKey();
        var otherKey = NewKey();
        var options = new HybridCacheEntryOptions { Flags = HybridCacheEntryFlags.DisableDistributedCacheWrite };
        await coherent.GetOrCreateAsync(key, _ => ValueTask.FromResult("value"), options);
        await other.GetOrCreateAsync(otherKey, _ => ValueTask.FromResult("value"), options);
        Task? dispatchedDisposal = null;
        void DisposeFromCleanup()
        {
            if (threadHop)
            {
                dispatchedDisposal = Task.Run(async () =>
                {
                    if (asynchronous) await coherent.DisposeAsync();
                    else coherent.Dispose();
                });
                // A bounded wait lets the original drain unwind in the negative control.
                dispatchedDisposal.WaitAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            }
            else if (asynchronous)
            {
                // Bound the failing-before-fix async join so its enclosing drain can unwind.
                coherent.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            }
            else coherent.Dispose();
        }
        otherMemory.OnRemove = DisposeFromCleanup;
        memory.OnRemove = nested
            ? () => other.RemoveAsync(otherKey).AsTask().GetAwaiter().GetResult()
            : DisposeFromCleanup;
        try
        {
            Exception? failure = null;
            try { await Task.Run(async () => await coherent.RemoveAsync(key)).WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception error) { failure = error; }
            await Assert.That(failure is InvalidOperationException).IsTrue();
            await Assert.That(failure!.Message).IsEqualTo("Cannot dispose the cache from its active retirement cleanup.");
            // Rejection occurs before closing admission or stopping the tracker.
            await Assert.That(coherent.TrackingClient.IsConnected).IsTrue();
            await coherent.TrackingClient.PingAsync();
            memory.OnRemove = null;
            otherMemory.OnRemove = null;
            await coherent.RemoveAsync(NewKey());
            await coherent.DisposeAsync();
            await Assert.That(coherent.TrackingClient.IsConnected).IsFalse();
        }
        finally
        {
            memory.OnRemove = null;
            otherMemory.OnRemove = null;
            if (dispatchedDisposal is not null)
            {
                try { await dispatchedDisposal.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
            }
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task InheritedCleanupScopeDoesNotRejectDisposalAfterDrainCompletes(bool asynchronous)
    {
        using var memory = new ProbingMemoryCache();
        await using var provider = BuildProvider(true, memory: memory);
        var coherent = Coherent(provider);
        var key = NewKey();
        await coherent.GetOrCreateAsync(key, _ => ValueTask.FromResult("value"),
            new HybridCacheEntryOptions { Flags = HybridCacheEntryFlags.DisableDistributedCacheWrite });
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? disposal = null;
        memory.OnRemove = () => disposal = Task.Run(async () =>
        {
            await release.Task;
            if (asynchronous) await coherent.DisposeAsync();
            else coherent.Dispose();
        });
        try
        {
            await coherent.RemoveAsync(key);
            await Assert.That(disposal is not null).IsTrue();
            memory.OnRemove = null;
            release.TrySetResult();
            await disposal!.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(coherent.TrackingClient.IsConnected).IsFalse();
        }
        finally
        {
            memory.OnRemove = null;
            release.TrySetResult();
            if (disposal is not null) { try { await disposal.WaitAsync(TimeSpan.FromSeconds(10)); } catch { } }
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task DisposalJoinsRetirementAlreadyClaimedByAnotherDrainer(bool asynchronous, bool cleanupFails)
    {
        using var memory = new ProbingMemoryCache();
        await using var provider = BuildProvider(true, memory: memory);
        var cache = provider.GetRequiredService<HybridCache>();
        var key = NewKey();
        await cache.GetOrCreateAsync(key, _ => ValueTask.FromResult("value"),
            new HybridCacheEntryOptions { Flags = HybridCacheEntryFlags.DisableDistributedCacheWrite });
        var coherent = Coherent(provider);
        using var subscription = coherent.TrackingClient.ClientSideCache!.SubscribeInvalidations(
            InstanceName + key, _ => { });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new InvalidOperationException("concurrent retirement cleanup failed");
        memory.OnRemove = () =>
        {
            entered.TrySetResult();
            release.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            if (cleanupFails) throw expected;
        };
        var retirement = Task.Run(async () => await cache.RemoveAsync(key));
        Task? disposal = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(coherent.ObservationCount).IsEqualTo(0);
            disposal = Task.Run(async () =>
            {
                if (asynchronous) await coherent.DisposeAsync();
                else coherent.Dispose();
            });
            // The public admission guard proves disposal has entered, rather than relying
            // on a delay after scheduling its task. This key has no observation to retire.
            var probeKey = NewKey();
            await UntilAsync(async () =>
            {
                try { await coherent.RemoveAsync(probeKey); return false; }
                catch (ObjectDisposedException) { return true; }
            });
            await Assert.That(disposal.IsCompleted).IsFalse();
            await Assert.That(subscription.Stopped.IsCancellationRequested).IsFalse();
            release.TrySetResult();
            Exception? retirementFailure = null;
            Exception? disposalFailure = null;
            try { await retirement.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception error) { retirementFailure = error; }
            try { await disposal.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception error) { disposalFailure = error; }
            await Assert.That(ReferenceEquals(retirementFailure, cleanupFails ? expected : null)).IsTrue();
            await Assert.That(ReferenceEquals(disposalFailure, cleanupFails ? expected : null)).IsTrue();
            await Assert.That(subscription.Stopped.IsCancellationRequested).IsTrue();
            await Assert.That(coherent.TrackingClient.IsConnected).IsFalse();
        }
        finally
        {
            release.TrySetResult();
            memory.OnRemove = null;
            try { await retirement.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
            if (disposal is not null) { try { await disposal.WaitAsync(TimeSpan.FromSeconds(10)); } catch { } }
            await coherent.TrackingClient.DisposeAsync();
        }
    }

    [Test]
    [Arguments(false, 1)]
    [Arguments(true, 1)]
    [Arguments(false, 2)]
    [Arguments(true, 2)]
    public async Task DisposalCleanupFailureStillStopsTheOwnedTracker(bool asynchronous, int keyCount)
    {
        using var memory = new ProbingMemoryCache();
        await using var provider = BuildProvider(true, memory: memory);
        var cache = provider.GetRequiredService<HybridCache>();
        var keys = Enumerable.Range(0, keyCount).Select(_ => NewKey()).ToArray();
        foreach (var key in keys)
            await cache.GetOrCreateAsync(key, _ => ValueTask.FromResult("value"),
                new HybridCacheEntryOptions { Flags = HybridCacheEntryFlags.DisableDistributedCacheWrite });
        var coherent = Coherent(provider);
        await Assert.That(coherent.ObservationCount).IsEqualTo(keyCount);
        using var subscription = coherent.TrackingClient.ClientSideCache!.SubscribeInvalidations(
            InstanceName + keys[0], _ => { });
        var expected = new InvalidOperationException("disposal memory cleanup failed");
        var removals = 0;
        memory.OnRemove = () =>
        {
            Interlocked.Increment(ref removals);
            throw expected;
        };
        try
        {
            Exception? failure = null;
            try
            {
                if (asynchronous) await coherent.DisposeAsync();
                else coherent.Dispose();
            }
            catch (Exception error) { failure = error; }
            await Assert.That(subscription.Stopped.IsCancellationRequested).IsTrue();
            await Assert.That(coherent.TrackingClient.IsConnected).IsFalse();
            await Assert.That(async () => await coherent.TrackingClient.PingAsync()).Throws<ObjectDisposedException>();
            await Assert.That(coherent.ObservationCount).IsEqualTo(0);
            await Assert.That(removals).IsEqualTo(keyCount);
            if (keyCount == 1) await Assert.That(ReferenceEquals(failure, expected)).IsTrue();
            else
            {
                await Assert.That(failure is AggregateException).IsTrue();
                var errors = ((AggregateException)failure!).InnerExceptions;
                await Assert.That(errors.Count).IsEqualTo(keyCount);
                await Assert.That(errors.All(error => ReferenceEquals(error, expected))).IsTrue();
            }
            // A failed first disposal still completes shutdown and remains single-shot.
            coherent.Dispose();
            await coherent.DisposeAsync();
            await Assert.That(removals).IsEqualTo(keyCount);
        }
        finally
        {
            memory.OnRemove = null;
            // The failing-before-fix run must also release the leaked tracker.
            await coherent.TrackingClient.DisposeAsync();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AcquisitionCleanupFailureRollsBackTheUnreturnedCall(bool repeatedFailure)
    {
        using var memory = new ProbingMemoryCache();
        await using var provider = BuildProvider(false, memory: memory);
        var firstKey = NewKey();
        var fillEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fillRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupFailure = new InvalidOperationException("cleanup failed during acquisition");
        var contexts = 0;
        RespireCoherentHybridCache? coherent = null;
        coherent = new RespireCoherentHybridCache(provider, _ =>
        {
            // The new context is created under the observation gate. Retire another
            // generation reentrantly so its cleanup is deferred to Acquire's final drain.
            if (++contexts == 3) coherent!.RemoveAsync(firstKey).GetAwaiter().GetResult();
            return new ControlledTagCache(() => ValueTask.CompletedTask);
        }, new());
        await using var owned = coherent;
        var fill = coherent.GetOrCreateAsync(firstKey, async _ =>
        {
            fillEntered.TrySetResult();
            await fillRelease.Task;
            return "value";
        }).AsTask();
        try
        {
            await fillEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var removals = 0;
            memory.OnRemove = () =>
            {
                if (++removals == 1 || repeatedFailure) throw cleanupFailure;
            };
            Exception? failure = null;
            var callbackRan = false;
            try
            {
                await coherent.GetOrCreateAsync(NewKey(), _ =>
                {
                    callbackRan = true;
                    return ValueTask.FromResult("unreachable");
                });
            }
            catch (Exception caught) { failure = caught; }
            await Assert.That(coherent.ObservationCount).IsEqualTo(0);
            coherent.SweepObservations();
            await Assert.That(coherent.ObservationCount).IsEqualTo(0);
            await Assert.That(callbackRan).IsFalse();
            if (repeatedFailure)
            {
                await Assert.That(failure is AggregateException).IsTrue();
                await Assert.That(((AggregateException)failure!).InnerExceptions.Count).IsEqualTo(2);
                await Assert.That(((AggregateException)failure!).InnerExceptions.All(error => ReferenceEquals(error, cleanupFailure))).IsTrue();
            }
            else await Assert.That(ReferenceEquals(failure, cleanupFailure)).IsTrue();
        }
        finally
        {
            memory.OnRemove = null;
            fillRelease.TrySetResult();
            await fill.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task LocalTagFailureStillObservesTheSharedWrite(bool remoteFails, bool synchronousRemoteFailure)
    {
        await using var provider = BuildProvider(false);
        var localFailure = new InvalidOperationException("local replay failed");
        var remoteFailure = new InvalidOperationException("shared marker failed");
        var remoteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remoteRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fillEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fillRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var contexts = 0;
        await using var coherent = new RespireCoherentHybridCache(provider, _ =>
            new ControlledTagCache(++contexts == 1 ? RemoteUpdate : () => new(Task.FromException(localFailure))), new());
        var fill = coherent.GetOrCreateAsync(NewKey(), async _ =>
        {
            fillEntered.TrySetResult();
            await fillRelease.Task;
            return "value";
        }).AsTask();
        Task? removal = null;
        try
        {
            await fillEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            removal = coherent.RemoveByTagAsync("tag").AsTask();
            await remoteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (!synchronousRemoteFailure)
                await Assert.That(removal.IsCompleted).IsFalse();
            remoteRelease.TrySetResult();
            Exception? failure = null;
            try { await removal.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception caught) { failure = caught; }
            if (remoteFails)
            {
                await Assert.That(failure is AggregateException).IsTrue();
                var failures = ((AggregateException)failure!).InnerExceptions;
                await Assert.That(failures.Contains(localFailure)).IsTrue();
                await Assert.That(failures.Contains(remoteFailure)).IsTrue();
                await Assert.That(failures.Count).IsEqualTo(2);
            }
            else await Assert.That(ReferenceEquals(failure, localFailure)).IsTrue();
        }
        finally
        {
            remoteRelease.TrySetResult();
            fillRelease.TrySetResult();
            if (removal is not null) { try { await removal; } catch { } }
            await fill.WaitAsync(TimeSpan.FromSeconds(10));
        }

        ValueTask RemoteUpdate()
        {
            remoteEntered.TrySetResult();
            if (synchronousRemoteFailure) throw remoteFailure;
            return new(CompleteRemoteAsync());
        }
        async Task CompleteRemoteAsync()
        {
            await remoteRelease.Task;
            if (remoteFails) throw remoteFailure;
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RetirementFailureStillObservesTheSharedTagWrite(bool remoteFails)
    {
        using var memory = new ProbingMemoryCache();
        await using var provider = BuildProvider(false, memory: memory);
        var cleanupFailure = new InvalidOperationException("memory cleanup failed");
        var remoteFailure = new InvalidOperationException("shared marker failed");
        var remoteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remoteRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fillEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fillRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var contexts = 0;
        var remoteCalls = 0;
        await using var coherent = new RespireCoherentHybridCache(provider, _ =>
            new ControlledTagCache(++contexts == 1 ? RemoteUpdate : () => ValueTask.CompletedTask),
            new() { MaxRememberedTagInvalidations = 1 });
        await coherent.RemoveByTagAsync("first-tag");
        var fill = coherent.GetOrCreateAsync(NewKey(), async _ =>
        {
            fillEntered.TrySetResult();
            await fillRelease.Task;
            return "value";
        }).AsTask();
        Task? removal = null;
        try
        {
            await fillEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            memory.OnRemove = () => throw cleanupFailure;
            removal = coherent.RemoveByTagAsync("second-tag").AsTask();
            await remoteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(removal.IsCompleted).IsFalse();
            remoteRelease.TrySetResult();
            Exception? failure = null;
            try { await removal.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception caught) { failure = caught; }
            if (remoteFails)
            {
                await Assert.That(failure is AggregateException).IsTrue();
                var failures = ((AggregateException)failure!).InnerExceptions;
                await Assert.That(failures.Contains(cleanupFailure)).IsTrue();
                await Assert.That(failures.Contains(remoteFailure)).IsTrue();
                await Assert.That(failures.Count).IsEqualTo(2);
            }
            else await Assert.That(ReferenceEquals(failure, cleanupFailure)).IsTrue();
        }
        finally
        {
            memory.OnRemove = null;
            remoteRelease.TrySetResult();
            fillRelease.TrySetResult();
            if (removal is not null) { try { await removal; } catch { } }
            await fill.WaitAsync(TimeSpan.FromSeconds(10));
        }

        ValueTask RemoteUpdate() => ++remoteCalls == 1 ? ValueTask.CompletedTask : new(CompleteRemoteAsync());
        async Task CompleteRemoteAsync()
        {
            remoteEntered.TrySetResult();
            await remoteRelease.Task;
            if (remoteFails) throw remoteFailure;
        }
    }

    [Test]
    public async Task RetirementRemovesMemoryOutsideTheObservationGate()
    {
        using var memory = new ProbingMemoryCache();
        await using var provider = BuildProvider(true, memory: memory);
        var cache = provider.GetRequiredService<HybridCache>();
        var key = NewKey();
        await cache.GetOrCreateAsync(key, _ => ValueTask.FromResult("value"),
            new HybridCacheEntryOptions { Flags = HybridCacheEntryFlags.DisableDistributedCacheWrite });
        await Assert.That(Coherent(provider).ObservationCount).IsEqualTo(1);
        var probes = 0;
        memory.OnRemove = () =>
        {
            // A separate thread must acquire the bridge gate while Remove is still active.
            var count = Task.Run(() => Coherent(provider).ObservationCount)
                .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            if (count != 0) throw new InvalidOperationException("Retirement was not published before cleanup.");
            Interlocked.Increment(ref probes);
        };
        await cache.RemoveAsync(key);
        await Assert.That(probes).IsEqualTo(1);
    }

    [Test]
    public async Task SynchronousDisposalDoesNotCaptureTheCallersSynchronizationContext()
    {
        await using var provider = BuildProvider(true);
        var cache = provider.GetRequiredService<HybridCache>();
        await cache.GetOrCreateAsync(NewKey(), _ => ValueTask.FromResult("value"),
            new HybridCacheEntryOptions { Flags = HybridCacheEntryFlags.DisableDistributedCacheWrite });
        var context = new RecordingSynchronizationContext();
        await Task.Run(() =>
        {
            var previous = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(context);
                Coherent(provider).Dispose();
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }).WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(context.Posts).IsEqualTo(0);
        await Assert.That(Coherent(provider).ObservationCount).IsEqualTo(0);
    }

    [Test]
    public async Task AFactoryAfterTagReplayStillWritesItsDistributedPayload()
    {
        var key = NewKey();
        var codec = new CountingCodec();
        await using var provider = BuildProvider(true, valueCodec: codec);
        var cache = provider.GetRequiredService<HybridCache>();
        await cache.RemoveByTagAsync("tag-" + key);
        await Assert.That(codec.Encodes).IsEqualTo(1);
        await cache.GetOrCreateAsync(key, _ => ValueTask.FromResult("payload"), LongLived);
        await Assert.That(codec.Encodes).IsEqualTo(2);
        await using var reader = BuildProvider(false);
        await Assert.That(await ReadAsync(reader.GetRequiredService<HybridCache>(), key)).IsEqualTo("payload");
    }

    private sealed class RecordingSynchronizationContext : SynchronizationContext
    {
        private int _posts;
        internal int Posts => Volatile.Read(ref _posts);
        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref _posts);
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }
    }

    private sealed class ProbingMemoryCache : IMemoryCache
    {
        private readonly MemoryCache _inner = new(Options.Create(new MemoryCacheOptions()));
        internal Action? OnRemove;
        public bool TryGetValue(object key, out object? value) => _inner.TryGetValue(key, out value);
        public ICacheEntry CreateEntry(object key) => _inner.CreateEntry(key);
        public void Remove(object key) { OnRemove?.Invoke(); _inner.Remove(key); }
        public void Dispose() => _inner.Dispose();
    }

    private sealed class ControlledTagCache(Func<ValueTask> removeTag) : HybridCache
    {
        public override ValueTask<T> GetOrCreateAsync<TState, T>(string key, TState state,
            Func<TState, CancellationToken, ValueTask<T>> underlyingDataCallback,
            HybridCacheEntryOptions? options = null, IEnumerable<string>? tags = null,
            CancellationToken cancellationToken = default) => underlyingDataCallback(state, cancellationToken);
        public override ValueTask SetAsync<T>(string key, T value, HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public override ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public override ValueTask RemoveByTagAsync(string tag, CancellationToken cancellationToken = default) => removeTag();
    }
}
