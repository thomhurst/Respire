---
title: Coordination leases and fencing-token locks
---

`Respire.Extensions.Coordination` adds fencing locks and named hash-field leases to an existing
client. A fencing lock carries a random `RespireLockToken` for ownership and a positive
64-bit fencing token for a cooperating protected resource. Install the optional package:

```bash
dotnet add package Respire.Extensions.Coordination
```

```csharp
using Respire.Extensions.Coordination;

await using var client = await RespireClient.ConnectAsync("localhost:6379");
var coordination = new RespireCoordination(client);
await using var attempt = await coordination.TryAcquireFencedLockAsync(
    "{invoice:42}:lease", "{invoice:42}:fence", TimeSpan.FromSeconds(30));
if (!attempt.Acquired) return;

Console.WriteLine($"Acquired fencing token {attempt.Lock.FencingToken}");
// Send this token with protected writes. The receiving resource must enforce it.
```

`TryAcquireFencedLockAsync` is immediate and does not wait or poll. An unsuccessful attempt has
`Acquired == false`; accessing its `Lock` throws `RespireLockNotAcquiredException`. The
coordinator does not own its client.

## Wait for a fenced lease

`AcquireFencedLockAsync` waits for server-assisted client-cache invalidations and retries the
atomic acquisition script after each wake. It subscribes before checking the lease key, so a
release or expiry during the check leaves a queued wake-up. Notifications are hints: only the
Lua acquisition script grants ownership. The waiter does not poll, and cancellation stops local
waiting without replaying an accepted acquisition.

Enable RESP3 client-side caching when creating the client. No `notify-keyspace-events` flags
need to be enabled on Redis; client tracking sends invalidations for tracked reads.

```csharp
using Respire.Extensions.Coordination;

await using var waitingClient = await RespireClient.ConnectAsync(new RespireOptions
{
    Endpoints = ["localhost:6379"],
    Protocol = RespProtocol.Resp3,
    ClientSideCache = new RespireClientSideCacheOptions(),
});
var waitingCoordination = new RespireCoordination(waitingClient);
await using var lease = await waitingCoordination.AcquireFencedLockAsync(
    "{invoice:42}:lease", "{invoice:42}:fence", TimeSpan.FromSeconds(30), CancellationToken.None);
```

The subscription uses the physical key after client prefixes are applied. In Cluster, the lease
and counter must share a slot, as above; the read and script route to that slot's owner. A lost
tracking connection flushes tracked state and wakes the waiter to check again. The waiter also
schedules one wake from the lease key's `PTTL`, so expiration does not depend on an immediate
invalidation. A protected resource must still validate the fencing token on every write.
The package also provides a distributed countdown latch.

## Keep the counter's history

Every contender for one resource must use the same two distinct keys. The lease key expires;
the counter key persists after release and expiry. Reserve both keys exclusively for this
primitive. Never delete, reset, expire or evict the counter. Use a non-evicting deployment
and durability settings appropriate for the protected resource. An existing expiring,
negative, malformed or overflowing counter fails acquisition without creating a lease.

In Cluster, both physical keys must hash to the same slot; matching hash tags, as above,
provide that placement. Client prefixes apply to both keys. Binary key storage is copied
before asynchronous work, and lease operations retain those copies. Prefixes containing a
hash tag can affect placement too. A cross-slot pair is rejected without mutation.

One atomic Lua script checks contention, increments the counter and creates the expiring
lease. Tokens can have gaps when execution or delivery fails. Redis returns the counter as
decimal bytes, preserving every positive `long` value, including values above 2^53.
The script uses EVALSHA with a definitive NOSCRIPT fallback to EVAL and works on Redis 7+
and compatible Valkey deployments; it does not require Redis 8.8 commands.

## Redis-backed rate limits

