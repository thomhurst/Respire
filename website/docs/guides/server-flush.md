---
title: Server flush
description: Delete every key in a database or on a server with FLUSHDB and FLUSHALL, and choose how memory is reclaimed.
---

# Server flush

`client.Server.FlushDatabaseAsync()` sends `FLUSHDB` and deletes every key in the selected database. `client.Server.FlushAllAsync()` sends `FLUSHALL` and deletes every key in every database. Use them to reset test servers or caches you own completely.

Both are administrative commands. Set `RespireOptions.AllowAdmin = true`, or add `allowAdmin=true` to the connection string. Without it, the calls throw `NotSupportedException` before sending.

```csharp
await redis.Server.FlushDatabaseAsync(ServerFlushMode.Async, cancellationToken: default);
await redis.Server.FlushAllAsync(ServerFlushMode.Sync, cancellationToken: default);
```

## Flush modes

The overloads without a mode use the server's configured default. Pass a `ServerFlushMode` to choose how Redis reclaims memory:

| Mode | Wire form | Minimum Redis version |
| --- | --- | --- |
| `Default` | `FLUSHDB` / `FLUSHALL` | Any |
| `Sync` | `... SYNC` | 6.2 |
| `Async` | `... ASYNC` | 4.0 |

Every mode removes keys logically before Redis replies. `Async` releases their memory in the background.

The mode overloads require an explicit cancellation token. Pass `default` when you do not need one. This keeps existing calls such as `FlushAllAsync(default)` unambiguous.

## Key prefixes do not limit a flush

A client key prefix does not limit `FLUSHDB` or `FLUSHALL`. They delete the whole database or server, including keys outside the prefix.

## Cluster

An immediate call on a Cluster client visits every discovered primary. It is not atomic: a failure can leave only some primaries flushed.

## Batches and transactions

`batch.Server.FlushDatabase(mode)`, `batch.Server.FlushAll(mode)`, and the same methods on a transaction return `RespirePending<bool>`. The value becomes `true` after successful execution. All three modes work in both queue types. The `AllowAdmin` check and mode validation happen when the command is queued. Queued flushes invalidate the client-side cache when the queue executes.

Queued flushes affect only their execution node:

- In a Cluster batch, keyless flushes form their own routing group on one discovered primary. Their order relative to keyed groups is not guaranteed.
- A transaction runs on the node selected by its keys or `WATCH` target, or on one primary when it has no keys.

Neither queue fans a flush out. Use the immediate methods to flush every primary. `MULTI`/`EXEC` cannot make that fan-out atomic.

## Custom implementations

Classes that implement `IServerCommands` must implement both mode overloads.

## Redis references

[FLUSHDB](https://redis.io/docs/latest/commands/flushdb/), [FLUSHALL](https://redis.io/docs/latest/commands/flushall/).
