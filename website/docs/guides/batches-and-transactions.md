---
title: Batches and transactions
description: Flush commands together and execute atomic Redis transactions.
---

# Batches and transactions

Respire pipelines concurrent commands automatically. Explicit batches help sequential code queue several commands before one flush; transactions add Redis atomicity.

For WAIT or WAITAOF acknowledgements of a batch's writes, use
[`ExecuteAndWaitForReplicationAsync` or `ExecuteAndWaitForAofAsync`](durability-acknowledgements.md).
These methods keep the writes and acknowledgement on one dedicated connection.

## Batch one flush

```csharp
using var batch = redis.CreateBatch();

RespirePending<string?> name = batch.GetString("name");
RespirePending<long> visits = batch.Increment("visits");

await batch.ExecuteAsync();

Console.WriteLine($"{name.Result}: {visits.Result}");
```

Always declare batches with `using var`. Disposal faults queued pendings when a batch is never
executed, including after an early return or exception. After execution, disposal preserves all
pending results and errors. Repeated disposal is safe.

`RespirePending<T>` is awaitable and exposes `.Result`. Inspect `Status`, `HasResult`, `Error`, or
use `TryGetResult` when exception-free state handling is preferable. Access before `ExecuteAsync`
throws `RespirePendingNotReadyException` instead of waiting forever for a batch that was never
flushed; command failures set `Status` to `Faulted` and expose the exception through `Error`.
`RespireBatchResult` summarizes the whole flush with `Count`, `FailureCount`, and `FirstError`.
Its `Failures` list identifies every faulted command by original queue index, operation name, and
exception; the list is empty without per-result allocation when all commands succeed.
`ExecuteAsync` completes every pending, then throws the first failure in original queue order.
It does not stop sending at the first error or roll back successful commands. Command errors,
conversion failures, timeouts, cancellation, and connection-acquisition failures follow this rule.
Successful results remain readable even when another command fails.

Use `TryExecuteAsync` when the application needs to inspect all failures instead of catching the
first one. It returns the same summary without rethrowing execution failures; invalid lifecycle
use (disposed or already-sent batches) still throws. Pre-release code that inspects failed
`ExecuteAsync` summaries must switch to `TryExecuteAsync`:

```csharp
using var batch = redis.CreateBatch();
batch.GetString("name");
batch.Increment("visits");
RespireBatchResult result = await batch.TryExecuteAsync();
foreach (var failure in result.Failures)
{
    Console.WriteLine($"Command {failure.Index} ({failure.Operation}): {failure.Error.Message}");
}
```

`result.ThrowIfAnyFailed()` remains available when inspection code later chooses to rethrow.

## The same facets as the client

Batches and transactions expose the client's facets — `Strings`, `Keys`, `Hashes`, `Lists`, `Sets`, `SortedSets`, `Bitmaps`, `HyperLogLog`, `Geo`, `Scripts`, `Functions`, and the non-blocking `Streams` subset. Except for `Scripts`, commands have matching names minus the `Async` suffix and the same parameter shapes. The missing suffix signals that each call only queues work. Deferred scripts use `Evaluate` rather than mirroring the client's `ExecuteAsync` variants. The return type is `RespirePending<T>` instead of `ValueTask<T>`, and there is no `CancellationToken` because `ExecuteAsync` / `CommitAsync` owns cancellation.

```csharp
using var batch = redis.CreateBatch();

RespirePending<long> pushed = batch.Lists.RightPush("queue", "job-1", "job-2");
RespirePending<bool> stored = batch.Hashes.Set("user:1", "name", "Ada");
RespirePending<long> ranked = batch.SortedSets.Add(
    "leaderboard", ("ada", 42));

await batch.ExecuteAsync();
```

`Keys.RenameAsync`, `Lists.TrimAsync`, `HyperLogLog.MergeAsync`, and `Strings.SetManyAsync`
return `ValueTask<bool>`, matching the `RespirePending<bool>` result of their batch and transaction
counterparts. Each returns `true` only after an `OK` confirmation; an unexpected reply or Redis
error faults the operation rather than returning `false`.

