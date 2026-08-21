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
geospatial indexes, Redis arrays, JSON, and vector sets. `GET` and `MGET` keep optimized per-key
entries and partial-hit behavior; other replies use exact command-and-argument identities.

Missing keys are cached too. Replies are deep-owned internally and converted for each call, so
enabling caching does not introduce shared mutable objects.

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
