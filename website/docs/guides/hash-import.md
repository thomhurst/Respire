---
title: Hash import sessions
description: Import hashes through connection-local fieldsets on Redis 8.10.
---

# Hash import sessions

Redis 8.10 adds [HIMPORT](https://redis.io/docs/latest/commands/himport/), which prepares an
ordered list of field names once and reuses it to import hashes. Each fieldset belongs to one
connection. Use a dedicated `RespireHashImportSession` so preparation and imports stay on that
connection, even when the parent client uses several multiplexed connections.

```csharp
await using var imports = await redis.Hashes.CreateImportSessionAsync();
await imports.PrepareAsync("person", "name", "city");
await imports.SetAsync("person:1", "person", "Ada", "London");
await imports.SetAsync("person:2", "person", "Grace", "New York");
bool removed = await imports.DiscardAsync("person");
long remainingRemoved = await imports.DiscardAllAsync();
```

`PrepareAsync` and `SetAsync` return `true` for `OK`. Preparation replaces an existing fieldset
with the same name. Duplicate fields, null arguments, and empty field/value lists are rejected
locally. Redis rejects unknown fieldsets and value counts that differ from the prepared field
count. Values pair with fields in their original supplied order.

`SetAsync` **replaces the entire hash** and clears its expiration. Fields from the old hash
disappear. An existing non-hash key causes `WRONGTYPE` and retains its value and expiration.
`DiscardAsync` returns whether the fieldset existed; `DiscardAllAsync`
returns how many fieldsets it removed. Discarding fieldsets leaves imported hashes intact.

## Batches and transactions

Create queues through the session. Ordinary client queues reject these connection-local APIs.
Session queues accept only the four HIMPORT operations.

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

Batches pipeline commands in order. Transactions use `MULTI`/`EXEC` on the same connection;
the session confirms `MULTI` before sending any imports. A rejected `MULTI` leaves prepared
fieldsets usable. A rejected `EXEC` closes the session because Redis may still be in transaction
mode. Transactions require one in-flight slot per queued command plus one for `EXEC`;
exceeding `MaxInflightCommands` fails before `MULTI` and preserves prepared fieldsets.
Credential renewal waits outside the complete `MULTI`/`EXEC` sequence. Credential expiry and
renewal deadlines still apply; a transaction cannot extend the connection's authentication lifetime.
Execution errors fault the affected pending and do not roll back other commands. Session
transactions do not provide WATCH. `ExecuteAndWaitForReplicationAsync` and
`ExecuteAndWaitForAofAsync` reject session batches because their execution contract creates a
fresh connection. Await each operation or queue execution before starting another operation.

## Ownership, routing, and failures

Names, fields, keys, and values support binary arguments. The session snapshots their bytes
before an asynchronous send or enqueue, so callers can reuse their buffers after the call.
Key prefixes apply only to imported keys; fieldset names and fields remain unchanged.
Imports invalidate cached results for the affected key. Batch and transaction execution also
uses the existing conservative cache fences.

For Redis Cluster, supply a routing key when opening the session:

```csharp
await using var imports = await redis.Hashes.CreateImportSessionAsync("{people}:anchor");
await imports.PrepareAsync("person", "name");
await imports.SetAsync("{people}:1", "person", "Ada");
```

The routing key is not read or written. All imported keys must share its effective slot after
prefixing. A session cannot span Cluster slots. A route change, MOVED/ASK redirect, connection
loss, timeout, or cancellation after sending invalidates the session. Commands are never
replayed onto another connection. Create a new session and prepare its fieldsets again.

Always use `await using`. Disposal closes the dedicated connection rather than returning its
fieldsets to another caller. Connection closure or Redis RESET drops all fieldsets; they have
no independent expiry timer. Disposing the parent client also invalidates the session.
Disposal does not wait for an in-flight operation: it closes the connection, causing that
operation to fail without replay.

Ordinary command errors leave the session usable. Routing errors, `READONLY`, and errors
that leave transaction state uncertain invalidate it. Cancellation proven to occur before
admission leaves its fieldsets intact, including a batch waiting for credential renewal.
Cancellation of an admitted command still expires the session. After an uncertain send, some imports may have
executed; inspect application data before retrying. Disposing a queue before execution does
not dispose the session or discard fieldsets prepared earlier.

`RespireFakeServer` supports all four operations with the same per-connection isolation,
overwrite behavior, validation, and WATCH invalidation. Other server versions must support
HIMPORT themselves; opening a session does not negotiate this feature.
