using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;

namespace Respire;

/// <summary>How Redis registers cached keys for invalidation.</summary>
public enum RespireClientTrackingMode
{
    /// <summary>
    /// Redis remembers each key this client reads (CLIENT TRACKING OPTIN) and invalidates only those
    /// keys. Precise, at the cost of server memory per tracked key. This is the default.
    /// </summary>
    OptIn,

    /// <summary>
    /// Redis invalidates every key matching <see cref="RespireClientSideCacheOptions.KeyPrefixes"/>
    /// (CLIENT TRACKING BCAST), whether or not this client read it. No per-key server memory, but more
    /// invalidation traffic. Best for a small set of hot, narrowly prefixed keys.
    /// </summary>
    Broadcast,
}

/// <summary>
/// Configures RESP3 server-assisted client-side caching. Assign <c>new()</c> to
/// <see cref="RespireOptions.ClientSideCache"/> to enable it with bounded defaults.
/// </summary>
/// <example>
/// <code>
/// ClientSideCache = new()
/// {
///     KeyPrefixes = ["product:", "price:"],
///     MaxEntries = 50_000,
///     LocalExpiration = TimeSpan.FromMinutes(1),
/// }
/// </code>
/// </example>
public sealed record RespireClientSideCacheOptions
{
    // Capacity and lifetime.

    /// <summary>Maximum resident cache entries. Defaults to 10,000.</summary>
    public int MaxEntries { get; init; } = 10_000;

    /// <summary>Approximate maximum bytes owned by cached replies. Defaults to 64 MiB.</summary>
    public long MaxSizeBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>
    /// Maximum time an entry stays in the local cache, independent of the key's Redis TTL.
    /// Defaults to five minutes. Null keeps entries until Redis invalidates them or they are evicted.
    /// </summary>
    /// <remarks>Redis invalidations normally remove changed entries immediately. This bound limits
    /// staleness while a broken connection is still being detected.</remarks>
    public TimeSpan? LocalExpiration { get; init; } = TimeSpan.FromMinutes(5);

    // What is cached and how Redis tracks it.

    /// <summary>
    /// Physical key prefixes eligible for caching. Empty (the default) caches every eligible key.
    /// </summary>
    /// <remarks>
    /// Reads of other keys go straight to Redis and are not tracked. In OptIn mode, the per-key
    /// MGET path (typed calls, or raw calls with <see cref="CoalesceConcurrentMisses"/> enabled)
    /// tracks mixed covered/uncovered misses as one command but caches only covered replies.
    /// Raw MGET with coalescing disabled uses exact-query caching: any uncovered key makes
    /// the whole reply uncached and leaves the command untracked. In
    /// <see cref="RespireClientTrackingMode.Broadcast"/> mode the prefixes are also sent to Redis as
    /// BCAST PREFIX arguments. Prefixes are binary-safe, must not overlap or repeat, and are matched
    /// against physical keys: include any <see cref="IRespireClient.WithKeyPrefix(string)"/> prefix.
    /// The client snapshots this collection and its bytes.
    /// </remarks>
    public IReadOnlyList<RespireKey> KeyPrefixes { get; init; } = [];

    /// <summary>How Redis tracks cached keys. Defaults to <see cref="RespireClientTrackingMode.OptIn"/>.</summary>
    public RespireClientTrackingMode TrackingMode { get; init; }

    // Optional behavior.

    /// <summary>
    /// Shares concurrent identical cache misses within this client so only one request reaches Redis
    /// (stampede protection). Also shares <c>GetOrSetAsync</c> factories for the same key, type and TTL.
    /// Defaults to false.
    /// </summary>
    /// <remarks>Off by default because sharing adds bookkeeping and result copies to every miss;
    /// enable it for hot keys read concurrently. Each caller can cancel independently; the shared
    /// request is canceled when its last caller leaves. The physical request retains its original
    /// <see cref="RespireOptions.CommandTimeout"/> deadline; joining later does not restart it.</remarks>
    public bool CoalesceConcurrentMisses { get; init; }

    /// <summary>
    /// Serves individual HMGET fields from cached HGET entries and fetches only the missing fields.
    /// Defaults to false.
    /// </summary>
    /// <remarks>Partial reads reduce transferred values for overlapping field lists but require
    /// more cache entries and allocations than exact-query caching. Results may combine values
    /// read at different times and do not form an atomic snapshot of the hash.
    /// Benchmark representative field counts and payload sizes.</remarks>
    public bool ReuseHashFields { get; init; }

    internal RespireClientSideCacheOptions ValidateAndSnapshot()
    {
        Require(Enum.IsDefined(TrackingMode), nameof(TrackingMode), "must be OptIn or Broadcast");
        Require(KeyPrefixes is not null, nameof(KeyPrefixes), "cannot be null");
        Require(MaxEntries >= 1, nameof(MaxEntries), "must be at least one");
        Require(MaxSizeBytes >= 1, nameof(MaxSizeBytes), "must be at least one");
        Require(LocalExpiration is null || LocalExpiration >= TimeSpan.FromMilliseconds(1),
            nameof(LocalExpiration), "must be null or at least one millisecond");
        return this with { KeyPrefixes = ClientCachePrefixSet.Create(KeyPrefixes!) };
    }

    private static void Require(bool condition, string optionName, string requirement)
    {
        if (!condition)
            throw new RespireConfigurationException($"RespireOptions.ClientSideCache.{optionName} {requirement}.");
    }
}

/// <summary>Cumulative and current state of a Respire client-side cache.</summary>
public readonly record struct RespireClientSideCacheStatistics(
    long Hits,
    long Misses,
    long Invalidations,
    long Evictions,
    long ContinuityFlushes,
    int Count,
    long SizeBytes);

/// <summary>Read-only cache diagnostics plus explicit local invalidation.</summary>
public interface IRespireClientSideCache
{
    /// <summary>Current resident entry count.</summary>
    int Count { get; }

    /// <summary>Approximate bytes owned by current resident entries.</summary>
    long SizeBytes { get; }

    /// <summary>Returns a point-in-time statistics snapshot.</summary>
    RespireClientSideCacheStatistics GetStatistics();

    /// <summary>Evicts every local entry and rejects older reads still in flight.</summary>
    void Clear();

