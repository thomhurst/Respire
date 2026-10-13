---
title: Client-side caching
description: Enable bounded RESP3 server-assisted caching for Redis reads.
---

# Client-side caching

Respire can cache eligible Redis reads in-process while Redis invalidation pushes keep entries
coherent across clients. A cache hit skips command encoding, socket I/O, Redis execution, reply
parsing, and network latency. Your code keeps using the same typed API.

This is Redis's server-assisted cache (`CLIENT TRACKING`), not a second application caching
abstraction. Redis tracks the keys Respire reads and pushes an invalidation when one of them
changes. Respire evicts the entry, and the next read fetches the new value. Redis never pushes
replacement values.

Application [keyspace notifications](../guides/keyspace-notifications.md) are a separate Pub/Sub
feature. They do not configure or replace this cache.

## Enable it

Enable caching once on the client:

```csharp
await using var redis = await RespireClient.ConnectAsync(new RespireOptions
{
    Endpoints = { new RespireEndpoint("localhost") },
    ClientSideCache = new(),
});
```

Typed APIs, catalog `ExecuteAsync` calls, interpolated commands, and `GetLeaseAsync` then use the
cache transparently. This covers deterministic keyed reads across strings, keys, hashes, lists,
sets, sorted sets, streams, bitmaps, geospatial indexes, Redis arrays, JSON, and vector sets (see
[Cached commands](#cached-commands)). Typed `GET` and `MGET` keep per-key entries, so an `MGET`
can combine local hits with one Redis request for the misses. Other replies are cached by their
exact command and arguments, including argument order and binary arguments. To let `HMGET` reuse
individual `HGET` field entries, see [Partial hash reads](#partial-hash-reads).

Missing keys are cached too. Cached replies are immutable and converted for each call, so caching
does not introduce shared mutable objects. Serializers run on every hit, and `GetBytesAsync` still
returns a caller-owned array. One cache belongs to each client and is shared by all of its
`WithKeyPrefix` views.

String reads keep their decoded text in the cache entry, so a later `GetStringAsync` hit returns
the same string without decoding again. That text counts against `MaxSizeBytes`, so string reads
use more cache memory than byte-only reads. An entry that would exceed the limit is returned
without being cached.

The cache requires RESP3. Connection setup fails if Redis cannot negotiate RESP3 or enable
`CLIENT TRACKING`. With `ClientSideCache = null` (the default) no cache, tracking handshake, or
invalidation handler is created.

## Cached commands

A read is cached only when Redis marks it eligible for client-side caching and Respire can name
every key it depends on:

| Group | Cached reads |
|---|---|
| Strings | `GET`, `MGET`, `STRLEN`, `GETRANGE`, `SUBSTR`, `DIGEST`, `LCS` |
| Keys | `EXISTS`, `EXPIRETIME`, `PEXPIRETIME`, `TYPE`, `OBJECT ENCODING`, `MEMORY USAGE ... SAMPLES 0`, `SORT_RO` without `BY` or `GET` patterns |
| Hashes | `HGET`, `HMGET`, `HGETALL`, `HEXISTS`, `HLEN`, `HSTRLEN`, `HKEYS`, `HVALS`, `HEXPIRETIME`, `HPEXPIRETIME` |
| Lists | `LINDEX`, `LLEN`, `LPOS`, `LRANGE` |
| Sets | `SCARD`, `SDIFF`, `SINTER`, `SINTERCARD`, `SISMEMBER`, `SMEMBERS`, `SMISMEMBER`, `SUNION`, `SDIFFCARD`, `SUNIONCARD` |
| Sorted sets | `ZCARD`, `ZCOUNT`, `ZDIFF`, `ZINTER`, `ZINTERCARD`, `ZLEXCOUNT`, `ZMSCORE`, `ZRANGE` and the legacy range aliases, `ZRANK`, `ZREVRANK`, `ZSCORE`, `ZUNION` |
| Streams | `XLEN`, `XRANGE`, `XREVRANGE`, summary-form `XPENDING`, `XINFO STREAM`, `XINFO GROUPS` |
| Bitmaps | `GETBIT`, `BITCOUNT`, `BITPOS`, `BITFIELD_RO` |
| Geospatial | `GEODIST`, `GEOHASH`, `GEOPOS`, `GEOSEARCH` (without `COUNT ... ANY`), `GEORADIUS_RO`, `GEORADIUSBYMEMBER_RO` |
| Arrays | every read-only command, including `ARSCAN` |
| JSON | `JSON.ARRINDEX`, `JSON.ARRLEN`, `JSON.GET`, `JSON.MGET`, `JSON.OBJKEYS`, `JSON.OBJLEN`, `JSON.RESP`, `JSON.STRLEN`, `JSON.TYPE` |
| Vector sets | `VCARD`, `VDIM`, `VEMB`, `VGETATTR`, `VINFO`, `VISMEMBER`, `VLINKS`, `VRANGE`, `VSIM` |

Everything else bypasses the cache and goes to Redis:

- Time-varying or nondeterministic replies: `DUMP`, relative TTLs such as `TTL` and `PTTL`, the
  core cursor scans (`SCAN`, `HSCAN`, `SSCAN`, `ZSCAN`), random commands, and detailed `XPENDING`,
  whose idle times change.
- `SORT_RO` with `BY` or `GET` patterns, whose dependencies cannot be enumerated; `GEOSEARCH` with
  `COUNT ... ANY`, which may return an arbitrary early subset; and sampled `MEMORY USAGE`, which is
  an estimate.
- `TOUCH`, probabilistic structures, blocking reads, scripts and functions, time series, Search, and
  unkeyed server state.
- Batches and transactions, which keep their server execution semantics and never consult the
  local cache.

While caching is enabled, `HELLO`, `RESET`, `SELECT`, `CLIENT CACHING`, and `CLIENT TRACKING`
throw `NotSupportedException`, because changing the protocol, database, or tracking state would
break coherence.

Writes evict the keys they change before they are sent, and reads that overlap a write are not
cached. When Respire cannot name the affected keys (unknown raw commands, scripts, cluster-wide
mutations, blocking commands, batches, transactions, and time-series writes that can update
compaction destinations), it flushes the whole local cache instead.

## Options

Every option has a bounded default, so `new()` is a complete configuration:

| Option | Default | Purpose |
|---|---|---|
| `MaxEntries` | `10_000` | Maximum resident entries. |
| `MaxSizeBytes` | 64 MiB | Approximate maximum bytes owned by cached replies. |
| `LocalExpiration` | 5 minutes | Maximum local lifetime of an entry, independent of the key's Redis TTL. `null` keeps entries until invalidated or evicted. |
| `KeyPrefixes` | empty (all keys) | Physical key prefixes eligible for caching. |
| `TrackingMode` | `OptIn` | How Redis tracks cached keys: per read (`OptIn`) or by prefix (`Broadcast`). |
| `CoalesceConcurrentMisses` | `false` | Share concurrent identical misses and `GetOrSetAsync` factories (stampede protection). |
| `ReuseHashFields` | `false` | Serve `HMGET` fields from cached `HGET` entries. |

<!-- doc-test-ignore: Object-initializer fragment for the RespireOptions.ClientSideCache property. -->
```csharp
ClientSideCache = new()
{
    KeyPrefixes = ["product:", "price:"],
    MaxEntries = 50_000,
    LocalExpiration = TimeSpan.FromMinutes(1),
    CoalesceConcurrentMisses = true,
},
```

Invalid values throw `RespireConfigurationException` when the client is created: `MaxEntries` and
`MaxSizeBytes` must be at least one, and `LocalExpiration` must be `null` or at least one
millisecond.

## Choose what gets cached

By default every eligible read is cached. Set `KeyPrefixes` to cache only the keys that benefit:
hot, read-mostly data such as catalogs or configuration. Reads of other keys go straight to Redis
and never compete for cache capacity. In `OptIn` mode they are also sent without
`CLIENT CACHING YES`, so Redis does not track them.

There is one exception. A typed `MGET` (or a raw `MGET` with `CoalesceConcurrentMisses = true`)
that mixes covered and uncovered keys sends its misses as one tracked command, which keeps `MGET`
atomic. Redis then tracks every key it reads, so the uncovered keys can generate invalidation
pushes, although they never enter the local cache. Read uncovered keys in a separate `MGET` to
avoid this. With the default `CoalesceConcurrentMisses = false`, a raw `ExecuteAsync(MGET, ...)`
is cached as one exact query instead: if any key is uncovered, the whole reply is uncached and the
command is sent without `CLIENT CACHING YES`. In `Broadcast` mode the same prefixes are also sent
to Redis.

Prefix rules:

- Prefixes are literal bytes, not Redis glob patterns; `*` and `?` match only themselves. Pass
  binary prefixes as `RespireKey` values. The options snapshot both the list and its bytes.
- Prefixes match **physical wire keys**. A `WithKeyPrefix("tenant:")` view does not add its prefix,
  so `KeyPrefixes = ["tenant:products:"]` covers `products:42` read through that view, but not an
  unprefixed `products:42` call.
- Duplicate and overlapping prefixes are rejected before connecting. One empty prefix covers
  everything and therefore cannot be combined with another prefix.
- A cached multi-key read requires **every** key it depends on to be covered. Hash fields inherit
  their hash key's coverage. Invalidation subscriptions require a covered key.

## Bypass the cache for one read

`WithoutClientCache()` returns a view whose reads always go to Redis. Use it when a code path
must observe the latest server value, for example immediately before a conditional write:

```csharp
var fresh = redis.WithoutClientCache();
var stock = await fresh.Strings.GetAsync<int>("product:42:stock");
```

The view shares the client's connections and keeps its key prefix and read routing. Writes
through the view still invalidate cached entries, and its reads never populate the cache.
`GetOrSetAsync` on the view reads Redis directly and never shares factories. When client-side
caching is disabled, `WithoutClientCache()` returns the same client.

## Concurrent misses

Set `ClientSideCache.CoalesceConcurrentMisses = true` to share concurrent misses for the same
command and byte-for-byte arguments within a client. Sharing is off by default. It covers typed
`GET` variants, identical ordered `MGET` miss lists, and all eligible deterministic query reads.
Typed and raw calls can join the same request; each caller still performs its own conversion and
receives independently owned results and leases. With sharing enabled, typed and raw `GET`/`MGET`
use the same per-key entries, so either can populate later hits for both.

Prefix views use resolved wire keys. Separate clients, databases, and Cluster routing contexts
never share work. `GET` and `MGET` are different identities, and partially overlapping `MGET`
lists are not split into individual `GET` requests.

Cancellation and failures:

- Canceling one caller does not cancel the others. When the last caller cancels, Respire cancels
  the shared request. A command Redis has already accepted may still execute.
- Each caller's cancellation token bounds only that caller's wait. The shared request keeps its
  original `CommandTimeout` deadline; joining it does not restart that deadline.
- Server and transport failures reach every remaining caller, and a later call can retry. A
  shared request is never replayed after a transport failure.
- Client disposal cancels all shared work.

Invalidations, `Clear()`, and connection continuity changes stop new callers from joining older
requests. Callers already waiting may receive the original result, but it is not inserted into the
cache. An invalidation currently stops joining for all pending requests, including those for
unrelated keys. Hit and miss counters count callers, not wire requests. The observable counter
`respire.client_cache.shared_read.retirements` counts pending requests that new callers could no
longer join because of invalidation, clearing, or continuity loss; use it to see how often churn
defeats sharing.

Sharing adds bookkeeping and result copies on every miss. Keep the default for workloads with
little contention. Cache hits are not affected.

## Partial hash reads

Set `ClientSideCache = new() { ReuseHashFields = true }` to enable partial hash reads. The default
is `false`, which caches each `HMGET` as one exact query. When enabled, `Hashes.GetManyAsync` and
raw `HMGET` calls look up each field using the same cache entry as `HGET`. Cached fields are
returned locally, and all missing fields are sent in one `HMGET`. Results keep the requested order,
duplicate fields, and nulls for absent hashes or fields. An all-hit request sends no command.
Typed calls apply `WithKeyPrefix`; raw commands must pass physical keys.

Each cached field depends on its hash key. A Redis invalidation or local write of the hash evicts
every cached field of that hash. A malformed reply is rejected before any field is cached.

With both `ReuseHashFields` and `CoalesceConcurrentMisses` enabled, concurrent requests for the
same hash with identical ordered missing fields share one `HMGET`. Different missing lists run
independently: `[a, b]` and `[b, a]` do not share a request. Hashes outside `KeyPrefixes` bypass
per-field reuse.

Hit and miss statistics count field lookups, including repeated fields. As with a cached `MGET`, a
result may combine values cached at different times; use an uncached transaction when you need an
atomic server snapshot.

Field reuse trades extra entries, lookup work, and allocations for fewer transferred values on
overlapping field lists. Small local responses can be slower in this mode, even with partial hits.
Benchmark your payload sizes, field overlap, and network conditions before enabling it.

## Populate missing values with a factory

`GetOrSetAsync<T>(key, factory, ttl, cancellationToken)` combines a tracked read with a
conditional cache-aside write. It requires `ClientSideCache` and Redis 7.0 or later for
[`SET NX GET`](https://redis.io/docs/latest/commands/set/). Hits use the normal local cache,
serialization, and key-prefix rules.

The helper does not check server capabilities first. On an unsupported server or proxy, a miss can
run the factory before the conditional write fails with the server error. Keep factories safe to
run without a subsequent write. `IRespireClient` implementations other than `RespireClient` throw
`NotSupportedException` unless they implement or forward the method.

<!-- doc-test-tail-declaration: split-before=public sealed record Product -->
```csharp
using Respire;

await using var client = await RespireClient.ConnectAsync(new RespireOptions
{
    Endpoints = [new("localhost", 6379)],
    ClientSideCache = new() { CoalesceConcurrentMisses = true },
});

var product = await client.GetOrSetAsync<Product>(
    "product:42", LoadProductAsync, TimeSpan.FromMinutes(5));

static ValueTask<Product?> LoadProductAsync(CancellationToken cancellationToken)
{
    cancellationToken.ThrowIfCancellationRequested();
    return ValueTask.FromResult<Product?>(new Product(42, "Coffee"));
}

public sealed record Product(int Id, string Name);
```

On a miss, the factory runs and `SET NX GET` stores the computed value only if the key is still
absent; otherwise it returns the value another writer stored. An existing winner keeps its TTL.
If the key was created and deleted again before the `SET` runs, the computed value is stored.
Another writer can change the key immediately afterward. There is no distributed lock or
exactly-once guarantee for the factory.

The TTL must be at least one millisecond, is truncated to whole milliseconds, and starts at the
successful server write. Hits and failed conditional writes do not extend it. `LocalExpiration`
is configured separately.

A factory that returns `null` does not write, and a later call can run it again. A stored `0`,
empty string, or serialized JSON `null` counts as present and does not run the factory. Each caller
receives its own deserialized result, never the factory's object. Use the same serialization for
every writer of a key.

`CoalesceConcurrentMisses` also controls factory sharing. With it enabled, calls on the same
client with the same physical key, generic type, and millisecond TTL share the first factory, so
different callbacks with that identity must be interchangeable. Prefix views share their root
client's factories; different clients do not. An invalidation stops new callers from joining, so a
later caller can start another factory while an earlier one is still running. With coalescing
disabled, each missed call runs its own factory. A factory must not await `GetOrSetAsync` for the
same key, type, and TTL.

Each cancellation token bounds that caller's wait. With sharing enabled, the factory's token is
canceled only when the last caller leaves or the client is disposed. Without sharing, the factory
receives the caller's token. A result that arrives after cancellation is not written, but a write
Redis has already accepted may still execute. `CommandTimeout` bounds Redis operations, not your
factory; pass a cancellation token with a timeout when you need one. Factory, serialization, and
Redis errors propagate without retrying the factory or replaying the write.

The write invalidates other tracking clients as usual. Its result is not inserted into the local
cache, because it is not a tracked read; the next `GET` registers tracking and populates the
cache.

## Why this is different

StackExchange.Redis 3.x supports RESP3 and exposes keyspace notifications, but it has no built-in
server-assisted local response cache (its 4.0 previews add an opt-in one). Its
[keyspace-notification documentation](https://stackexchange.github.io/StackExchange.Redis/KeyspaceNotifications.html)
presents notifications as a building block for an application-defined invalidation strategy.
That approach requires Redis server configuration plus application-owned storage, subscription,
node coverage, bounds, command eligibility, and invalidation-race handling.

Respire uses Redis `CLIENT TRACKING` directly. No notification channel or global
`notify-keyspace-events` setting is required. Redis's own
[client-side caching support table](https://redis.io/docs/latest/develop/clients/client-side-caching/#which-client-libraries-support-client-side-caching)
does not currently list StackExchange.Redis and warns that exposing `CLIENT TRACKING` alone is not
the same as implementing a client cache.

## Measured impact

These results compare a local Respire cache hit with an ordinary StackExchange.Redis server read.
They show the value of avoiding a network round trip. They do not mean Respire's uncached path is
hundreds of times faster: uncached Respire and StackExchange.Redis reads were statistically
equivalent in the same net10 run.

| net10 operation | StackExchange.Redis server read | Respire client-cache hit | Hit latency |
| --- | ---: | ---: | ---: |
| `GET`, present | 186.5 μs | 151.5 ns | 0.081% |
| `GET`, missing | 185.9 μs | 129.5 ns | 0.070% |
| `HGET` | 186.8 μs | 466.5 ns | 0.250% |
| `EXISTS` | 185.6 μs | 387.3 ns | 0.209% |

BenchmarkDotNet used Redis 8.10, two launches, three warmups, and three measured iterations on a
GitHub-hosted Linux runner. See the
[official net8/net10 run](https://github.com/thomhurst/Respire/actions/runs/31848970849) and
[benchmark source](https://github.com/thomhurst/Respire/blob/main/benchmarks/Respire.ComparisonBenchmarks/ClientSideCachingBenchmarks.cs).

## Invalidation flow

```text
read miss → Redis response → local entry
key changes → RESP3 invalidation push → local eviction
next read → Redis response → refreshed local entry
```

In `OptIn` mode, Redis tracks only the misses Respire sends with `CLIENT CACHING YES`. Local writes
evict before they are sent. If tracking continuity is lost, Respire clears the affected cache state
instead of trusting entries whose invalidations may have been missed.

## Broadcast tracking

`OptIn` is the default. Choose `Broadcast` when you would rather receive invalidations for a known
keyspace than have Redis register each key you read:

```csharp
await using var redis = await RespireClient.ConnectAsync(new RespireOptions
{
    Endpoints = { new RespireEndpoint("localhost") },
    ClientSideCache = new()
    {
        TrackingMode = RespireClientTrackingMode.Broadcast,
        KeyPrefixes = ["tenant:products:", "tenant:prices:"],
    },
});
```

Each caching connection sends `CLIENT TRACKING ON BCAST PREFIX ...` with the configured
`KeyPrefixes` during setup and again on reconnect. Empty `KeyPrefixes` sends plain `BCAST`,
covering every key. Broadcast reads omit `CLIENT CACHING YES`. Coverage rules are the same as in
[Choose what gets cached](#choose-what-gets-cached).

RESP3 is still required. Standalone, Sentinel-discovered, and Redis/Valkey Cluster data
connections all use the selected mode. Blocking commands, batches, and transactions still bypass
the cache. To change the mode or prefixes, create a new client. `MaxInflightCommands` must allow
the commands Respire sends together for one cached read: one for non-Cluster `Broadcast`; two for
non-Cluster `OptIn` (`CLIENT CACHING YES` plus the read) or Cluster `Broadcast` (`ASKING` plus the
read); three for Cluster `OptIn`.

Broadcast mode avoids per-key tracking state on the server, but this client receives invalidations
for every matching write, even for keys it never read. More prefixes add server work, and broad
prefixes can increase network and eviction traffic. See
[CLIENT TRACKING](https://redis.io/docs/latest/commands/client-tracking/) and the
[broadcast reference](https://redis.io/docs/latest/develop/reference/client-side-caching/#broadcasting-mode).

## Observe invalidations

Use `SubscribeInvalidations` to wake a local coordinator when a physical key may need to be
read again. Subscribe **before** the first read so an invalidation cannot fall between that
read and registration:

<!-- doc-test-ignore: Coordination loop fragment; redis, cancellationToken, and ProcessCurrentState are application-owned. -->
```csharp
var wakeUps = System.Threading.Channels.Channel.CreateBounded<bool>(1);
using var observation = redis.ClientSideCache!.SubscribeInvalidations(
    "jobs:ready", _ => wakeUps.Writer.TryWrite(true), cancellationToken);

while (!cancellationToken.IsCancellationRequested)
{
    var state = await redis.GetStringAsync("jobs:ready", cancellationToken);
    ProcessCurrentState(state);
    await wakeUps.Reader.ReadAsync(cancellationToken);
}
```

The callback receives a `RespireClientCacheInvalidation` whose `Key` is the physical wire key. The
subscription copies binary keys, so later changes to the caller's buffer do not affect it. Prefix
views share the same cache: a view with `WithKeyPrefix("tenant:")` must subscribe to
`tenant:jobs:ready`, not `jobs:ready`.

Registration does not send a command, warm an entry, or register server tracking. In `OptIn` mode
you receive notifications only after an eligible cached read, so read again after each wake-up to
rearm tracking. A key outside the configured `KeyPrefixes` throws `ArgumentException`. The
subscription survives reconnects but cannot recover events lost during a disconnect.

`Reasons` is a `RespireClientCacheInvalidationReason` flags value combining `ServerInvalidation`,
`LocalMutation`, `ExplicitClear`, and `ContinuityLost`. Local writes can notify before they are
sent and again after they complete, even when the value did not change. Redis-wide invalidations,
`Clear()`, writes with unknown keys, and continuity loss wake every subscription. TTL expiry and
capacity eviction do not notify.

Delivery rules:

- Callbacks run on the ThreadPool, one at a time per subscription, never inline on the socket
  reader. The registration's `ExecutionContext` does not flow.
- A slow observer has at most one pending notification; reason flags are combined. Intermediate
  events, event counts, and ordering across subscriptions are not preserved.
- A throwing callback does not stop eviction or other observers. The subscription's
  `LastObserverException` keeps the most recent exception, and later callbacks still run. Catch and
  log inside the callback if you need every failure. Use synchronous callbacks; `async void`
  exceptions cannot be captured.
- A global flush schedules one callback per subscription, and registering or removing a
  subscription costs time proportional to the number of observed keys. Use long-lived
  subscriptions rather than registering per request, and bound how many you keep.

Dispose the returned `IRespireClientCacheInvalidationSubscription` or cancel its token to stop
delivery. Client disposal also stops every subscription. Disposal does not wait for a callback
that is already running, so a callback may safely dispose its subscription or client.

These signals mean **recheck current state**, not "a write occurred." Do not use them as a durable
event log, distributed lock, or correctness guarantee for cross-process coordination. For
correctness-critical coordination, combine authoritative Redis commands with cancellation and
periodic reconciliation; an undetected network partition can delay notifications indefinitely.

Custom `IRespireClientSideCache` implementations that do not override `SubscribeInvalidations`
throw `NotSupportedException`.

## Bounds

Tune entry count, approximate owned bytes, and local lifetime together:

<!-- doc-test-ignore: Object-initializer fragment for the RespireOptions.ClientSideCache property. -->
```csharp
ClientSideCache = new RespireClientSideCacheOptions
{
    MaxEntries = 25_000,
    MaxSizeBytes = 128L * 1024 * 1024,
    LocalExpiration = TimeSpan.FromMinutes(2),
},
```

Both limits are hard: whichever is reached first triggers eviction. The size estimate covers reply
payloads, command arguments, dependency keys, and a fixed per-entry overhead. An oversized response
is returned without being cached. `LocalExpiration` is checked lazily on lookup; no timer runs.
See [Cached commands](#cached-commands) for what bypasses the cache.

## ASP.NET Core registration

`Respire.DependencyInjection` adds `UseClientSideCaching` to its options builder. Pass a
`RespireClientSideCacheOptions` to override the defaults:

```csharp
builder.Services.AddRespire(options =>
{
    options.Endpoints.Add(new RespireEndpoint("redis.internal"));
    options.UseClientSideCaching();
});
```

`RespireDistributedCache` reads through Lua scripts to preserve Microsoft-compatible sliding
expiration, so its operations never use this cache. Enable client-side caching on a separately
registered `IRespireClient` used for direct reads. `HybridCache` remains the better fit for an
application-level object cache; Respire's cache stores Redis replies and follows Redis
invalidations.

## Diagnostics

```csharp
var cache = redis.ClientSideCache!;
var statistics = cache.GetStatistics();

Console.WriteLine($"{statistics.Hits} hits; {statistics.SizeBytes} bytes");
cache.Clear();
```

`GetStatistics()` returns a point-in-time `RespireClientSideCacheStatistics` with hits, misses,
invalidations, evictions, continuity flushes, resident entries (`Count`), and approximate bytes
(`SizeBytes`). `Clear()` also rejects older reads still in flight, so they cannot refill the cache.
`ClientSideCache` is `null` when caching is disabled.

The `Respire` OpenTelemetry meter emits:

| Instrument | Meaning |
|---|---|
| `redis.client.csc.requests` | Cache lookups; `redis.client.csc.result` is `hit` or `miss`. |
| `redis.client.csc.evictions` | Responses removed; `redis.client.csc.reason` is `full`, `ttl`, or `invalidation`. |
| `respire.client_cache.invalidations` | Key or broadcast invalidations received. |
| `respire.client_cache.continuity_flushes` | Flushes caused by connection or topology uncertainty. |
| `respire.client_cache.shared_read.retirements` | Shared misses new callers could no longer join (see [Concurrent misses](#concurrent-misses)). |

The `redis.client.csc.*` counters are recorded only when the `RespireMetricGroups.ClientSideCaching`
group is enabled with `RespireMetrics.Configure` (it is not in `RespireMetricGroups.Default`); see
[Observability](../integrations/observability.md). They carry `db.system.name=redis` and
`redis.client.library=Respire:<version>`. One invalidated key can remove several cached responses
or none. Local writes, `Clear()`, and continuity flushes do not increment
`redis.client.csc.evictions`, although `GetStatistics().Evictions` counts entries removed by
flushes.

## Consistency boundary

Respire rejects a stale reply when an invalidation races its insertion into the cache. A read
that depends on several keys, or on several fields of one hash, is rejected if any of them changes
while it is in flight; a write to an unrelated key does not affect it. Reads that start while a
local write is in flight are not cached, even if their replies arrive after the write completes.

Respire also flushes the cache after writes with unknown keys and on detected connection loss,
reconnect, Cluster `MOVED`/`ASK` redirects, and topology changes. In `OptIn` mode an `ASK` retry
sends `ASKING`, `CLIENT CACHING YES`, and the read together, so the migration target tracks the
key.

`LocalExpiration` is an additional staleness bound, not a substitute for tracking. Like every
server-assisted client cache, Respire cannot observe invalidations across an undetected network
partition. When bounded failure detection matters, configure TCP keepalive (`TcpKeepAliveTime`)
or `ConnectionIdleReadTimeout`, and keep a finite `LocalExpiration`.
