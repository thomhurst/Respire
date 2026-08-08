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

## Atomic transactions

```csharp
RespireTransaction transaction = redis.CreateTransaction();

RespirePending<long> balance = transaction.IncrementAsync("balance", -100);
transaction.ListRightPushAsync("audit", "withdraw:100");

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
