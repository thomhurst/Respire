---
title: Performance
description: How Respire pipelines work and when to choose batches or leased reads.
---

import ComparisonBarChart from '@site/src/components/ComparisonBarChart';

# Performance

Respire is designed to make the default path fast under real concurrency, without requiring performance-specific application APIs.

## Automatic pipelining

Concurrent callers write commands into shared buffers. A persistent flush loop coalesces available work into fewer socket writes, while a FIFO inflight ring matches responses to awaiting callers.

```csharp
await Task.WhenAll(
    redis.GetStringAsync("a").AsTask(),
    redis.GetStringAsync("b").AsTask(),
    redis.IncrementAsync("hits").AsTask());
```

No batching mode is needed for this concurrent case.

## Make hot reads local

Redis server-assisted [client-side caching](./fundamentals/client-side-caching) changes the cost
model for read-heavy workloads. After a miss, eligible repeated reads return from bounded
in-process memory. Redis sends an invalidation when another client changes a tracked key, Respire
evicts it, and the next read refreshes lazily.

In the latest published net10 comparison run, a cached Respire `GET` measured 216.5 ns, versus
144.6 μs for an uncached Respire server read and 148.6 μs for a StackExchange.Redis server read.
Both Respire paths allocated 64 B, versus 527 B for StackExchange.Redis. StackExchange.Redis 3.1.13
has no equivalent built-in server-assisted local cache, so its result is necessarily an ordinary
server read. Missing `GET` and `EXISTS` cache hits allocated nothing. See the
[benchmark source](https://github.com/thomhurst/Respire/blob/main/benchmarks/Respire.ComparisonBenchmarks/ClientSideCachingBenchmarks.cs)
and [official run](https://github.com/thomhurst/Respire/actions/runs/31923908249).

<ComparisonBarChart
  title="GET latency: server reads vs cache hit"
  description="Latest published net10 comparison run. Shorter bars are faster."
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

One multiplexed connection is the default because it gives the write loop the deepest batches
and usually the best throughput. If profiling shows that large concurrent responses saturate
one socket or receive loop, benchmark two connections against your workload:

```csharp
var redis = await RespireClient.ConnectAsync(new RespireOptions
{
    Endpoints = { new RespireEndpoint("localhost", 6379) },
    Connections = 2,
});
```

More connections are not automatically faster: they divide commands into smaller batches and
add scheduling overhead. Increase this setting only when measurements show one connection is
the bottleneck.

## When explicit batches help

Use `CreateBatch` when the application discovers commands sequentially but wants one explicit flush. See [batches and transactions](./guides/batches-and-transactions).

## Buffer ownership

The network and parser layers use pooled buffers. Friendly APIs copy into managed strings or arrays before returning. For large payloads on measured hot paths, [leased reads](./fundamentals/values-and-serialization#zero-copy-leased-reads) expose pooled memory with explicit disposal.

## Dedicated blocking pool

Long-running `BLPOP`, `BLMOVE`, and stream reads do not occupy multiplexed connections. They rent from a dedicated pool so regular request latency remains independent of blocking waits.

## Measure your workload

Repository benchmarks compare Respire with StackExchange.Redis and include protocol, pipeline, throughput, allocation, and container-backed scenarios:

```bash
bash ./run-benchmarks.sh -t All
```

CI publishes the latest complete [comparison benchmarks](./benchmarks) and [sustained stress tests](./stress-tests) to this site. Use those results as evidence about this implementation, then profile your payload sizes, concurrency, server, network, and serialization choices. Benchmark sources live in the repository's [`benchmarks`](https://github.com/thomhurst/Respire/tree/main/benchmarks) directory.
