---
title: Blocking queues
description: Use blocking list and stream commands without stalling multiplexed traffic.
---

# Blocking queues

Respire supports Redis blocking commands without letting one long wait stall unrelated requests.

## Why dedicated connections matter

A multiplexed connection serves many callers. If `BLPOP` waited on that socket, commands queued behind it could not receive replies. Respire rents a separate connection for the blocking operation while regular traffic stays on the shared multiplexer.

No second client or manual connection is required.

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

`waitFor: null` performs non-blocking `LPOP`. Supplying a duration performs `BLPOP` on a dedicated pooled connection.

## Reliable queue movement

Move a job atomically from a pending queue into a processing queue:

```csharp
string? job = await redis.Lists.MoveAsync(
    source: "jobs:pending",
    destination: "jobs:processing",
    from: ListSide.Right,
    to: ListSide.Left,
    waitFor: TimeSpan.FromSeconds(30),
    cancellationToken: stoppingToken);
```

After successful processing, remove the item from `jobs:processing`. Recovery code can requeue abandoned items.

## Cancellation semantics

Cancellation stops your wait. If a blocking command has already reached the server, Respire safely retires or cleans up the dedicated connection rather than leaving a partial protocol exchange in the shared pool.

Use bounded wait durations when workers also need periodic housekeeping or health updates.
