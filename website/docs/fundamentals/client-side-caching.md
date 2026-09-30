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

Existing typed APIs and catalog `ExecuteAsync` calls then use the cache transparently. This covers
deterministic keyed reads across strings, keys, hashes, lists, sets, sorted sets, streams, bitmaps,
geospatial indexes, Redis arrays, JSON, and vector sets. Typed `GET` and `MGET` keep optimized
per-key entries and partial-hit behavior. Opting into `ReuseHashFields` lets `HMGET` reuse individual `HGET` field entries;
other replies use exact command-and-argument identities.

Missing keys are cached too. Replies are deep-owned internally and converted for each call, so
enabling caching does not introduce shared mutable objects.

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
the whole reply before any field is cached. Cluster MOVED recovery re-establishes tracked
reads; ASK replies are returned without caching the untracked migration target.

With both `ReuseHashFields` and `CoalesceConcurrentMisses` enabled, concurrent requests with the
same physical hash key and identical ordered missing fields share one HMGET producer. Full field
lists may differ when their cached fields differ. Each caller keeps its own cached values, output
order, and independently owned result. Different missing lists run independently; they are not
split into per-field requests. For example, missing lists `[a, b]` and `[b, a]` do not share
one producer: matching uses argument order, not set equality. Cancellation, invalidation, and continuity changes follow the
shared-read rules above. Hashes outside broadcast prefix coverage bypass per-field reuse.

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

## Broadcast tracking and physical prefixes

`OptIn` remains the default. Choose `Broadcast` when invalidations for a known keyspace are
preferable to Redis registering each read key:

```csharp
await using var redis = await RespireClient.ConnectAsync(new RespireOptions
{
    Endpoints = { new RespireEndpoint("localhost") },
    ClientSideCache = new()
    {
        TrackingMode = RespireClientTrackingMode.Broadcast,
        BroadcastPrefixes = ["tenant:products:", "tenant:prices:"],
    },
});
```

Each cache-bearing connection sends `CLIENT TRACKING ON BCAST PREFIX ...` during setup and
repeats it on reconnect. Broadcast reads omit `CLIENT CACHING YES`. Prefixes are literal
bytes, not Redis glob patterns; `*`, `?`, NUL, and non-UTF-8 bytes retain their literal meaning.
Pass binary prefixes as `RespireKey` values. Options snapshot both the list and its storage.

Prefixes identify **physical wire keys**. A `WithKeyPrefix("tenant:")` view does not add that
prefix to the tracking configuration. The example covers `products:42` through that view,
but not an unprefixed `products:42` call. Empty `BroadcastPrefixes` means every key.
Duplicates and overlapping prefixes are rejected before connecting; one empty prefix covers
everything and therefore cannot accompany another prefix. Prefixes are invalid in `OptIn` mode.

Uncovered reads still go to Redis and are returned normally, but are never inserted locally.
Mixed `MGET` calls retain covered hits and fetch misses together, caching only covered keys.
A cached multi-key projection requires **every** dependency to be covered. Hash fields inherit
their physical hash key's coverage. Local writes, in-flight invalidations, and reconnect
continuity use the same eviction and stale-insertion checks as `OptIn`.

RESP3 remains required. Standalone, discovered Sentinel data connections, and Redis/Valkey
Cluster data nodes retain the selected configuration; Cluster slot and database restrictions
still apply. Blocking/dedicated commands, batches, and transactions continue to bypass caching.
Changing mode or prefixes requires creating a new client. Non-Cluster `Broadcast` clients can
use one in-flight command slot. `OptIn` needs two for its validated command prefix, and Cluster
caching needs two in either mode for atomic `ASKING` plus command redirects.

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
per subscription, and registration/disposal briefly waits while that subscriber set is queued.
There is no additional global subscription limit. Bound the number of live subscriptions in
your application; the one-pending limit applies separately to each subscription. Only the most
recent callback exception is retained. Catch and log inside your callback if every failure must
be recorded. Third-party implementations of `IRespireClientSideCache` remain source-compatible;
the default observation method throws `NotSupportedException` unless implemented.

Dispose the returned subscription or cancel its token to discard pending delivery. Client
disposal also stops every subscription. Disposal does not wait for a callback already selected
for execution, so that callback may finish afterward and may safely dispose itself or its client.

These signals mean **recheck current state**, not “a write occurred.” They are unsuitable as a
durable event log, distributed lock, or correctness guarantee for cross-process coordination.
For correctness-critical coordination, combine authoritative Redis commands with cancellation
and periodic reconciliation; an undetected partition can delay notifications indefinitely.

## Bounds

Tune entry count, approximate owned bytes, and local TTL together:

<!-- doc-test-ignore: Object-initializer fragment for the RespireOptions.ClientSideCache property. -->
```csharp
ClientSideCache = new RespireClientSideCacheOptions
{
    MaxEntries = 25_000,
    MaxSizeBytes = 128L * 1024 * 1024,
    TimeToLive = TimeSpan.FromMinutes(2),
},
```

An oversized response is returned without being cached. `GetLeaseAsync` participates without
sharing lease ownership. `GEOSEARCH` with `COUNT ... ANY` is also excluded because Redis may return
an arbitrary early subset. Only exact `MEMORY USAGE ... SAMPLES 0` calls are cached; sampled size
estimates bypass the cache. Nondeterministic, random, probabilistic, blocking, script/function,
time-series, Search, and unkeyed commands bypass caching; so do batches and transactions. Unknown
mutations conservatively flush local entries before dispatch and after awaited completion.
Respire rejects raw commands that would change protocol, database, or tracking state while this
feature is enabled.

## ASP.NET Core registration

`Respire.Extensions.DependencyInjection` provides a helper on its mutable options builder:

```csharp
builder.Services.AddRespire(options =>
{
    options.Endpoints.Add(new RespireEndpoint("redis.internal"));
    options.UseClientSideCaching();
});
```

## Diagnostics

```csharp
var cache = redis.ClientSideCache!;
var statistics = cache.GetStatistics();

Console.WriteLine($"{statistics.Hits} hits; {statistics.SizeBytes} bytes");
cache.Clear();
```

The `Respire` OpenTelemetry meter emits hit, miss, invalidation, eviction, and continuity-flush
counters.

## Consistency boundary

Respire rejects a stale read response when an invalidation races cache insertion. It also flushes
after awaited local mutations and on detected connection loss, reconnect, redirect, and cluster
topology retirement. `ASK` retries return their value without caching because Redis applies both
`ASKING` and `CLIENT CACHING YES` to the next command. Like every server-assisted client cache, it
cannot observe invalidations across an undetected network partition. Configure TCP keepalive or
`ConnectionIdleReadTimeout`, and keep a finite local TTL, when bounded failure detection matters.
