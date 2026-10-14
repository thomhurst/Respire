---
title: Sorted-set pops
description: Pop the lowest or highest scores from one sorted set or the first nonempty of several, with optional blocking waits.
---

# Sorted-set pops

The `SortedSets` facet removes and returns members by score. Pop one key to take work from a priority queue; pop several keys to drain queues in priority order, optionally waiting for work.

## Pop from one key

`PopAsync(key)` sends `ZPOPMIN` and returns the lowest-scored entry, or `null` when the set is empty. Pass `descending: true` for `ZPOPMAX`. `PopManyAsync(key, count)` returns up to `count` entries as an array.

## Pop from the first nonempty key

Pass an ordered key span to pop from the first nonempty sorted set:

```csharp
await using var client = await RespireClient.ConnectAsync("localhost:6379");
var view = client.WithKeyPrefix("tenant:");
await view.SortedSets.AddAsync("{jobs}:ready", "task-7", 7);
var popped = await view.SortedSets.PopManyAsync(
    ["{jobs}:urgent", "{jobs}:ready"], count: 10);
if (popped is { } result)
{
    Console.WriteLine(result.Key);
    foreach (var entry in result.Entries)
        Console.WriteLine($"{entry.Member}: {entry.Score}");
    // The returned key has the view prefix removed and can be reused with the view.
    Console.WriteLine(await view.SortedSets.CountAsync(result.Key));
}
```

This sends `ZMPOP` (Redis 7.0+) and returns `null` when every key is empty.

- Selection follows key order, not the lowest or highest score across all keys.
- Entries come only from the selected set, lowest score first; `descending: true` returns the highest first. Equal scores follow Redis ordering.
- `count` defaults to 1 and must be positive.
- The key span must not be empty. Empty key names and duplicate keys are sent unchanged, in order.
- In Cluster, all prefixed keys must share a hash slot. Otherwise Respire throws `RespireServerException` with `Code` `CROSSSLOT` before sending.
- The returned `Key` has the view prefix removed and owns its bytes, including non-UTF-8 bytes. Entries stay valid after the reply is released.
- `PopManyAsync<T>` and `PopAsync<T>` deserialize members with the client serializer.

## Wait for an entry

Pass `waitFor` to wait until one of the keys has an entry:

```csharp
await using var client = await RespireClient.ConnectAsync("localhost:6379");
using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var result = await client.SortedSets.PopAsync(
    ["{jobs}:urgent", "{jobs}:ready"],
    waitFor: null,
    cancellationToken: cancellation.Token);
```

| Call | Redis command | Minimum Redis version |
| --- | --- | --- |
| `PopManyAsync(keys, waitFor, count, descending)` | BZMPOP | 7.0 |
| `PopAsync(keys, waitFor, descending)` | BZPOPMIN or BZPOPMAX | 5.0 (6.0 for fractional-second waits) |

- Blocking calls use dedicated connections, so other traffic on the client continues. See [Blocking queues](blocking-queues.md).
- Cancellation throws `OperationCanceledException`. A server timeout returns `null`.
- Explicit `null` waits indefinitely (`Timeout.InfiniteTimeSpan` remains a compatibility alias). `TimeSpan.Zero` waits the minimum of one millisecond. Other negative values throw `ArgumentOutOfRangeException`.
- Omit `waitFor` on `PopManyAsync` to run without blocking.

## Batches and transactions

`batch.SortedSets` and `transaction.SortedSets` expose nonblocking `PopMany(keys, count, descending)` and `PopMany<T>`, with the same nullable result. Blocking pops cannot be queued. Key spans are copied when queued.

```csharp
await using var client = await RespireClient.ConnectAsync("localhost:6379");
using var batch = client.CreateBatch();
var pending = batch.SortedSets.PopMany(["{jobs}:urgent", "{jobs}:ready"], count: 5);
await batch.ExecuteAsync();
Console.WriteLine(pending.Result?.Key);
```

## Redis references

[ZMPOP](https://redis.io/docs/latest/commands/zmpop/), [BZMPOP](https://redis.io/docs/latest/commands/bzmpop/), [BZPOPMIN](https://redis.io/docs/latest/commands/bzpopmin/), [BZPOPMAX](https://redis.io/docs/latest/commands/bzpopmax/).
