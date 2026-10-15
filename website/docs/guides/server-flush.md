---
title: Server flush
description: Delete every key in a database or on a server with FLUSHDB and FLUSHALL, and choose how memory is reclaimed.
---

# Server flush

`client.Server.FlushDatabaseAsync()` (`FLUSHDB`) deletes every key in the selected database. `client.Server.FlushAllAsync()` (`FLUSHALL`) deletes every key in every database. Use them to reset test servers or caches you fully own.

Both need `RespireOptions.AllowAdmin = true` or `allowAdmin=true` in the connection string. Otherwise they throw `NotSupportedException` before sending.

```csharp
await redis.Server.FlushDatabaseAsync(RespireServerFlushMode.Async, cancellationToken: default);
await redis.Server.FlushAllAsync(RespireServerFlushMode.Sync, cancellationToken: default);
```

## Flush modes

Overloads without a mode use the server default. Pass a `RespireServerFlushMode` to choose how Redis reclaims memory:

| Mode | Wire form | Minimum Redis version |
| --- | --- | --- |
| `Default` | `FLUSHDB` / `FLUSHALL` | Any |
| `Sync` | `... SYNC` | 6.2 |
| `Async` | `... ASYNC` | 4.0 |

Every mode removes keys before Redis replies; `Async` frees their memory in the background.

The mode overloads require an explicit cancellation token (pass `default` if you have none), so `FlushAllAsync(default)` stays unambiguous.

## Key prefixes do not limit a flush

A flush deletes the whole database or server, including keys outside the client's key prefix.

## Cluster

An immediate call on a Cluster client flushes every discovered primary. It is not atomic: a failure can leave only some primaries flushed.

## Batches and transactions

`batch.Server.FlushDatabase(mode)` and `batch.Server.FlushAll(mode)` (also on transactions) return `RespirePending<bool>`, which is `true` after successful execution. All three modes work. `AllowAdmin` and the mode are checked when the command is queued. The client-side cache is invalidated when the queue executes.

A queued flush affects only its execution node:

- In a Cluster batch, keyless flushes form their own routing group on one discovered primary. Their order relative to keyed groups is not guaranteed.
- A transaction runs on the node selected by its keys or `WATCH` target, or on one primary when it has no keys.

To flush every primary, use the immediate methods. `MULTI`/`EXEC` cannot make that fan-out atomic.

## Custom implementations

Classes that implement `IRespireServerCommands` must implement both mode overloads.

## Redis references

[FLUSHDB](https://redis.io/docs/latest/commands/flushdb/), [FLUSHALL](https://redis.io/docs/latest/commands/flushall/).
