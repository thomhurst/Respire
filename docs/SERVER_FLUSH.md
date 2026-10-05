# Server flush modes

`client.Server.FlushDatabaseAsync()` deletes keys in the selected database;
`FlushAllAsync()` deletes keys in every database. Both require
`RespireOptions.AllowAdmin = true`. Existing overloads use the server's configured default.
Custom `IServerCommands` implementations must implement the two added mode overloads,
following the repository's abstract command-interface contract.
Pass `ServerFlushMode.Sync` or `ServerFlushMode.Async` to override memory reclamation:

```csharp
await redis.Server.FlushDatabaseAsync(ServerFlushMode.Async);
await redis.Server.FlushAllAsync(ServerFlushMode.Sync);
```

All modes remove keys logically before replying. `ASYNC` releases their memory in the
background and requires Redis 4.0 or later; explicit `SYNC` requires Redis 6.2 or later.
See [FLUSHDB](https://redis.io/docs/latest/commands/flushdb/) and
[FLUSHALL](https://redis.io/docs/latest/commands/flushall/).

Immediate calls on a Cluster client visit every discovered primary. Failure can leave
only some primaries flushed; this operation is not atomic across the Cluster.

`batch.Server.FlushDatabase(mode)` and `transaction.Server.FlushAll(mode)` return
`RespirePending<bool>` values that become true after successful execution. Both commands
support all three modes in both queue types. The admin check and mode validation happen
before enqueueing. Queued flushes invalidate the client cache through the existing queue
execution lifecycle.

Queued commands affect only their execution node. In a Cluster batch, keyless flushes form
their own routing group; their ordering relative to keyed groups is not guaranteed. A
transaction executes on its selected node, determined by its keys or WATCH target, or on
one primary when it has no keys. Neither queue fans a flush out. Use the immediate API
when every primary must be flushed; MULTI/EXEC cannot make that fan-out atomic.

A client key prefix does not limit FLUSHDB or FLUSHALL. These commands delete the entire
database or server scope, including keys outside that prefix.
