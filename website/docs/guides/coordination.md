---
title: Fencing-token locks
---

`Respire.Extensions.Coordination` adds immediate fencing-lock acquisition to an existing
client. A successful lease carries a random `RespireLockToken` for ownership and a positive
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

Acquisition does not wait or poll. An unsuccessful attempt has `Acquired == false`; accessing
its `Lock` throws `RespireLockNotAcquiredException`. The coordinator does not own its client.
Notification-driven waiting and other coordination primitives are tracked separately.

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

Call `ResetExpiryAsync` before validity expires to renew on a quorum. Call `ReleaseAsync` to
remove the token from all nodes. `RemainingEstimate` is local timing information; it cannot
prove current ownership. Dispose the attempt to release best-effort. The clients remain owned
by the caller.

Redlock does not provide consensus or fencing tokens. Redis asynchronous replication, failover,
partitions and clock drift can violate mutual exclusion. A node that cannot be reached during
cleanup retains its lease until server-side expiry. Use a consensus-backed lock or a protected
resource that enforces fencing tokens when stale owners must be rejected.
