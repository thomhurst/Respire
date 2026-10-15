---
title: Migrating Redis coordination libraries
description: Move from RedLock.net or DistributedLock.Redis to Respire.Coordination, with explicit lease and safety differences.
---

# Migrating Redis coordination libraries

This guide maps RedLock.net and madelson's DistributedLock.Redis to `Respire.Coordination`.
Choose the coordination model before translating calls. A successful API migration does not
establish equivalent safety under pauses, network partitions, or failover.

```bash
dotnet add package Respire.Coordination
```

Examples use C# 14 and `using Respire.Coordination;` for the `client.Coordination` extension
property. On older language versions use `new RespireCoordination(client)`. Keep clients alive
for the application lifetime and dispose leases before clients. The coordinator and Redlock group
do not own their clients. See [connection setup](../fundamentals/connections.md) for authentication,
TLS, and database selection.

## Choose the semantics first

| Existing requirement | Respire choice | Migration boundary |
| --- | --- | --- |
| Redlock majority across independent deployments | `RespireRedlockGroup` | Requires an odd number of at least three independent standalone clients. Review timing, retries, renewal, and topology; this is not a drop-in replacement. |
| One Redis authority with an external resource that can reject stale writers | `TryAcquireFencedLockAsync` / `AcquireFencedLockAsync` | Uses a lease key and persistent counter on one deployment, not a multi-server quorum. The protected resource must enforce the token. |
| Ordinary single-key mutex without fencing | Core `client.Locks` | See [distributed locks](distributed-locks.md). This also does not provide a multi-server quorum. |

RedLock.net acquires a majority of independent Redis instances; replicas within one configured
replicated endpoint group count as one unit. DistributedLock.Redis also supports multi-database
Redlock for mutexes and reader/writer locks. Do not replace either configuration with one
Respire client and assume the same failure model. [RedLock.net usage][redlock],
[DistributedLock.Redis implementation notes][distributed-redis].

