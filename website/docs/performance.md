---
title: Performance
description: How Respire pipelines work and when to choose batches or leased reads.
---

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

## When explicit batches help

Use `CreateBatch` when the application discovers commands sequentially but wants one explicit flush. See [batches and transactions](./guides/batches-and-transactions).

## Buffer ownership

The network and parser layers use pooled buffers. Friendly APIs copy into managed strings or arrays before returning. For large payloads on measured hot paths, [leased reads](./fundamentals/values-and-serialization#zero-copy-leased-reads) expose pooled memory with explicit disposal.

## Dedicated blocking pool

Long-running `BLPOP`, `BLMOVE`, and stream reads do not occupy multiplexed connections. They rent from a dedicated pool so regular request latency remains independent of blocking waits.

## Measure your workload

Repository benchmarks compare Respire with StackExchange.Redis and include protocol, pipeline, throughput, allocation, and container-backed scenarios:

```bash
./run-benchmarks.sh all
```

Use results as evidence about this implementation, then profile your payload sizes, concurrency, server, network, and serialization choices. Benchmark reports live in the repository's [`benchmarks`](https://github.com/thomhurst/Respire/tree/main/benchmarks) directory.
