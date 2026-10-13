---
title: Sorted-set pops
description: Pop the lowest or highest scores from one sorted set or the first nonempty of several, with optional blocking waits.
---

# Sorted-set pops

The `SortedSets` facet removes and returns members by score. Use a single-key pop to take work from one priority queue. Use a multi-key pop to drain several queues in priority order, optionally waiting until one has work.

## Pop from one key

`PopAsync(key)` sends `ZPOPMIN` and returns the lowest-scored entry, or `null` when the set is empty. Pass `descending: true` for `ZPOPMAX`. `PopManyAsync(key, count)` returns up to `count` entries as an array.

## Pop from the first nonempty key

Pass an ordered key span to select the first nonempty sorted set:

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

This sends `ZMPOP` and requires Redis 7.0 or later. It returns `null` when every key is empty.

- Selection follows key order, not the lowest or highest score across all keys.
- Entries come only from the selected set. They are in ascending score order by default; `descending: true` returns the highest scores first. Equal scores follow Redis ordering.
- `count` defaults to 1 and must be positive.
- The key span must not be empty. An empty key name is valid. Duplicate keys are sent unchanged, in the order you supplied.
- In Cluster, all keys must share a hash slot after the key prefix is applied. Respire rejects mismatched slots before sending, with a `RespireServerException` whose `Code` is `CROSSSLOT`.
- The returned `Key` has the view prefix removed and owns its bytes, including non-UTF-8 bytes. Entries remain valid after the reply buffer is released.
- `PopManyAsync<T>` and `PopAsync<T>` deserialize members with the client serializer.
- Wrong-type errors from Redis remain server errors.

## Wait for an entry

Pass `waitFor` to wait until one of the keys has an entry:

```csharp
await using var client = await RespireClient.ConnectAsync("localhost:6379");
using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var result = await client.SortedSets.PopAsync(
    ["{jobs}:urgent", "{jobs}:ready"],
    waitFor: Timeout.InfiniteTimeSpan,
    cancellationToken: cancellation.Token);
```

| Call | Redis command | Minimum Redis version |
| --- | --- | --- |
| `PopManyAsync(keys, count, descending, waitFor)` | BZMPOP | 7.0 |
| `PopAsync(keys, waitFor, descending)` | BZPOPMIN or BZPOPMAX | 5.0 (6.0 for fractional-second waits) |

- Blocking calls run on dedicated pooled connections, so other traffic on the client continues. See [Blocking queues](blocking-queues.md).
- Normal completion returns the connection to the pool. Cancellation discards that connection and throws `OperationCanceledException`.
- A server timeout returns `null`.
- `Timeout.InfiniteTimeSpan` waits indefinitely. `TimeSpan.Zero` waits for the minimum of one millisecond. Other negative values throw `ArgumentOutOfRangeException`.
- Omit `waitFor` on `PopManyAsync` to run without blocking.

## Batches and transactions

`batch.SortedSets` and `transaction.SortedSets` expose nonblocking `PopMany(keys, count, descending)` and `PopMany<T>`. They return the same nullable result after execution. Blocking pops have no deferred form. Key spans are copied when the command is queued.

```csharp
await using var client = await RespireClient.ConnectAsync("localhost:6379");
using var batch = client.CreateBatch();
var pending = batch.SortedSets.PopMany(["{jobs}:urgent", "{jobs}:ready"], count: 5);
await batch.ExecuteAsync();
Console.WriteLine(pending.Result?.Key);
```

## Redis references

[ZMPOP](https://redis.io/docs/latest/commands/zmpop/), [BZMPOP](https://redis.io/docs/latest/commands/bzmpop/), [BZPOPMIN](https://redis.io/docs/latest/commands/bzpopmin/), [BZPOPMAX](https://redis.io/docs/latest/commands/bzpopmax/).
