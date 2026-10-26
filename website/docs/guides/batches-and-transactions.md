---
title: Batches and transactions
description: Flush commands together and execute atomic Redis transactions.
---

# Batches and transactions

Respire pipelines concurrent commands automatically. Use a batch when sequential code should queue several commands and send them in one flush. Use a transaction when they must run atomically.

## Observe or reject explicit executions

Set `RespireOptions.PipelineHook` to audit, time, or reject explicit batches and transaction
commits. Client views share the hook. The default is `null`: execution allocates no hook state
and uses the existing async path without a hook wrapper.

`RespireOptions.CommandHook` also applies to each explicit member. If both hooks are set,
aggregate admission runs first, then member admissions in queue order, all before any send.
Member completions run after cleanup and before aggregate completion. See
[aggregate command hooks](command-hooks.md#explicit-aggregate-members-and-watch) for rejection,
durability and watched-abort outcomes. Both hooks default to `null`.

<!-- doc-test-tail-declaration: split-before=sealed class AuditPipelineHook -->
```csharp
using System.Diagnostics;

await using var redis = await RespireClient.ConnectAsync(new RespireOptions
{
    Endpoints = [new("localhost", 6379)],
    PipelineHook = new AuditPipelineHook(),
});

sealed class AuditPipelineHook : RespirePipelineHook
{
    public override object? OnExecuting(RespirePipelineContext context)
    {
        if (context.Commands.Any(name => name is "FLUSHDB" or "FLUSHALL"))
            throw new InvalidOperationException("Flush commands are forbidden in this execution.");
        return Stopwatch.GetTimestamp();
    }

    public override void OnCompleted(RespirePipelineContext context, object? state,
        RespirePipelineOutcome outcome, Exception? error)
    {
        var elapsed = state is long started ? Stopwatch.GetElapsedTime(started) : TimeSpan.Zero;
        Console.WriteLine($"{context.Commands.Count} commands: {outcome} in {elapsed}; {error?.Message}");
    }
}
```

Admission runs synchronously once after lifecycle validation, before connection acquisition or
submission of any queued command. `Commands` is an immutable ordered snapshot of command names;
durability batches append `WAIT` or `WAITAOF`. It contains no keys or values. Transaction framing
(`MULTI`/`EXEC`) is represented by `IsTransaction`, rather than extra list entries. You may retain
the context after completion.

Throwing from admission rejects the whole execution and faults every pending with that exception.
`TryExecuteAsync` returns those failures; `ExecuteAsync`, durability methods, and transaction
commits throw. An empty batch's admission rejection also throws because there is no pending in
which to return a failure. Rejected executions are consumed and cannot be resubmitted.

Completion runs once with the state returned by admission (or `null` when admission threw):

| Outcome | Meaning |
| --- | --- |
| `Succeeded` | All queued results and any acknowledgement succeeded. |
| `Failed` | Execution failed, or at least one queued result failed, including conversion errors. |
| `Canceled` | The first execution failure was an `OperationCanceledException`. Timeouts are `Failed`. |
| `Aborted` | A watched commit returned `false`. |
| `Rejected` | Admission threw, including if the callback threw cancellation. |

The completion error is the first failure in queue order when several members fail. A transaction
can commit successfully and still report `Failed` because a member's pending contains a Redis
error. Completion runs after result publication and execution cleanup. Completion exceptions are
ignored so they cannot replace the result or leak response resources. Record observer errors
inside your hook if you need to diagnose them.

Internal retries and redirects are enclosed by the same pair of callbacks. Empty valid executions
also invoke the hook. Invalid lifecycle calls and failed preflight validation do not. An already
canceled ordinary execution still reaches admission; durability preflight can reject cancellation
before admission. A hook sees the caller's token and cannot replace it.

Hooks must be thread-safe: multiple batches and client views may invoke the same instance
concurrently. Keep callbacks short and nonblocking. A callback may start a separate execution,
which invokes the hook independently; guard against recursion in your own code. Do not mutate,
dispose, or resubmit the execution being observed.

This hook covers explicit batches, durability batches, transaction commits, and batches/commits
created by hash import sessions. `WATCH` and import-session setup occur before this boundary.
An external transaction retry helper creates a new transaction for each attempt, so each commit
has its own callbacks. Individually dispatched commands, automatic pipelining, cache hits, hedges,
subscriptions, and streaming operations outside an explicit batch do not invoke this hook. It is
**not a client-wide command authorization policy**; logical command interception is tracked
separately in [#1685](https://github.com/thomhurst/Respire/issues/1685).

## Batch one flush

```csharp
await using var batch = redis.CreateBatch();

RespirePending<string?> name = batch.GetString("name");
RespirePending<long> visits = batch.Increment("visits");

await batch.ExecuteAsync();

Console.WriteLine($"{name.Result}: {visits.Result}");
```

Always declare batches with `await using var`. Disposing a batch that never executed faults its pendings, for example after an early return or exception. After execution, disposal keeps all results and errors. Repeated disposal is safe.

A `RespirePending<T>` is not awaitable. Awaiting `ExecuteAsync` or `CommitAsync` runs the commands; then read the pending's `.Result`. For exception-free handling, inspect `Status`, `HasResult`, or `Error`, or call `TryGetResult`.

- Reading `.Result` before `ExecuteAsync` throws `RespirePendingNotReadyException`.
- A failed command sets `Status` to `Faulted` and puts its exception in `Error`.

### Ignoring queued results

You can ignore a queued result when you only need the command to execute:

```csharp
await using var batch = redis.CreateBatch();
batch.Expire("session:42", TimeSpan.FromMinutes(30));
await batch.ExecuteAsync(cancellationToken);
```

Because a pending is not awaitable, ignoring one raises no CS4014 warning and needs no discard.

Keep the pending in a variable when its value matters, then read `.Result` or use `TryGetResult` after execution. The RESP002 analyzer warns about `.Result` reads before execution when it can track the queue and pending within the same method.

### Failures

`ExecuteAsync` completes every pending, then throws the first failure in queue order. It does not stop sending at the first error or roll back successful commands, whose results stay readable. This applies to command errors, conversion failures, timeouts, cancellation, and connection-acquisition failures.

`RespireBatchResult` summarizes the flush with `Count`, `FailureCount`, `FirstError`, and `Failures`. Each failure has the command's queue index, operation name, and exception. Use `TryExecuteAsync` to get the summary without rethrowing. Disposed or already-sent batches still throw.

```csharp
await using var batch = redis.CreateBatch();
batch.GetString("name");
batch.Increment("visits");
RespireBatchResult result = await batch.TryExecuteAsync();
foreach (var failure in result.Failures)
{
    Console.WriteLine($"Command {failure.Index} ({failure.Operation}): {failure.Error.Message}");
}
```

Call `result.ThrowIfAnyFailed()` to rethrow later.

A batch is sent in one flush when the standalone connection is idle, the circuit breaker is disabled, and the batch fits `MaxInflightCommands`. Otherwise, commands are admitted in queue order and can span several flushes.

### Related

- To wait for WAIT or WAITAOF acknowledgements of a batch's writes, use [`ExecuteAndWaitForReplicationAsync` or `ExecuteAndWaitForAofAsync`](durability-acknowledgements.md). They keep the writes and acknowledgement on one dedicated connection.
- For Redis 8.10 HIMPORT, create batches or transactions through a [hash import session](hash-import.md) to keep connection-local fieldsets.

## The same facets as the client

Batches and transactions expose `Strings`, `Keys`, `Hashes`, `Lists`, `Arrays`, `Sets`, `SortedSets`, `Bitmaps`, `HyperLogLog`, `Geo`, `VectorSets`, `Scripts`, `Functions`, the nonblocking `Streams` subset, and a small `Server` subset.

- Methods match the client's names and parameters, minus the `Async` suffix. The missing suffix signals that a call only queues work.
- They return `RespirePending<T>` instead of `ValueTask<T>`.
- They take no `CancellationToken`; `ExecuteAsync` and `CommitAsync` own cancellation.
- Exception: deferred scripts use `Evaluate`, not the client's `ExecuteAsync` variants.

```csharp
await using var batch = redis.CreateBatch();

RespirePending<long> pushed = batch.Lists.RightPush("queue", "job-1", "job-2");
RespirePending<bool> stored = batch.Hashes.Set("user:1", "name", "Ada");
RespirePending<long> ranked = batch.SortedSets.Add(
    "leaderboard", ("ada", 42));

await batch.ExecuteAsync();
```

`Keys.RenameAsync`, `Lists.TrimAsync`, `HyperLogLog.MergeAsync`, and `Strings.SetManyAsync` return `ValueTask<bool>`, matching their queued `RespirePending<bool>`. They return `true` only on `OK`. An unexpected reply or Redis error faults the operation instead of returning `false`.

### Typed values

Typed overloads match the client: hash `Set<T>` and `TryGet<T>`, set `Contains<T>`, sorted-set `Add<T>`, and list `LeftPop<T>` / `RightPop<T>`.

- Values are serialized when queued. Results are deserialized after execution.
- Byte-backed arguments, including typed `byte[]` and `ReadOnlyMemory<byte>`, borrow their storage. Keep the bytes unchanged until execution or commit completes, or copy them before queuing.
- Hash `TryGet<T>` keeps the `Found` flag, so a missing field differs from a stored default.
- Typed list pops return the default value for an empty list.
- Booleans in set membership and sorted-set adds use Redis `1`/`0`, as in the client.

:::warning Omitting the type argument changes the encoding
Without a type argument, deferred hash writes, set membership checks, and sorted-set adds use the raw `RespireValue` conversion. This covers GUIDs, timestamps, durations, and byte buffers. The immediate facet uses typed serialization instead.

When you move `client.Hashes.SetAsync(key, field, id)` into a queue, write `batch.Hashes.Set<Guid>(key, field, id)` to keep the same encoding. The inferred `batch.Hashes.Set(key, field, id)` sends raw GUID text.

This relies on C# 13 or later, as do the library's `params` span APIs. Older compilers ignore overload priority and may pick a different encoding; specify the type argument or `RespireValue` conversion explicitly.
:::

### Shared helpers

Both queue types implement `IRespireCommandQueue`, which covers every deferred facet and the root shortcuts. A helper can queue work without choosing a batch or transaction:

<!-- doc-test-declaration: split-before=await using var batch -->
```csharp
static void QueueUserUpdate(IRespireCommandQueue queue, string userId)
{
    queue.Hashes.Set($"user:{userId}", "status", "active");
    queue.Expire($"user:{userId}", TimeSpan.FromHours(1));
}

await using var batch = redis.CreateBatch();
QueueUserUpdate(batch, "42");
await batch.ExecuteAsync();

await using RespireTransaction transaction = redis.CreateTransaction();
QueueUserUpdate(transaction, "43");
await transaction.CommitAsync();
```

Batches execute with `ExecuteAsync`; transactions with `CommitAsync`.

### Client-only operations

These have no deferred form:

- Blocking variants (a `waitFor` argument, such as `BLPOP` / `BLMOVE`). A queue cannot block.
- Streaming operations (`Keys.ScanAsync`, `Strings.GetLeaseAsync`). A lease borrows reply memory that is released when the batch completes.
- `Locks`, and server administration other than flushes and filtered CLIENT LIST/KILL.
- Blocking stream reads, consumer loops, claims, and group administration.

## Deferred server flushes

Both queues expose `Server.FlushDatabase(mode)` and `Server.FlushAll(mode)`, which return `RespirePending<bool>` (`true` for `OK`). Enable `RespireOptions.AllowAdmin` before queuing them.

- `RespireServerFlushMode.Default` uses the server's configured behavior. `Sync` and `Async` choose how memory is reclaimed. All modes remove keys logically before replying.
- A client key prefix does not narrow the database-wide or server-wide scope.
- A queued flush affects only its execution node. In Cluster batches, keyless flushes form a separate routing group, with no ordering relative to keyed groups. Transactions flush their selected node, including a node selected by keys queued later.
- To flush every discovered primary, use immediate `redis.Server.FlushDatabaseAsync(mode, cancellationToken)` or `FlushAllAsync(mode, cancellationToken)`. That fan-out is not atomic across the Cluster.

`Server.Clients(filter)` and `Server.KillClients(filter)` queue typed CLIENT LIST/KILL on the execution node. Filters and client IDs are server-local; a key prefix does not restrict them. KILL requires `AllowAdmin` and returns the number closed. See [client filters](./client-administration.md#filtering-and-closing-clients) for scope, snapshots, and Redis/Valkey versions.

The raw `Execute` queue method supports known nonblocking command forms; see [deferred raw commands](./deferred-raw-commands.md).

## Deferred Streams

Both queues expose nonblocking `Streams.Read` and `Streams.ReadGroup` for one or more same-slot streams, with owned results. Both accept `RespireStreamReadOptions`, but reject `WaitFor` when queued. See [stream reads](../commands/collections.md#reading-without-consumer-groups).

| Method | Command |
| --- | --- |
| `Add` | XADD |
| `Count` | XLEN |
| `Range` | XRANGE / XREVRANGE |
| `Remove` | XDEL, or XDELEX with a `RespireStreamReferencePolicy` |
| `Trim`, `TrimByMaxLength` | XTRIM |
| `Acknowledge` | XACK |
| `AcknowledgeAndRemove` | XACKDEL |
| `NegativeAcknowledge` | XNACK |
| `Configure` | XCFGSET |
| `Read`, `ReadGroup` | XREAD, XREADGROUP (nonblocking) |

- Parameters match the immediate methods, without cancellation tokens.
- `Add` with `RespireStreamAddOptions` returns a nullable ID, null when NOMKSTREAM skips an absent stream.
- `Range` supports inclusive bounds, count, and descending order.
- `Trim` accepts `RespireStreamTrimOptions`; see [stream trimming](../commands/collections.md#trimming).

```csharp
await using var transaction = redis.CreateTransaction();
transaction.Set("{order:42}:state", "ready");
RespirePending<RespireStreamId> appended = transaction.Streams.Add(
    "{order:42}:events", ("type", "ready"));
await transaction.CommitAsync();
Console.WriteLine(appended.Result);
```

- MULTI/EXEC applies the state write and event append atomically. Redis execution-time errors do not roll back other commands.
- A pending result cannot supply arguments to later queued commands. Use explicit entry IDs when later work needs the ID.
- Inputs are copied or serialized when queued; later changes to your keys, values, or arrays do not affect queued commands.
- `Range` results own their bytes and stay readable after queue disposal.
- Cluster batches route each command by its stream key. Cluster transactions require every queued key, on any facet, to share one hash slot.
- Group setup is immediate only. `Acknowledge` only acknowledges existing pending entries.

## Deferred script result ownership

Queued `Scripts.Evaluate(...)` copies replies, including nested arrays and payloads, into GC-owned storage. These results hold no pooled buffers, even if you never read them or another command fails, so disposing them is optional.

- Disposing a root result also invalidates its nested views.
- Disposing the batch or transaction does not invalidate a successful result.

Immediate script, function, and raw `ExecuteAsync(...)` results also use GC-owned storage.
Their explicit `ExecuteLeaseAsync(...)` alternatives return pooled leases that you must dispose with `using`.

Use `await using` for both batches and transactions, including watched transactions.
An unsent batch faults its pending results on disposal; a completed batch preserves them.
Synchronous `using` remains supported for existing batch code.

## Atomic transactions

```csharp
await using RespireTransaction transaction = redis.CreateTransaction();

RespirePending<long> balance = transaction.Increment("balance", -100);
transaction.Lists.RightPush("audit", "withdraw:100");

await transaction.CommitAsync();
Console.WriteLine(balance.Result);
```

- A transaction runs on one connection as `MULTI` / `EXEC`. Its commit returns no result: without `WATCH`, `EXEC` cannot abort.
- Always commit or dispose it, so its buffer and any WATCH connection are released. `await using` covers early returns and exceptions. Disposal after commit does nothing.
- Committing an empty transaction sends nothing.

For library code that enables CA2007, configure both asynchronous disposal and the commit await. Keep the transaction in its own variable so its command methods remain available:

```csharp
var transaction = redis.CreateTransaction();
await using var lifetime = transaction.ConfigureAwait(false);
transaction.Expire("session:42", TimeSpan.FromMinutes(30));
await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
```

The same disposal pattern works for batches and watched transactions. A watched transaction's asynchronous creation can also use `.ConfigureAwait(false)`.

## Conditional transactions (Valkey 9.2+)

Pass a `RespireCondition` to an unwatched transaction's `CommitAsync` to check current server state immediately before EXEC. This avoids WATCH and its dedicated connection lease.

```csharp
await using var transaction = redis.CreateTransaction();
var balance = transaction.Set("{account}:balance", "90");
transaction.Set("{account}:version", "2");
bool committed = await transaction.CommitAsync(
    RespireCondition.ValueEquals("{account}:version", "1"), cancellationToken);
```

Conditions map directly to [Valkey EXEC options](https://valkey.io/commands/exec/):

- `ValueEquals(key, expected)`: `IFEQ`; requires an existing string with equal bytes.
- `ValueNotEquals(key, expected)`: `IFNE`; requires a missing key or a string with different bytes.
- `KeyNotExists(key)`: `NX`; requires a missing key.
- `KeyExists(key)`: `XX`; requires an existing key of any type.

To require several conditions, pass a nonempty collection: `await transaction.CommitAsync([condition1, condition2], cancellationToken)`. Every condition must match. Conditions inspect state before queued commands execute. Values use raw wire bytes, independent of serialization. Condition creation copies binary keys and operands; transaction keys receive the client's key prefix.

A false result discards every queued command. Pendings have `Status == RespirePendingStatus.Aborted`; reading their results throws `RespireTransactionAbortedException`, as with a WATCH abort. Wrong-type comparisons and other server errors remain errors.

Respire checks `INFO SERVER` on the selected physical connection before sending MULTI and caches support on that connection. Redis and older Valkey throw `NotSupportedException`. INFO permission is required; a denied probe also throws `NotSupportedException` with the server error as its inner exception. There is no WATCH fallback. Empty conditional transactions still evaluate their conditions. Conditional commits do not use automatic command retries and are unavailable for hash import sessions.

In Cluster, every condition key and queued command key must share one hash slot, including after prefixing. Use matching hash tags such as `{account}`. Cross-slot conditions fail locally before MULTI; the transaction remains available for a corrected commit.

## Transient retries for batches and transactions

With `RespireOptions.RetryPolicy` set, batches and unconditional unwatched transactions retry as whole units. The client never resends only the failed commands.

- A standalone or Sentinel batch resends its whole pipeline. A Cluster batch retries each slot group separately. Pendings stay incomplete until the final decision, and only the final attempt's results are published.
- An unwatched transaction resends `MULTI`, its commands, and `EXEC` together, on standalone, Sentinel, and Cluster. A lost `EXEC` reply counts as a possible execution, not an abort.
- A resend needs a selected transient failure, and the policy must allow every command's retry category for the unit's write outcome. A single `INCR`, or a raw command without a declared category, keeps the unit to one send unless you accept that risk explicitly.
- Watched transactions retry only through the [automatic watched transaction retry](#automatic-watched-transaction-retries) helpers. Hash import sessions and units that continue a server-local cursor never retry.

See the [command retries guide](./command-retries.md#batches) for full rules and telemetry.

## Optimistic concurrency

Create the watched transaction first, read current values through the client, and queue only the writes on the transaction. Transaction reads return `RespirePending<T>` values that you cannot inspect until after commit, so they cannot drive the decision.

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

`false` means a watched key changed before `EXEC`. Each pending then has `Status == RespirePendingStatus.Aborted`, and reading its result throws `RespireTransactionAbortedException`. Dispose that attempt, create a new watched transaction, re-read state, and retry a bounded number of times. For complex compare-and-set logic, a Lua script often saves round trips.

With client-side caching, a successful WATCH invalidates the watched keys locally. Reads started after the transaction is created fetch fresh state.

### WATCH cost and pool sizing

`CreateTransactionAsync` and `RunTransactionWithReadsAsync` hold a dedicated connection
from WATCH until commit or disposal. A completed EXEC, including a WATCH conflict,
returns a healthy connection to the pool for reuse. An abandoned transaction, canceled
operation, or failed send discards its connection because WATCH state or unread replies
may remain. Always commit or dispose each attempt promptly.

Even with a warm pool, WATCH needs a round trip before callback reads, each awaited read
needs its own round trip, and the pipelined MULTI/EXEC commit needs another. A new dedicated
connection also pays connection and protocol setup costs. Connecting the client opens its
multiplexed connections; it does not prewarm the dedicated pool. Prefer a Lua script for
hot read-modify-write paths when the whole operation can run on the server. Pool reuse
cannot remove WATCH's extra round trips.

The default `DedicatedPool` retains four idle connections and has no total connection cap.
A burst of 64 watched transactions can therefore create 64 dedicated connections and close
60 when they return. Another burst can repeat that setup work. Raising `MaximumIdle`
retains more connections between bursts; it neither prewarms them nor limits concurrency.

For a WATCH-heavy workload, start with a bounded pool and retain the same number of idle
connections. For example, this allows up to eight dedicated connections per pool and
reuses them after a burst:

```csharp
var options = new RespireOptions
{
    DedicatedPool = new RespireDedicatedPoolOptions
    {
        MaximumSize = 8,
        MaximumIdle = 8,
    },
};
```

Eight is a starting point, not a throughput guarantee. Measure transaction throughput and
ordinary command latency with your concurrency, network latency, and server. Increase
both bounds if acquisition queueing dominates and the server has capacity. Set
`MaximumIdle` to your measured concurrent demand if retaining a larger warm pool is more
useful than limiting concurrency. `MinimumSize` only protects existing connections from
timed idle retirement; it does not prewarm them.

At `MaximumSize`, another WATCH waits for a lease. Pass a cancellation deadline to
`CreateTransactionAsync` or `RunTransactionWithReadsAsync` to bound that wait. Blocking
commands and other dedicated operations share pool capacity, so avoid holding every lease
while awaiting another dedicated operation. See [dedicated connection pools](../fundamentals/connections.md#dedicated-connection-pools)
for limits and lifetime settings.

Ordinary multiplexed reads use separate connections and do not wait for a dedicated lease.
They still share server and client resources with connection setup and transaction traffic,
so connection churn can affect their latency. A larger idle pool alone cannot guarantee
ordinary read latency under load; measure both paths when tuning the total cap.

## Automatic watched transaction retries

`RunTransactionWithReadsAsync` creates a fresh watched transaction for each attempt and retries when `EXEC` reports a WATCH conflict. Your callback reads inputs and queues writes; the helper commits and disposes the transaction.

```csharp
long updated = await redis.RunTransactionWithReadsAsync(
    ["balance"],
    async (reads, transaction, token) =>
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

| `RespireTransactionRetryOptions` | Default | Meaning |
|---|---|---|
| `MaxAttempts` | 5 | WATCH-conflict attempts, including the first. Exhaustion throws `RespireTransactionConflictException` with an `Attempts` count. |
| `Backoff` | null (no delay) | Receives the one-based failed attempt number and returns a delay from zero to 2,147,483,647 ms. Invalid delays and backoff exceptions propagate. |

`ExponentialBackoff(baseDelay, maxDelay)` doubles the delay ceiling per conflict up to `maxDelay`, then picks a random delay between zero and that ceiling. The failed transaction is disposed before the delay. Cancellation interrupts delays and later attempts.

Callback rules:

- Read every input through the supplied read view (`reads` above), and include those keys in WATCH. The view routes to the primary, bypasses the local cache, and keeps the client's key prefix, database, and serializer.
- The helpers cannot enforce routing or caching on other clients your callback captures, and cannot stop writes through the read view. Queue writes on the transaction.
- The view shares the caller's connections; the helper does not dispose the client. Custom clients must support `WithReadFrom` and `WithoutClientCache`, or the helper fails without retrying.
- Do not commit or dispose the supplied transaction.
- The callback can run several times. Keep external side effects outside it.
- The generic overload returns the successful attempt's result. The non-generic overload takes a callback with no result.
- WATCH keys, including binary key storage, are copied before the first await, so later changes cannot alter the keys watched on a retry.
- A callback that queues nothing still validates WATCH with an empty MULTI/EXEC.

Use `RunTransactionAsync` when the callback does its own reads; its callback receives only the transaction and token.

### Transient failures in the helpers

With the built-in client's `RespireOptions.RetryPolicy`, both helpers retry selected transient failures during WATCH setup, the callback, or commit. Each retry replays WATCH, the whole callback with fresh reads, and EXEC. Without a policy, these failures propagate. Custom `IRespireClient` implementations keep their own timeout behavior and do not use this policy.

- The budgets are independent. `RespireTransactionRetryOptions.MaxAttempts` bounds conflict attempts; `RespireOptions.RetryPolicy.MaxAttempts` bounds transient attempts, including the first. Neither resets or consumes the other.
- Transient delays use the retry policy's backoff and jitter.
- One `CommandTimeout` deadline and your cancellation token cover setup, callbacks, commits, and both kinds of backoff. Callbacks must honor the supplied token. Caller cancellation surfaces with your original token.
- Every queued command must allow replay for the commit's write outcome. A commit that was definitely not written can retry known categories under any enabled policy. Unknown commands and scripts stay `Never` unless declared with `WithRetryCategory`.

:::warning A lost EXEC reply is not a WATCH abort
The transaction may have run, so replay needs permission for every command's repeated effects. For example, replaying a transaction that contains INCR requires `MaxCommandRetryCategory = RespireCommandRetryCategory.WriteAccumulating`. That accepts duplicate increments and lost original results. WATCH does not provide exactly-once execution.
:::

- Direct `RespireWatchedTransaction.CommitAsync` is never replayed, even with a retry policy. Use a helper for transient retries.
- Timeouts, cancellation, unselected callback or server errors, and Cluster routing rejections propagate.
- Errors in an executed result array stay on their pendings and do not replay the transaction.

| `Respire` meter instrument | Emitted for |
|---|---|
| `respire.transaction.watch.conflicts` | Each discarded attempt. |
| `respire.transaction.watch.retries` | Each extra attempt after a conflict. |
| `respire.command.retries` | Each transient helper retry, with an `attempted`, `succeeded`, or `exhausted` outcome and the attempt's highest queued retry category (read-only for an empty attempt). |

Diagnostics listener failures do not interrupt the operation.

## Cluster WATCH transactions

Use matching hash tags for every watched and queued key, such as `{account:42}:balance` and `{account:42}:history`. Respire validates watched keys before any network I/O and rejects a cross-slot command when you queue it. Validation uses prefixed keys, including hash tags inside the prefix.

The slot owner's dedicated connection holds WATCH state. Cancellation, connection failure, or disposal before EXEC discards that connection, so no other caller inherits WATCH.

MOVED, ASK, or READONLY rejections during WATCH or queuing throw `RespireTransactionRetryException`; its `ServerError` holds the original rejection. Start a new watched transaction and re-read every input. Never replay queued writes built from old reads. ASK does not update the learned route, so attempts can keep failing until migration finishes: bound retries and add a delay.

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

A socket failure or timeout after sending is ambiguous and does not throw this exception. Errors inside an executed result array stay on their pendings; other commands may have succeeded, so Respire never replays the array. See Redis's [WATCH semantics](https://redis.io/docs/latest/commands/watch/) and [same-slot transaction requirement](https://redis.io/docs/latest/operate/oss_and_stack/reference/cluster-spec/).

## Redis Functions

- `Functions.Execute` and its typed, string, integer, and span variants queue Redis 7+ `FCALL` or `FCALL_RO`. Results own managed storage, like deferred script results. Keep binary arguments unchanged until execution completes.
- Load `RespireFunctionLibrary` instances before execution. Deferred calls never reload a missing function or replay a transaction. Successful commands stay applied when another command fails.
- `Functions.Load`, `List`, `Delete`, `Flush`, `Dump`, `Restore`, and `Stats` run only on their execution node. In a Cluster batch, the keyless administration group can differ from a keyed call's group. Use immediate `redis.Functions.LoadAsync(library)` to load every discovered primary before queuing calls.
