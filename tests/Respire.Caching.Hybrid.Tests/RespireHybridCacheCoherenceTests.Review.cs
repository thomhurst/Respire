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