    /// <summary>Observes invalidations relevant to one physical key without adding Redis tracking.</summary>
    /// <remarks>Subscribe before reading the key. The key must be covered by
    /// <see cref="RespireClientSideCacheOptions.KeyPrefixes"/>. OPTIN notifications require tracked reads. Callbacks run asynchronously, serially per
    /// subscription, with one coalesced pending notification. Recheck application state after every
    /// notification. Cancellation, subscription disposal, or client disposal stops future delivery;
    /// a callback already selected for execution may finish. The subscription snapshots binary key storage.
    /// Third-party cache implementations that do not support observation throw NotSupportedException.</remarks>
    IRespireClientCacheInvalidationSubscription SubscribeInvalidations(
        RespireKey key, Action<RespireClientCacheInvalidation> observer, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This client-side cache does not support invalidation observation.");
}

/// <summary>Per-client RESP3 server-assisted cache shared by the root client and its key-prefixed views.</summary>
/// <remarks>
/// <para>
/// Entries hold immutable, deep-owned <see cref="RespValue"/> replies keyed by resolved wire
/// identity (command, ordered arguments, physical keys); caller-owned binary arguments are
/// snapshotted before an asynchronous miss. Typed GET/MGET use per-key entries; other reads use
/// exact-query entries with explicit key dependencies.
/// </para>
/// <para>
/// Insertion invariant: a per-key read publishes only if its key generation, the continuity epoch,
/// and the active <see cref="CacheStore"/> all still match the values captured when the read began;
/// a query read additionally matches the query epoch. Invalidation advances the generation and
/// query epoch before removing dependent projections, and publication and invalidation are
/// serialized, so a racing invalidation is never undone by a stale insert. Cancellation, timeout,
/// protocol failure, and conversion failure release the token without publishing. A Cluster
/// redirect rebases the token after the continuity flush so the retried read can insert.
/// </para>
/// <para>
/// Local mutations fence their written keys before dispatch and after completion (including error
/// and cancellation). Unknown or unprovable effects, blocking commands, cluster-wide mutations,
/// batches, and transactions swap out the whole store at both points instead.
/// </para>
/// <para>
/// Continuity flushes (O(1) store swap plus epoch advance) run on connection close or reconnect,
/// node retirement, MOVED/ASK redirects, null or broadcast invalidation, <see cref="Clear"/>, and
/// conservative unknown-command invalidation. Disposal swaps out the store before connections are
/// released; retired stores stay reachable only from in-flight tokens.
/// </para>
/// <para>
/// When caching is disabled no coordinator exists; the ordinary command path pays only a null
/// check used for mutation fencing.
/// </para>
/// </remarks>
internal sealed partial class ClientSideCacheCoordinator : IRespireClientSideCache
{
    private const int EntryOverhead = 64;

    private readonly RespireClientSideCacheOptions _options;
    private readonly ClientCachePrefixSet _keyPrefixes;
    private readonly ConcurrentDictionary<RespireKey, InflightRead> _inflight = new();
    private readonly Lock _queryLock = new();
    private CacheStore _store;
    private long _continuityEpoch;
    private long _queryEpoch;
    private readonly RequestCounters[] _requests = new RequestCounters[
        BitOperations.RoundUpToPowerOf2((uint)Math.Clamp(Environment.ProcessorCount, 4, 64))];
    private long _invalidations;
    private long _evictions;
    private long _continuityFlushes;

    public ClientSideCacheCoordinator(RespireClientSideCacheOptions options)
    {
        _options = options;
        _keyPrefixes = ClientCachePrefixSet.Create(options.KeyPrefixes);
        _store = new CacheStore(options, RecordRemoval);
    }

    public int Count => Volatile.Read(ref _store).Count;

    internal bool ReuseHashFields => _options.ReuseHashFields;

    public long SizeBytes => Volatile.Read(ref _store).SizeBytes;

    public RespireClientSideCacheStatistics GetStatistics()
    {
        var store = Volatile.Read(ref _store);
        long hits = 0, misses = 0;
        foreach (ref var counters in _requests.AsSpan())
        {
            hits += Interlocked.Read(ref counters.Hits);
            misses += Interlocked.Read(ref counters.Misses);
        }
        return new(
            hits,
            misses,
            Interlocked.Read(ref _invalidations),
            Interlocked.Read(ref _evictions),
            Interlocked.Read(ref _continuityFlushes),
            store.Count,
            store.SizeBytes);
    }

    public void Clear() => Flush(continuityLost: false, RespireClientCacheInvalidationReason.ExplicitClear);

    internal bool TryGet(in RespireKey key, out RespValue value)
    {
        var store = Volatile.Read(ref _store);
        if (store.TryGet(in key, out var payload))
        {
            RecordRequest(hit: true);
            value = payload is null ? RespValue.Null : RespValue.BulkString(payload);
            return true;
        }

        RecordRequest(hit: false);
        value = default;
        return false;
    }

