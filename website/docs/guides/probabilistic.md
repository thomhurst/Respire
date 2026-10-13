---
title: Probabilistic data structures
---

`Respire.Probabilistic` adds typed commands for Redis Bloom filters, Cuckoo filters, Count-Min Sketches, Top-K sketches and t-digest sketches. These types are built into Redis Open Source 8+; older deployments need Redis Stack or the RedisBloom module.

```bash
dotnet add package Respire.Probabilistic
```

With C# 14 or later, import `Respire.Probabilistic` and use `client.Probabilistic` on `RespireClient` or `IRespireClient`. It returns one wrapper per client (a `WithKeyPrefix` view gets its own, keeping its prefix), does no network I/O, and never disposes the client. On earlier C# versions, use `new RespireProbabilisticClient(client)`.

```csharp
using Respire;
using Respire.Probabilistic;

await using var client = await RespireClient.ConnectAsync("localhost:6379");
var probabilistic = client.Probabilistic;

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

| Type | Operations |
| --- | --- |
| Bloom | Reserve, add, exists, multi-add, multi-exists, insert (capacity and error rate set independently), cardinality, info, incremental dump/load |
| Cuckoo | Reserve, add/add-if-absent, insert/insert-if-absent, delete, exists, multi-exists, count, info, incremental dump/load |
| Count-Min | Initialize by dimensions or probability, batched increment and query, info, merge with positive integer weights |
| Top-K | Reserve options, add, weighted increment, query, count, list (optionally with counts), info |
| t-digest | Create/reset, merge options, add, min/max, quantile, CDF, rank/reverse rank, by-rank/by-reverse-rank, trimmed mean, info |

- Cuckoo insert and insert-if-absent return a `RespireCuckooInsertResult` per item (`Inserted`, `AlreadyExists` or `FilterFull`), so a full filter is not reported as success.
- Top-K add and increment return evicted items as nullable byte arrays, preserving binary items.
- Keys receive the `WithKeyPrefix` prefix. Multi-key merges route by destination and source keys and enforce Cluster slot rules.
- Mutations, including merges, invalidate only the written key (the merge destination) in the client-side cache.
