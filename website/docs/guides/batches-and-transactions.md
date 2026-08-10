---
title: Batches and transactions
description: Flush commands together and execute atomic Redis transactions.
---

# Batches and transactions

Respire pipelines concurrent commands automatically. Explicit batches help sequential code queue several commands before one flush; transactions add Redis atomicity.

## Batch one flush

```csharp
RespireBatch batch = redis.CreateBatch();

RespirePending<string?> name = batch.GetStringAsync("name");
RespirePending<long> visits = batch.IncrementAsync("visits");

await batch.SendAsync();

Console.WriteLine($"{name.Result}: {visits.Result}");
```

`RespirePending<T>` is awaitable and exposes `.Result`. Access before `SendAsync` throws `InvalidOperationException` instead of waiting forever for a batch that was never flushed.

## The same facets as the client

Batches and transactions expose the client's facets — `Strings`, `Keys`, `Hashes`, `Lists`, `Sets`, `SortedSets`, `Bitmaps`, `HyperLogLog`, `Geo` — with the same method names and parameter shapes. Only the return type differs: a `RespirePending<T>` instead of a `ValueTask<T>`, and there is no `CancellationToken` because the `SendAsync` / `CommitAsync` call owns cancellation.

```csharp
RespireBatch batch = redis.CreateBatch();

RespirePending<long> pushed = batch.Lists.RightPushAsync("queue", "job-1", "job-2");
RespirePending<bool> stored = batch.Hashes.SetAsync("user:1", "name", "Ada");
RespirePending<long> ranked = batch.SortedSets.AddAsync("leaderboard", ("ada", 42));

await batch.SendAsync();
```

Both types implement the same facet interfaces (`IBatchListCommands`, `IBatchHashCommands`, …), so helper code can queue into a batch or a transaction interchangeably.

Two client facets have no deferred form: blocking variants (a `waitFor` argument, i.e. `BLPOP` / `BLMOVE`) and streaming ones (`Keys.ScanAsync`, `Strings.GetLeaseAsync`) — a batch cannot block, and a lease borrows reply memory that is released once the batch completes. `Streams`, `Server`, `Scripts`, and `Locks` remain client-only.

## Atomic transactions

```csharp
RespireTransaction transaction = redis.CreateTransaction();

RespirePending<long> balance = transaction.IncrementAsync("balance", -100);
transaction.Lists.RightPushAsync("audit", "withdraw:100");

bool committed = await transaction.CommitAsync();

if (committed)
{
    Console.WriteLine(balance.Result);
}
```

The transaction stays on one connection and maps to `MULTI` / `EXEC`.

## Optimistic concurrency

Watch keys before queuing operations:

```csharp
RespireTransaction transaction = await redis.CreateTransactionAsync(["balance"]);
transaction.IncrementAsync("balance", -100);

bool committed = await transaction.CommitAsync();
```

`false` means a watched key changed before `EXEC`. Re-read state and retry with a deliberate policy. For complex compare-and-set behavior, a Lua script often reduces round trips and makes atomic intent clearer.
