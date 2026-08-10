---
title: Distributed locks
description: Coordinate work with expiring, owner-checked Redis leases.
---

# Distributed locks

Respire's lock helpers use Redis leases: each lock has an owner token and an expiry. Acquisition is
`SET ... NX PX`; extension and release use server-side compare-and-update scripts, so an expired
handle cannot extend or delete a later owner's lock.

## Acquire a lock

`AcquireAsync` generates the owner token. Check `Acquired` before accessing `Lock`; disposing the
attempt releases the lock when acquisition succeeded and does nothing otherwise.

```csharp
await using var attempt = await redis.Locks.AcquireAsync(
    "locks:report",
    expiry: TimeSpan.FromSeconds(30),
    cancellationToken);

if (!attempt.Acquired)
{
    return; // another owner holds the lock
}

RespireLock mutex = attempt.Lock;
await RunReportAsync(cancellationToken);
```

When contention is exceptional, use `AcquireOrThrowAsync`. It returns the handle directly and
throws `RespireLockNotAcquiredException` when the lock is unavailable.

```csharp
await using var mutex = await redis.Locks.AcquireOrThrowAsync(
    "locks:report",
    expiry: TimeSpan.FromSeconds(30),
    cancellationToken);
```

## Wait for contention

Both acquisition styles have an overload that polls until a wait budget expires:

```csharp
await using var mutex = await redis.Locks.AcquireOrThrowAsync(
    "locks:report",
    expiry: TimeSpan.FromSeconds(30),
    wait: TimeSpan.FromSeconds(5),
    retryEvery: TimeSpan.FromMilliseconds(250),
    cancellationToken);
```

`AcquireAsync` returns an unsuccessful attempt after the budget. `AcquireOrThrowAsync` throws.
Cancellation interrupts the command, polling delay, or wait independently of contention.

## Treat the lock as a lease

The lock disappears when `Expiry` elapses, even if protected work is still running. Keep work
shorter than the lease or extend it before expiry:

```csharp
if (!await mutex.ExtendAsync(TimeSpan.FromSeconds(30), cancellationToken))
{
    return; // ownership was lost; stop protected writes
}
```

`ExtendAsync` returns `false` after expiry, release, or ownership loss. Do not retry protected
writes after that result: another process may now own the lock.

`await using` is the normal release path. Call `ReleaseAsync` explicitly when release success must
be observed; it returns whether this call deleted the lock. Disposal suppresses connection,
timeout, and disposed-client cleanup failures because the expiry remains the final safety net.

## Manage owner tokens directly

Use `TakeAsync`, `ExtendAsync`, `ReleaseAsync`, and `QueryAsync` when the token must be shared with
another process or outlive the acquiring process:

```csharp
var token = Guid.NewGuid().ToString("N");

if (await redis.Locks.TakeAsync("locks:report", token, TimeSpan.FromSeconds(30), cancellationToken))
{
    try
    {
        await RunReportAsync(cancellationToken);
    }
    finally
    {
        await redis.Locks.ReleaseAsync("locks:report", token);
    }
}
```

Keep tokens unique and secret to the owners. Release and extension succeed only when the stored
token matches. Client key prefixes apply to lock keys exactly as they do to other Respire commands.
