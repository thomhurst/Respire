---
title: Coordination leases and fencing-token locks
description: Fencing-token locks, hash-field leases, read-write leases, Redlock, latches, semaphores and rate limiters built on Redis.
---

The optional `Respire.Coordination` package adds coordination primitives to a client. For ordinary
single-key locks, use the core client's [distributed locks](distributed-locks.md) instead.

Moving from RedLock.net or DistributedLock.Redis? Read the
[coordination migration guide](coordination-migration.md) before choosing a primitive.

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

`RespireCoordination.RateLimiters` creates fixed-window, sliding-window, token-bucket, GCRA and composite
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

### Updating settings at runtime

Keep the same limiter and update it through the `RateLimiters` factory that owns it:

```csharp
using Respire.Coordination;

await using var client = await RespireClient.ConnectAsync("localhost:6379");
var factory = client.Coordination.RateLimiters;
await using var limiter = factory.FixedWindow(
    "limits:checkout", permitLimit: 100, window: TimeSpan.FromMinutes(1), queueLimit: 20);

await factory.UpdateAsync(limiter,
    RespireRateLimit.FixedWindow(200, TimeSpan.FromMinutes(2)),
    cancellationToken: cancellationToken);
```

`RespireRateLimit` definitions are immutable. Use `FixedWindow`, `SlidingWindow`, `TokenBucket`,
or `Gcra` to describe standalone settings; pass an ordered list to update a composite. Updates
cannot change the key, algorithm, queue capacity or queue order. Composite updates also retain
the number and algorithm of each indexed component. GCRA remains standalone-only. Durations in
update definitions round up to whole milliseconds, with the same bounds as composite definitions;
GCRA's full burst refill duration must fit in a `TimeSpan`.

An update stops new local Redis attempts, lets active attempts finish, migrates Redis state in one
script, then publishes the complete settings together. Ordinary Redis acquisitions remain concurrent.
Each attempt uses one consistent settings set; an acquisition queued across an update retries with
the new settings. Updates on one instance are serialized. Successful updates wake the local queue
immediately to reconsider old retry delays. Requests larger than the new capacity complete with an
unacquired lease without `RetryAfter`; other requests retain their queue order and cancellation.
Already granted leases stay consumed. Statistics continue to describe the latest acquisition reply.

| Algorithm | Treatment of existing usage |
| --- | --- |
| Fixed window | Retain the count and existing deadline. Capacity changes apply immediately; the new window width applies when the next window starts. A decrease below used capacity denies requests until that deadline. |
| Sliding window | Keep segment-end timestamps and counts still active under both the old and new windows. The new window determines their remaining lifetime; new requests use the new segmentation. Previously expired history is never recovered. A larger old segment can continue to delay permit expiry after reducing the segment width. |
| Token bucket | Settle complete refill periods at the old rate, clamp available tokens to the new capacity, and start the new refill period at migration time. Discard the old partial period. A capacity increase adds room to refill, without granting extra tokens immediately. |
| GCRA | Preserve outstanding permit debt, including a partial permit, and rescale its time by the new emission interval, rounding up to milliseconds. A capacity decrease can leave debt above capacity; acquisitions wait for it to drain. Migration rejects debt beyond the `TimeSpan` range. |
| Composite | Apply the corresponding rule to each component atomically and update the stored configuration signature. The lowest component availability still controls admission. |

An absent or expired key stays absent during migration: its first subsequent acquisition starts
with full capacity under the new settings. An identical settings update is a no-op. To intentionally
reset usage or change algorithms, coordinate a separate key migration instead.

**Coordinate every process sharing a key.** Respire does not distribute settings or elect an updater.
For a single limiter instance, the local update barrier is sufficient. For multiple instances:

1. Pause acquisitions everywhere, including local queue pumps (cancel/drain queued work first), and
   wait for active attempts to finish. Use your application's configuration rollout barrier.
2. Elect exactly one instance to call `UpdateAsync` with the default `RespireRateLimitUpdateMode.Migrate`.
3. After it succeeds, have every other existing instance call `UpdateAsync` with the same definitions
   and `RespireRateLimitUpdateMode.Adopt`. Adopt changes only local settings and does no Redis I/O.
   New instances can instead be constructed with the new settings.
4. Resume traffic only after every contender acknowledges the new settings.

Do not migrate the same change once per process: token refill and GCRA debt conversion would be
applied repeatedly. `Adopt` trusts the caller's coordination; it does not verify server settings.
Mixed configurations are unsupported. Composites reject mismatched signatures; standalone layouts
do not record a configuration signature. These rules apply equally to clients with key prefixes and
Cluster routing; migration uses the limiter's original binary key and the same single Cluster slot.

