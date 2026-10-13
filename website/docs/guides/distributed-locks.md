---
title: Distributed locks
description: Coordinate work with expiring, owner-checked Redis leases.
---

# Distributed locks

Respire locks are Redis leases: each lock has an owner token and an expiry. Acquisition uses
`SET ... NX PX`; extension and release compare the owner atomically, so an expired handle cannot
extend or delete a later owner's lock.

Acquisition takes `TimeSpan leaseDuration`, a relative duration that can be renewed from now.
It does not accept absolute `RespireExpiry` deadlines. Rename existing named `expiry:` acquisition
arguments to `leaseDuration:`; positional calls are unchanged. `ResetExpiryAsync(newDuration)`
continues to reset the lease to a relative duration from now.

Lock ownership alone does not protect an external resource. For an ordered token that the resource
can check, use [fencing-token locks](coordination.md) in `Respire.Coordination`.

## Acquire a lock

`AcquireAsync` generates the owner token. Check `Acquired` before accessing `Lock`. Disposing the
attempt releases the lock if it was acquired.

```csharp
await using var attempt = await redis.Locks.AcquireAsync(
    "locks:report",
    leaseDuration: TimeSpan.FromSeconds(30),
    cancellationToken);

if (!attempt.Acquired)
{
    return; // another owner holds the lock
}

RespireLock mutex = attempt.Lock;
await RunReportAsync(cancellationToken);
```

When contention is exceptional, `AcquireOrThrowAsync` returns the handle directly and throws
`RespireLockNotAcquiredException` when the lock is unavailable.

```csharp
await using var mutex = await redis.Locks.AcquireOrThrowAsync(
    "locks:report",
    leaseDuration: TimeSpan.FromSeconds(30),
    cancellationToken);
```

For long-running work, pass `keepAlive: true`. Disposing the attempt stops renewal, then releases.
The acquisition cancellation token does not control the keep-alive.

```csharp
await using var attempt = await redis.Locks.AcquireAsync(
    "locks:report",
    leaseDuration: TimeSpan.FromSeconds(30),
    keepAlive: true,
    cancellationToken);

if (!attempt.Acquired)
{
    return;
}

await RunReportAsync(attempt.Lock.KeepAliveCancellationToken);
```

## Wait for contention

Both methods have overloads that poll until a `wait` budget expires. `retryEvery` defaults to 50 ms.

```csharp
await using var mutex = await redis.Locks.AcquireOrThrowAsync(
    "locks:report",
    leaseDuration: TimeSpan.FromSeconds(30),
    wait: TimeSpan.FromSeconds(5),
    retryEvery: TimeSpan.FromMilliseconds(250),
    cancellationToken);
```

After the budget, `AcquireAsync` returns an unsuccessful attempt and `AcquireOrThrowAsync` throws.
Cancellation cannot recall a command already sent: Redis may acquire the lock without returning the
handle, and the lock then stays until it expires.

## Treat the lock as a lease

The lock disappears when `Duration` elapses, even if work is still running. `RemainingEstimate` and
`ExpiresAtEstimate` are estimates, not ownership checks; use `VerifyStillHeldAsync` to check Redis.

Keep work shorter than the lease, or extend it before expiry:

```csharp
if (!await mutex.ResetExpiryAsync(TimeSpan.FromSeconds(30), cancellationToken))
{
    return; // ownership was lost; stop protected writes
}
```

`ResetExpiryAsync` sets the duration from now; it does not add to the current expiry. It returns
`false` after expiry, release or ownership loss. Then stop protected writes: another process may own
the lock.

For longer work, use a keep-alive and pass its token to the protected operation:

```csharp
await using var keepAlive = await mutex.KeepAliveAsync(cancellationToken);
await RunReportAsync(keepAlive.CancellationToken);
```

- The keep-alive token is cancelled when renewal fails or ownership is lost. Stop protected writes
  when it fires.
