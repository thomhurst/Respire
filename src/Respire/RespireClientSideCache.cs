using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;

namespace Respire;

/// <summary>How Redis registers keys for client-cache invalidations.</summary>
public enum RespireClientTrackingMode
{
    /// <summary>Track each cache miss with CLIENT CACHING YES. This is the default.</summary>
    OptIn,
    /// <summary>Receive invalidations for all keys matching the configured physical prefixes.</summary>
    Broadcast,
}

/// <summary>Bounds and expiration policy for RESP3 server-assisted client-side caching.</summary>
public sealed record RespireClientSideCacheOptions
{
    /// <summary>Reuse HGET field entries across HMGET requests. Defaults to false.</summary>
    /// <remarks>Partial reads reduce transferred values for overlapping field lists but require
    /// more cache entries and allocations than exact-query caching. Results may combine values
    /// read at different times and do not form an atomic snapshot of the hash.
    /// Benchmark representative field counts and payload sizes.</remarks>
    public bool ReuseHashFields { get; init; }

    /// <summary>Redis tracking mode. Defaults to OptIn.</summary>
    public RespireClientTrackingMode TrackingMode { get; init; }

    /// <summary>Literal binary-safe physical prefixes for Broadcast mode; empty means every key.</summary>
    /// <remarks>Prefixes cannot overlap. Client key prefixes are not added automatically.
    /// Reads outside this set bypass local storage. The client snapshots this collection and its bytes.</remarks>
    public IReadOnlyList<RespireKey> BroadcastPrefixes { get; init; } = [];

    /// <summary>Maximum resident keys. Defaults to 10,000.</summary>
    public int MaxEntries { get; init; } = 10_000;

    /// <summary>Approximate maximum owned cache bytes. Defaults to 64 MiB.</summary>
    public long MaxSizeBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>
    /// Maximum local lifetime for an entry. Null relies only on Redis invalidations; the
    /// default five-minute bound limits staleness while a broken connection is being detected.
    /// </summary>
    public TimeSpan? TimeToLive { get; init; } = TimeSpan.FromMinutes(5);

    internal RespireClientSideCacheOptions SnapshotTracking()
    {
        if (!Enum.IsDefined(TrackingMode))
            throw new RespireConfigurationException("ClientSideCache.TrackingMode is invalid.");
        if (BroadcastPrefixes is null)
            throw new RespireConfigurationException("ClientSideCache.BroadcastPrefixes cannot be null.");
        if (TrackingMode != RespireClientTrackingMode.Broadcast && BroadcastPrefixes.Count != 0)
            throw new RespireConfigurationException("ClientSideCache.BroadcastPrefixes requires Broadcast tracking.");
        return this with { BroadcastPrefixes = BroadcastPrefixSet.Create(BroadcastPrefixes) };
    }

    /// <summary>
    /// Shares concurrent equivalent cache misses within this client. Each caller can cancel
    /// independently; the shared request is canceled when its last caller leaves. Defaults to false.
    /// </summary>
    /// <remarks>Each caller's cancellation token bounds only its own wait. The physical request
    /// retains its original <see cref="RespireOptions.CommandTimeout"/> deadline; joining later
    /// does not restart that deadline.</remarks>
    public bool CoalesceConcurrentMisses { get; init; }
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
    /// <remarks>Subscribe before reading the key. OPTIN notifications require tracked reads; Broadcast
    /// requires coverage by the configured prefixes. Callbacks run asynchronously, serially per
    /// subscription, with one coalesced pending notification. Recheck application state after every
    /// notification. Cancellation, subscription disposal, or client disposal stops future delivery;
    /// a callback already selected for execution may finish. The subscription snapshots binary key storage.
    /// Third-party cache implementations that do not support observation throw NotSupportedException.</remarks>
    IRespireClientCacheInvalidationSubscription SubscribeInvalidations(
        RespireKey key, Action<RespireClientCacheInvalidation> observer, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This client-side cache does not support invalidation observation.");
}

internal sealed partial class ClientSideCacheCoordinator : IRespireClientSideCache
{
    private const int EntryOverhead = 64;

    private readonly RespireClientSideCacheOptions _options;
    private readonly BroadcastPrefixSet _broadcastPrefixes;
    private readonly ConcurrentDictionary<RespireKey, InflightRead> _inflight = new();
    private readonly Lock _queryLock = new();
    private CacheStore _store;
    private long _continuityEpoch;
    private long _queryEpoch;
    private long _hits;
    private long _misses;
    private long _invalidations;
    private long _evictions;
    private long _continuityFlushes;

