---
title: HyperLogLog
description: Estimate distinct values and merge cardinality sketches.
---

# HyperLogLog

Use `redis.HyperLogLog` from the core `Respire` package on Redis 2.8.9+ to estimate the
number of distinct values. A HyperLogLog does not retain an enumerable set of members;
use [sets](collections.md#sets) when you need membership checks or exact counts.

```csharp
await using var redis = await RespireClient.ConnectAsync("redis://localhost:6379");
bool changed = await redis.HyperLogLog.AddAsync("{visits}:monday", "ada", "grace", "ada");
long monday = await redis.HyperLogLog.CountAsync("{visits}:monday");
long week = await redis.HyperLogLog.CountAsync("{visits}:monday", "{visits}:tuesday");
bool merged = await redis.HyperLogLog.MergeAsync(
    "{visits}:week", "{visits}:monday", "{visits}:tuesday");
```

| Method | Redis command | Result |
| --- | --- | --- |
| `AddAsync` | PFADD | Whether the sketch changed; not an exact new-member test |
| `CountAsync` | PFCOUNT | Estimated distinct count across one or more sketches |
| `MergeAsync` | PFMERGE | `true` on an `OK` reply |

Counting multiple keys estimates their union without creating a destination.
`MergeAsync` stores the union in the destination. Counts remain estimates even when
input values overlap; adding the same value repeatedly does not intentionally count it again.

Key prefixes apply to sketch keys, not the values added. All keys in a multi-key count
or merge must share a Cluster slot after prefixing; the `{visits}` tag above ensures this.
Span overloads accept a cancellation token when adding values, counting, or merging.

`batch.HyperLogLog` and `transaction.HyperLogLog` provide `Add`, `Count`, and `Merge`.
Await execution before reading pending results. See
[batches and transactions](../guides/batches-and-transactions.md).

HyperLogLog needs no module package. For Bloom filters, Cuckoo filters, Count-Min Sketch,
Top-K, and t-digest, see [probabilistic data structures](../guides/probabilistic.md).
