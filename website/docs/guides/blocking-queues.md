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
| `null` (default) | `LPOP` | `null` immediately |
| A duration | `BLPOP` | `null` after the timeout |
| `Timeout.InfiniteTimeSpan` | `BLPOP` | Waits indefinitely |

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