Migration: calls that simply await and ignore the result need no changes. Update custom facet
implementations, wrappers, delegates, and variables that explicitly use the previous non-generic
`ValueTask` return type to `ValueTask<bool>`.

Typed overloads follow the immediate facets too: hash `Set<T>` and `TryGet<T>`, set
`Contains<T>`, sorted-set `Add<T>`, and list `LeftPop<T>` / `RightPop<T>`. Typed values are
serialized when queued, but byte-backed arguments borrow their storage, including typed
`byte[]` and `ReadOnlyMemory<byte>` inputs. Keep their bytes unchanged until batch execution or
transaction commit completes, or copy them before queuing. Results are deserialized after
execution. Hash `TryGet<T>` preserves the
`Found` flag, distinguishing a missing field from a stored default value. Typed list pops return
the default value when the list is empty. Typed set membership and sorted-set additions preserve
Redis's `1`/`0` representation for boolean members, matching the immediate methods.

The new hash writes, set membership checks, and sorted-set additions preserve existing raw
`RespireValue` conversions when the type argument is omitted (C# 13 or later, as with the
library's `params` span APIs). This includes GUIDs, timestamps,
durations, and byte buffers. Specify the type argument explicitly, such as
`batch.Hashes.Set<Guid>(key, field, id)`, to use typed serialization instead. When moving an
inferred immediate call such as `client.Hashes.SetAsync(key, field, id)` into a deferred queue,
use `Set<Guid>` to retain that immediate call's typed encoding. The pre-existing inferred
batch call `Set(key, field, id)` retains raw GUID text for compatibility. Older C# compilers
ignore overload priority and may select a different encoding; use C# 13 or later, or specify
the type argument or `RespireValue` conversion explicitly.

Both types implement `IRespireCommandQueue`, which unifies every deferred facet and the root
shortcuts. Helpers can therefore queue work across facets without choosing an execution model:

<!-- doc-test-declaration: split-before=using var batch -->
```csharp
static void QueueUserUpdate(IRespireCommandQueue queue, string userId)
{
    queue.Hashes.Set($"user:{userId}", "status", "active");
    queue.Expire($"user:{userId}", TimeSpan.FromHours(1));
}

using var batch = redis.CreateBatch();
QueueUserUpdate(batch, "42");
await batch.ExecuteAsync();

await using RespireTransaction transaction = redis.CreateTransaction();
QueueUserUpdate(transaction, "43");
await transaction.CommitAsync();
```

Execution remains specific to the concrete type: batches call `ExecuteAsync`; transactions call
`CommitAsync`.

Blocking variants (a `waitFor` argument, i.e. `BLPOP` / `BLMOVE`) and streaming operations (`Keys.ScanAsync`, `Strings.GetLeaseAsync`) have no deferred form — a queue cannot block, and a lease borrows reply memory that is released once the batch completes. `Locks` and server administration other than the flush commands below remain client-only. Streams expose the non-blocking subset below; blocking reads, consumer loops, and group administration remain immediate operations.

## Deferred server flushes

Both queues expose `Server.FlushDatabase(mode)` and `Server.FlushAll(mode)`, returning
`RespirePending<bool>` (`true` for an OK reply). `ServerFlushMode.Default` uses the server's
configured behavior; `Sync` and `Async` explicitly select memory reclamation. All modes remove
keys logically before replying. `RespireOptions.AllowAdmin` must be enabled before enqueueing.
A client key prefix does not restrict either command's database-wide or server-wide scope.

Queued flushes affect only their execution node. In Cluster batches, keyless flushes form
a separate routing group with no ordering guarantee relative to keyed groups. Transactions
flush their selected node, including when keys queued later select that node. Use immediate
`redis.Server.FlushDatabaseAsync(mode, cancellationToken)` or `FlushAllAsync(mode, cancellationToken)` to visit all discovered
primaries; that fan-out is not atomic across the Cluster.

The raw `Execute` queue method supports known nonblocking command forms; see
[deferred raw commands](./deferred-raw-commands.md).

## Deferred Streams

Both queues also expose nonblocking `Streams.Read` for one or multiple same-slot streams,
with owned results and optional per-stream count. Blocking waits and continuous enumeration
remain immediate-client operations. See [stream reads](../commands/collections.md#reading-without-consumer-groups).

Both queues expose `Streams.Add`, `Count`, `Range`, `Remove`, `Trim`, `TrimByMaxLength`, and
`Acknowledge`, corresponding to XADD, XLEN, XRANGE/XREVRANGE, XDEL, XTRIM, and
XACK. Parameters mirror the immediate methods without cancellation tokens. `Add`
accepts `StreamAddOptions`; that overload returns a nullable id when NOMKSTREAM skips
an absent stream. `Range` supports inclusive bounds, count, and descending order.
`Trim` accepts `StreamTrimOptions` for MAXLEN/MINID and approximate LIMIT; see
[stream trimming](../commands/collections.md#trimming) for validation and defaults.

```csharp
await using var transaction = redis.CreateTransaction();
transaction.Set("{order:42}:state", "ready");
RespirePending<RespireStreamId> appended = transaction.Streams.Add(
    "{order:42}:events", ("type", "ready"));
await transaction.CommitAsync();
Console.WriteLine(appended.Result);
```

MULTI/EXEC applies the state write and event append atomically. Redis execution-time
errors do not roll back other transaction commands. Pending results cannot supply
arguments to later queued operations: use explicit entry ids when subsequent work
needs the id before execution. Inputs are copied or serialized at enqueue time; later changes
to supplied binary keys, values, or arrays do not change queued commands. Range
results own their field bytes and remain readable after queue disposal.

Stream keys receive the client prefix exactly once. Cluster batches route each
command by its stream key; Cluster transactions require every queued key to share
one hash slot, including commands on other facets. Group setup and reading remain
immediate operations; `Acknowledge` only acknowledges existing pending entries.
`IRespireCommandQueue` implementers must add the new `Streams` property.

## Deferred script result ownership

`batch.Scripts.Evaluate(...)` and `transaction.Scripts.Evaluate(...)` copy replies, including
nested arrays and payloads, into GC-owned storage. Their `RespireResult` does not retain pooled
reply buffers, even if you never read the pending result or another queued command fails.
Disposal is optional for these deferred results. If you dispose a root result, its nested views
become invalid too. Disposing the batch or transaction does not invalidate a successful result.

Immediate `redis.Scripts.ExecuteAsync(...)` and raw `redis.ExecuteAsync(...)` results remain
pooled leases and must be disposed with `using`.

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

When client-side caching is enabled, a successful WATCH invalidates the watched keys locally
and prevents earlier reads from restoring cached values. Reads started after transaction creation
therefore fetch fresh state even if tracking invalidations from earlier writes are still in transit.

## Automatic WATCH conflict retries

`RunTransactionAsync` creates a fresh watched transaction for every attempt and retries only
when `EXEC` reports a WATCH conflict. The callback reads inputs and queues writes; the helper
commits and disposes the transaction. `MaxAttempts` includes the first attempt and defaults
to five. Exhaustion throws `RespireTransactionConflictException` with its `Attempts` count.

```csharp
var reads = redis.WithReadFrom(RespireReadFrom.Primary).WithoutClientCache();
long updated = await redis.RunTransactionAsync(
    ["balance"],
    async (transaction, token) =>
    {
        var current = long.Parse(await reads.GetStringAsync("balance", token) ?? "0");
        transaction.Set("balance", current - 100);
        return current - 100;
    },
    new RespireTransactionRetryOptions
    {
        MaxAttempts = 5,
        Backoff = RespireTransactionRetryOptions.ExponentialBackoff(
            TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(1)),
    },
    cancellationToken);
```

Read again inside every callback using the primary and bypassing local caches. Do not commit
or dispose the supplied transaction. A callback may run multiple times, so keep external
side effects outside it. The generic overload returns only the successful attempt's result;
queued pending values cannot be inspected until after commit. The non-generic overload
accepts a callback without a result. WATCH key arrays and binary key storage are copied before
the first await, so later caller changes cannot change the keys being watched on a retry.
Callbacks that queue no commands still validate WATCH through an empty MULTI/EXEC before
returning a decision. The helper does not enforce the routing or caching settings of clients
captured by the callback; supplying a safe read view directly is tracked in
[#949](https://github.com/thomhurst/Respire/issues/949).

Backoff receives the one-based failed attempt number and returns a nonnegative delay of at
most 2,147,483,647 milliseconds. The failed transaction is disposed before the delay; caller
cancellation interrupts delays and subsequent attempts. Invalid delays and backoff exceptions
propagate. `ExponentialBackoff` doubles the delay ceiling per conflict up to `maxDelay`,
then picks a random delay from zero to that ceiling to spread concurrent retries.
Callback exceptions, network failures, timeouts, cancellation, Redis errors, and
Cluster routing rejections are not retried. A lost commit reply remains ambiguous and may
represent an executed transaction. Errors in an executed result array remain on its pendings.

The `Respire` meter emits `respire.transaction.watch.conflicts` for each discarded attempt
and `respire.transaction.watch.retries` for additional attempts started after conflicts.
Diagnostics listener failures do not interrupt the operation.

## Cluster WATCH transactions

Use matching hash tags for every watched and queued key, such as `{account:42}:balance`
and `{account:42}:history`. Respire validates watched keys before discovery or network I/O,
and rejects a cross-slot queued command before adding it to the transaction. Validation uses
keys after the client prefix is applied, including hash tags inside that prefix.

The slot owner's dedicated connection holds WATCH state through MULTI/EXEC. Successful commits
and watched aborts return the connection to its original node pool. Cancellation, connection
failure, or disposal before EXEC discards the connection so another caller cannot inherit WATCH.

MOVED, ASK, or READONLY rejections during WATCH or transaction queueing throw
`RespireTransactionRetryException`. Its `ServerError` preserves the original rejection. Start a
new watched transaction and re-read every input; never replay queued writes using old reads.
MOVED updates the learned route for that next attempt. ASK leaves the permanent route unchanged,
so an attempt may keep failing until migration finishes. Use a bounded retry policy and delay.

```csharp
bool committed = false;
for (var attempt = 0; attempt < 5 && !committed; attempt++)
{
    try
    {
        await using var transaction = await redis.CreateTransactionAsync(
            ["{account:42}:balance"], cancellationToken);
        long current = await redis.GetAsync<long>("{account:42}:balance", cancellationToken);
        transaction.Set("{account:42}:balance", current - 100);
        transaction.Set("{account:42}:history", "withdrawal");
        committed = await transaction.CommitAsync(cancellationToken);
    }
    catch (RespireTransactionRetryException) when (attempt < 4)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
    }
}
if (!committed) throw new InvalidOperationException("Watched keys kept changing.");
```

A socket failure or timeout after sending remains ambiguous and does not become this retry
exception. Errors inside an executed result array stay on their individual pending results;
other commands may have succeeded, so Respire never replays that array. These restrictions
preserve Redis's [WATCH semantics](https://redis.io/docs/latest/commands/watch/) and
[same-slot transaction requirement](https://redis.io/docs/latest/operate/oss_and_stack/reference/cluster-spec/).

## Redis Functions

`Functions.Execute` and its typed, string, integer, and span variants queue Redis 7+
`FCALL` or `FCALL_RO`. Deferred results own managed storage, like deferred script results.
Keep binary argument memory unchanged until execution completes.

Load reusable `RespireFunctionLibrary` instances before queue execution. Deferred calls
never automatically reload a missing function or replay a transaction. Successful commands
remain applied when another command reports an error. `Functions.Load`, `List`, `Delete`,
`Flush`, `Dump`, `Restore`, and `Stats` operate only on their execution node. A Cluster batch's
keyless administration group can differ from a keyed function's group; immediate
`redis.Functions.LoadAsync(library)` loads every discovered primary before queueing calls.