Cancellation is honored while waiting to start migration. After the migration command is submitted,
the update observes its result even if the cancellation token is canceled. An explicit migration
validation rejection (a composite configuration mismatch or GCRA debt overflow) preserves the original
local settings and queue because the script rejects these cases before writing. Other submission or
reply failures close the limiter and fail its queued requests: Redis may already have changed. Reconcile
shared state under the rollout barrier before constructing replacement instances; do not blindly retry
`Migrate`. Cancellation before submission leaves the original settings usable. As with ordinary
acquisitions, a disconnect or canceled in-flight command can have consumed permits on the server.

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

### GCRA

The Generic Cell Rate Algorithm allows an initial burst and then restores one permit per
emission interval. It stores a single theoretical arrival timestamp in Redis and uses Redis
server time to admit requests atomically across clients. For example, this permits up to 20
requests at once, then sustains 10 requests per second:

```csharp
using System.Threading.RateLimiting;
using Respire.Coordination;

await using var client = await RespireClient.ConnectAsync("localhost:6379");
var coordination = client.Coordination;
using var limiter = coordination.RateLimiters.Gcra(
    "limits:api", burstLimit: 20, emissionInterval: TimeSpan.FromMilliseconds(100));

using var lease = await limiter.AcquireAsync(1, cancellationToken);
if (!lease.IsAcquired && lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retry))
{
    // Retry no earlier than this delay; another caller may consume capacity first.
}
```

`burstLimit` is the total burst capacity, not an extra allowance above the steady rate.
Bulk acquisitions consume every requested permit or none. `AcquireAsync(0)` checks whether
one permit is available without changing the stored timestamp. Rejected requests also leave
it unchanged. `RetryAfter` includes the time needed for the entire requested batch; requests
larger than the burst are rejected without retry metadata.

The emission interval must be at least one millisecond and is rounded **up** to whole
milliseconds. The full burst refill duration must fit in a `TimeSpan`. State expires once
the full burst is available again. Disposing and recreating a limiter does not reset capacity.
Use identical settings in every process sharing a key, and dedicate that key to this algorithm.
Optional `queueLimit` and `queueProcessingOrder` behave like the other Redis-backed limiters.

### Composite limits

Use `Composite` to apply several limits to the same logical key. For example, combine a short
burst allowance with a longer rolling quota:

```csharp
using System.Threading.RateLimiting;
using Respire.Coordination;

await using var client = await RespireClient.ConnectAsync("localhost:6379");
await using var limiter = client.WithKeyPrefix("tenant:42:").Coordination.RateLimiters.Composite(
    "limits:checkout",
    [
        RespireRateLimit.TokenBucket(tokenLimit: 10, tokensPerPeriod: 2,
            replenishmentPeriod: TimeSpan.FromSeconds(1)),
        RespireRateLimit.SlidingWindow(permitLimit: 100, window: TimeSpan.FromMinutes(1), segments: 10),
        RespireRateLimit.FixedWindow(permitLimit: 1000, window: TimeSpan.FromHours(1))
    ]);

using var lease = await limiter.AcquireAsync(1, cancellationToken);
if (!lease.IsAcquired && lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retry))
{
    Console.WriteLine($"Every limit can next admit this request in {retry}.");
}
```

Each attempt sends one `EVAL` command, including the first call on a server with an empty script
cache. Connection setup and Cluster redirects can require additional network exchanges. The script
uses one Redis server timestamp, evaluates **every** component, and consumes the requested permits
from every component only if all allow it. A rejection or `AcquireAsync(0)` probe writes no state.
Disposing an acquired lease does not return permits. `RetryAfter` is the maximum delay across all
components for the entire requested batch; competing callers can still consume that capacity first.
Requests above any component's capacity are rejected locally without retry metadata.

Components support fixed windows, segmented sliding windows, and token buckets; GCRA is not a
composite component. Definitions are immutable and the ordered list is copied at construction.
Specify one to 32 components and one to 1024 segments per sliding window. Durations must be at
least one millisecond and round up to whole milliseconds. State retention must fit in a `TimeSpan`
(twice the window for sliding windows, or the full refill duration for token buckets).

Fixed windows start at their first successful acquisition. Sliding permits expire at their segment
end plus a full window, so they can remain charged for up to one extra segment. Token buckets start
full and retain elapsed partial refill periods. `GetStatistics().CurrentAvailablePermits` reports
the minimum component availability in the last response, not a fresh global observation.

