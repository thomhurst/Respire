# Sorted-set pops

Use a single key with `PopAsync(key, descending: false)` for `ZPOPMIN`, or
`descending: true` for `ZPOPMAX`. The existing single-key `PopManyAsync(key, count)`
overloads return arrays and keep their current bindings.

Pass an ordered key span to select the first nonempty sorted set. Selection follows
key order, not the lowest or highest score across all keys. Entries come only from
that selected set and follow ascending score order by default; `descending: true`
selects maximum scores first. Equal-score members follow Redis ordering.

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

`ZMPOP` requires Redis 7.0+. Count must be positive. An empty key span is rejected;
an empty key itself is valid. All keys must share a Cluster slot after the client's
prefix is applied. Returned keys own their binary bytes, including non-UTF-8 bytes.
Returned entries also remain valid after the reply buffer is released. Generic
`PopManyAsync<T>` and `PopAsync<T>` deserialize members with the client's serializer.

## Blocking calls

Supply `waitFor` to the multi-key `PopManyAsync` overload for `BZMPOP` (Redis 7.0+).
Multi-key `PopAsync(keys, waitFor, descending)` uses `BZPOPMIN` or `BZPOPMAX`
(Redis 5.0+; fractional-second timeouts require Redis 6.0+).

```csharp
await using var client = await RespireClient.ConnectAsync("localhost:6379");
using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var result = await client.SortedSets.PopAsync(
    ["{jobs}:urgent", "{jobs}:ready"],
    waitFor: Timeout.InfiniteTimeSpan,
    cancellationToken: cancellation.Token);
```

Blocking commands use dedicated pooled connections, so other client traffic can
continue. Normal completion returns the lease; cancellation discards that connection
and throws `OperationCanceledException`. A server timeout returns `null`.
`Timeout.InfiniteTimeSpan` waits indefinitely; `TimeSpan.Zero` requests a minimum
one-millisecond wait. Omit `waitFor` on `PopManyAsync` for nonblocking execution,
which returns `null` when every key is empty. Negative waits other than
`Timeout.InfiniteTimeSpan` are rejected. Redis wrong-type errors remain server errors.

## Batches and transactions

Both queues expose nonblocking `SortedSets.PopMany(keys, count, descending)` and
`PopMany<T>`, returning the same nullable selected-key result after execution.
Blocking calls have no deferred form. Key spans are snapshotted when queued.

```csharp
await using var client = await RespireClient.ConnectAsync("localhost:6379");
using var batch = client.CreateBatch();
var pending = batch.SortedSets.PopMany(["{jobs}:urgent", "{jobs}:ready"], count: 5);
await batch.ExecuteAsync();
Console.WriteLine(pending.Result?.Key);
```

See Redis documentation for [ZMPOP](https://redis.io/docs/latest/commands/zmpop/),
[BZMPOP](https://redis.io/docs/latest/commands/bzmpop/),
[BZPOPMIN](https://redis.io/docs/latest/commands/bzpopmin/), and
[BZPOPMAX](https://redis.io/docs/latest/commands/bzpopmax/).
