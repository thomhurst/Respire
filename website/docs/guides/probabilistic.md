---
title: Probabilistic data structures
---

`Respire.Probabilistic` adds typed command methods for Redis Bloom filters, Cuckoo filters, Count-Min Sketches, Top-K sketches, and t-digest sketches.

```bash
dotnet add package Respire.Probabilistic
```

```csharp
using Respire;
using Respire.Extensions.Probabilistic;

await using var client = await RespireClient.ConnectAsync("localhost:6379");
var probabilistic = new RespireProbabilisticClient(client);

await probabilistic.BloomReserveAsync("seen:users", errorRate: 0.01, capacity: 100_000);
await probabilistic.BloomAddAsync("seen:users", "user:42");
var maybeSeen = await probabilistic.BloomExistsAsync("seen:users", "user:42");

await probabilistic.CountMinInitializeByProbabilityAsync("events:counts", 0.001, 0.01);
await probabilistic.CountMinIncrementAsync("events:counts", new Dictionary<RespireValue, long>
{
    ["page:view"] = 1,
    ["purchase"] = 1,
});
var counts = await probabilistic.CountMinQueryAsync("events:counts", ["page:view", "purchase"]);

await probabilistic.TDigestCreateAsync("latency:api");
await probabilistic.TDigestAddAsync("latency:api", [12.4, 19.1, 35.8]);
var percentiles = await probabilistic.TDigestQuantileAsync("latency:api", [0.5, 0.95, 0.99]);
```

Bloom supports reserve, add, exists, multi-add, multi-exists, insert options, cardinality, info, and incremental dump/load. Cuckoo supports reserve, add/add-if-absent, insert/insert-if-absent, delete, exists, multi-exists, count, info, and incremental dump/load.

Count-Min supports dimension or probability initialization, batched increments and queries, info, and weighted merges. Top-K supports reserve options, add, weighted increment, query, count, list with optional counts, and info. t-digest supports create/reset, merge options, add, min/max, quantile, CDF, rank/reverse rank, by-rank/reverse rank, trimmed mean, and info.

The generated command interface uses the shared analyzer, so package consumers can define their own command interfaces with the same AOT-safe execution path. Known command keys receive `WithKeyPrefix` prefixes. Multi-key merge commands route using destination and source keys and enforce Cluster slot rules. Single-key mutations invalidate that key in the client-side cache; multi-key merges flush tracked entries. The caller owns the underlying client.