    public ClientSideCacheCoordinator(RespireClientSideCacheOptions options)
    {
        _options = options;
        _broadcastPrefixes = options.TrackingMode == RespireClientTrackingMode.Broadcast
            ? BroadcastPrefixSet.Create(options.BroadcastPrefixes)
            : BroadcastPrefixSet.Empty;
        _store = new CacheStore(options, RecordEviction);
    }

    public int Count => Volatile.Read(ref _store).Count;

    internal bool ReuseHashFields => _options.ReuseHashFields;

    public long SizeBytes => Volatile.Read(ref _store).SizeBytes;

    public RespireClientSideCacheStatistics GetStatistics()
    {
        var store = Volatile.Read(ref _store);
        return new(
            Interlocked.Read(ref _hits),
            Interlocked.Read(ref _misses),
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
            Interlocked.Increment(ref _hits);
            RespireTelemetry.ClientCacheHits.Add(1);
            value = payload is null ? RespValue.Null : RespValue.BulkString(payload);
            return true;
        }

        Interlocked.Increment(ref _misses);
        RespireTelemetry.ClientCacheMisses.Add(1);
        value = default;
        return false;
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

    internal bool TryCreateQuery<TCommand>(
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
            Interlocked.Increment(ref _hits);
            RespireTelemetry.ClientCacheHits.Add(1);
            return true;
        }

        Interlocked.Increment(ref _misses);
        RespireTelemetry.ClientCacheMisses.Add(1);
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

    internal void CompleteRead(in ReadToken token, in RespValue response, bool allowInsert)
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
                    token.Store.Set(state.Key, in response);
                }
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
        => _broadcastPrefixes.Contains(in key);

    private bool CanTrackAll(RespireKey[] keys)
    {
        if (_broadcastPrefixes.Count == 0) return true;
        foreach (var key in keys)
            if (!CanTrack(in key)) return false;
        return true;
    }

    internal void Invalidate(in RespireKey key,
        RespireClientCacheInvalidationReason reason = RespireClientCacheInvalidationReason.LocalMutation)
    {
        BeginSharedReadInvalidation();
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
                Volatile.Read(ref _store).Remove(in key, CacheRemoval.Invalidation);
            }
        }
        finally { EndSharedReadInvalidation(); }
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

        if (IsReadOnly(operation))
        {
            return default;
        }

        if (IsSingleKeyMutation(operation) && command.TryGetPrimaryKey(out var primaryKey))
        {
            var key = primaryKey.AsKey().Snapshot();
            Invalidate(in key);
            return MutationFence.ForKey(key);
        }

