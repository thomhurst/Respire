---
title: Performance
description: How Respire compares with StackExchange.Redis, and how to tune it for your workload.
---

# Performance

Respire is built for high-concurrency workloads with low allocation. Concurrent callers share
socket writes automatically, and most commands allocate little or nothing per call.

## Results against StackExchange.Redis

These figures come from BenchmarkDotNet runs on .NET 10, on a 4-core GitHub-hosted Linux runner
talking to a local Redis 8.10 container. Each comparison is between clients in the same run.
For the full, weekly-refreshed tables, see [Benchmarks](./benchmarks).

**Concurrent and pipelined reads.** When many GETs are in flight at once, Respire completes
them faster:

| Workload | vs StackExchange.Redis 3.4.5 | vs StackExchange.Redis 4.0.127-alpha |
| --- | --- | --- |
| 200 pipelined GETs | 1.63 vs 1.87 μs per GET (about 613,000 vs 535,000 GET/s) | 1.37 vs 1.72 μs per GET (about 728,000 vs 583,000 GET/s) |
| 50 concurrent GETs | 3.06 vs 3.44 μs per GET | 3.04 vs 3.68 μs per GET |

The margin varies between runs; in some runs, including the latest weekly one, it is within
measurement noise.

**Single commands.** One awaited command at a time is dominated by the network round trip and
Redis itself. Against StackExchange.Redis 3.4.5, Respire is on par for GET, SET, HGET, HSET,
INCR, EXISTS, SADD, SISMEMBER, PING and list operations. Against 4.0.127-alpha, Respire was about
10–20% faster across all 17 measured operations (for example GET at 90.8 vs 113.7 μs).

**Allocations.** Respire allocates far less per command. In the same runs, GET allocated 48 B
against 504 B (3.4.5) and 718 B (4.0.127-alpha); EXISTS, INCR, PING, SADD, HSET and SET allocated
0 B against roughly 300–550 B.

**Client-side cache hits.** With [client-side caching](./fundamentals/client-side-caching), a
repeated read is served from local memory instead of a roughly 100 μs server round trip.
Compared with StackExchange.Redis 4.0.127-alpha's built-in cache, a Respire hit took 114.9 vs
191.0 ns for a present key (0 B vs 112 B allocated) and 117.3 vs 135.6 ns for a missing key.

**Pub/sub.** Publish-to-consume latency for a single subscriber is on par with both versions,
with lower allocation (about 400 B vs 672 B on 3.4.5 and 1,064 B on 4.0.127-alpha).

:::note
4.0.127-alpha is a prerelease of StackExchange.Redis. The 3.4.5 and 4.0.127-alpha results come
from different runner machines, so compare clients within a column, not across columns. These
are local-container measurements, not Cluster, Sentinel, TLS or remote-network results.
:::

## Automatic pipelining

Concurrent calls share socket writes automatically. No batching mode is needed:

```csharp
await Task.WhenAll(
    redis.GetStringAsync("a").AsTask(),
    redis.GetStringAsync("b").AsTask(),
    redis.IncrementAsync("hits").AsTask());
```

The more work you have in flight, the more Respire can combine into each write. Issue
independent commands concurrently instead of awaiting them one by one.

## Connection count tuning

The default is one multiplexed connection, which usually gives the best throughput. If
profiling shows large concurrent responses saturating one socket, benchmark two:

```csharp
var redis = await RespireClient.ConnectAsync(new RespireOptions
{
    Endpoints = { new RespireEndpoint("localhost", 6379) },
    Connections = 2,
});
```

More connections split commands into smaller batches and add overhead. Raise this only when
measurements show one connection is the bottleneck.

## When explicit batches help

Use `CreateBatch` to queue commands one by one and send them in one flush. This helps when
you build a group of commands in a loop and want them to leave together. See
[batches and transactions](./guides/batches-and-transactions).

## Buffer ownership

Normal APIs return managed strings or arrays. For large payloads on measured hot paths,
[leased reads](./fundamentals/values-and-serialization#zero-copy-leased-reads) return pooled
memory that you must dispose.

## Dedicated blocking pool

`BLPOP`, `BLMOVE`, and blocking stream reads use a dedicated connection pool, so they do not
delay other commands.

## Measure your workload

Published numbers are a starting point. Your payload sizes, concurrency, serializer, server and
network matter more, so profile your own workload before tuning.

The repository's [`benchmarks`](https://github.com/thomhurst/Respire/tree/main/benchmarks)
folder contains the suites behind these results. `benchmarks/Respire.Benchmarks` covers protocol,
pipeline, throughput, allocation and container scenarios:

```bash
bash ./run-benchmarks.sh -t All
```

The StackExchange.Redis comparison lives in `benchmarks/Respire.ComparisonBenchmarks`. It uses
`REDIS_HOST`/`REDIS_PORT` when set, otherwise it starts a throwaway Redis container:

```bash
dotnet run -c Release -f net10.0 --project benchmarks/Respire.ComparisonBenchmarks -- --filter '*'
```