`RespireCoordination.RateLimiters` creates fixed-window, sliding-window and token-bucket
implementations of `System.Threading.RateLimiting.RateLimiter`. Redis scripts use server time
and apply each permit decision atomically. Fixed windows use `INCREX` on Redis 8.8 and later;
older Redis versions use an equivalent Lua counter with the same first-request window expiry.
After an older server rejects `INCREX`, every limiter from the same `RespireCoordination` uses
the Lua counter directly and probes `INCREX` again only after five minutes, so upgraded servers
regain the fast path.

:::warning Asynchronous only
Always acquire with `AcquireAsync`. Synchronous `AttemptAcquire` cannot reach Redis, so it returns
an unacquired lease without `RetryAfter` metadata for every permit count, even when permits are
free. ASP.NET Core rate-limiting middleware falls back to `AcquireAsync` after a failed probe, but
code that only calls `AttemptAcquire`, including the synchronous path of
`PartitionedRateLimiter.CreateChained`, is always rejected.
:::
Sliding-window state stores at most the permits allowed in one window. Token-bucket state stores
only its current token count and last server refill time.

```csharp
using System.Threading.RateLimiting;
using Respire.Extensions.Coordination;

var coordination = new RespireCoordination(redis);
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

Limiter keys accept binary values and use the configured client prefix. Each algorithm accesses one
Redis key, so one atomic script stays on one Cluster slot. Permit leases are consumptive: disposing
a successful lease does not return permits. Redis expiry and refill time govern availability.
A request for more permits than the configured limit can never succeed; `AcquireAsync` returns an
unacquired lease without `RetryAfter` instead of throwing, so callers that honour `RetryAfter` do
not retry it. Negative permit counts throw `ArgumentOutOfRangeException`. Queued
asynchronous acquisitions observe caller cancellation and limiter disposal. Waiting follows the
next server-calculated availability time instead of polling Redis. Queue order is local to one
limiter instance: callers in other processes, and new local callers that arrive while a queued
request is being retried, can take permits first.

`GetStatistics()` reports leases granted and rejected by this instance, its queued permits, and
the available permits from the latest Redis response. Other processes may have consumed permits
since that response. `IdleDuration` measures time without local acquisitions or queued requests,
so `PartitionedRateLimiter` can dispose idle limiters; the shared state stays in Redis.
If cancellation, disposal or a disconnect races with an accepted Redis script, the permit is
consumed even when the caller does not receive an acquired lease.

### Fixed window

```csharp
using Respire.Extensions.Coordination;

await using var client = await RespireClient.ConnectAsync("localhost:6379");
var coordination = new RespireCoordination(client);
using var limiter = coordination.RateLimiters.FixedWindow(
    "limits:api", permitLimit: 500, window: TimeSpan.FromMinutes(1));
```

### Sliding window

The limiter stores one aggregated count per active segment. It rounds each segment timestamp
forward to its end, so permits never expire early; this can delay availability by up to one
segment duration. More segments reduce that extra delay.

```csharp
using Respire.Extensions.Coordination;

await using var client = await RespireClient.ConnectAsync("localhost:6379");
var coordination = new RespireCoordination(client);
using var limiter = coordination.RateLimiters.SlidingWindow(
    "limits:api", permitLimit: 500, window: TimeSpan.FromMinutes(1), segments: 10);
```

### Token bucket

```csharp
using Respire.Extensions.Coordination;

await using var client = await RespireClient.ConnectAsync("localhost:6379");
var coordination = new RespireCoordination(client);
using var limiter = coordination.RateLimiters.TokenBucket(
    "limits:api", tokenLimit: 100, tokensPerPeriod: 10,
    replenishmentPeriod: TimeSpan.FromSeconds(1));
