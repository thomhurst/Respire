---
title: Coordination leases and fencing-token locks
description: Fencing-token locks, hash-field leases, read-write leases, Redlock, latches, semaphores and rate limiters built on Redis.
---

The optional `Respire.Coordination` package adds coordination primitives to a client. For ordinary
single-key locks, use the core client's [distributed locks](distributed-locks.md) instead.

- [Fencing-token locks](#fencing-token-locks): an expiring lease plus an increasing token that the
  protected resource checks.
- [Named leases in hash fields](#named-leases-in-hash-fields)
- [Read-write leases](#read-write-leases)
- [Multi-node Redlock](#multi-node-redlock)
- [Distributed countdown latch](#distributed-countdown-latch)
- [Distributed semaphores](#distributed-semaphores)
- [Redis-backed rate limiters](#redis-backed-rate-limits)

```bash
dotnet add package Respire.Coordination
```

With C# 14 or later, import `Respire.Coordination` and use the `client.Coordination` extension
property on `RespireClient` or `IRespireClient`. It returns one wrapper per client (key-prefixed
views get their own), including its `RateLimiters` factory, and does no network I/O. On older
language versions, use `new RespireCoordination(client)`. The coordinator never disposes the client.

## Fencing-token locks

A fencing lock carries a random `RespireLockToken` for ownership and a positive 64-bit fencing
token for the protected resource.

```csharp
using Respire.Coordination;

await using var client = await RespireClient.ConnectAsync("localhost:6379");
var coordination = client.Coordination;
await using var attempt = await coordination.TryAcquireFencedLockAsync(
    "{invoice:42}:lease", "{invoice:42}:fence", TimeSpan.FromSeconds(30));
if (!attempt.Acquired) return;

Console.WriteLine($"Acquired fencing token {attempt.Lock.FencingToken}");
// Send this token with protected writes. The receiving resource must enforce it.
```

`TryAcquireFencedLockAsync` returns immediately. If `Acquired` is `false`, accessing `Lock` throws
`RespireLockNotAcquiredException`. `RespireFencedLock` exposes `FencingToken`, `OwnerToken`,
`RemainingEstimate`, `ResetExpiryAsync`, `KeepAliveAsync`, `VerifyStillHeldAsync` and `ReleaseAsync`.

### Wait for a fenced lease

`AcquireFencedLockAsync` waits without polling. It wakes on client-cache invalidations and on the
lease key's `PTTL`, then retries the atomic acquisition script. Only the script grants ownership.
Cancellation stops waiting but does not undo an acquisition Redis already accepted.

Enable RESP3 client-side caching on the client. Redis needs no `notify-keyspace-events` flags.

```csharp
using Respire.Coordination;

await using var waitingClient = await RespireClient.ConnectAsync(new RespireOptions
{
    Endpoints = ["localhost:6379"],
    Protocol = RespProtocol.Resp3,
    ClientSideCache = new RespireClientSideCacheOptions(),
});
var waitingCoordination = waitingClient.Coordination;
await using var lease = await waitingCoordination.AcquireFencedLockAsync(
    "{invoice:42}:lease", "{invoice:42}:fence", TimeSpan.FromSeconds(30), CancellationToken.None);
```

### Keep the counter's history

- Every contender for a resource must use the same two distinct keys: the lease key (expires) and
  the counter key (persists).
- Reserve both keys for this primitive. Never delete, reset, expire or evict the counter. Use a
  non-evicting deployment with durability suited to the resource.
- An expiring, negative, malformed or overflowing counter fails acquisition without creating a lease.
- In Cluster, both physical keys (after client prefixes) must hash to the same slot; use matching
  hash tags as above. A prefix containing a hash tag also affects placement. A cross-slot pair is
  rejected without changes.
- Tokens can have gaps when execution or delivery fails. Every positive `long` value is preserved,
  including values above 2^53.
- Works on Redis 7+ and compatible Valkey; Redis 8.8 commands are not required.

### Enforce fencing at the protected resource

A fencing token does not stop an old process. The receiving resource must atomically compare each
write's token with the last accepted token. This in-memory example shows the check; real
enforcement belongs in the resource's durable transaction, shared by every writer.

<!-- doc-test-tail-declaration: split-before=public sealed class FencedRecord -->
```csharp
var record = new FencedRecord();
Console.WriteLine(record.TryWrite(12, "new owner")); // True
Console.WriteLine(record.TryWrite(11, "stale owner")); // False

public sealed class FencedRecord
{
    private readonly object _gate = new();
    private long _lastToken;
    public string? Value { get; private set; }

    public bool TryWrite(long token, string value)
    {
        lock (_gate)
        {
            if (token <= 0 || token < _lastToken) return false;
            _lastToken = token;
            Value = value;
            return true;
        }
    }
}
```

Equal tokens allow repeated writes during one lease. Release and renewal check the random ownership
token; the protected resource orders writes by the fencing token.

:::warning Not globally monotonic
Tokens increase only within a retained Redis history. Asynchronous replication can lose the counter
increment during failover, and backup restores, deletion or eviction can reuse or decrease tokens.
Use a consensus-backed fencing authority when you need monotonic fencing across failover or
rollback. See Redis's
[distributed-lock safety discussion](https://redis.io/docs/latest/develop/clients/patterns/distributed-locks/)
and [replication guarantees](https://redis.io/docs/latest/operate/oss_and_stack/management/replication/).
:::

## Named leases in hash fields

`TryAcquireLeaseAsync` stores each named lease as one hash field with its own expiry. It returns a
`RespireCoordinationLease`, or `null` when another owner holds the field. `AcquireLeaseAsync` waits
for tracking invalidations and the field's `HPTTL` deadline. Fields and hash keys accept binary
bytes. The owner token guards renewal, verification and release, so an expired owner cannot change
a replacement lease or another field.

```csharp
using Respire.Coordination;

await using var leaseClient = await RespireClient.ConnectAsync(new RespireOptions
{
    Endpoints = ["localhost:6379"],
    Protocol = RespProtocol.Resp3,
    ClientSideCache = new RespireClientSideCacheOptions(),
});
var leaseCoordination = leaseClient.Coordination;
await using var lease = await leaseCoordination.AcquireLeaseAsync(
    "coordination:leases", "worker:42", TimeSpan.FromSeconds(30));
```

Requirements:

- Redis 7.4 or later. Older servers fail before writing, with an error naming the required feature.
- The hash key must have no key-level expiry (Redis would delete the whole hash). Acquisition
  rejects expiring hash keys.
- With a concrete `RespireClient`, Redis ACL permission for `CLIENT ID` and `CLIENT KILL`, used to
  order cleanup after an uncertain acquisition.

Waiters watch the whole hash key, so a change to any field wakes every waiter on that hash for
another attempt.

## Renewal, release and uncertain outcomes

This section applies to fencing-token locks and hash-field leases.

- `ResetExpiryAsync` renews only the current owner. A fenced lock keeps its fencing token.
- `VerifyStillHeldAsync` checks ownership at one instant; it cannot vouch for a later write.
- `RemainingEstimate` is a local estimate measured from before acquisition.
  - Fenced lock: renewal after the estimate expires returns `false` without contacting Redis. A
    renewal already in flight can still return `true`.
  - Hash-field lease: renewal checks the owner field in Redis, so it can return `true` after the
    estimate expires while the field exists.
- Fenced locks support the core [managed lock lifecycle](distributed-locks.md), including
  `KeepAliveAsync`. Hash-field leases have no keep-alive; renew them explicitly before they expire.
- Managed renewal needs `CLIENT ID` and `CLIENT KILL` permissions.
- `ReleaseAsync` returns `LockReleaseOutcome.Released`, `AlreadyReleased` or `NotOwned`. Disposing a
  copied attempt is idempotent and never deletes another owner's lease.

Stop protected work when ownership expires or becomes uncertain.

Cancellation, timeout or disconnect can happen after Redis accepts an acquisition:

- Fenced lock: the counter may skip a value, and the unreturned lease expires after its duration.
- Hash-field lease: Respire does not replay the command; it tries an owner-checked release. If Redis
  is unreachable, the lease expires after its duration.
- A reply that arrives after the local estimate has elapsed is not reported as acquired.

When a hash-field lease's renewal or release outcome is uncertain, the handle fails closed: renewal
returns `false`, because an earlier renewal may still run and shorten the expiry. An owner-checked
release can settle ownership. A release queued behind an in-flight operation retries after it, even
past its own wait bound. Create a new handle only after Redis reports this owner no longer holds
the lease.

## Read-write leases

Immediate shared-read and exclusive-write leases:

```csharp
using Respire.Coordination;

var coordination = redis.Coordination;
await using var read = await coordination.TryAcquireReadLockAsync(
    "{account:42}:rw", TimeSpan.FromSeconds(30));
if (!read.Acquired) return;
// Multiple read leases can coexist. A write attempt succeeds only after all readers release or expire.
```

- `TryAcquireWriteLockAsync` takes an exclusive lease. Both methods return immediately on
  contention; they do not queue, poll or promise fairness.
- Renew with `ResetExpiryAsync`, check with `VerifyStillHeldAsync`, release with `ReleaseAsync` or
  `DisposeAsync`.
- A failed or uncertain renewal marks the handle lost. A failed verification leaves it unchanged, so
  release still removes the entry.
- `DisposeAsync` releases best-effort and ignores failures; an entry it misses blocks others until
  it expires. Call `ReleaseAsync` when you need to know release succeeded, and prefer short leases.
- Use a dedicated key. Client prefixes, binary keys and Cluster work without hash tags.
- After cancellation or a late reply, the client sends an owner-checked release. If the reply is
  lost, the entry expires after the requested duration; the client does not replay.
- Asynchronous failover can restore older lock state; this is not consensus-safe.

Overlapping readers keep being admitted, so a steady stream of readers can starve writers. Bound
writer retries with backoff:

```csharp
using Respire.Coordination;

var coordination = redis.Coordination;
for (var delay = TimeSpan.FromMilliseconds(50); ; delay *= 2)
{
    await using var write = await coordination.TryAcquireWriteLockAsync(
        "{account:42}:rw", TimeSpan.FromSeconds(30), cancellationToken);
    if (write.Acquired)
    {
        // Exclusive work here.
        break;
    }
    if (delay > TimeSpan.FromSeconds(2)) throw new TimeoutException("Write lease unavailable.");
    await Task.Delay(delay, cancellationToken);
}
```

## Multi-node Redlock

`RespireRedlockGroup` provides a quorum lease across an odd number (at least three) of independent
standalone Redis deployments. Each client must connect to a different deployment; the group never
disposes them. Acquisition runs on all nodes in parallel and succeeds only when a majority succeeds
with positive validity after elapsed time and clock drift. Failed attempts release on every
reachable node.

| `RespireRedlockOptions` | Default | Meaning |
| --- | --- | --- |
| `NodeTimeout` | 1 second | Per-node operation bound |
| `DriftFactor` | 0.01 | Clock-drift allowance |

```csharp
using Respire.Coordination;

await using var first = await RespireClient.ConnectAsync("redis://node-a:6379");
await using var second = await RespireClient.ConnectAsync("redis://node-b:6379");
await using var third = await RespireClient.ConnectAsync("redis://node-c:6379");
var group = new RespireRedlockGroup([first, second, third]);

await using var attempt = await group.TryAcquireAsync("invoice:42", TimeSpan.FromSeconds(10));
if (!attempt.Acquired) return;

Console.WriteLine($"Estimated lease time: {attempt.Lock.RemainingEstimate}");
// Complete protected work within the estimated validity.
```

- Accessing `Lock` on an unsuccessful attempt throws `InvalidOperationException`.
- `ResetExpiryAsync` renews on a quorum before validity expires. A renewal that no quorum confirms
  (including timeouts or cancellation) ends the lease and removes the token best-effort from every
  node. Acquire again to continue.
- `ReleaseAsync` removes the token from all nodes within `NodeTimeout`; its cancellation token only
  bounds the wait for a concurrent renewal. It returns `true` when a quorum replies. On `false`, the
  handle stops reporting ownership and keeps its token so you can retry.
- `DisposeAsync` makes one best-effort attempt.
- `RemainingEstimate` is local timing; it does not prove ownership.

:::warning No consensus or fencing
Replication, failover, partitions and clock drift can violate mutual exclusion. An unreachable node
keeps its lease until expiry. A node that exceeds `NodeTimeout` counts as failed, but its command
can still arrive after cleanup and hold the token until expiry, which can block quorum for the next
attempts. Use a consensus-backed lock or fencing tokens when stale owners must be rejected.
:::

## Distributed countdown latch

A latch generation starts with a nonnegative count. Each signal decrements it atomically; waiters
complete at zero.

```csharp
using Respire.Coordination;

await using var latchClient = await RespireClient.ConnectAsync("localhost:6379");
var coordination = latchClient.Coordination;
var latch = await coordination.CreateCountdownLatchAsync("{batch:42}:latch", count: 3);
var completed = latch.WaitAsync();

await latch.CountDownAsync();
await latch.CountDownAsync();
await latch.CountDownAsync();
if (await completed) Console.WriteLine("All workers finished");
```

| Method | Behavior |
| --- | --- |
| `CreateCountdownLatchAsync` | Fails when the key already holds a latch |
| `JoinCountdownLatchAsync(key)` | Joins the current generation; returns `null` when none exists |
| `ResetCountdownLatchAsync` | Create-or-replace; starts a new generation and makes old waiters return `false` |
| `CountDownAsync` | Returns `-1` against a replaced generation; fails on an already completed one |

- You choose the key lifetime. Keep the key until every participant is done; deleting it loses the
  generation, and waiters return `false`.
- Late signals from earlier work cannot decrement a reset latch.
- Client prefixes and binary keys work; Cluster needs no hash tags.
- Waiters re-read Redis after each Pub/Sub notification and every few seconds, so a lost
  notification delays completion but does not block it.
- Cancelling `WaitAsync` does not change the count. A cancelled signal may still have run.
- Asynchronous failover or restore can roll back signals or resets; this is not consensus-safe.

## Distributed semaphores

`RespireSemaphore` acquires permits immediately:

```csharp
using Respire.Coordination;

await using var client = await RespireClient.ConnectAsync("localhost:6379");
var semaphore = client.Coordination.CreateSemaphore("{batch:42}:permits", capacity: 4);
await using var attempt = await semaphore.TryAcquireAsync(TimeSpan.FromSeconds(30));
if (!attempt.Acquired) return;
// Run work while holding attempt.Permit.
```

- Release with `ReleaseAsync` or `DisposeAsync`, check with `VerifyStillHeldAsync`, renew or change
  expiry with `ResetExpiryAsync`.
- Pass `null` as the expiry for a permit that lasts until released.
- Expiry uses whole milliseconds and Redis server time. Expired permits are pruned on the next
  operation, so no cleanup worker is needed.
- `RemainingEstimate` is a conservative local estimate from when the command was sent.
- Every contender must use the same positive capacity. While any permit is active, a different
  capacity throws `RespireSemaphoreCapacityMismatchException`. Lowering capacity never revokes
  permits.
- Acquisition does not queue, poll or promise fairness. Use a dedicated key. Binary keys, client
  prefixes and Cluster work without hash tags.
- Requires Redis 5 or later (or compatible), even with `lua-replicate-commands` disabled.

Failure behavior:

| Operation | On failure or cancellation |
| --- | --- |
| `VerifyStillHeldAsync` | Throws and leaves the permit held; retry it. It prunes, so it runs on the primary and needs write permission. |
| `ResetExpiryAsync` | Waiting for another operation: no change. Once started: the permit is surrendered (`IsReleased` is true, `RemainingEstimate` is zero, later renewals return `false`), and Respire tries an owner-token release bounded to one second. The Redis lifetime is unknown; release and acquire a new permit. |
| `ReleaseAsync` | Tries one bounded owner-token release. If unconfirmed, ownership is uncertain; use the handle only to retry `ReleaseAsync`. |
| `DisposeAsync` | Never throws; waits about one second, then retries in the background with jittered backoff for up to one minute. |
| Acquisition | Respire tries owner-token cleanup; finite expiry is the fallback. An error reply (other than capacity mismatch) can follow partial writes, so Respire sends a release before throwing. |

With `RespireClient`, non-expiring acquisitions and acquisitions under a command timeout or
cancellation need Redis ACL permission for `CLIENT ID` and `CLIENT KILL`. Respire uses them to stop
a delayed acquire from recreating a released permit; if this fails, it sends no release. Other
`IRespireClient` implementations cannot do this, so a delayed acquire can overtake cleanup.

Abandoned background cleanup increments the `respire.coordination.cleanup.abandoned` counter
(tagged with stage and reason) and logs a warning through the client's `LoggerFactory`.

:::warning
Asynchronous failover can roll back permit state, and a replica with a skewed clock expires permits
early or late. Prefer a finite expiry: a non-expiring permit whose holder crashes (or whose cleanup
fails for a minute) consumes capacity and blocks capacity changes until removed. To recover, stop
every holder and delete the semaphore key, or remove the stale `P:`-prefixed members of its sorted
set.
:::

## Redis-backed rate limits

`RespireCoordination.RateLimiters` creates fixed-window, sliding-window and token-bucket
`System.Threading.RateLimiting.RateLimiter` implementations. Scripts use Redis server time and
decide each permit atomically. Fixed windows use `INCREX` on Redis 8.8+ and an equivalent Lua
counter on older servers; after a rejection, Respire re-probes `INCREX` every five minutes.

:::warning Asynchronous only
Always acquire with `AcquireAsync`. Synchronous `AttemptAcquire` cannot reach Redis, so it returns
an unacquired lease without `RetryAfter` metadata for every permit count, even when permits are
free. ASP.NET Core rate-limiting middleware falls back to `AcquireAsync` after a failed probe, but
code that only calls `AttemptAcquire`, including the synchronous path of
`PartitionedRateLimiter.CreateChained`, is always rejected.
:::

```csharp
using System.Threading.RateLimiting;
using Respire.Coordination;

var coordination = redis.Coordination;
await using var limiter = coordination.RateLimiters.FixedWindow(
    "limits:checkout", permitLimit: 100, window: TimeSpan.FromMinutes(1), queueLimit: 20,
    queueProcessingOrder: QueueProcessingOrder.OldestFirst);

using var lease = await limiter.AcquireAsync(1, cancellationToken);
if (!lease.IsAcquired
    && lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
{
    Console.WriteLine($"Try again after {retryAfter}.");
}
```

| Option | Default | Meaning |
| --- | --- | --- |
| `queueLimit` | 0 | Permits that can wait in a local queue |
| `queueProcessingOrder` | `OldestFirst` | Order of the local queue |

- Keys accept binary values and use the client prefix. Each limiter uses one key, so it works in
  Cluster.
- Leases are consumptive: disposing one does not return permits.
- Requesting more permits than the limit returns an unacquired lease without `RetryAfter` (no
  exception). Negative counts throw `ArgumentOutOfRangeException`.
- Queued acquisitions honor cancellation and disposal and wait for the server-calculated
  availability time instead of polling. Queue order is local: other processes, and new local
  callers during a retry, can take permits first.
- If cancellation, disposal or a disconnect races an accepted script, the permit is consumed even
  without an acquired lease.
- `GetStatistics()` reports this instance's granted and rejected leases, queued permits, and
  available permits from the latest Redis response. `IdleDuration` lets `PartitionedRateLimiter`
  dispose idle limiters; shared state stays in Redis.

To use these limiters with ASP.NET Core rate-limiting middleware, return them from your
rate-limiting configuration. Dispose each limiter when its owner stops; you own the client.

### Fixed window

```csharp
using Respire.Coordination;

await using var client = await RespireClient.ConnectAsync("localhost:6379");
var coordination = client.Coordination;
using var limiter = coordination.RateLimiters.FixedWindow(
    "limits:api", permitLimit: 500, window: TimeSpan.FromMinutes(1));
```

### Sliding window

The limiter stores one count per active segment. Segment timestamps round up, so permits never
expire early but can return up to one segment late. More segments shrink that delay.

```csharp
using Respire.Coordination;

await using var client = await RespireClient.ConnectAsync("localhost:6379");
var coordination = client.Coordination;
using var limiter = coordination.RateLimiters.SlidingWindow(
    "limits:api", permitLimit: 500, window: TimeSpan.FromMinutes(1), segments: 10);
```

### Token bucket

The limiter stores only its token count and last server refill time.

```csharp
using Respire.Coordination;

await using var client = await RespireClient.ConnectAsync("localhost:6379");
var coordination = client.Coordination;
using var limiter = coordination.RateLimiters.TokenBucket(
    "limits:api", tokenLimit: 100, tokensPerPeriod: 10,
    replenishmentPeriod: TimeSpan.FromSeconds(1));
```
