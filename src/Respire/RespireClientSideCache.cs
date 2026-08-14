using System.Collections.Concurrent;
using System.Diagnostics;
using Respire.Internal;
using Respire.Protocol;

namespace Respire;

/// <summary>Bounds and expiration policy for RESP3 server-assisted client-side caching.</summary>
public sealed record RespireClientSideCacheOptions
{
    /// <summary>Maximum resident keys. Defaults to 10,000.</summary>
    public int MaxEntries { get; init; } = 10_000;

    /// <summary>Approximate maximum owned cache bytes. Defaults to 64 MiB.</summary>
    public long MaxSizeBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>
    /// Maximum local lifetime for an entry. Null relies only on Redis invalidations; the
    /// default five-minute bound limits staleness while a broken connection is being detected.
    /// </summary>
    public TimeSpan? TimeToLive { get; init; } = TimeSpan.FromMinutes(5);
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
}

internal sealed class ClientSideCacheCoordinator : IRespireClientSideCache
{
    private const int EntryOverhead = 64;

    private readonly RespireClientSideCacheOptions _options;
    private readonly ConcurrentDictionary<RespireKey, InflightRead> _inflight = new();
    private CacheStore _store;
    private long _continuityEpoch;
    private long _hits;
    private long _misses;
    private long _invalidations;
    private long _evictions;
    private long _continuityFlushes;

    public ClientSideCacheCoordinator(RespireClientSideCacheOptions options)
    {
        _options = options;
        _store = new CacheStore(options, RecordEviction);
    }

    public int Count => Volatile.Read(ref _store).Count;

    public long SizeBytes => Volatile.Read(ref _store).SizeBytes;

    public RespireClientSideCacheStatistics GetStatistics()
        => new(
            Interlocked.Read(ref _hits),
            Interlocked.Read(ref _misses),
            Interlocked.Read(ref _invalidations),
            Interlocked.Read(ref _evictions),
            Interlocked.Read(ref _continuityFlushes),
            Count,
            SizeBytes);

    public void Clear() => Flush(continuityLost: false);

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