    internal bool TryGetString(in RespireKey key, out string? value)
    {
        var found = Volatile.Read(ref _store).TryGetString(in key, out value);
        RecordRequest(found);
        return found;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RecordRequest(bool hit)
    {
        ref var counters = ref _requests[Thread.GetCurrentProcessorId() & (_requests.Length - 1)];
        if (hit) Interlocked.Increment(ref counters.Hits);
        else Interlocked.Increment(ref counters.Misses);
        RespireTelemetry.RecordCacheRequest(hit);
    }

    // Processor stripes and separated hit/miss lanes avoid one shared cache line.
    // Atomic increments retain exact totals once concurrent lookups have finished.
    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct RequestCounters
    {
        [FieldOffset(0)] internal long Hits;
        [FieldOffset(64)] internal long Misses;
    }

    internal bool TryPeek(in RespireKey key, out RespValue value)
    {
        if (Volatile.Read(ref _store).TryGet(in key, out var payload))
        {
            value = payload is null ? RespValue.Null : RespValue.BulkString(payload);
            return true;
        }
        value = default;
        return false;
    }

    internal bool TryPeek(in QueryRequest request, out RespValue value)
    {
        var query = request.Query;
        return Volatile.Read(ref _store).TryGet(in query, out value);
    }

    internal static bool TryCreateQuery<TCommand>(
        string operation,
        in TCommand command,
        out QueryRequest request)
        where TCommand : struct, IRespCommand
    {
        if (IsCacheableRead(operation)
            && command.TryGetClientCacheKey(operation, out var query)
            && command.TryGetPrimaryKey(out var primaryKey)
            && HasValidDependencies(operation, in query))
        {
            request = new QueryRequest(query, primaryKey.AsKey());
            return true;
        }

        request = default;
        return false;
    }

    internal static bool CanCacheOperation(string operation) => IsCacheableRead(operation);

    internal bool TryGet(in QueryRequest request, out RespValue value)
    {
        var store = Volatile.Read(ref _store);
        var query = request.Query;
        if (store.TryGet(in query, out value))
        {
            RecordRequest(hit: true);
            return true;
        }

        RecordRequest(hit: false);
        return false;
    }

    internal QueryReadToken BeginRead(string operation, in QueryRequest request)
    {
        var query = request.Query.Snapshot();
        var primaryKey = request.PrimaryKey;
        var dependencies = CreateDependencies(operation, in query, in primaryKey);
        return new QueryReadToken(
            query,
            dependencies,
            Volatile.Read(ref _queryEpoch),
            Volatile.Read(ref _continuityEpoch),
            Volatile.Read(ref _store),
            CanTrackAll(dependencies));
    }

    internal void CompleteRead(in QueryReadToken token, in RespValue response, bool allowInsert)
    {
        if (!allowInsert
            || !token.CanCache
            || Volatile.Read(ref _queryEpoch) != token.QueryEpoch
            || Volatile.Read(ref _continuityEpoch) != token.ContinuityEpoch
            || !ReferenceEquals(Volatile.Read(ref _store), token.Store))
        {
            return;
        }

        var query = token.Query;
        if (!token.Store.TryCreateEntry(
                in query, token.Dependencies, in response, out var entry))
        {
            return;
        }

        var published = false;
        lock (_queryLock)
        {
            if (Volatile.Read(ref _queryEpoch) == token.QueryEpoch
                && Volatile.Read(ref _continuityEpoch) == token.ContinuityEpoch
                && ReferenceEquals(Volatile.Read(ref _store), token.Store))
            {
                published = token.Store.Set(in query, entry);
            }
        }

        if (published)
        {
            token.Store.Trim();
        }
    }

    internal QueryReadToken RebaseRead(in QueryReadToken token)
        => new(
            token.Query,
            token.Dependencies,
            Volatile.Read(ref _queryEpoch),
            Volatile.Read(ref _continuityEpoch),
            Volatile.Read(ref _store),
            token.CanCache);

    internal ReadToken BeginRead(in RespireKey key)
    {
        var ownedKey = key.Snapshot();
        if (!CanTrack(in ownedKey))
        {
            // Keep owned wire arguments without registering an invalidation generation for
            // a key that can never be cached. Coverage is immutable for this coordinator.
            return new ReadToken(new InflightRead(ownedKey, canCache: false) { Readers = 1 }, 0,
                Volatile.Read(ref _continuityEpoch), Volatile.Read(ref _store));
        }
        while (true)
        {
            var state = _inflight.GetOrAdd(ownedKey, static key => new InflightRead(key));
            lock (state)
            {
                if (state.Retired)
                {
                    continue;
                }

                state.Readers++;
                return new ReadToken(
                    state,
                    state.Generation,
                    Volatile.Read(ref _continuityEpoch),
                    Volatile.Read(ref _store));
            }
        }
    }

    internal CacheEntry? CompleteRead(in ReadToken token, in RespValue response, bool allowInsert, string? decodedText = null)
    {
        var state = token.State;
        lock (state)
        {
            try
            {
                if (allowInsert
                    && state.CanCache
                    && state.Generation == token.Generation
                    && Volatile.Read(ref _continuityEpoch) == token.ContinuityEpoch
                    && ReferenceEquals(Volatile.Read(ref _store), token.Store))
                {
                    return token.Store.Set(state.Key, in response, decodedText);
                }
                return null;
            }
            finally
            {
                state.Readers--;
                if (state.Readers == 0)
                {
                    state.Retired = true;
                    if (state.CanCache)
                    {
                        ((ICollection<KeyValuePair<RespireKey, InflightRead>>)_inflight)
                            .Remove(new KeyValuePair<RespireKey, InflightRead>(state.Key, state));
                    }
                }
            }
        }
    }

    internal ReadToken RebaseRead(in ReadToken token)
    {
        var empty = default(RespValue);
        var key = token.State.Key;
        CompleteRead(in token, in empty, allowInsert: false);
        return BeginRead(in key);
    }

    internal bool CanTrack(in RespireKey key)
        => _keyPrefixes.Contains(in key);

    private bool CanTrackAll(RespireKey[] keys)
    {
        if (_keyPrefixes.Count == 0) return true;
        foreach (var key in keys)
            if (!CanTrack(in key)) return false;
        return true;
    }

    internal void Invalidate(in RespireKey key,
        RespireClientCacheInvalidationReason reason = RespireClientCacheInvalidationReason.LocalMutation)
    {
        BeginSharedReadInvalidation();
        int removed;
        try
        {
            if (_inflight.TryGetValue(key, out var state))
            {
                lock (state)
                {
                    state.Generation++;
                }
            }

            lock (_queryLock)
            {
                Interlocked.Increment(ref _queryEpoch);
                removed = Volatile.Read(ref _store).Invalidate(in key);
            }
        }
        finally { EndSharedReadInvalidation(); }
        if (reason == RespireClientCacheInvalidationReason.ServerInvalidation)
            RespireTelemetry.RecordCacheEvictions(removed, "invalidation");
        Interlocked.Increment(ref _invalidations);
        PublishInvalidation(in key, reason);
        RespireTelemetry.ClientCacheInvalidations.Add(1);
    }

    internal MutationFence BeforeCommand<TCommand>(string operation, in TCommand command)
        where TCommand : struct, IRespCommand
    {
        if (DisruptsClientCacheTracking(operation, in command))
        {
            throw new NotSupportedException(
                $"{operation} cannot execute while client-side caching is enabled because it changes " +
                "the connection protocol, database, or Redis tracking state.");
        }

        // Indirect writes (for example TimeSeries compaction) cannot be bounded by argument keys,
        // even when a caller supplies a narrower command declaration.
        var mutationKind = RawCommandKeyLayouts.GetMutationKind(operation);
        if (mutationKind == RawCommandKeyLayouts.MutationKind.IndirectKeys)
            return BeginUnknownMutation();

        var mutation = command.GetCacheMutation(operation);
        if (mutation == RespireCacheMutation.ReadOnly) return default;

        if (mutation == RespireCacheMutation.SingleKey
            && command.TryGetClientCacheKey(operation, out var singleKeyArguments)
            && RawCommandKeyLayouts.TryGetMutationLayout(operation, in singleKeyArguments, out var singleKeyLayout)
            && singleKeyLayout.Count == 1 && singleKeyLayout.Extra < 0)
        {
            var key = singleKeyArguments.GetArgument(singleKeyLayout.Start).AsKey().Snapshot();
            Invalidate(in key);
            return MutationFence.ForKey(key);
        }

        if (mutation == RespireCacheMutation.SingleKey && command.TryGetPrimaryKey(out var primaryKey))
        {
            var key = primaryKey.AsKey().Snapshot();
            Invalidate(in key);
            return MutationFence.ForKey(key);
        }

        if (mutation == RespireCacheMutation.Mutation
            && RawCommandKeyLayouts.HasSingleFirstKeyLayout(operation)
            && command.TryGetPrimaryKey(out primaryKey))
        {
            var key = primaryKey.AsKey().Snapshot();
            Invalidate(in key);
            return MutationFence.ForKey(key);
        }

        if (mutation == RespireCacheMutation.Mutation
            && command.TryGetClientCacheKey(operation, out var destinationArguments)
            && RawCommandKeyLayouts.TryGetMutationLayout(operation, in destinationArguments, out var destinationLayout)
            && destinationLayout.Extra >= 0)
        {
            var destination = destinationArguments.GetArgument(destinationLayout.Extra).AsKey().Snapshot();
            Invalidate(in destination);
            return MutationFence.ForKey(destination);
        }

        if (mutation is RespireCacheMutation.MultiKey or RespireCacheMutation.Mutation
            && command.TryGetClientCacheKey(operation, out var arguments)
            && RawCommandKeyLayouts.TryGetMutationLayout(operation, in arguments, out var layout,
                includeReadKeys: mutation == RespireCacheMutation.MultiKey))
        {
            if (mutation == RespireCacheMutation.MultiKey || layout.Count != 1 || layout.Extra >= 0)
                return BeginMultiKeyMutation(in arguments, layout);

            var key = arguments.GetArgument(layout.Start).AsKey().Snapshot();
            Invalidate(in key);
            return MutationFence.ForKey(key);
        }

        return BeginUnknownMutation();
    }

    internal MutationFence BeginUnknownMutation()
    {
        Flush(continuityLost: false);
        return MutationFence.All;
    }

    internal void CompleteMutation(in MutationFence fence)
    {
        switch (fence.Kind)
        {
            case MutationFenceKind.Key:
                var single = fence.Key;
                Invalidate(in single);
                break;
            case MutationFenceKind.Keys:
                foreach (var key in fence.Keys!)
                    Invalidate(in key);
                break;
            case MutationFenceKind.All:
                Flush(continuityLost: false);
                break;
        }
    }

    // Below this many keys a linear scan beats hashing; above it a set keeps fencing linear.
    private const int LinearDeduplicationLimit = 8;

    private MutationFence BeginMultiKeyMutation(
        in ClientCacheCommandKey arguments, RawCommandKeyLayouts.KeyLayout layout)
    {
        var keyCapacity = layout.Count + (layout.Extra >= 0 ? 1 : 0);
        if (keyCapacity == 0)
        {
            return BeginUnknownMutation();
        }

        if (keyCapacity == 1)
        {
            var key = arguments.GetArgument(layout.Extra >= 0 ? layout.Extra : layout.Start).AsKey().Snapshot();
            Invalidate(in key);
            return MutationFence.ForKey(key);
        }

        // Duplicate keys (DEL a a) are skipped so each key is invalidated, published and counted once.
        var keys = new RespireKey[keyCapacity];
        var keyCount = 0;
        var seen = keyCapacity > LinearDeduplicationLimit ? new HashSet<RespireKey>(keyCapacity) : null;
        for (var index = 0; index < layout.Count; index++)
        {
            var key = arguments.GetArgument(layout.Start + index * layout.Stride).AsKey().Snapshot();
            if (seen is not null ? !seen.Add(key) : ContainsKey(keys, keyCount, in key)) continue;
            keys[keyCount++] = key;
            Invalidate(in key);
        }
        if (layout.Extra >= 0)
        {
            var key = arguments.GetArgument(layout.Extra).AsKey().Snapshot();
            if (seen is not null ? seen.Add(key) : !ContainsKey(keys, keyCount, in key))
            {
                keys[keyCount++] = key;
                Invalidate(in key);
            }
        }

        if (keyCount == 1) return MutationFence.ForKey(keys[0]);
        if (keyCount != keys.Length) Array.Resize(ref keys, keyCount);
        return MutationFence.ForKeys(keys);

        static bool ContainsKey(RespireKey[] keys, int count, in RespireKey key)
        {
            for (var index = 0; index < count; index++)
                if (keys[index] == key) return true;
            return false;
        }
    }

    internal void HandlePush(in RespValue push)
    {
        var elements = push.AsArray();
        if (elements.Length != 2 || !elements[0].AsSpan().SequenceEqual("invalidate"u8))
        {
            return;
        }

        var keys = elements[1];
        if (keys.IsNull)
        {
            Interlocked.Increment(ref _invalidations);
            RespireTelemetry.ClientCacheInvalidations.Add(1);
            Flush(continuityLost: false, RespireClientCacheInvalidationReason.ServerInvalidation);
            return;
        }

        foreach (ref readonly var value in keys.AsArray())
        {
            var key = new RespireKey(value.AsMemory());
            Invalidate(in key, RespireClientCacheInvalidationReason.ServerInvalidation);
        }
    }

    internal void FlushForContinuityLoss() => Flush(continuityLost: true);

    internal void FlushForUnknownCommand() => Flush(continuityLost: false);

    // Mutate state and queue observations only; callers may hold membership and health gates.
    internal int FlushForContinuityLossWithoutMetrics() => FlushState(continuityLost: true);

    // A second MOVING barrier fences cache reads admitted by an old socket after the first
    // continuity flush but before that socket stopped accepting commands.
    internal void FlushForMovingRetirementFence()
    {
        var removed = FlushState(continuityLost: false, RespireClientCacheInvalidationReason.ContinuityLost);
        PublishFlushMetrics(removed, continuityLost: false);
    }

    internal static void PublishContinuityFlushMetrics(int removed)
        => PublishFlushMetrics(removed, continuityLost: true);

    private void Flush(bool continuityLost,
        RespireClientCacheInvalidationReason reason = RespireClientCacheInvalidationReason.LocalMutation)
        => PublishFlushMetrics(FlushState(continuityLost, reason), continuityLost,
            reason == RespireClientCacheInvalidationReason.ServerInvalidation ? "invalidation" : null);

    private int FlushState(bool continuityLost,
        RespireClientCacheInvalidationReason reason = RespireClientCacheInvalidationReason.LocalMutation)
    {
        BeginSharedReadInvalidation();
        int removed;
        try
        {
            Interlocked.Increment(ref _continuityEpoch);
            Interlocked.Increment(ref _queryEpoch);
            var replacement = new CacheStore(_options, RecordRemoval);
            removed = Interlocked.Exchange(ref _store, replacement).Retire();
            if (removed > 0)
            {
                Interlocked.Add(ref _evictions, removed);
            }

            if (continuityLost)
            {
                Interlocked.Increment(ref _continuityFlushes);
            }
        }
        finally { EndSharedReadInvalidation(); }
        PublishInvalidationForAll(continuityLost ? RespireClientCacheInvalidationReason.ContinuityLost : reason);
        return removed;
    }

    private static void PublishFlushMetrics(int removed, bool continuityLost, string? reason = null)
    {
        if (removed > 0 && reason is not null)
        {
            RespireTelemetry.RecordCacheEvictions(removed, reason);
        }
        if (continuityLost)
        {
            RespireTelemetry.ClientCacheContinuityFlushes.Add(1);
        }
    }

    private void RecordRemoval(CacheRemoval reason)
    {
        if (reason is CacheRemoval.Capacity or CacheRemoval.Expiration) Interlocked.Increment(ref _evictions);
        RespireTelemetry.RecordCacheEvictions(1, reason switch
        {
            CacheRemoval.Capacity => "full",
            CacheRemoval.Expiration => "ttl",
            _ => null,
        });
    }

    private static bool DisruptsClientCacheTracking<TCommand>(string operation, in TCommand command)
        where TCommand : struct, IRespCommand
    {
        if (operation is "CLIENT CACHING" or "CLIENT TRACKING" or "HELLO" or "RESET" or "SELECT")
        {
            return true;
        }

        return operation == "CLIENT"
               && command.TryGetClientCacheKey(operation, out var query)
               && query.ArgumentCount > 0
               && (query.GetArgument(0).EqualsAsciiIgnoreCase("CACHING")
                   || query.GetArgument(0).EqualsAsciiIgnoreCase("TRACKING"));
    }

    // Mirrors Redis client-side-cache eligibility: keyed, read-only, deterministic, non-blocking,
    // and not a script/function, probabilistic structure, time series, or Search command.
    private static bool IsCacheableRead(string operation)
        => operation is
            "GET" or "MGET" or "STRLEN" or "GETRANGE" or "SUBSTR" or "DIGEST" or "LCS" or
            "EXISTS" or "EXPIRETIME" or "PEXPIRETIME" or "TYPE" or "OBJECT ENCODING" or "MEMORY USAGE" or
            "HGET" or "HMGET" or "HGETALL" or "HEXISTS" or "HLEN" or "HSTRLEN" or
            "HKEYS" or "HVALS" or "HEXPIRETIME" or "HPEXPIRETIME" or
            "LLEN" or "LRANGE" or "LINDEX" or "LPOS" or
            "SISMEMBER" or "SMISMEMBER" or "SCARD" or "SMEMBERS" or
            "SINTER" or "SUNION" or "SDIFF" or "SINTERCARD" or "SUNIONCARD" or "SDIFFCARD" or
            "ZSCORE" or "ZMSCORE" or "ZCARD" or "ZCOUNT" or "ZLEXCOUNT" or "ZRANK" or "ZREVRANK" or
            "ZRANGE" or "ZRANGEBYLEX" or "ZRANGEBYSCORE" or
            "ZREVRANGE" or "ZREVRANGEBYLEX" or "ZREVRANGEBYSCORE" or
            "ZINTER" or "ZUNION" or "ZDIFF" or "ZINTERCARD" or
            "XLEN" or "XRANGE" or "XREVRANGE" or "XPENDING" or
            "XINFO STREAM" or "XINFO GROUPS" or
            "GETBIT" or "BITCOUNT" or "BITPOS" or "BITFIELD_RO" or
            "GEODIST" or "GEOHASH" or "GEOPOS" or "GEOSEARCH" or
            "GEORADIUS_RO" or "GEORADIUSBYMEMBER_RO" or
            "ARCOUNT" or "ARGET" or "ARGETRANGE" or "ARGREP" or "ARINFO" or
            "ARLASTITEMS" or "ARLEN" or "ARMGET" or "ARNEXT" or "AROP" or "ARSCAN" or
            "JSON.ARRINDEX" or "JSON.ARRLEN" or "JSON.GET" or "JSON.MGET" or
            "JSON.OBJKEYS" or "JSON.OBJLEN" or "JSON.RESP" or "JSON.STRLEN" or "JSON.TYPE" or
            "VCARD" or "VDIM" or "VEMB" or "VGETATTR" or "VINFO" or
            "VISMEMBER" or "VLINKS" or "VRANGE" or "VSIM" or
            "SORT_RO";

    private static bool HasValidDependencies(string operation, in ClientCacheCommandKey query)
    {
        if (operation == "LCS")
        {
            return query.ArgumentCount >= 2;
        }

        if (operation == "XPENDING")
        {
            // The summary form is stable; range replies contain a time-varying idle duration.
            return query.ArgumentCount == 2;
        }

        if (operation == "JSON.MGET")
        {
            return query.ArgumentCount >= 2;
        }

        if (operation == "GEOSEARCH")
        {
            return !ContainsGeoSearchAny(in query);
        }

        if (operation == "MEMORY USAGE")
        {
            return query.ArgumentCount == 3
                   && query.GetArgument(1).EqualsAsciiIgnoreCase("SAMPLES")
                   && query.GetArgument(2).TryGetInt64(out var samples)
                   && samples == 0;
        }

        if (operation == "SORT_RO")
        {
            return !ContainsImplicitSortDependency(in query);
        }

        if (UsesEveryArgumentAsKey(operation))
        {
            return query.ArgumentCount > 0;
        }

        if (UsesCountedKeys(operation))
        {
            return TryGetKeyCount(in query, out _);
        }

        return true;
    }

    private static RespireKey[] CreateDependencies(
        string operation,
        in ClientCacheCommandKey query,
        in RespireKey primaryKey)
    {
        if (operation == "LCS")
        {
            return [query.GetArgument(0).AsKey().Snapshot(), query.GetArgument(1).AsKey().Snapshot()];
        }

        if (UsesEveryArgumentAsKey(operation))
        {
            var keys = new RespireKey[query.ArgumentCount];
            for (var index = 0; index < keys.Length; index++)
            {
                keys[index] = query.GetArgument(index).AsKey().Snapshot();
            }

            return keys;
        }

        if (operation == "JSON.MGET")
        {
            var keys = new RespireKey[query.ArgumentCount - 1];
            for (var index = 0; index < keys.Length; index++)
            {
                keys[index] = query.GetArgument(index).AsKey().Snapshot();
            }

            return keys;
        }

        if (UsesCountedKeys(operation) && TryGetKeyCount(in query, out var count))
        {
            var keys = new RespireKey[count];
            for (var index = 0; index < count; index++)
            {
                keys[index] = query.GetArgument(index + 1).AsKey().Snapshot();
            }

            return keys;
        }

        return [primaryKey.Snapshot()];
    }

    private static bool UsesEveryArgumentAsKey(string operation)
        => operation is "MGET" or "EXISTS" or "SINTER" or "SUNION" or "SDIFF";

    private static bool UsesCountedKeys(string operation)
        => operation is
            "SINTERCARD" or "SUNIONCARD" or "SDIFFCARD" or
            "ZINTER" or "ZUNION" or "ZDIFF" or "ZINTERCARD";

    private static bool ContainsImplicitSortDependency(in ClientCacheCommandKey query)
    {
        for (var index = 1; index < query.ArgumentCount; index++)
        {
            var argument = query.GetArgument(index);
            if (argument.EqualsAsciiIgnoreCase("BY") || argument.EqualsAsciiIgnoreCase("GET"))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsGeoSearchAny(in ClientCacheCommandKey query)
    {
        for (var index = 1; index + 2 < query.ArgumentCount; index++)
        {
            if (query.GetArgument(index).EqualsAsciiIgnoreCase("COUNT")
                && query.GetArgument(index + 2).EqualsAsciiIgnoreCase("ANY"))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetKeyCount(in ClientCacheCommandKey query, out int count)
    {
        if (query.ArgumentCount > 1
            && query.GetArgument(0).TryGetInt64(out var value)
            && value > 0
            && value <= query.ArgumentCount - 1
            && value <= int.MaxValue)
        {
            count = (int)value;
            return true;
        }

        count = 0;
        return false;
    }

    internal readonly record struct ReadToken(
        InflightRead State,
        long Generation,
        long ContinuityEpoch,
        CacheStore Store);

    // Canonical GET text is separate from arbitrary caller converters. Coalesced
    // callers copy response storage but share this immutable text/publication handle.
    internal readonly struct GetReadResult(
        RespValue response, RespireKey key, CacheStore? store, CacheEntry? entry, string? text)
    {
        internal readonly RespValue Response = response;
        private readonly RespireKey _key = key;
        private readonly CacheStore? _store = store;
        private readonly CacheEntry? _entry = entry;
        private readonly string? _text = text;
        private readonly SharedText? _sharedText;

        private GetReadResult(RespValue response, RespireKey key, CacheStore? store,
            CacheEntry? entry, string? text, SharedText? sharedText)
            : this(response, key, store, entry, text) => _sharedText = sharedText;

        internal string? GetString()
            => _text ?? (_sharedText is not null ? _sharedText.GetString(in this) : GetPublicationString());

        private string? GetPublicationString()
            => _text ?? (_entry is not null ? _store!.GetString(in _key, _entry)
                : Internal.ResponseReader.StringOrNull(in Response));

        internal GetReadResult ToOwned(bool shareText = false)
            => new(Response.ToOwned(), _key, _store, _entry, _text,
                _sharedText ?? (shareText && _text is null ? new SharedText() : null));

        // A byte/custom producer may have no resident entry, or its entry may be
        // invalidated before a string waiter runs. Share text independently of
        // admission, without retaining response bytes or changing cache accounting.
        private sealed class SharedText
        {
            private string? _value;

            internal string? GetString(in GetReadResult result)
            {
                var value = Volatile.Read(ref _value);
                if (value is not null) return value;
                value = result.GetPublicationString();
                return value is null ? null : Interlocked.CompareExchange(ref _value, value, null) ?? value;
            }
        }
    }

    internal bool TryPeekRead(in RespireKey key, out GetReadResult result)
        => Volatile.Read(ref _store).TryGetRead(in key, out result);

    internal readonly record struct QueryRequest(ClientCacheCommandKey Query, RespireKey PrimaryKey);

    internal readonly record struct QueryReadToken(
        ClientCacheCommandKey Query,
        RespireKey[] Dependencies,
        long QueryEpoch,
        long ContinuityEpoch,
        CacheStore Store,
        bool CanCache);

    internal sealed class InflightRead(RespireKey key, bool canCache = true)
    {
        public RespireKey Key { get; } = key;
        public bool CanCache { get; } = canCache;
        public long Generation;
        public int Readers;
        public bool Retired;
    }

    /// <summary>One generation of resident entries; a full flush replaces the whole store.</summary>
    /// <remarks>
    /// Hits are lock-free <see cref="ConcurrentDictionary{TKey, TValue}"/> probes with lazy monotonic
    /// TTL checks (<see cref="Stopwatch.GetTimestamp"/>): no timer, linked-list mutation, queue growth,
    /// or global lock. <see cref="RespireClientSideCacheOptions.MaxEntries"/> and
    /// <see cref="RespireClientSideCacheOptions.MaxSizeBytes"/> are hard limits; the first exceeded
    /// triggers eviction, which enumerates only after a limit is crossed so auxiliary state stays
    /// bounded under invalidate/reinsert churn. Size counts deep payloads, decoded GET strings, arguments, dependency keys,
    /// and a fixed per-entry overhead. A single value larger than the limit is returned uncached.
    /// </remarks>
    internal sealed class CacheStore
    {
        private readonly ConcurrentDictionary<RespireKey, CacheEntry> _entries = new();
        private readonly ConcurrentDictionary<ClientCacheCommandKey, QueryCacheEntry> _queries = new();
        private readonly Dictionary<RespireKey, HashSet<ClientCacheCommandKey>> _dependencies = new();
        // When both gates are needed, take _dependencyLock before _removalLock, never the reverse.
        // Retirement shares the removal gate so its count is atomic with publication and removal claims.
        private readonly Lock _dependencyLock = new();
        private readonly Lock _removalLock = new();
        private readonly RespireClientSideCacheOptions _options;
        private readonly Action<CacheRemoval> _recordRemoval;
        private int _trimming;
        private long _sizeBytes;
        // Read and transition only under _removalLock; Retire is the only transition.
        private StoreState _state;

        private enum StoreState { Active, Retired }

        public CacheStore(RespireClientSideCacheOptions options, Action<CacheRemoval> recordRemoval)
        {
            _options = options;
            _recordRemoval = recordRemoval;
        }

        public int Count => _entries.Count + _queries.Count;
        public long SizeBytes => Interlocked.Read(ref _sizeBytes);

        /// <summary>Claims flush removals atomically with individual removals, suppressing later eviction reports.</summary>
        public int Retire()
        {
            lock (_removalLock)
            {
                if (_state == StoreState.Retired) return 0;
                var count = Count;
                _state = StoreState.Retired;
                return count;
            }
        }

        public bool TryGet(in RespireKey key, out byte[]? payload)
        {
            var found = TryGetEntry(in key, out var entry);
            payload = entry?.Payload;
            return found;
        }

        public bool TryGetString(in RespireKey key, out string? value)
        {
            if (!TryGetEntry(in key, out var entry))
            {
                value = null;
                return false;
            }
            // Preserve the warm-hit probe without another helper call.
            if (entry.Payload is null)
            {
                value = null;
                return true;
            }
            value = entry.DecodedText;
            if (value is not null) return true;
            value = GetString(in key, entry);
            return true;
        }

        internal bool TryGetRead(in RespireKey key, out GetReadResult result)
        {
            if (!TryGetEntry(in key, out var entry))
            {
                result = default;
                return false;
            }
            var response = entry.Payload is null ? RespValue.Null : RespValue.BulkString(entry.Payload);
            result = new(response, key.Snapshot(), this, entry, entry.DecodedText);
            return true;
        }

        internal string? GetString(in RespireKey key, CacheEntry entry)
        {
            if (entry.Payload is null)
            {
                return null;
            }
            var value = entry.DecodedText;
            if (value is not null) return value;

            var decoded = Encoding.UTF8.GetString(entry.Payload);
            lock (_removalLock)
            {
                value = entry.DecodedText;
                if (value is not null) return value;
                value = decoded;
                // An invalidated/replaced/retired entry can still serve this borrowed
                // lookup, but must never increase the current store's accounted size.
                if (_state == StoreState.Retired || !_entries.TryGetValue(key, out var current)
                    || !ReferenceEquals(current, entry)) return value;
                var added = CacheEntry.DecodedSize(decoded);
                entry.SetDecodedText(decoded, added);
                Interlocked.Add(ref _sizeBytes, added);
            }
            Trim();
            return value;
        }

        private bool TryGetEntry(in RespireKey key, [NotNullWhen(true)] out CacheEntry? found)
        {
            while (_entries.TryGetValue(key, out var entry))
            {
                if (entry.ExpiresAt == 0 || Stopwatch.GetTimestamp() < entry.ExpiresAt)
                {
                    found = entry;
                    return true;
                }

                if (Remove(in key, entry, CacheRemoval.Expiration))
                {
                    break;
                }
            }

            found = null;
            return false;
        }

        public bool TryGet(in ClientCacheCommandKey query, out RespValue value)
        {
            while (_queries.TryGetValue(query, out var entry))
            {
                if (entry.ExpiresAt == 0 || Stopwatch.GetTimestamp() < entry.ExpiresAt)
                {
                    value = entry.Value;
                    return true;
                }

                if (Remove(in query, entry, CacheRemoval.Expiration))
                {
                    break;
                }
            }

            value = default;
            return false;
        }

        public CacheEntry? Set(RespireKey key, in RespValue response, string? decodedText = null)
        {
            if (!response.IsNull && response.Type is not RespDataType.BulkString and not RespDataType.SimpleString)
            {
                return null;
            }

            var payloadLength = response.IsNull ? 0 : response.AsSpan().Length;
            var size = (long)EntryOverhead + key.WireLength + payloadLength;
            if (decodedText is not null) size += CacheEntry.DecodedSize(decodedText);
            if (size > _options.MaxSizeBytes)
            {
                return null;
            }

            var payload = response.IsNull ? null : response.AsSpan().ToArray();
            var expiresAt = ExpirationTimestamp(_options.LocalExpiration);
            var entry = new CacheEntry(payload, size, expiresAt, decodedText);
            lock (_removalLock)
            {
                if (_state == StoreState.Retired) return null;
                if (_entries.TryGetValue(key, out var previous))
                {
                    if (_entries.TryUpdate(key, entry, previous))
                    {
                        Interlocked.Add(ref _sizeBytes, entry.Size - previous.Size);
                    }
                }
                else if (_entries.TryAdd(key, entry))
                {
                    Interlocked.Add(ref _sizeBytes, size);
                }
            }

            Trim();
            return entry;
        }

        public bool TryCreateEntry(
            in ClientCacheCommandKey query,
            RespireKey[] dependencies,
            in RespValue response,
            [NotNullWhen(true)] out QueryCacheEntry? entry)
        {
            if (response.IsError)
            {
                entry = null;
                return false;
            }

            var size = EntryOverhead + query.OwnedSize + response.GetOwnedSize();
            for (var index = 0; index < dependencies.Length; index++)
            {
                size += dependencies[index].WireLength;
            }

            if (size > _options.MaxSizeBytes)
            {
                entry = null;
                return false;
            }

            entry = new QueryCacheEntry(
                response.ToOwned(), dependencies, size, ExpirationTimestamp(_options.LocalExpiration));
            return true;
        }

        public bool Set(in ClientCacheCommandKey query, QueryCacheEntry entry)
        {
            lock (_dependencyLock)
            lock (_removalLock)
            {
                if (_state == StoreState.Retired) return false;
                if (_queries.TryGetValue(query, out var previous))
                {
                    if (!_queries.TryUpdate(query, entry, previous))
                    {
                        return false;
                    }

                    RemoveDependencies(in query, previous.Dependencies);
                    Interlocked.Add(ref _sizeBytes, entry.Size - previous.Size);
                }
                else if (_queries.TryAdd(query, entry))
                {
                    Interlocked.Add(ref _sizeBytes, entry.Size);
                }
                else
                {
                    return false;
                }

                AddDependencies(in query, entry.Dependencies);
            }

            return true;
        }

        private static long ExpirationTimestamp(TimeSpan? timeToLive)
        {
            if (timeToLive is not { } ttl)
            {
                return 0;
            }

            var now = Stopwatch.GetTimestamp();
            var duration = ttl.TotalSeconds * Stopwatch.Frequency;
            return duration >= long.MaxValue - now
                ? long.MaxValue
                : now + (long)duration;
        }

        // Key invalidation returns its claimed response count to the coordinator, which
        // publishes telemetry after this method releases all cache locks.
        public int Invalidate(in RespireKey key)
        {
            var removed = 0;
            CacheEntry? entry;
            bool reportRemoval;
            lock (_removalLock)
            {
                _entries.TryRemove(key, out entry);
                reportRemoval = entry is not null && AccountRemoval(entry.Size);
            }
            if (reportRemoval) removed++;

            lock (_dependencyLock)
            {
                if (!_dependencies.Remove(key, out var queries))
                {
                    return removed;
                }

                foreach (var query in queries)
                {
                    QueryCacheEntry? queryEntry;
                    lock (_removalLock)
                    {
                        _queries.TryRemove(query, out queryEntry);
                        reportRemoval = queryEntry is not null && AccountRemoval(queryEntry.Size);
                    }
                    if (queryEntry is not null)
                    {
                        RemoveDependencies(in query, queryEntry.Dependencies);
                        if (reportRemoval) removed++;
                    }
                }
            }
            return removed;
        }

        private bool Remove(in RespireKey key, CacheEntry expected, CacheRemoval reason)
        {
            bool reportRemoval;
            lock (_removalLock)
            {
                if (!((ICollection<KeyValuePair<RespireKey, CacheEntry>>)_entries)
                    .Remove(new KeyValuePair<RespireKey, CacheEntry>(key, expected)))
                {
                    return false;
                }
                reportRemoval = AccountRemoval(expected.Size);
            }

            RecordRemoval(reason, reportRemoval);
            return true;
        }

        private void RecordRemoval(CacheRemoval reason, bool reportRemoval)
        {
            if (reportRemoval && reason is (CacheRemoval.Capacity or CacheRemoval.Expiration)) _recordRemoval(reason);
        }

        // Call under _removalLock after a successful removal, before publication can trim
        // against stale bytes. Reporting stays outside the gates and excludes retired stores.
        private bool AccountRemoval(long size)
        {
            Interlocked.Add(ref _sizeBytes, -size);
            return _state == StoreState.Active;
        }

        private bool Remove(
            in ClientCacheCommandKey query,
            QueryCacheEntry expected,
            CacheRemoval reason)
        {
            bool reportRemoval;
            lock (_dependencyLock)
            {
                lock (_removalLock)
                {
                    if (!((ICollection<KeyValuePair<ClientCacheCommandKey, QueryCacheEntry>>)_queries)
                        .Remove(new KeyValuePair<ClientCacheCommandKey, QueryCacheEntry>(query, expected)))
                    {
                        return false;
                    }
                    reportRemoval = AccountRemoval(expected.Size);
                }

                RemoveDependencies(in query, expected.Dependencies);
            }
            RecordRemoval(reason, reportRemoval);
            return true;
        }

        private void AddDependencies(
            in ClientCacheCommandKey query,
            ReadOnlySpan<RespireKey> dependencies)
        {
            foreach (ref readonly var dependency in dependencies)
            {
                if (!_dependencies.TryGetValue(dependency, out var queries))
                {
                    queries = [];
                    _dependencies.Add(dependency, queries);
                }

                queries.Add(query);
            }
        }

        private void RemoveDependencies(
            in ClientCacheCommandKey query,
            ReadOnlySpan<RespireKey> dependencies)
        {
            foreach (ref readonly var dependency in dependencies)
            {
                if (!_dependencies.TryGetValue(dependency, out var queries))
                {
                    continue;
                }

                queries.Remove(query);
                if (queries.Count == 0)
                {
                    _dependencies.Remove(dependency);
                }
            }
        }

        internal void Trim()
        {
            while (IsOverCapacity)
            {
                if (Interlocked.CompareExchange(ref _trimming, 1, 0) != 0)
                {
                    return;
                }

                try
                {
                    TrimToCapacity();
                }
                finally
                {
                    Volatile.Write(ref _trimming, 0);
                }
            }
        }

        private bool IsOverCapacity
            => Count > _options.MaxEntries || SizeBytes > _options.MaxSizeBytes;

        private void TrimToCapacity()
        {
            while (IsOverCapacity)
            {
                var removed = false;
                foreach (var pair in _entries)
                {
                    var key = pair.Key;
                    removed |= Remove(in key, pair.Value, CacheRemoval.Capacity);
                    if (!IsOverCapacity)
                    {
                        break;
                    }
                }

                if (IsOverCapacity)
                {
                    foreach (var pair in _queries)
                    {
                        var query = pair.Key;
                        removed |= Remove(in query, pair.Value, CacheRemoval.Capacity);
                        if (!IsOverCapacity)
                        {
                            break;
                        }
                    }
                }

                if (!removed)
                {
                    return;
                }
            }
        }
    }

    internal sealed class CacheEntry(byte[]? payload, long size, long expiresAt, string? decodedText = null)
    {
        private string? _decodedText = decodedText;
        private long _size = size;
        public byte[]? Payload { get; } = payload;
        public long Size => Interlocked.Read(ref _size);
        public long ExpiresAt { get; } = expiresAt;
        internal string? DecodedText => Volatile.Read(ref _decodedText);

        internal static long DecodedSize(string text) => (24L + text.Length * sizeof(char) + 7) & ~7L;

        // The store's removal gate serializes accounting, publication and removal.
        internal void SetDecodedText(string text, long addedSize)
        {
            Interlocked.Add(ref _size, addedSize);
            Volatile.Write(ref _decodedText, text);
        }
    }

    internal sealed class QueryCacheEntry(
        RespValue value,
        RespireKey[] dependencies,
        long size,
        long expiresAt)
    {
        public RespValue Value { get; } = value;
        public RespireKey[] Dependencies { get; } = dependencies;
        public long Size { get; } = size;
        public long ExpiresAt { get; } = expiresAt;
    }

    internal enum CacheRemoval
    {
        Capacity,
        Expiration,
    }

    internal enum MutationFenceKind : byte
    {
        /// <summary>No fence: the command is read-only or caching is disabled.</summary>
        None,
        /// <summary>Re-invalidate one key on completion.</summary>
        Key,
        /// <summary>Re-invalidate several distinct keys on completion.</summary>
        Keys,
        /// <summary>Flush the whole cache on completion.</summary>
        All,
    }

    /// <summary>
    /// What a mutation must re-invalidate when its reply arrives. Constructed only through the factories,
    /// so the payload always matches <see cref="Kind"/>.
    /// </summary>
    internal readonly struct MutationFence
    {
        private readonly RespireKey _key;
        private readonly RespireKey[]? _keys;

        private MutationFence(MutationFenceKind kind, RespireKey key, RespireKey[]? keys)
            => (Kind, _key, _keys) = (kind, key, keys);

        internal static MutationFence All => new(MutationFenceKind.All, default, null);

        internal static MutationFence ForKey(RespireKey key) => new(MutationFenceKind.Key, key, null);

        internal static MutationFence ForKeys(RespireKey[] keys) => new(MutationFenceKind.Keys, default, keys);

        internal MutationFenceKind Kind { get; }

        /// <summary>The fenced key when <see cref="Kind"/> is <see cref="MutationFenceKind.Key"/>.</summary>
        internal RespireKey Key => _key;

        /// <summary>The distinct fenced keys when <see cref="Kind"/> is <see cref="MutationFenceKind.Keys"/>.</summary>
        internal RespireKey[]? Keys => _keys;

        internal bool IsRequired => Kind != MutationFenceKind.None;
    }
}