Multiple endpoints on a Cluster or Sentinel client are not independent Redlock votes. Separate
client objects pointing to the same server do not make independent deployments either.
`RespireRedlockGroup` offers no fencing token or consensus guarantee. The single-deployment
fenced lock offers a token, but it is monotonic only while the Redis counter history survives.
Asynchronous failover or a restored backup can lose increments. For stronger requirements, use a
consensus-backed authority or enforce concurrency in the resource's own transaction. See
[Redlock limitations](coordination.md#multi-node-redlock) and
[fencing history](coordination.md#keep-the-counters-history).

## From RedLock.net

An immediate RedLock.net attempt typically looks like this (`factory` is an existing
`RedLockFactory`):

<!-- doc-test-ignore: Before-migration example uses the external RedLock.net package. -->
```csharp
await using var handle = await factory.CreateLockAsync(
    "reports:daily", TimeSpan.FromSeconds(30));
if (!handle.IsAcquired) return;
// Protected work.
```

For a multi-deployment lease, create the Respire group once and reuse it. All participants must
agree on the deployments, database, key bytes, and prefixes:

```csharp
using Respire;
using Respire.Coordination;

await using var first = await RespireClient.ConnectAsync("redis://node-a:6379");
await using var second = await RespireClient.ConnectAsync("redis://node-b:6379");
await using var third = await RespireClient.ConnectAsync("redis://node-c:6379");
var group = new RespireRedlockGroup([first, second, third], new RespireRedlockOptions
{
    NodeTimeout = TimeSpan.FromSeconds(1),
    DriftFactor = 0.01,
});

await using var attempt = await group.TryAcquireAsync(
    "reports:daily", TimeSpan.FromSeconds(30), CancellationToken.None);
if (!attempt.Acquired) return;

Console.WriteLine(attempt.Lock.RemainingEstimate);
// Finish protected work within the remaining validity, or renew before it expires.
```

`Acquired` replaces `IsAcquired`; check it before accessing `Lock`. A failed attempt means no
usable quorum was obtained, which can include contention or unavailable nodes. It is not proof
that another owner holds the resource. Disposal releases the acquired lease best-effort.

RedLock.net's `wait` / `retry` acquisition overload has no direct group equivalent. Wrap
`TryAcquireAsync` in your application's bounded retry policy: use a linked cancellation source
with `CancelAfter(wait)`, pass its token to each attempt and delay, and dispose every failed
attempt. Check cancellation before each retry and use jitter under contention. `NodeTimeout`
bounds a node operation, not the total acquisition wait or protected work. Cleanup can extend the
observed cancellation latency. An uncertain command can still arrive after cleanup; unreachable
nodes rely on expiry. [RedLock.net acquisition overloads][redlock].

### Renewal and lease loss

RedLock.net starts automatic extension for an acquired handle. Respire's Redlock handle has no
automatic keep-alive or lost-handle cancellation token. Arrange explicit
`attempt.Lock.ResetExpiryAsync(duration, cancellationToken)` calls before validity expires,
with time left for node operations. Stop protected work if renewal returns `false`, throws, or
ownership becomes uncertain. Do not continue using a lost handle; acquire a new one.
[RedLock.net renewal behavior][redlock].

`RemainingEstimate` is a local time estimate, not an ownership proof. A renewal must succeed on a
quorum within the previous validity. `ReleaseAsync` returns whether a quorum confirmed release;
after a failed release the handle no longer reports ownership, but release can be retried.
`DisposeAsync` makes one best-effort release. Always bound work and account for process pauses;
neither disposal nor a timer prevents an old worker from writing after expiry.

## From DistributedLock.Redis

The source mutex API returns a nullable handle from `TryAcquireAsync`; `AcquireAsync` throws
`TimeoutException` when its acquisition timeout elapses. Both support cancellation. The timeout
is a wait budget, separate from the automatically renewed lease lifetime.
[DistributedLock acquisition API][distributed-readme].

<!-- doc-test-ignore: Before-migration example uses the external DistributedLock.Redis package. -->
```csharp
var mutex = new Medallion.Threading.Redis.RedisDistributedLock(
    "reports:daily", connection.GetDatabase());
await using var handle = await mutex.TryAcquireAsync(
    TimeSpan.FromSeconds(5), cancellationToken);
if (handle is null) return;
// Protected work, observing handle.HandleLostToken where appropriate.
```

For a single Redis deployment, a fenced lock gives the protected resource a way to reject stale
writes. This changes the protocol: reserve two keys and pass the fencing token to every protected
write. The following reusable method accepts your resource-writing function:

<!-- doc-test-declaration -->
```csharp
using Respire;
using Respire.Coordination;

static async Task<bool> TryRunReportAsync(
    RespireClient client,
    Func<long, CancellationToken, Task> writeWithFenceAsync,
    CancellationToken cancellationToken)
{
    await using var attempt = await client.Coordination.TryAcquireFencedLockAsync(
        "{reports:daily}:lease", "{reports:daily}:fence",
        TimeSpan.FromSeconds(30), cancellationToken);
    if (!attempt.Acquired) return false;

    await using var keepAlive = await attempt.Lock.KeepAliveAsync(cancellationToken);
    await writeWithFenceAsync(attempt.Lock.FencingToken, keepAlive.CancellationToken);
    return true;
}
```

This is an immediate attempt, equivalent to a zero acquisition-wait policy, not the five-second
wait in the source example. `Acquired == false` replaces a null handle. Dispose the keep-alive
before the attempt; the declaration order above ensures that. Keep-alive renews halfway through
the lease duration and its token signals cancellation or uncertain ownership. Pass that token
to work and stop writes when it fires. Acquiring alone does not start renewal. Managed renewal
requires Redis ACL permission for `CLIENT ID` and `CLIENT KILL`.

The callback must implement fencing at the receiving resource: atomically reject a token lower
than the last accepted token in the same durable transaction as the write. Equal tokens may
perform repeated writes within one lease. Merely logging or checking a token in the caller does
not fence anything. Never expire, evict, reset, or delete the counter key. Matching hash tags keep
both physical keys in one Cluster slot; account for client prefixes too. See the
[fencing example and limitations](coordination.md#enforce-fencing-at-the-protected-resource).

### Preserve a bounded acquisition wait

`AcquireFencedLockAsync` waits for tracking invalidations and expiry, without a `timeout`
parameter. It needs RESP3 client-side caching. Configure the application client with
`Protocol = RespireProtocol.Resp3` and `ClientSideCache = new RespireClientSideCacheOptions()`;
see [waiting client setup](coordination.md#wait-for-a-fenced-lease).

To preserve `TryAcquireAsync(timeout)`'s null-on-timeout contract, translate only the acquisition
deadline's cancellation. Let caller cancellation propagate:

<!-- doc-test-declaration -->
```csharp
using Respire;
using Respire.Coordination;

static async Task<RespireFencedLock?> TryAcquireReportWithinAsync(
    RespireClient client, TimeSpan wait, CancellationToken cancellationToken)
{
    if (wait < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(wait));
    if (wait == TimeSpan.Zero)
    {
        var attempt = await client.Coordination.TryAcquireFencedLockAsync(
            "{reports:daily}:lease", "{reports:daily}:fence",
            TimeSpan.FromSeconds(30), cancellationToken);
        // Transfer disposal responsibility to the caller when acquired.
        return attempt.Acquired ? attempt.Lock : null;
    }

    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    deadline.CancelAfter(wait);
    try
    {
        return await client.Coordination.AcquireFencedLockAsync(
            "{reports:daily}:lease", "{reports:daily}:fence",
            TimeSpan.FromSeconds(30), deadline.Token);
    }
    catch (OperationCanceledException) when (
        deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
    {
        return null;
    }
}
```

The caller must `await using` a returned handle and start its keep-alive with the **work** token,
not the acquisition deadline. To preserve `AcquireAsync(timeout)`, throw `TimeoutException` when
this helper returns null. For an unlimited wait call `AcquireFencedLockAsync` directly with the
caller token. This helper accepts finite nonnegative waits supported by `CancelAfter`; zero
performs one immediate attempt. A lease duration is neither an acquisition timeout nor a work
deadline. Transport/server errors remain errors rather than becoming contention.

Cancellation cannot recall a command already accepted by Redis. An unreturned fenced lease can
remain until expiry and its counter may have advanced. Never interpret cancellation as proof that
no acquisition occurred. `VerifyStillHeldAsync` checks only one instant. On explicit renewal,
`ResetExpiryAsync` returning false means stop work. Use `ReleaseAsync` if you need its
`RespireLockReleaseOutcome`; disposal uses best-effort cleanup with expiry as the fallback.

### Constructs that need redesign

DistributedLock.Redis provides mutexes, reader/writer locks, semaphores, and a synchronization
provider. It renews leases internally. Its multi-database reader/writer locks cannot be represented
by Respire's single-deployment read/write leases. [Redis provider APIs][distributed-redis].

| Source construct | Respire migration |
| --- | --- |
| Multi-database `RedisDistributedLock` | Consider `RespireRedlockGroup` and the topology/renewal changes above. A fenced lock is a different safety model. |
| `RedisDistributedReaderWriterLock` | Use `TryAcquireReadLockAsync` / `TryAcquireWriteLockAsync` only after accepting a single deployment, immediate attempts, explicit renewal, and no fairness guarantee. Continuous readers can starve writers. There is no quorum reader/writer equivalent; retain the source implementation if required. |
| `RedisDistributedSemaphore` | Use `CreateSemaphore(key, capacity)` and `TryAcquireAsync(duration)`, check `Acquired`, then use `Permit`. Implement bounded retries and renewal explicitly. All participants must agree on capacity. Choose a finite expiry for crash recovery; a null expiry can leak a permit after a crash. |
| `HandleLostToken` | Fenced locks expose a cancellation token through an explicit `KeepAliveAsync` scope. Redlock, read/write leases, and semaphore permits have no equivalent background loss token; bound work, renew explicitly, and stop on failed/uncertain renewal. |
| `IDistributedLockProvider` and related interfaces | Respire does not implement Medallion's interfaces. Change your application abstraction and DI registration, or keep the existing provider for consumers requiring that contract. Do not silently change timeout or disposal semantics in an adapter. |
| Provider `TryAcquireAllLocksAsync` and related composite helpers | No direct all-or-nothing helper. Acquire in a globally consistent order under one wait budget, release every earlier handle if any acquisition fails, and handle renewal/loss for the whole set. This is not an atomic multi-resource transaction. |
| Synchronous acquisition/disposal | Make the call chain async and use `await using`. Avoid blocking on `ValueTask`. |

`HandleLostToken`, provider interfaces, and composite helpers are documented in the upstream
[other topics guide][distributed-other]. Respire's [read/write leases](coordination.md#read-write-leases)
and [semaphores](coordination.md#distributed-semaphores) describe their failure and release behavior.
Do not copy `Expiry`, `MinValidityTime`, `ExtensionCadence`, or `BusyWaitSleepTime` options by name:
choose a lease duration, renewal schedule, and wait policy for the selected Respire primitive.

## Cut over all participants together

These libraries do not promise a shared on-wire coordination protocol. Reusing a logical lock name
does not establish interoperability; formats, prefixes, data types, and renewal rules can differ.
Using different keys while old workers remain active also allows both groups to enter the critical
section. Drain old workers and their leases, switch all producers/consumers of the resource, then
start the new implementation. Never delete a live lock to accelerate migration.

Before rollout, exercise contention, cancellation during acquisition, process pauses beyond the
lease, renewal failures, and unavailable Redis nodes in your deployment. Verify that stale writes
are rejected by the resource when using fencing. Keep an explicit rollback procedure that drains
the new workers too, and preserves every fencing counter's history.

Upstream references below are pinned to the source revisions checked for this guide, rather than
promising compatibility with every package version:

[redlock]: https://github.com/samcook/RedLock.net/blob/dcfe373b3e288caac4f45b78c88b5943f8b47e42/README.md
[distributed-redis]: https://github.com/madelson/DistributedLock/blob/8007a8686909be235dc339564adca4a0673843bb/docs/DistributedLock.Redis.md
[distributed-readme]: https://github.com/madelson/DistributedLock/blob/8007a8686909be235dc339564adca4a0673843bb/README.md
[distributed-other]: https://github.com/madelson/DistributedLock/blob/8007a8686909be235dc339564adca4a0673843bb/docs/Other%20topics.md
