---
title: Client-side caching
description: Cache Redis reads in-process, kept coherent by RESP3 invalidation pushes.
---

# Client-side caching

Respire can cache eligible Redis reads in-process. Redis tracks the keys Respire reads
(`CLIENT TRACKING`) and pushes an invalidation when one changes. Respire evicts the entry, and the
next read fetches the new value. A hit skips the network round trip, and your code keeps the same
typed API.

This is Redis's server-assisted cache, not an application object cache. Application
[keyspace notifications](../guides/keyspace-notifications.md) are a separate Pub/Sub feature.

## Enable it

```csharp
await using var redis = await RespireClient.ConnectAsync(new RespireOptions
{
    Endpoints = { new RespireEndpoint("localhost") },
    ClientSideCache = new(),
});
```

- The cache requires RESP3. Connection setup fails if Redis cannot negotiate RESP3 or enable
  `CLIENT TRACKING`.
- With `ClientSideCache = null` (the default), no cache, tracking handshake, or invalidation handler
  is created.
- Typed APIs, catalog `ExecuteAsync` calls, interpolated commands, and `GetLeaseAsync` use the cache
  transparently.
- Typed `GET` and `MGET` keep per-key entries, so an `MGET` combines local hits with one Redis
  request for the misses. Other replies are cached by their exact command and arguments, including
  argument order and binary arguments. To let `HMGET` reuse `HGET` field entries, see
  [Partial hash reads](#partial-hash-reads).
- Missing keys are cached too.
- Cached replies are immutable and converted for each call. Serializers run on every hit, and
  `GetBytesAsync` still returns a caller-owned array.
- Each client has one cache, shared by all of its `WithKeyPrefix` views.
- String reads also keep their decoded text, so later `GetStringAsync` hits skip decoding. That text
  counts against `MaxSizeBytes`.

## Cached commands

A read is cached only when Redis marks it cacheable and Respire can name every key it depends on:

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

Everything else goes to Redis:

- Time-varying replies: `DUMP`, relative TTLs (`TTL`, `PTTL`), cursor scans (`SCAN`, `HSCAN`,
  `SSCAN`, `ZSCAN`), random commands, and detailed `XPENDING`.
- Reads with unknowable or arbitrary results: `SORT_RO` with `BY` or `GET`, `GEOSEARCH` with
  `COUNT ... ANY`, and sampled `MEMORY USAGE`.
- `TOUCH`, probabilistic structures, blocking reads, scripts and functions, time series, Search, and
  unkeyed server state.
- Batches and transactions, which never consult the local cache.

While caching is enabled, `HELLO`, `RESET`, `SELECT`, `CLIENT CACHING`, and `CLIENT TRACKING`
throw `NotSupportedException`, because they would break coherence.

Writes evict the keys they change before they are sent, and reads that overlap a write are not
cached. When Respire cannot name the affected keys, it flushes the whole local cache. This applies
to unknown raw commands, scripts, cluster-wide mutations, blocking commands, batches, transactions,
and time-series writes that can update compaction destinations.

## Options

Every option has a bounded default, so `new()` is a complete configuration:

| Option | Default | Meaning |
|---|---|---|
| `MaxEntries` | `10_000` | Maximum resident entries. Must be at least 1. |
| `MaxSizeBytes` | 64 MiB | Approximate maximum bytes owned by cached replies. Must be at least 1. |
| `LocalExpiration` | 5 minutes | Maximum local lifetime of an entry, independent of the key's Redis TTL. `null` keeps entries until invalidated or evicted; otherwise at least 1 ms. |
| `KeyPrefixes` | empty (all keys) | Physical key prefixes eligible for caching. |
| `TrackingMode` | `OptIn` | How Redis tracks cached keys: selected reads (`OptIn`), reads by default (`OptOut`), or by prefix (`Broadcast`). |
| `NoLoop` | `false` | Suppress invalidations caused by the tracking connection's own writes. Local mutation eviction remains enabled. |
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

- Invalid values throw `RespireConfigurationException` when the client is created.
- `MaxEntries` and `MaxSizeBytes` are both hard limits: whichever is reached first triggers eviction.
- The size estimate covers reply payloads, command arguments, dependency keys, and a fixed per-entry
  overhead. A response that would exceed the limit is returned without being cached.
- `LocalExpiration` is checked lazily on lookup; no timer runs.

## Choose what gets cached

By default every eligible read is cached. Set `KeyPrefixes` to cache only hot, read-mostly keys such
as catalogs or configuration. Reads of other keys go straight to Redis and use no cache capacity. In
`OptIn` mode they are also sent without `CLIENT CACHING YES`, so Redis does not track them.
In `OptOut` mode `KeyPrefixes` controls local admission only: Redis can track uncovered reads,
and other reads that Respire does not cache. Use `WithoutClientCache()` when a read must explicitly
opt out of server tracking as well as local caching.

Prefix rules:

- Prefixes are literal bytes, not Redis glob patterns; `*` and `?` match only themselves. Pass
  binary prefixes as `RespireKey` values. The options snapshot both the list and its bytes.
- Prefixes match **physical wire keys**. A `WithKeyPrefix("tenant:")` view does not add its prefix,
  so `KeyPrefixes = ["tenant:products:"]` covers `products:42` read through that view, but not an
  unprefixed `products:42` call.
- Duplicate and overlapping prefixes are rejected before connecting. One empty prefix covers
  everything and cannot be combined with another prefix.
- A cached multi-key read requires **every** key it depends on to be covered. Hash fields inherit
  their hash key's coverage. Invalidation subscriptions require a covered key.
- In `Broadcast` mode the prefixes are also sent to Redis.

In `OptIn` mode, `MGET` with a mix of covered and uncovered keys behaves as follows:

- A typed `MGET`, or a raw `MGET` with `CoalesceConcurrentMisses = true`, sends its misses as one
  tracked command to stay atomic. Redis then tracks every key it reads, so uncovered keys can
  generate invalidation pushes, although they never enter the cache. Read uncovered keys in a
  separate `MGET` to avoid this.
- A raw `ExecuteAsync(MGET, ...)` with `CoalesceConcurrentMisses = false` is cached as one exact
  query. If any key is uncovered, the reply is not cached and the command is sent without
  `CLIENT CACHING YES`.

## Bypass the cache for one read

`WithoutClientCache()` returns a view whose reads always go to Redis. Use it when a code path must
see the latest server value, for example right before a conditional write:

```csharp
var fresh = redis.WithoutClientCache();
var stock = await fresh.Strings.GetAsync<int>("product:42:stock");
```

- The view shares the client's connections and keeps its key prefix and read routing.
- Writes through the view still invalidate cached entries; its reads never populate the cache.
- On a tracking connection in `OptOut` mode, a bypass read sends `CLIENT CACHING NO` immediately
  before the read. This prevents that command from adding tracking entries; it does **not**
  remove tracking established by an earlier read. Previously cached keys still invalidate normally.
  Connections with tracking disabled, such as replica and dedicated connections, need no prelude.
- In `Broadcast` mode, bypassing a read does not change the connection's prefix subscriptions.
- `GetOrSetAsync` on the view reads Redis directly and never shares factories.
- When client-side caching is disabled, `WithoutClientCache()` returns the same client.

## Concurrent misses

Set `CoalesceConcurrentMisses = true` to share concurrent misses for the same command and
byte-identical arguments within a client. It covers typed `GET` variants, identical ordered `MGET`
miss lists, and all eligible deterministic query reads.

- Typed and raw calls can join the same request. Each caller still gets its own conversion and
  independently owned results and leases.
- Typed and raw `GET`/`MGET` share per-key entries, so either can populate hits for both.
- Prefix views share by resolved wire key. Separate clients, databases, and Cluster routing contexts
  never share work.
- `GET` and `MGET` never share, and partially overlapping `MGET` lists are not split.

Cancellation and failures:

- Canceling one caller does not cancel the others. When the last caller cancels, Respire cancels
  the shared request. A command Redis has already accepted may still execute.
- Each caller's token bounds only that caller's wait. The shared request keeps its original
  `CommandTimeout` deadline; joining does not restart it.
- Server and transport failures reach every remaining caller, and a later call can retry. A shared
  request is never replayed after a transport failure.
- Client disposal cancels all shared work.

Invalidations, `Clear()`, and connection continuity changes stop new callers from joining older
requests. Callers already waiting may get the original result, but it is not cached. An invalidation
stops joining for all pending requests, including those for unrelated keys. Hit and miss counters
count callers, not wire requests. The counter `respire.client_cache.shared_read.retirements` shows
how often this churn stops sharing.

Sharing adds bookkeeping and result copies on every miss; hits are unaffected. Keep it off for
low-contention workloads.

## Partial hash reads

Set `ReuseHashFields = true` to serve `HMGET` fields from cached `HGET` entries. By default each
`HMGET` is cached as one exact query.

- `Hashes.GetManyAsync` and raw `HMGET` look up each field in the same entry as `HGET`. Cached
  fields return locally; all missing fields go in one `HMGET`. An all-hit request sends no command.
- Results keep the requested order, duplicate fields, and nulls for absent hashes or fields.
- Typed calls apply `WithKeyPrefix`; raw commands must pass physical keys.
- A Redis invalidation or local write of the hash evicts every cached field of that hash. A
  malformed reply is rejected before any field is cached.
- With `CoalesceConcurrentMisses` also on, concurrent requests for the same hash with identical
  ordered missing fields share one `HMGET`. `[a, b]` and `[b, a]` do not share.
- Hashes outside `KeyPrefixes` bypass per-field reuse.
- Hit and miss statistics count field lookups, including repeated fields.
- Like a cached `MGET`, a result may combine values cached at different times. Use an uncached
  transaction when you need an atomic snapshot.

Field reuse costs extra entries, lookups, and allocations in exchange for transferring fewer values.
Small local responses can be slower, even with partial hits. Benchmark your payloads, field overlap,
and network before enabling it.

## Populate missing values with a factory

`GetOrSetAsync<T>(key, factory, expiry, cancellationToken)` combines a tracked read with a conditional
cache-aside write. It requires `ClientSideCache` and Redis 7.0 or later for
[`SET NX GET`](https://redis.io/docs/latest/commands/set/). Hits follow the normal cache,
serialization, and key-prefix rules.

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

On a miss, the factory runs and `SET NX GET` stores its value only if the key is still absent.
Otherwise it returns the value another writer stored, and that winner keeps its TTL. If the key was
created and deleted before the `SET` runs, the computed value is stored.

- **No lock or exactly-once guarantee.** Another writer can change the key right afterward.
- **No capability check.** On an unsupported server or proxy, the factory can run before the write
  fails. Keep factories safe to run without a following write.
- `GetOrSetAsync` is a required `IRespireClient` member. Custom implementations and decorators
  must implement or forward it; omitting it is a compile error.
- **Expiry** uses `RespireExpiry`: pass a `TimeSpan` for a relative TTL or a `DateTimeOffset` /
  `RespireExpiry.At(instant)` for an absolute deadline. Relative TTLs must be at least 1 ms, are
  truncated to whole milliseconds, and start at the successful server write. Absolute deadlines
  use PXAT and must be after the Unix epoch; Redis applies a past deadline immediately, even if
  the factory has just produced the returned value. `None`, `Keep`, and `Persist` are rejected.
  Hits and failed conditional writes do not extend expiry. `LocalExpiration` is separate.
  Existing positional `TimeSpan` calls still compile; rename named `ttl:` arguments to `expiry:`.
- **Null:** a factory that returns `null` does not write, and a later call can run it again. A stored
  `0`, empty string, or serialized JSON `null` counts as present.
- Each caller gets its own deserialized result, never the factory's object. Use the same
  serialization for every writer of a key.
- The write invalidates other tracking clients but is not inserted into the local cache. The next
  `GET` registers tracking and populates it.

With a [command hook](../guides/command-hooks.md#cache-operation-owners), each caller
receives one `GETORSET` scope before cache lookup or factory execution, including hits
and cache-bypass views. Completion includes serialization, insertion and final conversion;
application commands inside the factory have independent scopes.

Factory sharing follows `CoalesceConcurrentMisses`:

- Enabled: calls on the same client with the same physical key, generic type, and expiry form/value
  share the first factory, so different callbacks with that identity must be interchangeable.
  Prefix views share their root client's factories; different clients do not. An invalidation stops
  new callers from joining, so a second factory can start while the first still runs.
- Disabled: each missed call runs its own factory.
- A factory must not await `GetOrSetAsync` for the same key, type, and expiry.

Cancellation and errors:

- Each token bounds that caller's wait. With sharing, the factory's token is canceled only when the
  last caller leaves or the client is disposed. Without sharing, the factory gets the caller's token.
- A result that arrives after cancellation is not written, but a write Redis already accepted may
  still execute.
- `CommandTimeout` bounds Redis operations, not your factory. Pass a token with a timeout if you need
  one.
- Factory, serialization, and Redis errors propagate without retrying the factory or replaying the
  write.

## Protect factories across app instances

The overload with `RespireCacheAsideOptions` coordinates through Redis. It does not require
`ClientSideCache`, and its behavior is the same on prefix and `WithoutClientCache()` views:

<!-- doc-test-tail-declaration: split-before=public sealed record Product -->
```csharp
using Respire;

await using var client = await RespireClient.ConnectAsync(new RespireOptions
{
    Endpoints = [new("localhost", 6379)],
});

var protection = new RespireCacheAsideOptions
{
    DistributedSingleFlight = true,
    EarlyRefreshBeta = 1,
    LockTimeout = TimeSpan.FromSeconds(30),
    WaitTimeout = TimeSpan.FromSeconds(5),
    StaleWhileRevalidate = TimeSpan.FromSeconds(20),
};

var product = await client.GetOrSetAsync<Product>(
    "product-cache-v2:42",
    protection,
    LoadProductAsync,
    expiry: TimeSpan.FromMinutes(5));

static ValueTask<Product?> LoadProductAsync(CancellationToken cancellationToken)
{
    cancellationToken.ThrowIfCancellationRequested();
    return ValueTask.FromResult<Product?>(new Product(42, "Coffee"));
}

public sealed record Product(int Id, string Name);
```

Use dedicated keys. This overload stores a **Redis hash**, containing the serialized payload,
recompute duration, generation token, and stale retention. Do not use ordinary GET/SET or the
overload without options on those keys. Start with a new namespace when migrating existing string
entries. Delete the hash to invalidate it. All callers must agree on type, factory meaning, expiry,
and options. Custom `IRespireClient` implementations and decorators must implement the new
member explicitly, as they do the existing cache-aside overload.

- **Single-flight** (enabled by default) elects a factory with `SET NX PX` and a unique token.
  Other callers retry every `RetryDelay` (default 25 ms), returning the published value. A missing
  value never causes an unprotected factory after contention: `WaitTimeout` ends with
  `TimeoutException`. A zero wait budget allows one acquisition attempt. Redis command timeouts
  bound individual network operations, so the wall-clock wait can exceed the contention budget
  by an in-progress command. No local factory coalescing option is needed.
- **Early refresh** (`EarlyRefreshBeta`, default zero) uses the
  [XFetch exponential election](https://cseweb.ucsd.edu/~avattani/papers/cache_stampede.pdf).
  A read elects refresh when `-duration * beta * log(U)` reaches the remaining freshness, with a
  uniform sample `U`. Duration is the preceding factory's elapsed time, rounded up to at least
  1 ms and stored with the payload. Start with beta 1; larger factors refresh earlier. Set
  `DistributedSingleFlight = false` to use early refresh alone. That reduces simultaneous misses
  probabilistically and does not guarantee one factory: every elected reader can recompute concurrently.
  Concurrent publications compare generation
  tokens so an older factory cannot overwrite a newer result.
- **Stale while revalidating** retains the hash for `StaleWhileRevalidate` beyond its logical
  expiry. One caller awaits refresh; contenders can return the retained value while its lease is
  held. This needs single-flight. There are no detached tasks, and an idle key is not refreshed.
  Before expiry, contenders can always return the still-fresh value during an early refresh.

Relative expiry begins at publication; absolute expiry uses the Redis clock. Hits do not extend
freshness. An active refresh may retain the hash's generation until its lease deadline so natural
expiry cannot reject its publication; this does not extend the period in which readers can use the old
payload. Cold fills create a temporary generation marker in the data hash before calling the factory,
so explicit deletion or replacement also prevents a first fill from publishing. The marker expires
after `LockTimeout`, including when single-flight is disabled; a slower fill can return its computed
value without caching it. Null results, failures, and cancellation remove only the caller's own empty
marker during bounded cleanup. Each refresh
replaces the payload and its duration atomically. Reads always go to the primary
through Lua scripts, including hits, so this trades local-cache hit speed for shared coordination.
Redis ACLs must allow scripting, hash reads/writes (including HINCRBY), SET, DEL, PTTL, PEXPIRE, PEXPIREAT and TIME.
Absolute-expiry publication uses PEXPIREAT to preserve the supplied deadline, including any stale-retention interval. The deterministic
binary lease key shares the data key's Redis Cluster slot, including binary and prefixed keys.
Single-flight also uses CLIENT ID and CLIENT KILL to order cleanup after an acquisition whose reply
was lost or canceled. Cleanup only releases a lease with that caller's token.

Choose `LockTimeout` above normal recompute latency. A crashed process's lease expires without
cleanup. A factory receives its caller's cancellation plus a cooperative lease timeout; a factory
that ignores cancellation can continue running. Lease timeout is reported as `TimeoutException`;
caller cancellation remains `OperationCanceledException`. Token checks prevent that expired owner from
publishing or deleting a successor's lease. Failover and lease expiry can still allow overlapping
factories: this is cache population, not exactly-once execution of side effects. Canceling a waiting
caller does not cancel another caller's factory. Null results and failures are not cached; a later
caller may try again. Stale values are not a fallback for factory or Redis failures.
If publication loses a generation race, the caller receives a fresh winner when available, otherwise
an independent copy of its computed value. That losing result is not written back to Redis.

`IDistributedCache` has no factory API, and HybridCache owns its factory lifecycle. Their integrations
keep their existing storage formats and do not automatically use these options. Call this overload
where the application owns the factory; do not point those integrations at its hash keys.

## Why this is different

StackExchange.Redis 3.x supports RESP3 and keyspace notifications but has no built-in
server-assisted local cache (its 4.0 previews add an opt-in one). Its
[keyspace-notification documentation](https://stackexchange.github.io/StackExchange.Redis/KeyspaceNotifications.html)
presents notifications as a building block for your own invalidation strategy. That needs Redis
server configuration plus application-owned storage, subscriptions, node coverage, bounds, command
eligibility, and race handling.

Respire uses `CLIENT TRACKING` directly; no `notify-keyspace-events` setting is needed. Redis's
[client-side caching support table](https://redis.io/docs/latest/develop/clients/client-side-caching/#which-client-libraries-support-client-side-caching)
does not list StackExchange.Redis.

## Measured impact

These results compare a local Respire cache hit with an ordinary StackExchange.Redis server read,
showing the value of skipping the round trip. Uncached Respire and StackExchange.Redis reads were
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

In `OptIn` mode, Redis tracks only the misses Respire sends with `CLIENT CACHING YES`. If tracking
continuity is lost, Respire clears the affected cache state rather than trust entries whose
invalidations may have been missed.

## Opt-out tracking and local writes

Choose `OptOut` when most reads should be tracked. Each caching connection sends
`CLIENT TRACKING ON OPTOUT` at setup and reconnect; ordinary cache misses need no per-read
`CLIENT CACHING YES` command. Redis still stores per-key tracking state, including for reads that
are not admitted to the local cache. On tracking connections, `WithoutClientCache()` sends
`CLIENT CACHING NO` with its reads to opt out explicitly.
For bypass transactions containing reads on tracking connections, the prelude is paired with
`MULTI` and suppresses tracking for the whole transaction. Empty and write-only transactions
need no caching prelude or extra reply slot. Watched transactions use dedicated connections
without tracking and need no caching prelude.

```csharp
await using var redis = await RespireClient.ConnectAsync(new RespireOptions
{
    Endpoints = { new RespireEndpoint("localhost") },
    ClientSideCache = new()
    {
        TrackingMode = RespireClientTrackingMode.OptOut,
        NoLoop = true,
    },
});
```

`NoLoop` adds `NOLOOP` to the tracking handshake in any mode. Redis then suppresses invalidations
caused by writes on that same physical connection. It does not suppress writes from another
connection in the same client pool, or from another client.

Local writes must update or evict cached values themselves when `NoLoop` is enabled. Respire
already evicts affected entries and prevents overlapping reads from being cached, including
writes through `WithoutClientCache()`; these protections stay enabled. `NOLOOP` reduces redundant
server pushes without replacing local eviction. Tracking mode and `NoLoop` survive reconnects
and apply to discovered Sentinel and Cluster data connections. Create a new client to change them.

## Broadcast tracking

Choose `Broadcast` to receive invalidations for a known keyspace instead of having Redis register
each key you read:

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

- Each caching connection sends `CLIENT TRACKING ON BCAST PREFIX ...` with `KeyPrefixes` at setup
  and on reconnect. Empty `KeyPrefixes` sends plain `BCAST`, covering every key.
- Reads omit `CLIENT CACHING YES`. Coverage follows [Choose what gets cached](#choose-what-gets-cached).
- RESP3 is still required. Standalone, Sentinel, and Redis/Valkey Cluster connections all use the
  selected mode. Blocking commands, batches, and transactions still bypass the cache.
- To change the mode or prefixes, create a new client.
- This client receives invalidations for every matching write, even for keys it never read. More
  prefixes add server work; broad prefixes add network and eviction traffic.

`MaxInflightCommands` must allow the commands Respire sends together for one cached read:

| Mode | Non-Cluster | Cluster |
|---|---|---|
| `Broadcast` | 1 | 2 (`ASKING` + read) |
| `OptIn` | 2 (`CLIENT CACHING YES` + read) | 3 |
| `OptOut` | 1 for ordinary reads; 2 for bypass reads (`CLIENT CACHING NO` + read) | 2 for ordinary reads; 3 for bypass reads |

Because bypass reads remain available in `OptOut` mode, its minimum `MaxInflightCommands` is 2
outside Cluster and 3 in Cluster, the same as `OptIn`.

See [CLIENT TRACKING](https://redis.io/docs/latest/commands/client-tracking/) and the
[broadcast reference](https://redis.io/docs/latest/develop/reference/client-side-caching/#broadcasting-mode).

## Observe invalidations

`SubscribeInvalidations` wakes a local coordinator when a physical key may need re-reading.
Subscribe **before** the first read so an invalidation cannot slip between the read and
registration:

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

- The callback receives a `RespireClientCacheInvalidation` whose `Key` is the physical wire key.
  Binary keys are copied. A `WithKeyPrefix("tenant:")` view must subscribe to `tenant:jobs:ready`,
  not `jobs:ready`.
- Registration sends no command and registers no server tracking. In `OptIn` mode you get
  notifications only after an eligible cached read, so read again after each wake-up.
  `OptOut` also requires a read to register a key; a bypass read does not register it.
- A key outside `KeyPrefixes` throws `ArgumentException`.
- The subscription survives reconnects but cannot recover events lost during a disconnect.
- `Reasons` is a `RespireClientCacheInvalidationReason` flags value: `ServerInvalidation`,
  `LocalMutation`, `ExplicitClear`, `ContinuityLost`. Local writes can notify before they are sent
  and again after they complete, even if the value did not change.
- Redis-wide invalidations, `Clear()`, writes with unknown keys, and continuity loss wake every
  subscription. TTL expiry and capacity eviction do not notify.

Delivery rules:

- Callbacks run on the ThreadPool, one at a time per subscription, never inline on the socket
  reader. The registration's `ExecutionContext` does not flow.
- A slow observer has at most one pending notification, with reason flags combined. Intermediate
  events, counts, and ordering across subscriptions are not preserved.
- A throwing callback does not stop eviction or other observers. `LastObserverException` keeps the
  latest exception. Catch and log inside the callback if you need every failure. Use synchronous
  callbacks; `async void` exceptions cannot be captured.
- A global flush schedules one callback per subscription, and registering or removing one costs time
  proportional to the number of observed keys. Keep subscriptions long-lived and bounded.

Dispose the returned `IRespireClientCacheInvalidationSubscription` or cancel its token to stop
delivery; client disposal stops all subscriptions. Disposal does not wait for a running callback, so
a callback may dispose its own subscription or client.

These signals mean **recheck current state**, not "a write occurred". Do not use them as a durable
event log, distributed lock, or cross-process correctness guarantee. For correctness-critical
coordination, combine authoritative Redis commands with cancellation and periodic reconciliation; an
undetected network partition can delay notifications indefinitely.

`SubscribeInvalidations` is a required `IRespireClientSideCache` member. Custom implementations
and decorators must implement or forward it; omitting it is a compile error.

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

`RespireDistributedCache` reads through Lua scripts to keep Microsoft-compatible sliding expiration,
so it never uses this cache. Enable client-side caching on a separately registered `IRespireClient`
for direct reads. For an application-level object cache, use `HybridCache`.

## Diagnostics

```csharp
var cache = redis.ClientSideCache!;
var statistics = cache.GetStatistics();

Console.WriteLine($"{statistics.Hits} hits; {statistics.SizeBytes} bytes");
cache.Clear();
```

- `ClientSideCache` is `null` when caching is disabled.
- `GetStatistics()` returns a point-in-time `RespireClientSideCacheStatistics`: hits, misses,
  invalidations, evictions, continuity flushes, resident entries (`Count`), and approximate bytes
  (`SizeBytes`).
- `Clear()` also rejects older reads still in flight, so they cannot refill the cache.

The `Respire` OpenTelemetry meter emits:

| Instrument | Meaning |
|---|---|
| `redis.client.csc.requests` | Cache lookups; `redis.client.csc.result` is `hit` or `miss`. |
| `redis.client.csc.evictions` | Responses removed; `redis.client.csc.reason` is `full`, `ttl`, or `invalidation`. |
| `respire.client_cache.invalidations` | Key or broadcast invalidations received. |
| `respire.client_cache.continuity_flushes` | Flushes caused by connection or topology uncertainty. |
| `respire.client_cache.shared_read.retirements` | Shared misses new callers could no longer join (see [Concurrent misses](#concurrent-misses)). |

- `redis.client.csc.*` counters are recorded only when `RespireMetricGroups.ClientSideCaching` is
  enabled with `RespireMetrics.Configure`; it is not in `RespireMetricGroups.Default`. See
  [Observability](../integrations/observability.md).
- They carry `db.system.name=redis` and `redis.client.library=Respire:<version>`.
- One invalidated key can remove several cached responses or none.
- Local writes, `Clear()`, and continuity flushes do not increment `redis.client.csc.evictions`, but
  `GetStatistics().Evictions` counts entries removed by flushes.

## Consistency boundary

- Respire rejects a stale reply when an invalidation races its insertion. A read that depends on
  several keys, or several fields of one hash, is rejected if any of them changes while in flight; a
  write to an unrelated key does not affect it.
- Reads that start while a local write is in flight are not cached, even if they finish after it.
- Respire flushes the cache after writes with unknown keys and on detected connection loss,
  reconnect, Cluster `MOVED`/`ASK` redirects, and topology changes. In `OptIn` mode an `ASK` retry
  sends `ASKING`, `CLIENT CACHING YES`, and the read together, so the migration target tracks the key.
- Like every server-assisted client cache, Respire cannot see invalidations across an undetected
  network partition. `LocalExpiration` adds a staleness bound but does not replace tracking. For
  bounded failure detection, configure `TcpKeepAliveTime` or `ConnectionIdleReadTimeout`, and keep a
  finite `LocalExpiration`.
