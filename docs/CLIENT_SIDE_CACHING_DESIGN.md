# Server-assisted client-side caching

This document records the implementation design for
[issue #297](https://github.com/thomhurst/Respire/issues/297).

## Public API

Caching is an opt-in connection policy. Existing deterministic read APIs become cached; no
parallel `GetCachedAsync` method family or wrapper client is required.

```csharp
await using var redis = await RespireClient.ConnectAsync(new RespireOptions
{
    Endpoints = { new("localhost") },
    ClientSideCache = new RespireClientSideCacheOptions
    {
        MaxEntries = 10_000,
        MaxSizeBytes = 64 * 1024 * 1024,
        TimeToLive = TimeSpan.FromMinutes(5),
    },
});

var first = await redis.Hashes.GetAllAsync("user:42"); // Redis
var second = await redis.Hashes.GetAllAsync("user:42"); // local command cache
```

`RespireOptions.ClientSideCache = null` is disabled. Enabling it promotes the validated option
snapshot to RESP3; failure to negotiate RESP3 or enable tracking fails connection setup.

Diagnostics and explicit local invalidation are exposed without making storage replaceable:

```csharp
IRespireClientSideCache? cache = redis.ClientSideCache;
RespireClientSideCacheStatistics statistics = cache!.GetStatistics();
cache.Clear();
```

`IRespireClientSideCache` reports hits, misses, invalidations, evictions, continuity flushes,
resident count, and approximate bytes. `Clear` advances the cache epoch, so an older read still
in flight cannot refill the new store.

## Eligibility and value representation

Respire caches every keyed Redis 8.10 read that meets Redis client-side-cache eligibility and for
which it can prove the full dependency set:

- strings: `GET`, `MGET`, `STRLEN`, `GETRANGE`, `SUBSTR`, `DIGEST`, `LCS`;
- keys: `EXISTS`, `EXPIRETIME`, `PEXPIRETIME`, `TYPE`, `OBJECT ENCODING`, exact
  `MEMORY USAGE ... SAMPLES 0`,
  and self-contained `SORT_RO` calls without `BY` or `GET` key patterns;
- hashes: `HGET`, `HMGET`, `HGETALL`, `HEXISTS`, `HLEN`, `HSTRLEN`, `HKEYS`, `HVALS`,
  `HEXPIRETIME`, `HPEXPIRETIME`;
- lists: `LINDEX`, `LLEN`, `LPOS`, `LRANGE`;
- sets: `SCARD`, `SDIFF`, `SINTER`, `SINTERCARD`, `SISMEMBER`, `SMEMBERS`, `SMISMEMBER`,
  `SUNION`, `SDIFFCARD`, `SUNIONCARD`;
- sorted sets: `ZCARD`, `ZCOUNT`, `ZDIFF`, `ZINTER`, `ZINTERCARD`, `ZLEXCOUNT`, `ZMSCORE`,
  `ZRANGE`, the legacy range aliases, `ZRANK`, `ZREVRANK`, `ZSCORE`, `ZUNION`;
- streams: `XLEN`, `XRANGE`, `XREVRANGE`, summary-form `XPENDING`, `XINFO STREAM`,
  `XINFO GROUPS`;
- bitmaps: `GETBIT`, `BITCOUNT`, `BITPOS`, `BITFIELD_RO`;
- geospatial: `GEODIST`, `GEOHASH`, `GEOPOS`, `GEOSEARCH`, `GEORADIUS_RO`,
  `GEORADIUSBYMEMBER_RO`;
- arrays: all read-only commands, including `ARSCAN`;
- JSON: `JSON.ARRINDEX`, `JSON.ARRLEN`, `JSON.GET`, `JSON.MGET`, `JSON.OBJKEYS`,
  `JSON.OBJLEN`, `JSON.RESP`, `JSON.STRLEN`, `JSON.TYPE`;
- vector sets: every deterministic read (`VCARD`, `VDIM`, `VEMB`, `VGETATTR`, `VINFO`,
  `VISMEMBER`, `VLINKS`, `VRANGE`, `VSIM`).

Typed APIs, catalog `ExecuteAsync`, interpolated commands, and `GetLeaseAsync` use the same policy.
Typed `GET` and `MGET` retain optimized per-key storage: one entry serves every typed GET
shape and typed MGET fetches only misses. Raw GET/MGET retain exact-query identities by default; coalescing uses the typed per-key entries.
With `ReuseHashFields = true`, immediate HMGET (typed, catalog, string, or interpolated) uses HGET identities for individual
fields and sends only misses in one HMGET. Cached nulls, field order, and duplicates are
preserved; raw arguments support binary fields. The entire returned array is validated before
any field is published. Each field remains dependent on the physical hash key, so invalidation
removes all field projections and existing epochs reject stale in-flight insertion. Hash and
string multi-read hit/miss statistics count each field/key lookup. These cached compositions
can contain values read at different times; they are not atomic server snapshots.
The option defaults to false because per-field entries and independently owned responses cost
more allocations and CPU than an exact-query entry for small responses. The default HMGET path
and other replies are stored by exact command invocation, including command name and ordered
wire-equivalent arguments. With both options enabled, an identical physical hash key and ordered
missing-field list share one HMGET producer. Each waiter merges the owned reply into its own
cached values and output indexes. Different missing lists proceed independently. The existing
coordinator detaches canceled waiters and ends joining on invalidation or continuity loss.
Hashes outside broadcast coverage use exact-query handling without per-field lookup overhead.

Commands marked with nondeterministic output (`DUMP`, relative TTL, the core cursor scans), random
commands, probabilistic structures, blocking reads, scripts/functions, time series, Search,
unkeyed server state, and `TOUCH` are deliberately excluded. `SORT_RO` calls using `BY` or `GET`
patterns are excluded because those patterns create dependencies the client cannot enumerate.
`GEOSEARCH` with `COUNT ... ANY` is excluded because Redis may return an arbitrary early subset.
Sampled `MEMORY USAGE` is excluded because Redis estimates aggregate sizes; `SAMPLES 0` requests
an exact traversal and remains cacheable.
Detailed `XPENDING` is excluded because its idle-duration field changes with time. Batches and
transactions preserve their server execution semantics and do not consult the local cache.
Commands that would break cache coherence by changing protocol, database, or tracking state
(`HELLO`, `RESET`, `SELECT`, `CLIENT CACHING`, and `CLIENT TRACKING`) are rejected while caching is enabled.

Cached entries hold immutable, deep-owned RESP values rather than deserialized objects. Therefore:

- serializer behavior remains identical on a hit;
- mutable objects are never shared between callers;
- `GetBytesAsync` still returns caller-owned arrays;
- scalar, null, array, map, set, and nested replies can all be cached safely.

One cache belongs to `ClientCore`. The root client and all key-prefixed views share it. Redis key
dependencies and command arguments use exact resolved wire identity, preserving text/binary
equivalence, argument order, and prefixes. Caller-owned binary arguments are snapshotted before an
asynchronous miss is sent.

## RESP3 tracking protocol

By default, every multiplexed command connection performs:

```text
HELLO 3
CLIENT TRACKING ON OPTIN
```

On a miss, Respire appends the opt-in prelude and read atomically under one write gate:

```text
CLIENT CACHING YES
<cacheable read and arguments>
```

Both in-flight response slots are reserved before either frame is written. A pooled multi-reply
source validates the prelude, drains both replies, and returns only the read response. No other
producer can interleave a command between `CLIENT CACHING YES` and its read.

For an `ASK` redirect, the retry is intentionally not cached. Both `ASKING` and
`CLIENT CACHING YES` apply to the next command, so they cannot safely prefix the same read.
Respire appends only:

```text
ASKING
<cacheable read and arguments>
```

`MOVED` retries remain cacheable on the authoritative node. Both `MOVED` and `ASK` advance the
continuity epoch before retrying. Every discovered cluster node is created with the same RESP3
push handler and tracking handshake.

`OPTIN` limits Redis tracking memory and push traffic to actual cache misses. RESP3 invalidations
remain on the command connection that performed the read, preserving server wire order without a
redirected invalidation connection.

`RespireClientSideCacheOptions.TrackingMode = Broadcast` instead configures
`CLIENT TRACKING ON BCAST [PREFIX ...]` on every cache-bearing connection. Its reads have no
`CLIENT CACHING YES` prelude. The options snapshot owns nonoverlapping literal physical
`BroadcastPrefixes`; an empty list covers all keys. Prefixes are not transformed by client
key-prefix views. Per-key insertion requires that key to be covered, while a command projection
requires every dependency to be covered. Uncovered reads still execute but cannot create cache
entries. These insertion guards also cover mixed covered/uncovered MGET misses.

Broadcast retains the same invalidation generations, query epochs, local mutation fences,
response ownership, and continuity flushes. ASK retries remain conservatively uncached because
the temporary owner is not an authoritative source of future invalidations. Every replacement
and discovered command node retains its mode and prefixes. Dedicated operations, batches, and
transactions retain their existing uncached behavior.

## Race correctness

Each optimized `GET`/`MGET` key has an in-flight generation. General command entries capture a
global query epoch, every explicit Redis key dependency, the continuity epoch, and active store
identity. Invalidation increments the relevant key generation and query epoch before removing all
projections that depend on that key. Publication and invalidation are serialized, closing the
final check/insert race.

For the optimized per-key path, a response may insert only when:

```text
current key generation == captured generation
current continuity epoch == captured epoch
current active store == captured store
```

Thus an invalidation that races a response can never be undone by stale insertion. Cancellation,
timeout, protocol failure, redirect, and conversion failure release the in-flight token without
publishing a value.

Local mutations invalidate their written keys and projections before sending and after reply
completion. The generated command catalog classifies keyspace effects; `RawCommandKeyLayouts`
combines that metadata with argument layouts and destination-only overrides. Known raw writes
use those same layouts. Set-store and merge commands retain cached source values, while an
explicit `MultiKey` declaration still fences every declared key. Unknown effects, unproven
dependencies, blocking commands, cluster-wide mutations, batches, and transactions swap out the entire
store before dispatch and again when their awaited execution finishes. Completion fences also run
on error and cancellation. Redis pushes remain authoritative for mutations from other clients.

TimeSeries sample writes and deletes, including `TS.MADD`, can change compaction destinations
absent from their arguments, so they retain full-cache fences. `TS.CREATERULE` and
`TS.DELETERULE` fence both named series. The layout classification test audits unknown and
indirect cases explicitly; cacheable-read eligibility remains separate from mutation effects.

## Continuity and failure behavior

The store is swapped and the epoch advanced whenever tracking continuity becomes uncertain:

- command connection close or reconnect;
- cluster node retirement or topology redirect;
- null/broadcast invalidation;
- explicit `Clear` or conservative unknown-command invalidation.

`RespireConnectionMultiplexer` observes receive-loop completion immediately. It reports the lost
slot and starts replacement even when every application read would otherwise be a local cache hit.
The replacement repeats `HELLO 3` and the configured OPTIN or BCAST/PREFIX tracking command
before publication.

Server-assisted caching cannot provide linearizability across an undetected network partition.
Until the operating system detects a half-open socket, a previously cached value can be returned.
Configure TCP keepalive and/or `ConnectionIdleReadTimeout` when the deployment needs a bounded
detection interval. Local TTL is an additional staleness bound, not a substitute for tracking.

## Bounded storage

Both `MaxEntries` and `MaxSizeBytes` are hard policies; the first exceeded limit triggers
eviction. Approximate size includes deep RESP payloads, command arguments, dependency keys, and a
fixed per-entry estimate.
An individually oversized value is returned but not cached. The store uses:

- `ConcurrentDictionary` probes on the hit path;
- immutable payload arrays and deep-owned RESP aggregates;
- `Stopwatch.GetTimestamp()` for lazy monotonic TTL checks;
- no timer, linked-list mutation, queue growth, or global lock on hits;
- an O(1) epoch/store swap for full flushes.

Capacity eviction enumerates only after a limit is crossed. This keeps auxiliary eviction state
bounded even under repeated invalidate/reinsert churn.

Disposal swaps out the resident store before connections are released. Old stores remain reachable
only by already in-flight tokens and become collectible when those reads finish.

## Observability

`GetStatistics()` supplies cheap point-in-time process diagnostics. The `Respire` meter also emits:

| Instrument | Meaning |
| --- | --- |
| `respire.client_cache.hits` | reads served locally |
| `respire.client_cache.misses` | reads requiring Redis |
| `respire.client_cache.invalidations` | key or broadcast invalidations |
| `respire.client_cache.evictions` | capacity, TTL, and flush removals |
| `respire.client_cache.continuity_flushes` | flushes caused by connection/topology uncertainty |

When disabled, no coordinator, dictionary, payload, tracking handshake, push handler, or metric
recording exists. The ordinary command path pays only the predictable null coordinator check used
for mutation safety.

## Microsoft.Extensions integration

`Respire.Extensions.DependencyInjection` mirrors the core option and provides an idiomatic helper.
The action receives `RespireOptionsBuilder`, whose `Endpoints` collection supports `Add`.
The explicit parameter type selects this overload instead of the service-provider factory:

```csharp
builder.Services.AddRespire((RespireOptionsBuilder options) =>
{
    options.Endpoints.Add(new RespireEndpoint("redis.internal"));
    options.UseClientSideCaching();
});
```

`RespireDistributedCache` uses Lua reads to preserve Microsoft-compatible sliding-expiration
semantics, so its operations do not use the command cache. Configure client-side caching on a
separately registered `IRespireClient` used for direct deterministic reads. `HybridCache` remains
the preferred application-level object L1; Respire's cache stores protocol replies and follows
Redis invalidations.

## Test matrix

The implementation is covered by deterministic wire, concurrency, and Redis integration tests:

- tracked handshake and atomic prelude validation;
- scalar, negative, aggregate, lease, raw/catalog, structured-command, argument-identity, and
  partial-hit `MGET` and `HMGET` behavior, including shared `HGET` fields and malformed replies;
- single-key and multi-key projection invalidation, typed conversion, and local mutation;
- explicit exclusion of cursor, random, time-varying, probabilistic, and blocking reads;
- key and null invalidation pushes;
- invalidation racing a delayed read response;
- entry/byte bounds, TTL, oversized values, statistics, clear, and disposal;
- binary keys and prefixed views;
- external-client mutation and connection kill/reconnect against Redis;
- cluster `ASK` with `ASKING` immediately before the read, no caching prelude, and no cache insertion;
- disabled options and Microsoft dependency-injection configuration.
