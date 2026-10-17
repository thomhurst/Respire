---
title: Blocking queues
description: Use blocking list commands without stalling multiplexed traffic.
---

# Blocking queues

Respire runs blocking list commands such as `BLPOP` on a separate pooled connection, so a long wait never stalls other requests on the shared connection. You need no second client or manual connection.

## Wait for work

```csharp
while (!stoppingToken.IsCancellationRequested)
{
    string? job = await redis.Lists.LeftPopAsync(
        "jobs",
        waitFor: TimeSpan.FromSeconds(30),
        cancellationToken: stoppingToken);

    if (job is not null)
    {
        await ProcessAsync(job, stoppingToken);
    }
}
```

| `waitFor` | Command | Result when empty |
| --- | --- | --- |
| Omitted (immediate overload) | `LPOP` | `null` immediately |
| A duration | `BLPOP` | `null` after the timeout |
| Explicit `null` | `BLPOP` | Waits indefinitely |
| `TimeSpan.Zero` | `BLPOP` | Waits at least one millisecond |

`RightPopAsync` (`RPOP`/`BRPOP`) and the typed `LeftPopAsync<T>`/`RightPopAsync<T>` overloads take the same `waitFor` argument.

## Reliable queue movement

Move a job atomically from a pending queue to a processing queue:

```csharp
string? job = await redis.Lists.MoveAsync(
    source: "jobs:pending",
    destination: "jobs:processing",
    from: RespireListSide.Right,
    to: RespireListSide.Left,
    waitFor: TimeSpan.FromSeconds(30),
    cancellationToken: stoppingToken);
```

With `waitFor` this runs `BLMOVE`; without it, `LMOVE`. After processing, remove the item from `jobs:processing`. Recovery code can requeue abandoned items.

For consumer groups with acknowledgements and retry, use a [hosted stream consumer](stream-workers.md).

## Cancellation

Cancellation stops your wait. By default, Respire discards the dedicated connection. To reuse blocked connections when workers cancel frequently, opt in when creating the client:

```csharp
var options = new RespireOptions
{
    Endpoints = [new("localhost", 6379)],
    DedicatedPool = new() { UnblockOnCancel = true },
};
```

With this option, Respire records the dedicated connection's Redis client ID from `HELLO` or `CLIENT ID`. On cancellation, it sends [`CLIENT UNBLOCK <id> TIMEOUT`](https://redis.io/docs/latest/commands/client-unblock/) through a healthy multiplexed connection to the same physical server, then drains the blocking reply before returning the connection. Unblock and reply drain share a one-second budget, after which Respire discards the connection. This works with standalone, Cluster (including redirected commands), and Sentinel primary connections. If no matching control connection is available, Redis or ACLs reject the operation, or either reply fails to arrive, Respire falls back to discarding the dedicated connection. Standalone replica pools without a matching control connection also use this fallback.

Cancellation can race with completion. A reply delivered before cancellation can succeed normally. When cancellation wins, Respire reports `OperationCanceledException` even if Redis already consumed a value; cleanup drains that reply and does not restore the value. This race also exists when closing a connection. For delivery that survives cancellation, use pending-to-processing movement or consumer groups with acknowledgements.

The option defaults to `false` while performance evidence is collected. Use bounded waits when workers also need periodic housekeeping or health updates.

## Migrating blocking waits

Blocking list pops, sorted-set pops, moves, and stream reads use a required `TimeSpan? waitFor` argument. Omit the argument to select the immediate overload; pass `null` explicitly to wait indefinitely. No existing call that omits the wait starts blocking. `Timeout.InfiniteTimeSpan` is still accepted as a compatibility alias for `null`; other negative durations throw before sending.

```csharp
// Immediate (previously also selected by an explicit null).
var available = await redis.Lists.LeftPopAsync("jobs");

// Indefinite, with caller cancellation.
var next = await redis.Lists.LeftPopAsync("jobs", waitFor: null,
    cancellationToken: stoppingToken);
```

Remove explicit `waitFor: null` arguments from old calls that must stay immediate. Replace `Timeout.InfiniteTimeSpan` with `null` for indefinite waits. On overloads that previously placed an optional wait after count, side, or cursor arguments, the required wait now precedes those optional arguments; use named arguments when migrating positional calls. The single-key sorted-set `PopManyAsync(key, count, descending, waitFor)` overload already required all four arguments and keeps that order. Its immediate overload returns an entry array; use `PopManyAsync([key])` when you need the selected-key result shape.

For streams, leave `RespireStreamReadOptions.WaitFor` unset for immediate reads. Assigning it, including `WaitFor = null`, sets `IsBlocking` and requests a dedicated connection. Batch and transaction reads reject any explicitly assigned wait. Stream group iterators retain their periodic default when the option is omitted, respect explicit indefinite waits, and read pending entries without blocking.

`MIGRATE` I/O timeouts and `WAIT`/`WAITAOF` acknowledgement timeouts remain operation time budgets. They do not use this blocking-wait convention.
