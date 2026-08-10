---
title: Batches and transactions
description: Flush commands together and execute atomic Redis transactions.
---

# Batches and transactions

Respire pipelines concurrent commands automatically. Explicit batches help sequential code queue several commands before one flush; transactions add Redis atomicity.

## Batch one flush

```csharp
RespireBatch batch = redis.CreateBatch();

RespirePending<string?> name = batch.GetString("name");
RespirePending<long> visits = batch.Increment("visits");

RespireBatchResult result = await batch.ExecuteAsync();
result.ThrowIfAnyFailed();

Console.WriteLine($"{name.Result}: {visits.Result}");
```

`RespirePending<T>` is awaitable and exposes `.Result`. Inspect `Status`, `HasResult`, `Error`, or
use `TryGetResult` when exception-free state handling is preferable. Access before `ExecuteAsync`
throws `RespirePendingNotReadyException` instead of waiting forever for a batch that was never
flushed; command failures set `Status` to `Faulted` and expose the exception through `Error`.
`RespireBatchResult` summarizes the whole flush with `Count`, `FailureCount`, and `FirstError`.
The flush itself does not throw for command or connection-acquisition failures; call
`ThrowIfAnyFailed()` when fail-fast handling is preferable.

## The same facets as the client

Batches and transactions expose the client's facets — `Strings`, `Keys`, `Hashes`, `Lists`, `Sets`, `SortedSets`, `Bitmaps`, `HyperLogLog`, `Geo` — with matching command names minus the `Async` suffix and the same parameter shapes. The missing suffix signals that each call only queues work. The return type is `RespirePending<T>` instead of `ValueTask<T>`, and there is no `CancellationToken` because `ExecuteAsync` / `CommitAsync` owns cancellation.

```csharp
RespireBatch batch = redis.CreateBatch();

RespirePending<long> pushed = batch.Lists.RightPush("queue", "job-1", "job-2");
RespirePending<bool> stored = batch.Hashes.Set("user:1", "name", "Ada");
RespirePending<long> ranked = batch.SortedSets.Add(
    "leaderboard", new SortedSetEntry("ada", 42));

RespireBatchResult result = await batch.ExecuteAsync();
```

Both types implement the same facet interfaces (`IBatchListCommands`, `IBatchHashCommands`, …), so helper code can queue into a batch or a transaction interchangeably.

Two client facets have no deferred form: blocking variants (a `waitFor` argument, i.e. `BLPOP` / `BLMOVE`) and streaming ones (`Keys.ScanAsync`, `Strings.GetLeaseAsync`) — a batch cannot block, and a lease borrows reply memory that is released once the batch completes. `Streams`, `Server`, `Scripts`, and `Locks` remain client-only.

## Atomic transactions

```csharp
await using RespireTransaction transaction = redis.CreateTransaction();

RespirePending<long> balance = transaction.Increment("balance", -100);
transaction.Lists.RightPush("audit", "withdraw:100");

bool committed = await transaction.CommitAsync();

if (committed)
{
    Console.WriteLine(balance.Result);
}
```

The transaction stays on one connection and maps to `MULTI` / `EXEC`.

Always commit or dispose a transaction so its pooled buffer and any dedicated WATCH connection
are released. `await using` also covers early returns and exceptions while commands are queued;
disposal is a no-op after commit. Committing an empty transaction succeeds as a no-op and sends
nothing to Redis.

## Optimistic concurrency

Watch keys before queuing operations:

```csharp
await using RespireTransaction transaction = await redis.CreateTransactionAsync(["balance"]);
transaction.Increment("balance", -100);

bool committed = await transaction.CommitAsync();
```

`false` means a watched key changed before `EXEC`. Each pending then has `Status ==
RespirePendingStatus.Aborted`; reading its result throws `RespireTransactionAbortedException`.
Re-read state and retry with a deliberate policy. For complex compare-and-set behavior, a Lua
script often reduces round trips and makes atomic intent clearer.