    internal ReadToken BeginRead(in RespireKey key)
    {
        var ownedKey = key.Snapshot();
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
                    ((ICollection<KeyValuePair<RespireKey, InflightRead>>)_inflight)
                        .Remove(new KeyValuePair<RespireKey, InflightRead>(state.Key, state));
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

    internal void Invalidate(in RespireKey key)
    {
        if (_inflight.TryGetValue(key, out var state))
        {
            lock (state)
            {
                state.Generation++;
            }
        }

        Volatile.Read(ref _store).Remove(in key, CacheRemoval.Invalidation);
        Interlocked.Increment(ref _invalidations);
        RespireTelemetry.ClientCacheInvalidations.Add(1);
    }

    internal MutationFence BeforeCommand<TCommand>(string operation, in TCommand command)
        where TCommand : struct, IRespCommand
    {
        if (IsReadOnly(operation))
        {
            return default;
        }

        if (IsSingleKeyMutation(operation) && command.TryGetPrimaryKey(out var primaryKey))
        {
            var key = primaryKey.AsKey().Snapshot();
            Invalidate(in key);
            return new MutationFence(key, FlushAll: false);
        }

        Flush(continuityLost: false);
        return new MutationFence(Key: null, FlushAll: true);
    }

    internal void CompleteMutation(in MutationFence fence)
    {
        if (fence.Key is { } key)
        {
            Invalidate(in key);
        }
        else if (fence.FlushAll)
        {
            Flush(continuityLost: false);
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
            Flush(continuityLost: false);
            return;
        }

        foreach (ref readonly var value in keys.AsArray())
        {
            var key = new RespireKey(value.AsMemory());
            Invalidate(in key);
        }
    }

    internal void FlushForContinuityLoss() => Flush(continuityLost: true);

    internal void FlushForUnknownCommand() => Flush(continuityLost: false);

    private void Flush(bool continuityLost)
    {
        Interlocked.Increment(ref _continuityEpoch);
        var replacement = new CacheStore(_options, RecordEviction);
        var removed = Interlocked.Exchange(ref _store, replacement).Count;
        if (removed > 0)
        {
            Interlocked.Add(ref _evictions, removed);
            RespireTelemetry.ClientCacheEvictions.Add(removed);
        }

        if (continuityLost)
        {
            Interlocked.Increment(ref _continuityFlushes);
            RespireTelemetry.ClientCacheContinuityFlushes.Add(1);
        }
    }

    private void RecordEviction()
    {
        Interlocked.Increment(ref _evictions);
        RespireTelemetry.ClientCacheEvictions.Add(1);
    }

    private static bool IsReadOnly(string operation)
        => operation is
            "GET" or "MGET" or "STRLEN" or "GETRANGE" or "LCS" or
            "EXISTS" or "PTTL" or "TYPE" or "SCAN" or "KEYS" or "RANDOMKEY" or
            "HGET" or "HMGET" or "HGETALL" or "HEXISTS" or "HLEN" or "HKEYS" or "HVALS" or "HSCAN" or
            "LLEN" or "LRANGE" or "LINDEX" or
            "SISMEMBER" or "SMISMEMBER" or "SCARD" or "SMEMBERS" or "SRANDMEMBER" or "SSCAN" or
            "SINTER" or "SUNION" or "SDIFF" or "SINTERCARD" or
            "ZSCORE" or "ZMSCORE" or "ZCARD" or "ZCOUNT" or "ZRANK" or "ZREVRANK" or
            "ZRANGE" or "ZINTER" or "ZUNION" or "ZDIFF" or "ZINTERCARD" or "ZSCAN" or
            "XLEN" or "XRANGE" or "XREVRANGE" or "XPENDING" or
            "GETBIT" or "BITCOUNT" or "BITPOS" or "BITFIELD_RO" or
            "PFCOUNT" or "GEODIST" or "GEOHASH" or "GEOPOS" or "GEOSEARCH" or
            "PING" or "ECHO" or "DBSIZE" or "INFO" or "TIME" or "LASTSAVE" or
            "COMMAND COUNT" or "COMMAND LIST" or "CLIENT LIST" or "MEMORY USAGE" or "MEMORY STATS" or
            "PUBSUB" or "PUBSUB CHANNELS" or "PUBSUB NUMPAT" or "PUBSUB NUMSUB" or "PUBSUB SHARDCHANNELS" or
            "PUBSUB SHARDNUMSUB" or "ROLE" or "SLOWLOG GET" or "LATENCY LATEST" or "CONFIG GET";

    private static bool IsSingleKeyMutation(string operation)
        => operation is
            "SET" or "GETDEL" or "GETEX" or "APPEND" or "SETRANGE" or
            "INCR" or "INCRBY" or "INCRBYFLOAT" or "DECR" or "DECRBY" or
            "PEXPIRE" or "PEXPIREAT" or "PERSIST" or
            "HSET" or "HSETNX" or "HDEL" or "HINCRBY" or "HINCRBYFLOAT" or
            "HEXPIRE" or "HEXPIREAT" or "HPERSIST" or
            "LPUSH" or "RPUSH" or "LPOP" or "RPOP" or "LREM" or "LTRIM" or "LSET" or "LINSERT" or
            "SADD" or "SREM" or "SPOP" or
            "ZADD" or "ZINCRBY" or "ZREM" or "ZREMRANGEBYRANK" or "ZREMRANGEBYSCORE" or "ZREMRANGEBYLEX" or
            "XADD" or "XACK" or "XDEL" or "XTRIM" or "XGROUP CREATE" or "XGROUP DESTROY" or
            "SETBIT" or "BITFIELD" or "PFADD" or "GEOADD";

    internal readonly record struct ReadToken(
        InflightRead State,
        long Generation,
        long ContinuityEpoch,
        CacheStore Store);

    internal sealed class InflightRead(RespireKey key)
    {
        public RespireKey Key { get; } = key;
        public long Generation;
        public int Readers;
        public bool Retired;
    }

    internal sealed class CacheStore
    {
        private readonly ConcurrentDictionary<RespireKey, CacheEntry> _entries = new();
        private readonly RespireClientSideCacheOptions _options;
        private readonly Action _recordEviction;
        private int _trimming;
        private long _sizeBytes;

        public CacheStore(RespireClientSideCacheOptions options, Action recordEviction)
        {
            _options = options;
            _recordEviction = recordEviction;
        }

        public int Count => _entries.Count;
        public long SizeBytes => Interlocked.Read(ref _sizeBytes);

        public bool TryGet(in RespireKey key, out byte[]? payload)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                payload = null;
                return false;
            }

            if (entry.ExpiresAt != 0 && Stopwatch.GetTimestamp() >= entry.ExpiresAt)
            {
                Remove(in key, CacheRemoval.Expiration);
                payload = null;
                return false;
            }

            payload = entry.Payload;
            return true;
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
                    Interlocked.Add(ref _sizeBytes, size - previous.Size);
                }
            }
            else if (_entries.TryAdd(key, entry))
            {
                Interlocked.Add(ref _sizeBytes, size);
            }

            Trim();
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
            if (!_entries.TryRemove(key, out var entry))
            {
                return;
            }

            Interlocked.Add(ref _sizeBytes, -entry.Size);
            if (reason is CacheRemoval.Capacity or CacheRemoval.Expiration)
            {
                _recordEviction();
            }
        }

        private void Trim()
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
                    Remove(in key, CacheRemoval.Capacity);
                    removed = true;
                    if (!IsOverCapacity)
                    {
                        break;
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

    internal enum CacheRemoval
    {
        Invalidation,
        Capacity,
        Expiration,
    }

    internal readonly record struct MutationFence(RespireKey? Key, bool FlushAll)
    {
        internal bool IsRequired => Key is not null || FlushAll;
    }
}