```

For automatic DI and ASP.NET Core middleware integration, register the returned `RateLimiter`
through the application's rate-limiting configuration. Dispose each limiter when its owner stops;
the Respire client remains caller-owned.

## Named leases in hash fields

`TryAcquireLeaseAsync` stores each named lease as one hash field and applies an independent field
expiry. `AcquireLeaseAsync` waits for tracking invalidations and the field's `HPTTL` deadline.
Lease fields and hash keys accept binary bytes. The owner token guards renewal, verification,
and release, so an expired owner's handle cannot change a replacement lease or another field in
the same hash.

Hash-field expiration requires Redis 7.4 or later. The containing hash key must have no key-level
expiry; Redis deletes the whole hash when that expiry elapses. Acquisition rejects expiring hash
keys before writing the lease field. Older servers fail before the lease field is written, with
an error that identifies the required Redis feature.

```csharp
using Respire.Extensions.Coordination;

await using var leaseClient = await RespireClient.ConnectAsync(new RespireOptions
{
    Endpoints = ["localhost:6379"],
    Protocol = RespProtocol.Resp3,
    ClientSideCache = new RespireClientSideCacheOptions(),
});
var leaseCoordination = new RespireCoordination(leaseClient);
await using var lease = await leaseCoordination.AcquireLeaseAsync(
    "coordination:leases", "worker:42", TimeSpan.FromSeconds(30));
```

## Enforce fencing at the protected resource

A fencing token does not stop an old process. The receiving database, file service or other
resource must atomically compare the token with its last accepted token when applying each
write. This small in-memory example illustrates the check; real enforcement belongs in the
resource's durable transaction, shared by every writer.

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

Equal tokens allow repeated writes during one lease. Ownership tokens and fencing tokens
have different roles: release and renewal compare the random ownership token, while the
protected resource orders writes by the fencing token.

Tokens increase only within a retained Redis history. Redis asynchronous replication can
lose acknowledged writes during failover, including the counter increment. Backup restores,
counter deletion and eviction can also reuse or decrease tokens. Consequently, this API
does **not** provide globally monotonic fencing across arbitrary failover or rollback, and
the example cannot repair a lost counter history. Use a consensus-backed fencing authority
when that guarantee is required. See Redis's
[distributed-lock safety discussion](https://redis.io/docs/latest/develop/clients/patterns/distributed-locks/)
and [replication guarantees](https://redis.io/docs/latest/operate/oss_and_stack/management/replication/).

## Renewal, release and uncertain outcomes

`ResetExpiryAsync` renews only the current owner and retains its fencing token.
`VerifyStillHeldAsync` checks ownership at one instant; it cannot promise ownership for a
later write. `RemainingEstimate` measures elapsed time from before acquisition and is a
local estimate. Stop protected work when the lease expires or ownership becomes uncertain.

Renewal and release reuse the existing [managed lock lifecycle](distributed-locks.md).
Managed renewal requires `CLIENT ID` and `CLIENT KILL` permissions to fence uncertain
commands. Release reports `Released`, `AlreadyReleased` or `NotOwned`. Disposing a copied
attempt is idempotent through the shared handle and never deletes another owner's lease.

Cancellation, timeout or disconnect can occur after Redis accepted acquisition. Respire
does not replay that command after uncertain acceptance. It can leave a counter gap and
an unreturned lease that expires after its server-side duration. A reply arriving after the
local lease estimate elapses is not returned as acquired. There is no acquisition-owned
keep-alive loop in this API; explicitly renew within a valid lease when needed.

## Multi-node Redlock

`RespireRedlockGroup` provides a quorum lease across an odd number of at least three independent
standalone Redis deployments. Each supplied client must connect to a different deployment;
the group never owns or disposes those clients. Acquisition runs against all nodes in parallel,
uses one random ownership token, and returns only when a majority succeeds with positive
validity after elapsed time and drift allowance. Failed attempts release that token on every
reachable node. Node operations have a bounded timeout, configurable with `NodeTimeout`.

```csharp
using Respire.Extensions.Coordination;

