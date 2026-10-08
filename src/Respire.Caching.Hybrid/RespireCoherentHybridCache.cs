using System.Buffers;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
#if !NET9_0_OR_GREATER
using Lock = System.Object;
#endif

namespace Respire.Caching.Hybrid;

internal sealed class RespireCoherentHybridCache : HybridCache, IDisposable, IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Observation> _observations = new(StringComparer.Ordinal);
    private readonly Queue<Observation> _retiredObservations = new();
    private readonly Dictionary<string, DateTimeOffset> _removedTags = new(StringComparer.Ordinal);
    private readonly IServiceProvider _services;
    private readonly Func<IServiceProvider, HybridCache> _factory;
    private readonly IMemoryCache _memory;
    private readonly RespireDistributedCache _distributed;
    private readonly HybridCacheOptions _hybridOptions;
    private readonly IOptionsMonitor<HybridCacheOptions> _fixedOptions;
    private readonly HybridCache _unobserved;
    private readonly int _maxObservedKeys;
    private readonly int _maxRememberedTags;
    private readonly TagReplayClock _clock;
    private readonly Timer _sweep;
    private bool _trackingStopped;
    private bool _tagHistoryExhausted;
    private int _disposed;
    private readonly DrainTracker _retirementDrains = new();

    internal RespireCoherentHybridCache(IServiceProvider services,
        Func<IServiceProvider, HybridCache> factory, RespireHybridCacheCoherenceOptions options)
    {
        _services = services;
        _factory = factory;
        _memory = services.GetRequiredService<IMemoryCache>();
        _hybridOptions = services.GetRequiredService<IOptions<HybridCacheOptions>>().Value;
        _fixedOptions = new FixedHybridOptions(_hybridOptions);
        var distributed = _hybridOptions.DistributedCacheServiceKey is { } key
            ? services.GetRequiredKeyedService<IDistributedCache>(key)
            : services.GetRequiredService<IDistributedCache>();
        _distributed = distributed as RespireDistributedCache
            ?? throw new InvalidOperationException("HybridCache coherence requires RespireDistributedCache as its L2 backend.");
        _maxObservedKeys = options.MaxObservedKeys;
        _maxRememberedTags = options.MaxRememberedTagInvalidations;
        _clock = new TagReplayClock(services.GetService<TimeProvider>() ?? TimeProvider.System);
        _unobserved = CreateContext(null, _distributed);
        TrackingClient = _distributed.CreateCoherenceTrackingClient(options.TrackingOptions);
        _sweep = new Timer(static state => ((RespireCoherentHybridCache)state!).SweepObservations(),
            this, options.ObservationSweepInterval, options.ObservationSweepInterval);
    }

    internal RespireClient TrackingClient { get; }
    internal int ObservationCount { get { lock (_gate) return _observations.Count; } }

    private HybridCache CreateContext(Observation? observation, IDistributedCache distributed)
        => _factory(new LocalServiceProvider(_services, new LocalMemoryCache(this, observation),
            _clock, distributed, _fixedOptions));

    public override async ValueTask<T> GetOrCreateAsync<TState, T>(string key, TState state,
        Func<TState, CancellationToken, ValueTask<T>> underlyingDataCallback,
        HybridCacheEntryOptions? options = null, IEnumerable<string>? tags = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var observation = Acquire(key, options);
        try
        {
            var cache = await SelectCacheAsync(observation, cancellationToken).ConfigureAwait(false);
            return await cache.GetOrCreateAsync(key, state, underlyingDataCallback,
                ReferenceEquals(cache, _unobserved) ? WithoutLocalCache(options) : options,
                tags, cancellationToken).ConfigureAwait(false);
        }
        finally { Release(observation); }
    }

    public override async ValueTask SetAsync<T>(string key, T value,
        HybridCacheEntryOptions? options = null, IEnumerable<string>? tags = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RetireKey(key);
        var observation = Acquire(key, options);
        try
        {
            var cache = await SelectCacheAsync(observation, cancellationToken).ConfigureAwait(false);
            await cache.SetAsync(key, value,
                ReferenceEquals(cache, _unobserved) ? WithoutLocalCache(options) : options,
                tags, cancellationToken).ConfigureAwait(false);
        }
        finally { Release(observation); }
    }

    public override ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        RetireKey(key);
        return _unobserved.RemoveAsync(key, cancellationToken);
    }

    public override async ValueTask RemoveByTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        Task[]? updates = null;
        try
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (string.IsNullOrWhiteSpace(tag)) return;
                var timestamp = _clock.GetUtcNow();
                if (!_tagHistoryExhausted)
                {
                    if (!_removedTags.ContainsKey(tag) && _removedTags.Count >= _maxRememberedTags)
                    {
                        _tagHistoryExhausted = true;
                        _removedTags.Clear();
                        RetireAll();
                    }
                    else _removedTags[tag] = timestamp;
                }
                var localUpdates = _observations.Values.Select(observation =>
                    ReplayTagAsync(observation.Cache, tag, timestamp)).ToArray();
                // The fallback uses the original backend: this is the single shared L2 write.
                Task remoteUpdate;
                try
                {
                    using (_clock.Replay(timestamp))
                        remoteUpdate = _unobserved.RemoveByTagAsync(tag, cancellationToken).AsTask();
                }
                catch (Exception error) { remoteUpdate = Task.FromException(error); }
                updates = [remoteUpdate, .. localUpdates];
            }
        }
        finally
        {
            if (TryDrainRetiredObservations() is { } error)
            {
                // A custom memory-cache failure must not abandon an already-started marker write.
                if (updates is null) ThrowCleanupFailure(error);
                else updates = [.. updates, Task.FromException(error)];
            }
        }
        var completion = Task.WhenAll(updates);
        try { await completion.ConfigureAwait(false); }
        catch when (completion.Exception?.InnerExceptions.Count > 1)
        {
            // Observe every operation, preserving both shared-marker and local failures.
            throw completion.Exception;
        }
    }

    private async Task ReplayTagAsync(HybridCache cache, string tag, DateTimeOffset timestamp)
    {
        using (_clock.Replay(timestamp))
            await cache.RemoveByTagAsync(tag).ConfigureAwait(false);
    }

    private HybridCacheEntryOptions WithoutLocalCache(HybridCacheEntryOptions? options) => new()
    {
        Expiration = options?.Expiration,
        LocalCacheExpiration = options?.LocalCacheExpiration,
        Flags = EffectiveFlags(options) | HybridCacheEntryFlags.DisableLocalCache,
    };

    private HybridCacheEntryFlags EffectiveFlags(HybridCacheEntryOptions? options)
        => options?.Flags ?? _hybridOptions.DefaultEntryOptions?.Flags ?? HybridCacheEntryFlags.None;

    private Observation? Acquire(string key, HybridCacheEntryOptions? options)
    {
        Observation? acquired = null;
        try
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                // Let HybridCache itself retain its validation and uncached-factory behavior.
                if (_trackingStopped || _tagHistoryExhausted || string.IsNullOrWhiteSpace(key) || key.Length > _hybridOptions.MaximumKeyLength
                    || HasControlCharacter(key)
                    || (EffectiveFlags(options) & HybridCacheEntryFlags.DisableLocalCache) == HybridCacheEntryFlags.DisableLocalCache)
                    return null;
                if (_observations.TryGetValue(key, out var current))
                {
                    current.ActiveCalls++;
                    return acquired = current;
                }
                if (_observations.Count >= _maxObservedKeys) return null;

                var physicalKey = _distributed.ResolveCoherenceKey(key);
                if (!RespireDistributedCache.CanTrackCoherenceKey(TrackingClient, in physicalKey)) return null;

                var observation = new Observation(this, key);
                _observations.Add(key, observation);
                try
                {
                    observation.Subscription = TrackingClient.ClientSideCache!.SubscribeInvalidations(
                        physicalKey, _ => Retire(observation));
                    observation.Stopped = observation.Subscription.Stopped.UnsafeRegister(
                        static state => ((Observation)state!).Owner.TrackingStopped((Observation)state!), observation);
                    if (_distributed.CanObserveCoherenceSourceKey(in physicalKey)
                        && _distributed.CoherenceSourceCache is { } source)
                    {
                        observation.SourceSubscription = source.SubscribeInvalidations(physicalKey, invalidation =>
                        {
                            // Lua reads conservatively signal LocalMutation on the original
                            // connection. Actual writes are observed by the independent tracker.
                            if ((invalidation.Reasons & (RespireClientCacheInvalidationReason.ExplicitClear
                                | RespireClientCacheInvalidationReason.ContinuityLost)) != 0) Retire(observation);
                        });
                        observation.SourceStopped = observation.SourceSubscription.Stopped.UnsafeRegister(
                            static state => ((Observation)state!).Owner.TrackingStopped((Observation)state!), observation);
                    }

                    observation.Cache = CreateContext(observation, new TagReplayBackend(_distributed, _clock));
                    observation.TagReplay = Task.WhenAll(_removedTags.Select(pair =>
                        ReplayTagAsync(observation.Cache, pair.Key, pair.Value)));
                    // HEXISTS is a typed, cache-eligible hash read. It establishes actual OPTIN
                    // tracking before L2 is read, without duplicating the serialized payload.
                    observation.Tracking = TrackingClient.Hashes.ExistsAsync(physicalKey, "data").AsTask();
                    return acquired = observation;
                }
                catch (ObjectDisposedException)
                {
                    _trackingStopped = true;
                    RetireAll();
                    return null;
                }
                catch
                {
                    Retire(observation);
                    throw;
                }
            }
        }
        finally
        {
            if (TryDrainRetiredObservations() is { } cleanupError)
            {
                // The caller cannot release a call that Acquire never returned.
                // Roll it back even if the failing cleanup belongs to another key.
                try { Release(acquired); }
                catch (Exception rollbackError) { cleanupError = CombineFailures(cleanupError, rollbackError)!; }
                ThrowCleanupFailure(cleanupError);
            }
        }
    }

    private static bool HasControlCharacter(string key)
    {
        foreach (var character in key)
            if (character <= 31) return true;
        return false;
    }

    private async ValueTask<HybridCache> SelectCacheAsync(Observation? observation, CancellationToken cancellationToken)
    {
        if (observation is null) return _unobserved;
        try
        {
            await observation.Tracking.WaitAsync(cancellationToken).ConfigureAwait(false);
            await observation.TagReplay.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            // Tracking failure must never turn an ordinary L2 request into an unobserved L1 hit.
            Retire(observation);
            return _unobserved;
        }
        lock (_gate) return observation.Retired ? _unobserved : observation.Cache;
    }

    private void Release(Observation? observation)
    {
        if (observation is null) return;
        lock (_gate)
        {
            observation.ActiveCalls--;
            RetireIfIdle(observation);
        }
        DrainRetiredObservations();
    }

    private void RetireIfIdle(Observation observation)
    {
        if (!observation.Retired && observation.ActiveCalls == 0
            && !_memory.TryGetValue(observation.MemoryKey, out _)) Retire(observation);
    }

    internal void SweepObservations()
    {
        lock (_gate)
        {
            foreach (var observation in _observations.Values.ToArray()) RetireIfIdle(observation);
        }
        DrainRetiredObservations();
    }

    private void TrackingStopped(Observation observation)
    {
        lock (_gate)
        {
            if (observation.Retired) return;
            _trackingStopped = true;
            RetireAll();
        }
        DrainRetiredObservations();
    }

    private void RetireKey(string key)
    {
        lock (_gate)
        {
            if (key is not null && _observations.TryGetValue(key, out var observation)) Retire(observation);
        }
        DrainRetiredObservations();
    }

    private void RetireAll()
    {
        MarkAllRetired();
        DrainRetiredObservations();
    }

    private void MarkAllRetired()
    {
        lock (_gate)
        {
            foreach (var observation in _observations.Values.ToArray()) Retire(observation);
        }
    }

    private void Retire(Observation observation)
    {
        lock (_gate)
        {
            if (observation.Retired) return;
            observation.Retired = true;
            // Readers check Retired under this gate, so deferred physical removal
            // cannot expose this generation. Entry publication stays fenced here too.
            _observations.Remove(observation.Key);
            _retiredObservations.Enqueue(observation);
        }
        DrainRetiredObservations();
    }

    private void DrainRetiredObservations() => ThrowCleanupFailure(TryDrainRetiredObservations());

    // Return errors to the owner so admission, tag writes and disposal can apply their
    // own completion policy. A failed removal still cleans subscriptions and later entries.
    private Exception? TryDrainRetiredObservations()
    {
        // Subscribe/Stopped registration can invoke a callback synchronously during Acquire.
        // The outer gate owner drains after publishing all handles and releasing the gate.
#if NET9_0_OR_GREATER
        if (_gate.IsHeldByCurrentThread) return null;
#else
        if (Monitor.IsEntered(_gate)) return null;
#endif
        Exception? failure = null;
        while (true)
        {
            Observation observation;
            lock (_gate)
            {
                if (!_retiredObservations.TryDequeue(out observation!)) return failure;
                _retirementDrains.Claim();
            }
            // No external memory-cache or subscription cleanup runs under the bridge gate.
            Exception? cleanupFailure = null;
            try { _memory.Remove(observation.MemoryKey); }
            catch (Exception error) { cleanupFailure = error; }
            try
            {
                observation.Stopped.Unregister();
                observation.SourceStopped.Unregister();
                // Unregister never joins callbacks, and subscription Dispose explicitly does
                // not join observers. Retire may therefore run from either callback itself.
                observation.Subscription?.Dispose();
                observation.SourceSubscription?.Dispose();
            }
            catch (Exception error) { cleanupFailure = CombineFailures(cleanupFailure, error); }
            finally { CompleteRetirementDrain(cleanupFailure); }
            failure = CombineFailures(failure, cleanupFailure);
        }
    }

    private void CompleteRetirementDrain(Exception? failure)
    {
        (TaskCompletionSource<Exception?>? Completion, Exception? Failure) result;
        lock (_gate)
        {
            result = _retirementDrains.Complete(failure, Volatile.Read(ref _disposed) != 0,
                _retiredObservations.Count == 0);
        }
        result.Completion?.TrySetResult(result.Failure);
    }

    private ValueTask<Exception?> RetireAllAndWaitAsync()
    {
        // Disposal has closed admission. Marking under the gate also includes an Acquire
        // that entered before closure. Claiming and counting each drain is atomic there.
        MarkAllRetired();
        _ = TryDrainRetiredObservations();
        lock (_gate)
        {
            return _retirementDrains.Join(_retiredObservations.Count == 0);
        }
    }

    private bool TryBeginDisposal()
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0) return false;
            // A cleanup cannot join its own call stack. Reject before closing admission,
            // allowing the outer caller to dispose after that cleanup has unwound.
            if (_retirementDrains.IsDrainingCurrentThread)
                throw new InvalidOperationException("Cannot dispose the cache from its active retirement cleanup.");
            return Interlocked.Exchange(ref _disposed, 1) == 0;
        }
    }

    // Every member runs under the owner's gate. External cleanup and completion signalling
    // remain outside it. Per-thread depth also handles cleanup nested through another cache.
    private sealed class DrainTracker
    {
        private readonly Dictionary<int, int> _threads = new();
        private int _count;
        private TaskCompletionSource<Exception?>? _completion;
        private Exception? _failure;

        internal bool IsDrainingCurrentThread => _threads.ContainsKey(Environment.CurrentManagedThreadId);

        internal void Claim()
        {
            var thread = Environment.CurrentManagedThreadId;
            _threads.TryGetValue(thread, out var depth);
            _threads[thread] = depth + 1;
            _count++;
        }

        internal (TaskCompletionSource<Exception?>? Completion, Exception? Failure) Complete(
            Exception? failure, bool disposing, bool queueEmpty)
        {
            var thread = Environment.CurrentManagedThreadId;
            var depth = _threads[thread] - 1;
            if (depth == 0) _threads.Remove(thread);
            else _threads[thread] = depth;
            if (disposing) _failure = CombineFailures(_failure, failure);
            _count--;
            if (_count != 0 || !queueEmpty) return default;
            var completion = _completion;
            _completion = null;
            var error = _failure;
            if (completion is not null) _failure = null;
            return (completion, error);
        }

        internal ValueTask<Exception?> Join(bool queueEmpty)
        {
            if (_count == 0 && queueEmpty)
            {
                var failure = _failure;
                _failure = null;
                return new(failure);
            }
            return new((_completion ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task);
        }
    }

    private static Exception? CombineFailures(Exception? first, Exception? second)
    {
        if (first is null) return second;
        return second is null ? first : new AggregateException(first, second);
    }

    private static void ThrowCleanupFailure(Exception? error)
    {
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    public void Dispose()
    {
        if (!TryBeginDisposal()) return;
        Exception? failure = null;
        try
        {
            _sweep.Dispose();
            failure = RetireAllAndWaitAsync().GetAwaiter().GetResult();
        }
        catch (Exception error) { failure = error; }
        // RespireClient exposes only async disposal. Its owned shutdown awaits use
        // ConfigureAwait(false), so this wait does not require the caller's context.
        try { TrackingClient.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        catch (Exception error) { failure = CombineFailures(failure, error); }
        ThrowCleanupFailure(failure);
    }

    public async ValueTask DisposeAsync()
    {
        if (!TryBeginDisposal()) return;
        Exception? failure = null;
        try
        {
            await _sweep.DisposeAsync().ConfigureAwait(false);
            failure = await RetireAllAndWaitAsync().ConfigureAwait(false);
        }
        catch (Exception error) { failure = error; }
        try { await TrackingClient.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { failure = CombineFailures(failure, error); }
        ThrowCleanupFailure(failure);
    }

    private sealed class Observation(RespireCoherentHybridCache owner, string key)
    {
        internal RespireCoherentHybridCache Owner { get; } = owner;
        internal string Key { get; } = key;
        internal object MemoryKey { get; } = new();
        internal int ActiveCalls = 1;
        internal bool Retired;
        internal HybridCache Cache = null!;
        internal Task Tracking = Task.CompletedTask;
        internal Task TagReplay = Task.CompletedTask;
        internal IRespireClientCacheInvalidationSubscription? Subscription;
        internal IRespireClientCacheInvalidationSubscription? SourceSubscription;
        internal CancellationTokenRegistration Stopped;
        internal CancellationTokenRegistration SourceStopped;
    }

    private sealed class LocalServiceProvider(IServiceProvider services, IMemoryCache memory,
        TimeProvider clock, IDistributedCache distributed, IOptionsMonitor<HybridCacheOptions> options)
        : IServiceProvider, IKeyedServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(IMemoryCache)) return memory;
            if (serviceType == typeof(TimeProvider)) return clock;
            if (serviceType == typeof(IDistributedCache)) return distributed;
            if (serviceType == typeof(IOptionsMonitor<HybridCacheOptions>)) return options;
            return services.GetService(serviceType);
        }
        public object? GetKeyedService(Type serviceType, object? serviceKey)
            => serviceType == typeof(IDistributedCache) ? distributed : ((IKeyedServiceProvider)services).GetKeyedService(serviceType, serviceKey);
        public object GetRequiredKeyedService(Type serviceType, object? serviceKey)
            => serviceType == typeof(IDistributedCache) ? distributed : ((IKeyedServiceProvider)services).GetRequiredKeyedService(serviceType, serviceKey);
    }

    // Match ordinary singleton HybridCache: configure default options once, then retain
    // that snapshot for every context rather than activating named options per key.
    private sealed class FixedHybridOptions(HybridCacheOptions options) : IOptionsMonitor<HybridCacheOptions>
    {
        public HybridCacheOptions CurrentValue => options;
        public HybridCacheOptions Get(string? name) => options;
        public IDisposable? OnChange(Action<HybridCacheOptions, string?> listener) => null;
    }

    // Replay scopes wrap only RemoveByTagAsync, never GetOrCreateAsync or user factories.
    // Replay public tag removal at its original timestamp, changing only the context's
    // local metadata. No payload parsing, private fields, or duplicate tag-marker writes.
    private sealed class TagReplayClock(TimeProvider clock) : TimeProvider
    {
        private readonly AsyncLocal<DateTimeOffset?> _replay = new();
        internal bool IsReplaying => _replay.Value.HasValue;
        public override DateTimeOffset GetUtcNow() => _replay.Value ?? clock.GetUtcNow();
        public override long GetTimestamp() => clock.GetTimestamp();
        public override long TimestampFrequency => clock.TimestampFrequency;
        public override TimeZoneInfo LocalTimeZone => clock.LocalTimeZone;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => clock.CreateTimer(callback, state, dueTime, period);
        internal IDisposable Replay(DateTimeOffset timestamp)
        {
            var previous = _replay.Value;
            _replay.Value = timestamp;
            return new ReplayScope(() => _replay.Value = previous);
        }
        private sealed class ReplayScope(Action restore) : IDisposable { public void Dispose() => restore(); }
    }

    private sealed class TagReplayBackend(RespireDistributedCache distributed, TagReplayClock clock) : IBufferDistributedCache
    {
        public byte[]? Get(string key) => distributed.Get(key);
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => distributed.GetAsync(key, token);
        public bool TryGet(string key, IBufferWriter<byte> destination) => distributed.TryGet(key, destination);
        public ValueTask<bool> TryGetAsync(string key, IBufferWriter<byte> destination, CancellationToken token = default)
            => distributed.TryGetAsync(key, destination, token);
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        { if (!clock.IsReplaying) distributed.Set(key, value, options); }
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
            => clock.IsReplaying ? Task.CompletedTask : distributed.SetAsync(key, value, options, token);
        public void Set(string key, ReadOnlySequence<byte> value, DistributedCacheEntryOptions options)
        { if (!clock.IsReplaying) distributed.Set(key, value, options); }
        public ValueTask SetAsync(string key, ReadOnlySequence<byte> value, DistributedCacheEntryOptions options, CancellationToken token = default)
            => clock.IsReplaying ? ValueTask.CompletedTask : distributed.SetAsync(key, value, options, token);
        public void Refresh(string key) => distributed.Refresh(key);
        public Task RefreshAsync(string key, CancellationToken token = default) => distributed.RefreshAsync(key, token);
        public void Remove(string key) => distributed.Remove(key);
        public Task RemoveAsync(string key, CancellationToken token = default) => distributed.RemoveAsync(key, token);
    }

    // Only memory keys receive a generation namespace. The original string key reaches
    // HybridCache's stampede machinery and serialized L2 payload unchanged. A new context
    // cannot join an old stampede, including a producer whose last caller cancelled.
    private sealed class LocalMemoryCache(RespireCoherentHybridCache owner, Observation? observation) : IMemoryCache
    {
        private object MemoryKey(object key)
            => observation is not null && observation.Key.Equals(key) ? observation.MemoryKey : (observation, key);

        public bool TryGetValue(object key, out object? value)
        {
            lock (owner._gate)
            {
                value = null;
                return observation is { Retired: false }
                    && owner._memory.TryGetValue(MemoryKey(key), out value);
            }
        }

        public ICacheEntry CreateEntry(object key)
            => new LocalCacheEntry(owner, observation, owner._memory.CreateEntry(MemoryKey(key)));

        public void Remove(object key)
        {
            if (observation is not null) owner._memory.Remove(MemoryKey(key));
        }

        public void Dispose() { /* The service provider owns the shared memory cache. */ }
    }

    private sealed class LocalCacheEntry(RespireCoherentHybridCache owner, Observation? observation, ICacheEntry entry) : ICacheEntry
    {
        public object Key => entry.Key;
        public object? Value { get => entry.Value; set => entry.Value = value; }
        public DateTimeOffset? AbsoluteExpiration { get => entry.AbsoluteExpiration; set => entry.AbsoluteExpiration = value; }
        public TimeSpan? AbsoluteExpirationRelativeToNow { get => entry.AbsoluteExpirationRelativeToNow; set => entry.AbsoluteExpirationRelativeToNow = value; }
        public TimeSpan? SlidingExpiration { get => entry.SlidingExpiration; set => entry.SlidingExpiration = value; }
        public IList<Microsoft.Extensions.Primitives.IChangeToken> ExpirationTokens => entry.ExpirationTokens;
        public IList<PostEvictionCallbackRegistration> PostEvictionCallbacks => entry.PostEvictionCallbacks;
        public CacheItemPriority Priority { get => entry.Priority; set => entry.Priority = value; }
        public long? Size { get => entry.Size; set => entry.Size = value; }

        public void Dispose()
        {
            lock (owner._gate)
            {
                if (observation is null || observation.Retired)
                {
                    // Commit as already expired, so Microsoft's recycling callbacks still run.
                    entry.AbsoluteExpirationRelativeToNow = null;
                    entry.AbsoluteExpiration = DateTimeOffset.MinValue;
                }
                else
                {
                    entry.PostEvictionCallbacks.Add(new PostEvictionCallbackRegistration
                    {
                        State = observation,
                        EvictionCallback = static (_, _, _, state) =>
                        {
                            var observed = (Observation)state!;
                            lock (observed.Owner._gate) observed.Owner.RetireIfIdle(observed);
                            observed.Owner.DrainRetiredObservations();
                        },
                    });
                }
                entry.Dispose();
            }
            owner.DrainRetiredObservations();
        }
    }
}
