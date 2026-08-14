---
title: Client-side caching
description: Enable bounded RESP3 server-assisted caching for Redis reads.
---

# Client-side caching

Respire can cache eligible Redis reads in-process while Redis invalidation pushes keep entries
coherent across clients. Enable it once on the client:

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

## Bounds

Tune entry count, approximate owned bytes, and local TTL together:

```csharp
ClientSideCache = new RespireClientSideCacheOptions
{
    MaxEntries = 25_000,
    MaxSizeBytes = 128L * 1024 * 1024,
    TimeToLive = TimeSpan.FromMinutes(2),
},
```

An oversized response is returned without being cached. `GetLeaseAsync` participates without
sharing lease ownership. Nondeterministic, random, probabilistic, blocking, script/function,
time-series, Search, and unkeyed commands bypass caching; so do batches and transactions. Unknown
mutations conservatively flush local entries before dispatch and after awaited completion.

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