await using var first = await RespireClient.ConnectAsync("redis://node-a:6379");
await using var second = await RespireClient.ConnectAsync("redis://node-b:6379");
await using var third = await RespireClient.ConnectAsync("redis://node-c:6379");
var group = new RespireRedlockGroup([first, second, third]);

await using var attempt = await group.TryAcquireAsync("invoice:42", TimeSpan.FromSeconds(10));
if (!attempt.Acquired) return;

Console.WriteLine($"Estimated lease time: {attempt.Lock.RemainingEstimate}");
// Complete protected work within the estimated validity.
```

Call `ResetExpiryAsync` before validity expires to renew on a quorum. A renewal that a quorum
does not confirm, including one ended by node timeouts or caller cancellation, ends the lease:
the handle reports released and the token is removed best-effort from every node, because a
partial renewal leaves nodes with different expiries. Acquire again if work must continue.

Call `ReleaseAsync` to remove the token from all nodes. Its cancellation token only bounds the
wait for a concurrent renewal; once started, release runs on every node within `NodeTimeout`.
It returns true when a quorum replies, whether each token was removed or was already absent.
If it returns false, the handle stops reporting ownership and retains its token for a later
cleanup retry. `DisposeAsync` makes one best-effort attempt; retry `ReleaseAsync` if cleanup
does not reach a quorum. `RemainingEstimate` is local timing information; it cannot prove
current ownership. The clients remain owned by the caller.

Redlock does not provide consensus or fencing tokens. Redis asynchronous replication, failover,
partitions and clock drift can violate mutual exclusion. A node that cannot be reached during
cleanup retains its lease until server-side expiry. A node that exceeds `NodeTimeout` counts as
failed, but its command can still arrive after cleanup has run; that node then holds the token
until expiry, which can make the next attempts on that key fail to reach a quorum. Use a consensus-backed lock or a protected
resource that enforces fencing tokens when stale owners must be rejected.
## Distributed countdown latch

Create one latch generation with a nonnegative count. Each signal decrements the count atomically;
waiters complete at zero. Reset replaces the generation, wakes old waiters, and makes their
`WaitAsync` return `false`. A signal against a replaced generation returns `-1`; signaling an
already completed generation fails rather than underflowing.

```csharp
using Respire.Extensions.Coordination;

await using var latchClient = await RespireClient.ConnectAsync("localhost:6379");
var coordination = new RespireCoordination(latchClient);
var latch = await coordination.CreateCountdownLatchAsync("{batch:42}:latch", count: 3);
var completed = latch.WaitAsync();

await latch.CountDownAsync();
await latch.CountDownAsync();
await latch.CountDownAsync();
if (await completed) Console.WriteLine("All workers finished");
```

Other processes can join the current generation by key with
`JoinCountdownLatchAsync("{batch:42}:latch")`. It returns `null` when no latch state exists.
`ResetCountdownLatchAsync` is create-or-replace: when the key is missing or expired, it starts a
new latch rather than failing.

The caller chooses the key lifetime and cleanup policy. Keep the key until every participant
has finished using its generation; deletion loses the current generation. A reset creates a
new unique generation so late signals from earlier work cannot decrement the replacement.
Client prefixes apply to the key, and binary keys are copied before asynchronous work. The
single-key scripts work in Cluster without cross-slot operations.

Waiters subscribe before checking Redis, then re-read the authoritative generation and count
after every notification and every few seconds without one. Pub/Sub is only a wake-up hint:
a notification lost across a reconnect delays completion but does not block it. A waiter
returns `false` when its generation was replaced or the key was deleted. Cancellation stops that waiter's local subscription and does not
change the Redis count. A canceled signal may still have executed if Redis accepted it before
the cancellation was observed; callers should treat that result as uncertain.

The count and generation live in Redis and follow its persistence and failover guarantees.
Asynchronous failover or restore can roll back acknowledged signals or resets. Use suitable
Redis durability for the work being coordinated; this latch does not provide consensus across
independent Redis histories.
