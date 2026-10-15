---
title: Hash import sessions
description: Import hashes through connection-local fieldsets on Redis 8.10.
---

# Hash import sessions

:::warning Experimental API: RSP0004
This feature requires an explicit compiler opt-in and is outside the 1.0 compatibility promise.
See [RSP0004](../diagnostics/RSP0004.md) for the affected APIs and the exact `NoWarn` or `#pragma` opt-in.
:::

Redis 8.10 [HIMPORT](https://redis.io/docs/latest/commands/himport/) prepares an ordered list of field names once, then reuses it to import hashes. Each fieldset belongs to one connection, so use a `RespireHashImportSession`: it keeps preparation and imports on one dedicated connection.

```csharp
await using var imports = await redis.Hashes.CreateImportSessionAsync();
await imports.PrepareAsync("person", "name", "city");
await imports.SetAsync("person:1", "person", "Ada", "London");
await imports.SetAsync("person:2", "person", "Grace", "New York");
bool removed = await imports.DiscardAsync("person");
long remainingRemoved = await imports.DiscardAllAsync();
```

- `PrepareAsync` and `SetAsync` return `true` for `OK`. Preparing an existing name replaces that fieldset.
- Values pair with fields in the order supplied.
- Duplicate fields, null arguments, and empty field or value lists are rejected locally. Redis rejects unknown fieldsets and value counts that differ from the field count.
- `DiscardAsync` returns whether the fieldset existed. `DiscardAllAsync` returns how many it removed. Discarding leaves imported hashes intact.

:::warning
`SetAsync` **replaces the entire hash** and clears its expiration. Fields from the old hash disappear. An existing non-hash key causes `WRONGTYPE` and keeps its value and expiration.
:::

## Batches and transactions

Create queues through the session. Ordinary client queues reject these APIs, and session queues accept only the four HIMPORT operations.

```csharp
await using var imports = await redis.Hashes.CreateImportSessionAsync();
using var batch = imports.CreateBatch();
var prepared = batch.Hashes.PrepareImport("person", "name", "city");
var imported = batch.Hashes.Import("person:3", "person", "Katherine", "Hampton");
var discarded = batch.Hashes.DiscardImport("person");
var cleared = batch.Hashes.DiscardAllImports();
await batch.ExecuteAsync();
Console.WriteLine(imported.Result);
```

```csharp
await using var imports = await redis.Hashes.CreateImportSessionAsync();
await using var transaction = imports.CreateTransaction();
var prepared = transaction.Hashes.PrepareImport("person", "name", "city");
var imported = transaction.Hashes.Import("person:4", "person", "Dorothy", "Hampton");
var discarded = transaction.Hashes.DiscardImport("person");
await transaction.CommitAsync();
Console.WriteLine(imported.Result);
```

- Batches pipeline commands in order. Transactions use `MULTI`/`EXEC` on the session's connection.
- Await each operation or queue execution before starting another.
- Execution errors fault the affected `RespirePending<T>` and do not roll back other commands.
- Session transactions do not support WATCH.
- A transaction needs one in-flight slot per command plus one for `EXEC`. Exceeding `MaxInflightCommands` fails before `MULTI` and keeps prepared fieldsets.
- `ExecuteAndWaitForReplicationAsync` and `ExecuteAndWaitForAofAsync` reject session batches, because they use a fresh connection.
- A transaction cannot extend the connection's authentication lifetime; credential expiry and renewal deadlines still apply.

| Transaction failure | Session |
|---|---|
| `MULTI` rejected | Usable; fieldsets kept. |
| Queue-time error (for example a denied HIMPORT subcommand) where `EXEC` confirms the discard | Usable; fieldsets kept. The error still faults the commit and every pending. |
| `EXEC` rejected | Closed, because Redis may still be in transaction mode. |

## Ownership, routing, and failures

Names, fields, keys, and values accept binary arguments. The session copies their bytes before sending or queuing, so you can reuse buffers after the call. Key prefixes apply only to imported keys, not to fieldset names or fields. With [client-side caching](../fundamentals/client-side-caching.md), imports invalidate the affected key, and batch or transaction execution flushes the local cache.

For Redis Cluster, pass a routing key when opening the session:

```csharp
await using var imports = await redis.Hashes.CreateImportSessionAsync("{people}:anchor");
await imports.PrepareAsync("person", "name");
await imports.SetAsync("{people}:1", "person", "Ada");
```

The routing key is never read or written. All imported keys must share its slot after prefixing; a session cannot span slots.

Always use `await using`. Disposal closes the dedicated connection, and closing the connection or Redis `RESET` drops all fieldsets (they have no expiry of their own). Disposing the parent client also invalidates the session. Disposal does not wait for an in-flight operation, which then fails without replay. Disposing a queue before execution leaves the session and its fieldsets intact.

What happens to the session after an error:

- **Stays usable:** ordinary command errors, and cancellation before admission (including a batch waiting for credential renewal).
- **Invalidated:** route changes, MOVED/ASK, `READONLY`, connection loss, timeouts, cancellation after sending, and errors that leave transaction state uncertain. Commands are never replayed on another connection. Create a new session and prepare its fieldsets again.

After an uncertain send, some imports may have run. Check your data before retrying.

[Command hooks](command-hooks.md#imports-bulk-writes-and-health-probes) admit session acquisition
as `HIMPORT OPEN` before connection setup, and each immediate operation by its HIMPORT
subcommand. Completion follows conversion and cleanup. Rejection preserves fieldsets.
Disposal and automatic expiry are unconditional cleanup and never require hook admission.

`RespireFakeServer` from `Respire.Testing` ([in-memory testing](in-memory-testing.md)) supports all four operations with the same per-connection isolation, overwrite behavior, validation, and WATCH invalidation. Real servers must support HIMPORT; opening a session does not check for it.
