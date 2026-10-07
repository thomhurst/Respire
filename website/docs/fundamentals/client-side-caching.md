---
title: Client-side caching
description: Enable bounded RESP3 server-assisted caching for Redis reads.
---

# Client-side caching

Respire can cache eligible Redis reads in-process while Redis invalidation pushes keep entries
coherent across clients. Repeated reads skip command encoding, socket I/O, Redis execution, reply
parsing, and network latency while the application keeps using the same typed API.

This is Redis's server-assisted cache—not a second application caching abstraction. Redis tracks
the keys Respire reads, pushes only invalidations when those keys change, and Respire evicts them.
The next caller refreshes lazily; Redis never pushes replacement values.

Application [keyspace notifications](../guides/keyspace-notifications.md) are a separate Pub/Sub feature. They do not configure or replace this `CLIENT TRACKING` cache.

## Enable it

Enable caching once on the client:

```csharp
await using var redis = await RespireClient.ConnectAsync(new RespireOptions
{
    Endpoints = { new RespireEndpoint("localhost") },
    ClientSideCache = new(),
});
```

Existing typed APIs, catalog `ExecuteAsync` calls, interpolated commands, and `GetLeaseAsync` then
use the cache transparently. This covers deterministic keyed reads across strings, keys, hashes,
lists, sets, sorted sets, streams, bitmaps, geospatial indexes, Redis arrays, JSON, and vector sets
(see [Cached commands](#cached-commands)). Typed `GET` and `MGET` keep optimized
per-key entries and partial-hit behavior. Opting into `ReuseHashFields` lets `HMGET` reuse individual `HGET` field entries;
other replies use exact command-and-argument identities, including argument order and binary
arguments.

Missing keys are cached too. Replies are deep-owned internally and converted for each call, so
enabling caching does not introduce shared mutable objects; serializers run on every hit and
`GetBytesAsync` still returns a caller-owned array. One cache belongs to each client and is shared
by all of its `WithKeyPrefix` views.

Enabling the cache requires RESP3: connection setup fails if Redis cannot negotiate RESP3 or enable
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

While caching is enabled, Respire rejects `HELLO`, `RESET`, `SELECT`, `CLIENT CACHING`, and
`CLIENT TRACKING`, because changing protocol, database, or tracking state would break coherence.

Writes evict the keys they change before dispatch and again after completion, including on error
and cancellation. When Respire cannot name the affected keys (unknown raw commands, scripts,
cluster-wide mutations, blocking commands, batches, transactions, and time-series writes that can
update compaction destinations), it flushes the whole local cache instead.

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

## Choose what gets cached

By default every eligible read is cached. Set `KeyPrefixes` to cache only the keys that benefit:
hot, read-mostly data such as catalogs or configuration. Reads of other keys go straight to Redis
and never compete for cache capacity. In `OptIn` mode they are also sent without
`CLIENT CACHING YES`, so Redis does not track them, with one exception: the per-key `MGET`
path used by typed calls (and raw calls with `CoalesceConcurrentMisses = true`) sends mixed
covered/uncovered misses as one tracked command, so Redis tracks every key it reads.
Those uncovered keys can generate invalidation pushes but never enter the local cache.
Respire does not split the command, which keeps `MGET` atomic. Read uncovered keys in a separate
`MGET` to avoid their tracking. This works in both tracking modes; in `Broadcast` mode the same
prefixes are also sent to Redis. With the default `CoalesceConcurrentMisses = false`, raw
`ExecuteAsync(MGET, ...)` uses exact-query caching instead: if any key is uncovered, the whole
reply is uncached and the command is sent without `CLIENT CACHING YES`.

Prefixes are literal bytes, not Redis glob patterns; `*`, `?`, NUL, and non-UTF-8 bytes retain
their literal meaning. Pass binary prefixes as `RespireKey` values. Options snapshot both the list
and its storage. Prefixes identify **physical wire keys**: a `WithKeyPrefix("tenant:")` view does
not add its prefix, so `KeyPrefixes = ["tenant:products:"]` covers `products:42` read through
that view, but not an unprefixed `products:42` call. Duplicates and overlapping prefixes are
rejected before connecting; one empty prefix covers everything and therefore cannot accompany
another prefix.

Mixed per-key `MGET` calls retain covered hits and fetch misses together, caching only covered keys
(in `OptIn` mode that fetch is tracked as described above).
A cached multi-key projection requires **every** dependency to be covered. Hash fields inherit
their physical hash key's coverage. Invalidation subscriptions require a covered key.

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
command and byte-for-byte arguments within a client. Sharing is opt-in; the default is `false`. This covers typed `GET` variants, identical ordered `MGET` miss lists, and all
eligible deterministic query reads. Typed and raw calls can join the same wire command; each
caller still performs its own conversion and receives independently owned results and leases.
With sharing enabled, typed and raw `GET`/`MGET` use the same per-key entries, so either caller
can populate later hits for both APIs. Raw `MGET` also reuses the typed partial-hit path.
Binary keys and arguments are snapshotted. Prefix views use resolved wire keys; separate clients,
databases, and Cluster routing contexts never share work. `GET` and `MGET` are different identities,
and partially overlapping `MGET` lists are not split into individual `GET` requests.

Canceling one caller does not cancel callers still waiting for the shared request. When the last
caller cancels, Respire retires and cancels that request. An already accepted command may still
execute on Redis; its reply is drained in protocol order. The next caller can start a new request.
Each caller's cancellation token bounds only that caller's wait. The physical request keeps its
original `CommandTimeout` deadline; joining an existing request does not restart that deadline.
Client disposal cancels all shared work, including reads retired by an earlier invalidation.
Server and transport failures reach every remaining caller, retire the shared request, and allow
a later call to retry. Sharing never replays an accepted command after a transport failure.

Invalidations, explicit `Clear()`, and tracking continuity changes prevent new callers from joining
older work. Callers already waiting may receive their original read result, but the existing
invalidation fences reject stale cache insertion. Retirement is conservative: an invalidation
currently ends joining for all pending identities, even those with unrelated keys. Reads
started during an invalidation run independently and
cannot become a source for later callers. Overlapping invalidations keep that interval open
until every cache-state change finishes. Completed work
is always removed, including oversized responses and other replies that cannot enter the cache.
Cache hit/miss counters remain per caller, not per wire request. The process-wide observable
counter `respire.client_cache.shared_read.retirements` counts pending identities removed by invalidation,
clearing, or continuity loss. Normal completion and last-caller cancellation are excluded.
Use this counter to assess how often churn prevents new callers from joining existing work.

Sharing adds bookkeeping and owned-result copies on misses. Keep the default independent
requests for workloads with little contention. A sole remaining waiter can take the producer's
owned result; other waiters receive separate copies. Cache hits retain their existing fast path. The CI contention benchmark
compares default single-caller misses, opted-in single-caller misses, and opted-in 32-caller bursts
for `GET`, `MGET`, and `HGET`, plus hot `GET`, against both same-run baseline controls. Latency and allocations
include one complete burst and its local cache eviction. Process CPU counters include benchmark
warmup/calibration and background client work; they are diagnostic, not per-operation CPU samples.

## Partial hash reads

Set `ClientSideCache = new() { ReuseHashFields = true }` to enable partial hash reads.
The default is false and retains exact-query HMGET caching. When enabled, immediate
`Hashes.GetManyAsync` and raw `HMGET` calls look up each field using the same
cache identity as `HGET`. Cached fields are returned locally; all missing fields are sent
in one `HMGET`. Results retain requested order, duplicate fields, and nulls for absent hashes
or fields. An all-hit request sends no command. Binary fields are supported through raw
command arguments, which are snapshotted before asynchronous work. Typed facets resolve
`WithKeyPrefix`; raw commands continue to require physical keys explicitly.

The cache stores each field with a dependency on its hash key. Redis invalidations and
local hash writes evict every cached field of that hash. In-flight invalidation, clear,
and reconnect reject stale insertion. A malformed array or invalid field response rejects
the whole reply before any field is cached. Cluster `MOVED` and `ASK` recovery flush continuity
and re-establish tracked reads on the redirected node.

With both `ReuseHashFields` and `CoalesceConcurrentMisses` enabled, concurrent requests with the
same physical hash key and identical ordered missing fields share one HMGET producer. Full field
lists may differ when their cached fields differ. Each caller keeps its own cached values, output
order, and independently owned result. Different missing lists run independently; they are not
split into per-field requests. For example, missing lists `[a, b]` and `[b, a]` do not share
one producer: matching uses argument order, not set equality. Cancellation, invalidation, and continuity changes follow the
shared-read rules above. Hashes outside `KeyPrefixes` bypass per-field reuse.

Hit/miss statistics count field lookups, including repeated fields. As with cached MGET,
a result may combine values cached at different times; use an uncached transaction when
an atomic server snapshot is required. Batched/transactional commands retain their existing
execution behavior. Disabling client-side caching leaves the ordinary HMGET wire path unchanged.

Typed `Strings.GetManyAsync` retains its existing per-key partial-hit path and checks all
Cluster slots even on all-hit requests. Raw MGET uses exact-query caching by default and the typed per-key path when coalescing is enabled;
it does not split overlapping lists into individual GET requests.

Field reuse trades additional entries, lookup work, and owned-result allocations for fewer
transferred values on overlapping field lists. Small local Redis responses can be slower in
this mode, even with partial hits; it is not a universal optimization. Benchmark your payload
sizes, field overlap, and network conditions before enabling it.

The CI hash benchmark compares default and opted-in 0/4, 2/4, and 4/4 cached fields, with
the default path also checked against two same-run baseline controls. Each measured batch reads
512 independent hashes once. Cache clearing and field
priming happen outside the measured batch, so misses cannot turn into hits partway through
an iteration. Process CPU diagnostics include that priming and benchmark warmup/calibration.

## Populate missing values with a factory

`GetOrSetAsync<T>(key, factory, ttl, cancellationToken)` combines a tracked read with a
conditional cache-aside write. Enable `ClientSideCache`; Redis 7 or later is required for
[`SET NX GET`](https://redis.io/docs/latest/commands/set/). Existing hits use normal local
caching, serialization and key-prefix rules.

Support for `NX` and `GET` together starts in Redis 7.0; `GET` alone was introduced in 6.2.
The helper does not preflight server capabilities. On an unsupported server or proxy, a miss
can run the factory before the conditional write fails with the original server error.
Factory work and accepted writes are not replayed. Use this helper only on deployments
supporting that command combination, and keep factories safe to run without a subsequent write.
The default `IRespireClient` implementation throws `NotSupportedException`; third-party
implementations and decorators must implement or forward the helper explicitly.

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

On a miss, the factory runs once for that producer. `SET NX GET` atomically stores the
computed value only if the key is still absent, or returns the current writer's value.
It preserves an existing winner's TTL. It does not fence an intervening create/delete cycle:
if the key is absent again when SET runs, the computed value can be installed. Results describe
the read or atomic SET, and another writer can change the key immediately afterward.
There is no distributed lock or exactly-once factory guarantee.

The TTL must be at least one millisecond, is truncated to whole milliseconds, and begins at
the successful server write. Hits and failed conditional writes do not extend expiry. Local
cache lifetime is configured separately; server expiration is observed through normal tracking
invalidation, subject to notification delivery and connection-failure detection.

A factory returning `null` does not write or memoize its result. Later calls can run it again.
An existing stored `0`, empty string, or serialized JSON null is present and does not run the
factory. Mutable typed results are deserialized independently for each caller. The factory's
object is never returned directly. Use the same serialization contract for every writer of a key.

`CoalesceConcurrentMisses` also controls factory sharing. With it enabled, calls on the same
client with the same physical key, generic type and millisecond TTL share the first factory.
Different callbacks with that identity must be interchangeable. Prefix views share their root's
coordinator; different clients do not. Invalidation retires joining, so a later caller can start
another factory while an earlier one is still running. With coalescing disabled, each missed call
can run its own factory. A factory must not recursively await the same shared identity.

Each cancellation token bounds that caller's wait. With sharing enabled, one cancellation
does not stop remaining callers; the last caller leaving or client disposal cancels the factory
token cooperatively. Exceptions from shared-token cancellation callbacks can fault remaining
waiters but do not interrupt client cleanup. Without sharing, the factory receives the caller's
token. Factories must observe cancellation to stop promptly. A late result after cancellation or client disposal is
not sent as a new write, but a write already accepted by Redis may still execute. `CommandTimeout`
bounds Redis operations, not application factory work; supply a caller timeout when needed.
Factory, serialization and Redis errors propagate without automatically retrying a factory or
replaying an accepted write.

Writes use the ordinary invalidation fences and invalidate other tracking clients. The SET
result is deliberately not inserted directly into the local cache: it is not a tracked read.
The next GET registers tracking and populates the cache. This also avoids caching a losing
factory's value or a response made stale by a subsequent write.

The cache-aside benchmark compares a hot hit and complete miss bursts against manual
GET/SET NX GET composition on the same baseline/candidate/baseline runner. It measures default
single callers, opted-in single callers and 32 callers with a simulated asynchronous loader.
Reports retain allocations, factory invocation counts, latency and diagnostic process CPU;
loader latency means these results are not raw Redis throughput measurements.

## Why this is different

StackExchange.Redis 3.1.13 supports RESP3 and exposes keyspace notifications, but it does not
provide an equivalent built-in server-assisted local response cache. Its
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
They demonstrate the value of avoiding a network round trip—not a claim that Respire's uncached
wire path is hundreds of times faster. Uncached Respire and StackExchange.Redis reads were
statistically equivalent in the same net10 run.

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

With `OPTIN`, Redis tracks only misses Respire deliberately sends with `CLIENT CACHING YES`.
Local mutations also evict before and after execution. If tracking continuity is lost, Respire
clears affected cache state instead of trusting entries whose invalidations may have been missed.

## Broadcast tracking

`OptIn` remains the default. Choose `Broadcast` when invalidations for a known keyspace are
preferable to Redis registering each read key:

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

Each cache-bearing connection sends `CLIENT TRACKING ON BCAST PREFIX ...` with the configured
`KeyPrefixes` during setup and repeats it on reconnect; empty `KeyPrefixes` sends plain `BCAST`,
covering every key. Broadcast reads omit `CLIENT CACHING YES`. Coverage rules are the same as in
[Choose what gets cached](#choose-what-gets-cached). Local writes, in-flight invalidations, and
reconnect continuity use the same eviction and stale-insertion checks as `OptIn`.

RESP3 remains required. Standalone, discovered Sentinel data connections, and Redis/Valkey
Cluster data nodes retain the selected configuration; Cluster slot and database restrictions
still apply. Blocking/dedicated commands, batches, and transactions continue to bypass caching.
Changing mode or prefixes requires creating a new client. `MaxInflightCommands` must allow the
atomic prefix frames: one for non-Cluster `Broadcast`, two for non-Cluster `OptIn`
(`CLIENT CACHING YES` plus the read) or Cluster `Broadcast` (`ASKING` plus the read), and three
for Cluster `OptIn` (`ASKING`, `CLIENT CACHING YES`, and the read).

Redis documents that BCAST trades per-read tracking entries for invalidations on all matching
writes, even when this client never read those keys. More prefixes add server work, and broad
prefixes can increase network and eviction traffic. See [CLIENT TRACKING](https://redis.io/docs/latest/commands/client-tracking/)
and the [broadcast reference](https://redis.io/docs/latest/develop/reference/client-side-caching/#broadcasting-mode).
The cache-tracking CI benchmark compares local hits and an external write/invalidation/read
cycle for OPTIN, BCAST-all, and BCAST with one or 256 prefixes, plus OPTIN against two pinned
baseline controls. The invalidation cycle includes cooperative polling until the application
observes eviction; its CPU totals include that work. Process totals also include warmup and
background work; no universal performance win is implied.

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

The owned `RespireClientCacheInvalidation.Key` identifies the physical wire key. Binary keys,
including empty keys, retain byte identity after registration even if the caller changes the
original buffer. Prefix views share the same cache: a view with `WithKeyPrefix("tenant:")`
must subscribe to `tenant:jobs:ready`, not `jobs:ready`.

Registration does not issue a command, warm an entry, or register server tracking. OPTIN
requires an eligible cached read; read again after each wake-up to rearm tracking. BCAST
requires a key inside the configured physical prefixes; uncovered subscriptions throw
`ArgumentException`. The subscription remains registered across reconnects, but cannot
recover events lost during a disconnect. No server notification is promised for untracked keys.

`Reasons` combines `ServerInvalidation`, `LocalMutation`, `ExplicitClear`, and `ContinuityLost`
flags. Local mutations can invalidate before dispatch and after completion, including when
the value did not change. Redis-wide invalidations, `Clear()`, unknown mutations, and continuity
loss wake every subscription. TTL expiry and capacity eviction do not produce notifications.
`LocalMutation` also covers conservative whole-cache flushes for unknown commands; it does
not identify which observed key, if any, changed on the server.
Cache eviction and stale-read rejection happen before notifications are scheduled.

Each subscription serializes callbacks on the ThreadPool without flowing the registration's
`ExecutionContext`. Callbacks never run inline on the socket parser or invalidating thread.
A slow observer has at most one pending wake-up: its reason flags are combined, preserving
continuity loss. Intermediate events, event counts, and cross-subscription ordering are not
preserved. Keep callbacks short, as in the bounded channel example. A throwing callback cannot
interrupt eviction or other observers; `LastObserverException` retains its latest exception
and later callbacks continue. Use synchronous callbacks; `async void` exceptions cannot be
captured by this API.

Dispatch cost scales with the number of subscriptions: a global flush can schedule one worker
per subscription. Registration and disposal copy the observer dictionary and the affected key's
subscriber array, then publish an immutable snapshot. Per-key invalidation and global flushes read
that snapshot without taking the registration gate or allocating a lookup snapshot. Registering
or removing an observer costs O(observed keys + subscribers for that key); use long-lived
subscriptions rather than registering on every request. A global flush
still does work proportional to the subscriber count on its caller, which can hold other cache gates.
Client disposal detaches the entire registry once and stops subscriptions without copying it
or reacquiring the registration gate for each subscription.
There is no additional global subscription limit. Bound the number of live subscriptions in
your application; the one-pending limit applies separately to each subscription. Only the most
recent callback exception is retained. Catch and log inside your callback if every failure must
be recorded. Third-party implementations of `IRespireClientSideCache` remain source-compatible;
the default observation method throws `NotSupportedException` unless implemented. Implementations
that support observation can return their own public `IRespireClientCacheInvalidationSubscription`.

Dispose the returned subscription or cancel its token to discard pending delivery. Client
disposal also stops every subscription. Disposal does not wait for a callback already selected
for execution, so that callback may finish afterward and may safely dispose itself or its client.

These signals mean **recheck current state**, not “a write occurred.” They are unsuitable as a
durable event log, distributed lock, or correctness guarantee for cross-process coordination.
For correctness-critical coordination, combine authoritative Redis commands with cancellation
and periodic reconciliation; an undetected partition can delay notifications indefinitely.

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

Both limits are hard: whichever is exceeded first triggers eviction. The size estimate covers reply
payloads, command arguments, dependency keys, and a fixed per-entry overhead. An oversized response
is returned without being cached. `GetLeaseAsync` participates without sharing lease ownership.
`LocalExpiration` is checked lazily on lookup; no timer runs. See [Cached commands](#cached-commands)
for what bypasses the cache.

## ASP.NET Core registration

`Respire.DependencyInjection` provides a helper on its mutable options builder:

```csharp
builder.Services.AddRespire(options =>
{
    options.Endpoints.Add(new RespireEndpoint("redis.internal"));
    options.UseClientSideCaching();
});
```

`RespireDistributedCache` reads through Lua scripts to preserve Microsoft-compatible sliding
expiration, so its operations never use this command cache. Enable client-side caching on a
separately registered `IRespireClient` used for direct deterministic reads. `HybridCache` remains
the better fit for an application-level object L1; Respire's cache stores protocol replies and
follows Redis invalidations.

## Diagnostics

```csharp
var cache = redis.ClientSideCache!;
var statistics = cache.GetStatistics();

Console.WriteLine($"{statistics.Hits} hits; {statistics.SizeBytes} bytes");
cache.Clear();
```

`GetStatistics()` returns a cheap point-in-time snapshot of hits, misses, invalidations, evictions,
continuity flushes, resident entries, and approximate bytes. `Clear()` also rejects older reads
still in flight, so they cannot refill the cache.

The `Respire` OpenTelemetry meter emits:

| Instrument | Meaning |
|---|---|
| `redis.client.csc.requests` | Cache lookups; `redis.client.csc.result` is `hit` or `miss`. |
| `redis.client.csc.evictions` | Responses removed; `redis.client.csc.reason` is `full`, `ttl`, or `invalidation`. |
| `respire.client_cache.invalidations` | Key or broadcast invalidations received. |
| `respire.client_cache.continuity_flushes` | Flushes caused by connection or topology uncertainty. |

The `redis.client.csc.*` counters are recorded only when the `RespireMetricGroups.ClientSideCaching`
metric group is enabled, and carry `db.system.name=redis` and
`redis.client.library=Respire:<version>`. One invalidated key can remove several cached responses
or none. Local writes, `Clear()`, and continuity flushes do not increment
`redis.client.csc.evictions`, although `GetStatistics().Evictions` counts flushes.

## Consistency boundary

Respire rejects a stale read response when an invalidation races cache insertion. It also flushes
after awaited local mutations and on detected connection loss, reconnect, redirect, and cluster
topology retirement. In `OptIn` mode an `ASK` retry sends `ASKING`, `CLIENT CACHING YES`, and the
read as one uninterrupted sequence, so the migration target tracks the key. Local TTL is an
additional staleness bound, not a substitute for tracking. Like every server-assisted client cache, it
cannot observe invalidations across an undetected network partition. Configure TCP keepalive or
`ConnectionIdleReadTimeout`, and keep a finite local TTL, when bounded failure detection matters.
