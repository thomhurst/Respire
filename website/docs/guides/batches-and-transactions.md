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
Its `Failures` list identifies every faulted command by original queue index, operation name, and
exception; the list is empty without per-result allocation when all commands succeed.
The flush itself does not throw for command or connection-acquisition failures; call
`ThrowIfAnyFailed()` when fail-fast handling is preferable.

## The same facets as the client

Batches and transactions expose the client's facets — `Strings`, `Keys`, `Hashes`, `Lists`, `Sets`, `SortedSets`, `Bitmaps`, `HyperLogLog`, `Geo`, and `Scripts`. Except for `Scripts`, commands have matching names minus the `Async` suffix and the same parameter shapes. The missing suffix signals that each call only queues work. Deferred scripts use `Evaluate` rather than mirroring the client's `ExecuteAsync` variants. The return type is `RespirePending<T>` instead of `ValueTask<T>`, and there is no `CancellationToken` because `ExecuteAsync` / `CommitAsync` owns cancellation.

```csharp
RespireBatch batch = redis.CreateBatch();

RespirePending<long> pushed = batch.Lists.RightPush("queue", "job-1", "job-2");
RespirePending<bool> stored = batch.Hashes.Set("user:1", "name", "Ada");
RespirePending<long> ranked = batch.SortedSets.Add(
    "leaderboard", new SortedSetEntry("ada", 42));

RespireBatchResult result = await batch.ExecuteAsync();
```

Both types implement `IRespireCommandQueue`, which unifies every deferred facet and the root
shortcuts. Helpers can therefore queue work across facets without choosing an execution model:

```csharp
static void QueueUserUpdate(IRespireCommandQueue queue, string userId)
{
    queue.Hashes.Set($"user:{userId}", "status", "active");
    queue.Expire($"user:{userId}", TimeSpan.FromHours(1));
}

RespireBatch batch = redis.CreateBatch();
QueueUserUpdate(batch, "42");
await batch.ExecuteAsync();

await using RespireTransaction transaction = redis.CreateTransaction();
QueueUserUpdate(transaction, "43");
await transaction.CommitAsync();
```

Execution remains specific to the concrete type: batches call `ExecuteAsync`; transactions call
`CommitAsync`.

Blocking variants (a `waitFor` argument, i.e. `BLPOP` / `BLMOVE`) and streaming operations (`Keys.ScanAsync`, `Strings.GetLeaseAsync`) have no deferred form — a queue cannot block, and a lease borrows reply memory that is released once the batch completes. `Streams`, `Server`, and `Locks` remain client-only.

## Atomic transactions

```csharp
await using RespireTransaction transaction = redis.CreateTransaction();

RespirePending<long> balance = transaction.Increment("balance", -100);
transaction.Lists.RightPush("audit", "withdraw:100");

await transaction.CommitAsync();
Console.WriteLine(balance.Result);
```

The transaction stays on one connection and maps to `MULTI` / `EXEC`. Its commit has no result:
without `WATCH`, `EXEC` cannot abort.

Always commit or dispose a transaction so its pooled buffer and any dedicated WATCH connection
are released. `await using` also covers early returns and exceptions while commands are queued;
disposal is a no-op after commit. Committing an empty transaction succeeds as a no-op and sends
nothing to Redis.

## Optimistic concurrency

Create the watched transaction first, read current values through the client, and queue only the
resulting writes on the transaction. Transaction reads return `RespirePending<T>` values, which
cannot be inspected until after commit and therefore cannot drive the decision:

```csharp
const int maxAttempts = 5;
bool committed = false;

for (var attempt = 0; attempt < maxAttempts && !committed; attempt++)
{
    await using RespireWatchedTransaction transaction =
        await redis.CreateTransactionAsync(["balance"], cancellationToken);

    long current = await redis.GetAsync<long>("balance", cancellationToken);
    transaction.Set("balance", current - 100);
    committed = await transaction.CommitAsync(cancellationToken);
}

if (!committed)
{
    throw new InvalidOperationException("Balance changed too often; retry later.");
}
```

`false` means a watched key changed before `EXEC`. Each pending then has `Status ==
RespirePendingStatus.Aborted`; reading its result throws `RespireTransactionAbortedException`.
Dispose that attempt, create a new watched transaction, re-read state, and retry with a bounded
policy. For complex compare-and-set behavior, a Lua script often reduces round trips and makes
atomic intent clearer.
