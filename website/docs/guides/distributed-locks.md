---
title: Distributed locks
description: Coordinate work with expiring, owner-checked Redis leases.
---

# Distributed locks

Respire's lock helpers use Redis leases: each lock has an owner token and an expiry. Acquisition is
`SET ... NX PX`; extension and release use atomic owner comparisons, so an expired
handle cannot extend or delete a later owner's lock.

For leases carrying an ordered token that a protected resource can check, use the optional
[fencing-token locks](coordination.md) in `Respire.Extensions.Coordination`. Its persistent
counter has explicit failover and rollback limitations; ordinary lock ownership alone does
not enforce fencing at an external resource.

## Native commands and compatibility

Respire chooses the operation supported by each physical server connection:

| Server | Extension | Release |
| --- | --- | --- |
| Redis 8.4+ | `SET key token IFEQ token PX milliseconds` | `DELEX key IFEQ token` |
| Valkey 8.1–8.x | `SET key token IFEQ token PX milliseconds` | Lua compare-and-delete |
| Valkey 9.0+ | `SET key token IFEQ token PX milliseconds` | `DELIFEQ key token` |
| Older Redis/Valkey | Lua compare-and-`PEXPIRE` | Lua compare-and-delete |

The first operation tries the native command. Release tries `DELEX`, then `DELIFEQ`, then Lua
only when the preceding command returns its standard unknown-command error. Extension falls
back only when the validated `SET ... IFEQ ... PX` command returns `ERR syntax error`. These
replies confirm that the attempted operation did not execute. Missing scripts use the usual
`EVALSHA` followed by `EVAL` after `NOSCRIPT`.

Unsupported capabilities are remembered per physical connection. Each new connection discovers
capabilities independently, including reconnects, failover destinations, and Cluster redirects.
A mixed-version Cluster can therefore use native commands on some nodes and Lua on others.
No `INFO` or `COMMAND` permission is needed for discovery. Managed renewal retains its existing
`CLIENT ID` / `CLIENT KILL` permissions and cancellation fence on the connection that executed it.

ACLs must permit the commands selected for the server: `SET` for acquisition and native renewal,
`DELEX` for native release on Redis 8.4+, or `DELIFEQ` for native release on Valkey 9.0+.
Lua fallback needs `EVALSHA`/`EVAL` and the script's `GET`, `DEL`, and `PEXPIRE` permissions.
When upgrading Respire or the server, update any command allowlist that previously permitted
only the Lua release/renewal path. `NOPERM` is returned to the caller; permission denial does
not select a different implementation. There is no native-command opt-out setting.

An ownership mismatch returns `false` without fallback. Timeouts, cancellation, connection loss,
ACL denial, and other server errors do not trigger a Lua retry. A write with an uncertain outcome
is never replayed for capability discovery. Token equality uses the original bytes; renewing a
missing, expired, or replaced lock cannot recreate it or change another owner's TTL. As before,
use a fresh token for each acquisition, and treat expiry estimates as local estimates.

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

For long-running work, acquisition can start a keep-alive owned by the returned lock. Disposing
the attempt stops renewal before releasing the lock; its acquisition cancellation token does not
control the keep-alive after acquisition succeeds.

