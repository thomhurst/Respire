---
title: Performance
description: How Respire pipelines work and when to choose batches or leased reads.
---

import ComparisonBarChart from '@site/src/components/ComparisonBarChart';

# Performance

Respire's default path is fast under concurrency without special APIs. This page covers what it does for you and the few settings worth tuning.

## Automatic pipelining

Concurrent calls share socket writes automatically. No batching mode is needed:

```csharp
await Task.WhenAll(
    redis.GetStringAsync("a").AsTask(),
    redis.GetStringAsync("b").AsTask(),
    redis.IncrementAsync("hits").AsTask());
```

## Make hot reads local

With [client-side caching](./fundamentals/client-side-caching), repeated reads return from
bounded local memory after the first miss. When a tracked key changes, Redis invalidates it and
the next read refreshes.

In a net10 [comparison run](https://github.com/thomhurst/Respire/actions/runs/31923908249),
a Respire cache hit took 216.5 ns, versus 144.6 μs for a Respire server read and 148.6 μs for a
StackExchange.Redis 3.1.13 server read. Both Respire paths allocated 64 B; StackExchange.Redis
allocated 527 B. Missing-key `GET` and `EXISTS` cache hits allocate nothing. StackExchange.Redis
has no built-in server-assisted cache, so its figure is a server read. See
[benchmarks](./benchmarks) for the latest weekly results and the
[benchmark source](https://github.com/thomhurst/Respire/blob/main/benchmarks/Respire.ComparisonBenchmarks/ClientSideCachingBenchmarks.cs).

<ComparisonBarChart
  title="GET latency: server reads vs cache hit"
  description="Comparison run 31923908249 (net10). Shorter bars are faster."
  format="duration-ns"
  respireLabel="Respire cache hit"
  scale="group"
  data={[
    {label: 'GET', other: 148628.4, respireServer: 144572.8, respire: 216.5},
  ]}
/>

<ComparisonBarChart
  title="GET allocation: server reads vs cache hit"
  description="Allocated bytes per operation in the same run. Shorter bars are better."
  format="bytes"
  respireLabel="Respire cache hit"
  scale="group"
  data={[
    {label: 'GET', other: 527, respireServer: 64, respire: 64},
  ]}
/>

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

Use `CreateBatch` to queue commands one by one and send them in one flush. See [batches and transactions](./guides/batches-and-transactions).

## Buffer ownership

Normal APIs return managed strings or arrays. For large payloads on measured hot paths, [leased reads](./fundamentals/values-and-serialization#zero-copy-leased-reads) return pooled memory that you must dispose.

## Dedicated blocking pool

`BLPOP`, `BLMOVE`, and blocking stream reads use a dedicated connection pool, so they do not delay other commands.

## Measure your workload

`benchmarks/Respire.Benchmarks` covers protocol, pipeline, throughput, allocation, and
container scenarios, some against StackExchange.Redis. Run it on net8.0 and net10.0:

```bash
bash ./run-benchmarks.sh -t All
```

The published comparison comes from `benchmarks/Respire.ComparisonBenchmarks`. It uses
`REDIS_HOST`/`REDIS_PORT` when set, otherwise a throwaway Testcontainer:

```bash
dotnet run -c Release -f net10.0 --project benchmarks/Respire.ComparisonBenchmarks -- --filter '*'
```

CI publishes [comparison benchmarks](./benchmarks) and [stress tests](./stress-tests) weekly. Treat them as a starting point, then profile your own payloads, concurrency, server, network, and serializer. Sources are in [`benchmarks`](https://github.com/thomhurst/Respire/tree/main/benchmarks); transport details are in [transport write evidence](./guides/transport-write-evidence).