All state lives in a **single dedicated Redis hash** at the supplied key: a configuration signature
and indexed component fields. Binary keys and client prefixes are preserved, and Cluster needs no
extra hash tag or derived keys. The hash expires after every component can return to full capacity.
Standalone limiters use different state layouts and cannot share this key. Every process must use
the same ordered definitions; a configuration mismatch raises a Redis error without consuming
permits. Use the [runtime update protocol](#updating-settings-at-runtime) to change settings while
retaining usage, or coordinate a new key when changing the component count or algorithms.

Optional `queueLimit` and `queueProcessingOrder` use the same local queue behavior as the other
limiters. Queued retries each make another single atomic attempt after the composite retry delay.
Cancellation or a disconnect after Redis accepts an attempt can leave permits consumed without
a returned lease. As with the other limiters, asynchronous failover can lose recent state.

### Deny lists and admission metrics

An optional Redis deny list rejects identities before they consume rate-limit permits. Share one
`RespireRateLimitDenyList` across your process and bind an identity when creating each limiter:

```csharp
using Respire.Coordination;

await using var client = await RespireClient.ConnectAsync("localhost:6379");
var denyList = new RespireRateLimitDenyList(client, "limits:denied",
    cacheDuration: TimeSpan.FromSeconds(1), cacheCapacity: 1024);
await denyList.SetDeniedAsync("customer:42", denied: true, cancellationToken);

await using var limiter = client.Coordination.RateLimiters
    .WithDenyList(denyList, "customer:42")
    .FixedWindow("limits:customer:42", 100, TimeSpan.FromMinutes(1));
using var lease = await limiter.AcquireAsync(1, cancellationToken);
// A deny-list rejection has ReasonPhrase = "deny_list" and no RetryAfter.

await denyList.SetDeniedAsync("customer:42", denied: false, cancellationToken);
```

`WithDenyList` returns a separate factory and works with fixed windows, sliding windows, token
buckets, GCRA and composite limits. It does not change the original factory. Denied identities,
including zero-permit probes, receive an unacquired lease without touching the capacity key or
entering the queue. Queued requests recheck membership before each capacity retry and leave the
queue if denied. An update does not wake queued requests; they recheck when they reach the head
of the queue and their next scheduled retry runs.

Storage is a dedicated Redis **set**: exact, case-sensitive UTF-8 identity strings are members.
Empty identities are rejected. `SetDeniedAsync` uses `SADD` or `SREM` through Lua; reads use
`SISMEMBER` through Lua. Redis scripting and these set commands must be permitted. Membership
persists until removed; no expiry is set. Binary storage keys and client prefixes are preserved.
The deny-list and capacity keys may occupy different Cluster slots because they are read separately.
A cache miss therefore adds a Redis round trip. Combining the check with the capacity script would
require both keys to share a hash slot and would prevent sharing one deny list across independently
placed limiter keys; separate reads preserve that flexibility.
Do not share the deny-list key with a limiter's state or another data type.

The local cache holds at most `cacheCapacity` identities (default 1024). Both allowed and denied
memberships are cached for a fixed `cacheDuration` from the start of the read, default one second,
maximum one minute. Hits do not extend expiry. Zero disables caching. Overflow clears the cache
before inserting the new entry; expired entries are removed when read and count toward the bound.
Concurrent misses may issue separate Redis reads. Share the instance to bound the total cache;
creating one per identity multiplies the bound. Identity length is application-controlled.

Updates clear the calling instance's entire cache before and after the Redis write, even if the
write fails. `Invalidate()` clears it explicitly and prevents older in-flight reads from refilling
it. Other instances see updates on expiry or after their own `Invalidate()` call; there is no
cross-process invalidation channel. An external `SADD`/`SREM` has the same remote-cache delay.
Cached decisions remain usable until expiry during a Redis outage. On a miss, read errors propagate
and prevent capacity acquisition; failures are not cached. A failed or cancelled write may already
have reached Redis, so read again to determine membership. Concurrent writes follow Redis order.

Membership checks and capacity acquisition are **not atomic**: an update or invalidation does not
revoke an admission already in progress, including an in-flight membership read. Even with caching
disabled, a concurrent update can occur between the check and acquisition. This is an admission
filter with bounded cache staleness, not an immediate revocation or authorization mechanism.

Subscribe to meter `Respire.Coordination.RateLimiting` for all Redis-backed rate limiters, with or
without deny lists:

| Counter | Tags | Meaning |
| --- | --- | --- |
| `respire.rate_limit.allowed` | None | Final acquired asynchronous leases |
| `respire.rate_limit.blocked` | `reason=deny_list` or `reason=capacity` | Final unacquired asynchronous leases |

Each completed acquisition counts once, regardless of permit count or internal queue retries.
Zero-permit asynchronous probes count; synchronous `AttemptAcquire` fallback probes do not.
Capacity includes oversized requests and local queue rejection/eviction. Exceptions, cancellations
and disposal failures produce no admission count. Keys, identities and endpoints are never tags.
The same final outcomes feed each limiter's `GetStatistics()` success/failure totals.