        if (IsMultiKeyMutation(operation)
            && command.TryGetClientCacheKey(operation, out var arguments)
            && RawCommandKeyLayouts.TryGetMutationLayout(operation, in arguments, out var layout))
        {
            return BeginMultiKeyMutation(in arguments, layout);
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
        if (layout.Count == 1)
        {
            var key = arguments.GetArgument(layout.Start).AsKey().Snapshot();
            Invalidate(in key);
            return MutationFence.ForKey(key);
        }

        // Duplicate keys (DEL a a) are skipped so each key is invalidated, published and counted once.
        var keys = new RespireKey[layout.Count];
        var keyCount = 0;
        var seen = layout.Count > LinearDeduplicationLimit ? new HashSet<RespireKey>(layout.Count) : null;
        for (var index = 0; index < layout.Count; index++)
        {
            var key = arguments.GetArgument(layout.Start + index * layout.Stride).AsKey().Snapshot();
            if (seen is not null ? !seen.Add(key) : ContainsKey(keys, keyCount, in key)) continue;
            keys[keyCount++] = key;
            Invalidate(in key);
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

    internal static void PublishContinuityFlushMetrics(int removed)
        => PublishFlushMetrics(removed, continuityLost: true);

    private void Flush(bool continuityLost,
        RespireClientCacheInvalidationReason reason = RespireClientCacheInvalidationReason.LocalMutation)
        => PublishFlushMetrics(FlushState(continuityLost, reason), continuityLost);

    private int FlushState(bool continuityLost,
        RespireClientCacheInvalidationReason reason = RespireClientCacheInvalidationReason.LocalMutation)
    {
        BeginSharedReadInvalidation();
        int removed;
        try
        {
            Interlocked.Increment(ref _continuityEpoch);
            Interlocked.Increment(ref _queryEpoch);
            var replacement = new CacheStore(_options, RecordEviction);
            removed = Interlocked.Exchange(ref _store, replacement).Count;
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

    private static void PublishFlushMetrics(int removed, bool continuityLost)
    {
        if (removed > 0)
        {
            RespireTelemetry.ClientCacheEvictions.Add(removed);
        }
        if (continuityLost)
        {
            RespireTelemetry.ClientCacheContinuityFlushes.Add(1);
        }
    }

    private void RecordEviction()
    {
        Interlocked.Increment(ref _evictions);
        RespireTelemetry.ClientCacheEvictions.Add(1);
    }

    // Read-only here means preserving cached keyspace values. Diagnostic and script state may change.
    private static bool IsReadOnly(string operation)
        => IsCacheableRead(operation)
           || operation is
            "DUMP" or "TTL" or "PTTL" or "HTTL" or "HPTTL" or
            "SCAN" or "KEYS" or "RANDOMKEY" or "HSCAN" or
            "HRANDFIELD" or "SRANDMEMBER" or "SSCAN" or "ZRANDMEMBER" or "ZSCAN" or
            "VRANDMEMBER" or "XREAD" or "XINFO CONSUMERS" or
            "OBJECT FREQ" or "OBJECT IDLETIME" or "OBJECT REFCOUNT" or "TOUCH" or
            "EVAL_RO" or "EVALSHA_RO" or "FCALL_RO" or
            "FUNCTION LOAD" or "FUNCTION LIST" or "FUNCTION DELETE" or "FUNCTION FLUSH" or
            "FUNCTION DUMP" or "FUNCTION RESTORE" or "FUNCTION STATS" or
            "SCRIPT EXISTS" or "SCRIPT LOAD" or "SCRIPT FLUSH" or
            "BF.CARD" or "BF.DEBUG" or "BF.EXISTS" or "BF.INFO" or "BF.MEXISTS" or "BF.SCANDUMP" or
            "CF.COUNT" or "CF.DEBUG" or "CF.EXISTS" or "CF.INFO" or
            "CF.MEXISTS" or "CF.SCANDUMP" or "CMS.INFO" or "CMS.QUERY" or
            "TDIGEST.BYRANK" or "TDIGEST.BYREVRANK" or "TDIGEST.CDF" or "TDIGEST.INFO" or
            "TDIGEST.MAX" or "TDIGEST.MIN" or "TDIGEST.QUANTILE" or "TDIGEST.RANK" or
            "TDIGEST.REVRANK" or "TDIGEST.TRIMMED_MEAN" or
            "TOPK.COUNT" or "TOPK.INFO" or "TOPK.LIST" or "TOPK.QUERY" or
            "TS.GET" or "TS.INFO" or "TS.MGET" or "TS.MRANGE" or "TS.MREVRANGE" or
            "TS.NRANGE" or "TS.NREVRANGE" or "TS.QUERYINDEX" or "TS.QUERYLABELS" or
            "TS.RANGE" or "TS.READ" or "TS.REVRANGE" or "TIMESERIES.REFRESHCLUSTER" or
            "FT.AGGREGATE" or "FT.ALIASLIST" or "FT.CURSOR" or "FT.CURSOR DEL" or
            "FT.CURSOR GC" or "FT.CURSOR READ" or "FT.DICTDUMP" or "FT.EXPLAIN" or
            "FT.EXPLAINCLI" or "FT.INFO" or "FT.PROFILE" or "FT.SEARCH" or
            "FT.SPELLCHECK" or "FT.SUGGET" or "FT.SUGLEN" or "FT.SYNDUMP" or "FT.TAGVALS" or
            "JSON.DEBUG" or "LOLWUT" or
            "PING" or "ECHO" or "DBSIZE" or "INFO" or "TIME" or "LASTSAVE" or
            "COMMAND COUNT" or "COMMAND LIST" or "CLIENT LIST" or "MEMORY STATS" or
            "PUBSUB" or "PUBSUB CHANNELS" or "PUBSUB NUMPAT" or "PUBSUB NUMSUB" or "PUBSUB SHARDCHANNELS" or
            "PUBSUB SHARDNUMSUB" or "ROLE" or "SLOWLOG GET" or "SLOWLOG LEN" or "LATENCY LATEST" or "CONFIG GET" or
            "LATENCY DOCTOR" or "LATENCY HISTORY" or "LATENCY HISTOGRAM" or "MEMORY DOCTOR" or "MEMORY PURGE" or
            "ACL WHOAMI" or "ACL LIST" or "ACL GETUSER" or "ACL CAT" or "ACL LOG" or "ACL DRYRUN" or
            "COMMAND INFO" or "COMMAND DOCS" or "COMMAND GETKEYS" or "MODULE LIST" or
            "CLUSTER INFO" or "CLUSTER NODES" or "CLUSTER SHARDS" or "CLUSTER LINKS" or "CLUSTER MYID" or
            "CLUSTER MYSHARDID" or "CLUSTER KEYSLOT" or "CLUSTER COUNTKEYSINSLOT" or "CLUSTER SLOT-STATS" or
            "COMMANDLOG GET" or "COMMANDLOG LEN" or
            "HOTKEYS GET" or "HOTKEYS START" or "HOTKEYS STOP" or "HOTKEYS RESET";

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

    private static bool IsSingleKeyMutation(string operation)
        => operation is
            "SET" or "DELEX" or "DELIFEQ" or "RESTORE" or "GETDEL" or "GETEX" or "APPEND" or "SETRANGE" or
            "BF.RESERVE" or "BF.ADD" or "BF.MADD" or "BF.INSERT" or "BF.LOADCHUNK" or
            "CF.RESERVE" or "CF.ADD" or "CF.ADDNX" or "CF.INSERT" or "CF.INSERTNX" or "CF.DEL" or "CF.LOADCHUNK" or
            "CMS.INITBYDIM" or "CMS.INITBYPROB" or "CMS.INCRBY" or "CMS.MERGE" or
            "TOPK.RESERVE" or "TOPK.ADD" or "TOPK.INCRBY" or
            "TDIGEST.CREATE" or "TDIGEST.RESET" or "TDIGEST.ADD" or "TDIGEST.MERGE" or
            "JSON.SET" or "JSON.DEL" or "JSON.FORGET" or "JSON.CLEAR" or "JSON.ARRAPPEND" or "JSON.ARRINSERT" or
            "JSON.ARRPOP" or "JSON.ARRTRIM" or "JSON.MERGE" or "JSON.NUMINCRBY" or "JSON.NUMMULTBY" or "JSON.NUMPOWBY" or "JSON.STRAPPEND" or "JSON.TOGGLE" or
            "INCR" or "INCRBY" or "INCRBYFLOAT" or "DECR" or "DECRBY" or
            "PEXPIRE" or "PEXPIREAT" or "PERSIST" or
            "HSET" or "HSETNX" or "HDEL" or "HINCRBY" or "HINCRBYFLOAT" or
            "HEXPIRE" or "HEXPIREAT" or "HPERSIST" or
            "LPUSH" or "RPUSH" or "LPUSHX" or "RPUSHX" or "LPOP" or "RPOP" or "LREM" or "LTRIM" or "LSET" or "LINSERT" or
            "SADD" or "SREM" or "SPOP" or
            "ZADD" or "ZINCRBY" or "ZREM" or "ZREMRANGEBYRANK" or "ZREMRANGEBYSCORE" or "ZREMRANGEBYLEX" or
            "XADD" or "XACK" or "XDEL" or "XTRIM" or "XSETID" or
            "XGROUP CREATE" or "XGROUP DESTROY" or "XGROUP CREATECONSUMER" or
            "SETBIT" or "BITFIELD" or "PFADD" or "GEOADD" or "VADD" or "VREM" or "VSETATTR";

    private static bool IsMultiKeyMutation(string operation)
        => operation is "MSET" or "MSETNX" or "MSETEX" or "DEL" or "UNLINK" or "JSON.MSET";

    internal readonly record struct ReadToken(
        InflightRead State,
        long Generation,
        long ContinuityEpoch,
        CacheStore Store);

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

    internal sealed class CacheStore
    {
        private readonly ConcurrentDictionary<RespireKey, CacheEntry> _entries = new();
        private readonly ConcurrentDictionary<ClientCacheCommandKey, QueryCacheEntry> _queries = new();
        private readonly Dictionary<RespireKey, HashSet<ClientCacheCommandKey>> _dependencies = new();
        private readonly Lock _dependencyLock = new();
        private readonly RespireClientSideCacheOptions _options;
        private readonly Action _recordEviction;
        private int _trimming;
        private long _sizeBytes;

        public CacheStore(RespireClientSideCacheOptions options, Action recordEviction)
        {
            _options = options;
            _recordEviction = recordEviction;
        }

        public int Count => _entries.Count + _queries.Count;
        public long SizeBytes => Interlocked.Read(ref _sizeBytes);

        public bool TryGet(in RespireKey key, out byte[]? payload)
        {
            while (_entries.TryGetValue(key, out var entry))
            {
                if (entry.ExpiresAt == 0 || Stopwatch.GetTimestamp() < entry.ExpiresAt)
                {
                    payload = entry.Payload;
                    return true;
                }

                if (Remove(in key, entry, CacheRemoval.Expiration))
                {
                    break;
                }
            }

            payload = null;
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

        public void Set(RespireKey key, in RespValue response)
        {
            if (!response.IsNull && response.Type is not RespDataType.BulkString and not RespDataType.SimpleString)
            {
                return;
            }

            var payloadLength = response.IsNull ? 0 : response.AsSpan().Length;
            var size = (long)EntryOverhead + key.WireLength + payloadLength;
            if (size > _options.MaxSizeBytes)
            {
                return;
            }

            var payload = response.IsNull ? null : response.AsSpan().ToArray();
            var expiresAt = ExpirationTimestamp(_options.TimeToLive);
            var entry = new CacheEntry(payload, size, expiresAt);
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

            Trim();
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
                response.ToOwned(), dependencies, size, ExpirationTimestamp(_options.TimeToLive));
            return true;
        }

        public bool Set(in ClientCacheCommandKey query, QueryCacheEntry entry)
        {
            lock (_dependencyLock)
            {
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

        public void Remove(in RespireKey key, CacheRemoval reason)
        {
            if (_entries.TryRemove(key, out var entry))
            {
                RecordRemoval(entry, reason);
            }

            lock (_dependencyLock)
            {
                if (!_dependencies.Remove(key, out var queries))
                {
                    return;
                }

                foreach (var query in queries)
                {
                    if (_queries.TryRemove(query, out var queryEntry))
                    {
                        RemoveDependencies(in query, queryEntry.Dependencies);
                        RecordRemoval(queryEntry, reason);
                    }
                }
            }
        }

        private bool Remove(in RespireKey key, CacheEntry expected, CacheRemoval reason)
        {
            if (!((ICollection<KeyValuePair<RespireKey, CacheEntry>>)_entries)
                .Remove(new KeyValuePair<RespireKey, CacheEntry>(key, expected)))
            {
                return false;
            }

            RecordRemoval(expected, reason);
            return true;
        }

        private void RecordRemoval(CacheEntry entry, CacheRemoval reason)
        {
            Interlocked.Add(ref _sizeBytes, -entry.Size);
            if (reason is CacheRemoval.Capacity or CacheRemoval.Expiration)
            {
                _recordEviction();
            }
        }

        private bool Remove(
            in ClientCacheCommandKey query,
            QueryCacheEntry expected,
            CacheRemoval reason)
        {
            lock (_dependencyLock)
            {
                if (!((ICollection<KeyValuePair<ClientCacheCommandKey, QueryCacheEntry>>)_queries)
                    .Remove(new KeyValuePair<ClientCacheCommandKey, QueryCacheEntry>(query, expected)))
                {
                    return false;
                }

                RemoveDependencies(in query, expected.Dependencies);
                RecordRemoval(expected, reason);
                return true;
            }
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

        private void RecordRemoval(QueryCacheEntry entry, CacheRemoval reason)
        {
            Interlocked.Add(ref _sizeBytes, -entry.Size);
            if (reason is CacheRemoval.Capacity or CacheRemoval.Expiration)
            {
                _recordEviction();
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

    internal sealed class CacheEntry(byte[]? payload, long size, long expiresAt)
    {
        public byte[]? Payload { get; } = payload;
        public long Size { get; } = size;
        public long ExpiresAt { get; } = expiresAt;
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
        Invalidation,
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