- Disposing `keepAlive` stops renewal but does not release `mutex`.
- Managed `ResetExpiryAsync` and `KeepAliveAsync` need Redis ACL permission for `CLIENT ID` and
  `CLIENT KILL`, used to fence cancelled or timed-out renewals. Restricted users can use the
  [raw-token APIs](#manage-owner-tokens-directly), which lack this fencing.

### Release

Disposing `mutex` is the normal release path; it suppresses connection, timeout, cancellation and
disposed-client failures because expiry is the safety net. Call `ReleaseAsync` to observe the
result: `LockReleaseOutcome.Released`, `AlreadyReleased` or `NotOwned`.

If `ReleaseAsync` is cancelled or times out:

- Before the delete is sent: the handle still owns the lock; call `ReleaseAsync` again.
- After the delete may have reached Redis: the handle treats ownership as lost, cancels the
  keep-alive token and fences the connection with `CLIENT KILL`. Without `CLIENT ID`/`CLIENT KILL`
  permission, Respire uses a plain compare-and-delete.

Concurrent `ReleaseAsync` calls share one release; a caller that cancels stops waiting while the
release continues.

## Manage owner tokens directly

Use `TryTakeAsync`, `ResetExpiryAsync`, `ReleaseAsync` and `GetOwnerTokenAsync` when the token must
be shared with another process or outlive the acquiring one:

```csharp
RespireLockToken token = Guid.NewGuid().ToString("N");

if (await redis.Locks.TryTakeAsync("locks:report", token, TimeSpan.FromSeconds(30), cancellationToken))
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

Use a fresh, secret token for each acquisition. Release and extension succeed only when the stored
token matches; renewal cannot recreate a missing lock or change another owner's TTL. Client key
prefixes apply to lock keys.

### `RespireLockToken`

- `TryTakeAsync`, `ResetExpiryAsync`, `ReleaseAsync` and `RespireLock.Token` use
  `RespireLockToken`. `GetOwnerTokenAsync` returns `RespireLockToken?` (`null` means the key is
  missing).
- Strings convert implicitly as UTF-8; unpaired surrogates throw `EncoderFallbackException`.
- For binary tokens, use `new RespireLockToken(bytes)` or an explicit cast from a byte array or
  memory slice. The bytes are copied, so later buffer changes do not affect ownership. Reuse the
  token to avoid repeated copying.
- `==`, `Equals` and hash codes use the exact bytes. `Bytes` exposes them; `ToString()` shows
  uppercase hex; `ToUtf8String()` throws `DecoderFallbackException` on invalid UTF-8.
- Default and empty tokens are rejected.

```csharp
RespireLockToken token = "owner-42";
RespireLockToken? owner = await redis.Locks.GetOwnerTokenAsync("locks:report", cancellationToken);
bool sameOwner = owner == token;
```

### Upgrading from `RespireValue` tokens

Earlier previews typed tokens as `RespireValue` and returned owners as `byte[]?`. Replace typed
token variables and custom interface parameters with `RespireLockToken`. Read owner bytes with
`owner.Value.Bytes` after a `null` check, and handle bytes with `mutex.Token.Bytes`. Wrap byte-array
or memory arguments in `new RespireLockToken(...)`; strings still convert implicitly.

## Native commands and compatibility

Respire picks the operation each server connection supports:

| Server | Extension | Release |
| --- | --- | --- |
| Redis 8.4+ | `SET key token IFEQ token PX milliseconds` | `DELEX key IFEQ token` |
| Valkey 8.1–8.x | `SET key token IFEQ token PX milliseconds` | Lua compare-and-delete |
| Valkey 9.0+ | `SET key token IFEQ token PX milliseconds` | `DELIFEQ key token` |
| Older Redis/Valkey | Lua compare-and-`PEXPIRE` | Lua compare-and-delete |

- Release tries `DELEX`, then `DELIFEQ`, then Lua, moving on only after an unknown-command error.
  Extension falls back only after `ERR syntax error`. Both replies confirm nothing ran.
- Support is remembered per physical connection and rediscovered after reconnects, failover and
  Cluster redirects. A mixed-version Cluster can use native commands on some nodes and Lua on
  others. Discovery needs no `INFO` or `COMMAND` permission.
- Ownership mismatch returns `false`. Timeouts, cancellation, connection loss, ACL denial and other
  errors never trigger a Lua retry, and an uncertain write is never replayed.
- There is no setting to opt out of native commands.

ACLs must permit the commands for your server:

| Operation | Commands |
| --- | --- |
| Acquire, native renewal | `SET` |
| Native release | `DELEX` (Redis 8.4+) or `DELIFEQ` (Valkey 9.0+) |
| Lua fallback | `EVALSHA`, `EVAL`, plus `GET`, `DEL`, `PEXPIRE` |
| Managed renewal fencing | `CLIENT ID`, `CLIENT KILL` |

When upgrading Respire or the server, update allowlists that only permitted the Lua path. `NOPERM`
is returned to the caller; it does not select a different implementation.