```csharp
await using var attempt = await redis.Locks.AcquireAsync(
    "locks:report",
    expiry: TimeSpan.FromSeconds(30),
    keepAlive: true,
    cancellationToken);

if (!attempt.Acquired)
{
    return;
}

await RunReportAsync(attempt.Lock.KeepAliveCancellationToken);
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
Cancellation interrupts the response wait, polling delay, or contention wait. It cannot recall a
command already written to Redis. If cancellation races with acquisition, Redis may acquire the
lock without returning its handle or generated owner token; the lock then remains until its expiry.

## Treat the lock as a lease

The lock disappears when `Duration` elapses, even if protected work is still running. Keep work
shorter than the lease or extend it before expiry:

`RemainingEstimate` and `ExpiresAtEstimate` are approximate timing values, not ownership checks.
Use `VerifyStillHeldAsync` when current ownership must be checked against Redis.

```csharp
if (!await mutex.ResetExpiryAsync(TimeSpan.FromSeconds(30), cancellationToken))
{
    return; // ownership was lost; stop protected writes
}
```

`ResetExpiryAsync` applies the supplied duration from now; it does not add time to the current
expiry. It returns `false` after expiry, release, or ownership loss. Do not retry protected
writes after that result: another process may now own the lock.

For longer work, use a keep-alive and pass its cancellation token to the protected operation:

```csharp
await using var keepAlive = await mutex.KeepAliveAsync(cancellationToken);
await RunReportAsync(keepAlive.CancellationToken);
```

The keep-alive token is cancelled when renewal fails or ownership is lost. Protected operations
must honor it and stop protected writes after cancellation. Disposing `keepAlive` stops renewal;
it does not release `mutex`, which remains held until `ReleaseAsync`, mutex disposal, or expiry.

Managed `RespireLock.ResetExpiryAsync` and `KeepAliveAsync` fence cancelled or timed-out renewals with
Redis `CLIENT ID` and `CLIENT KILL`. The authenticated Redis user must permit both commands. The
raw-token APIs remain available for restricted users, but do not provide this managed fencing.

Disposing `mutex` is the normal release path. Call `ReleaseAsync` explicitly when release success
must be observed; it returns `LockReleaseOutcome.Released`, `AlreadyReleased`, or `NotOwned`.
Disposal suppresses connection, timeout, cancellation, and disposed-client cleanup failures because
expiry remains the final safety net.

## Manage owner tokens directly

Use `TryTakeAsync`, `ResetExpiryAsync`, `ReleaseAsync`, and `GetOwnerTokenAsync` when the token must be
shared with another process or outlive the acquiring process:

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

Keep tokens unique and secret to the owners. Release and extension succeed only when the stored
token matches. Client key prefixes apply to lock keys exactly as they do to other Respire commands.

`TryTakeAsync`, `ResetExpiryAsync`, and `ReleaseAsync` accept `RespireLockToken`.
`GetOwnerTokenAsync` returns `RespireLockToken?` (`null` means the key is missing), and
`RespireLock.Token` uses the same type. Strings convert implicitly as UTF-8; unpaired UTF-16 surrogates throw
`EncoderFallbackException` instead of silently changing the token. For binary tokens,
construct `new RespireLockToken(bytes)` or use an explicit cast from a byte array or memory slice.
These operations copy the bytes without text decoding, so later changes to the original buffer
cannot change ownership checks. Reuse the constructed token to avoid repeated copying.

Compare tokens with `==` or `Equals`; equality and hash codes use the exact bytes. Use `Bytes` for
binary transport. `ToString()` and the debugger show lossless uppercase hexadecimal. Use
`ToUtf8String()` for text tokens; it throws `DecoderFallbackException` for invalid UTF-8 rather
than replacing bytes. Compare tokens directly for ownership checks.
Default and empty tokens are rejected by take, release, and renewal commands.

```csharp
RespireLockToken token = "owner-42";
RespireLockToken? owner = await redis.Locks.GetOwnerTokenAsync("locks:report", cancellationToken);
bool sameOwner = owner == token;
```

Migration: replace explicitly typed `RespireValue` lock-token variables and custom interface
parameters with `RespireLockToken`. Owner queries now return `RespireLockToken?` instead of
`byte[]?`; access `owner.Value.Bytes` after checking for `null`. Managed handles expose bytes as
`mutex.Token.Bytes` instead of `mutex.Token`. String command arguments still convert implicitly;
wrap byte-array or memory arguments in `new RespireLockToken(...)`. Reuse a constructed token
across operations to avoid repeated encoding or copying.
