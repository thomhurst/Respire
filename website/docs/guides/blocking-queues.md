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
    from: ListSide.Right,
    to: ListSide.Left,
    waitFor: TimeSpan.FromSeconds(30),
    cancellationToken: stoppingToken);
```

With `waitFor` this runs `BLMOVE`; without it, `LMOVE`. After processing, remove the item from `jobs:processing`. Recovery code can requeue abandoned items.

For consumer groups with acknowledgements and retry, use a [hosted stream consumer](stream-workers.md).

## Cancellation

Cancellation stops your wait. If the command already reached the server, Respire retires or cleans up the dedicated connection so the shared pool stays consistent. Use bounded waits when workers also need periodic housekeeping or health updates.

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

For streams, leave `StreamReadOptions.WaitFor` unset for immediate reads. Assigning it, including `WaitFor = null`, sets `IsBlocking` and requests a dedicated connection. Batch and transaction reads reject any explicitly assigned wait. Stream group iterators retain their periodic default when the option is omitted, respect explicit indefinite waits, and read pending entries without blocking.

`MIGRATE` I/O timeouts and `WAIT`/`WAITAOF` acknowledgement timeouts remain operation time budgets. They do not use this blocking-wait convention.
