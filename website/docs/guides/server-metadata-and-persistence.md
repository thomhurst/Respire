# Server metadata and persistence

The Server facet exposes owned command/module metadata and explicit persistence and
configuration operations. Ordinary methods query one execution node. Every method
below also has an `OnAllNodesAsync` variant returning endpoint-associated
`RespireServerResult<T>` values or failures, including replicas and slotless members.
These calls do not reconcile metadata, change client routing, or replicate configuration.

| Method | Command | Minimum Redis version |
| --- | --- | --- |
| `CommandInfoAsync` | COMMAND INFO | 2.8.13; empty requests mean all commands from 7.0 |
| `CommandDocsAsync` | COMMAND DOCS | 7.0 |
| `CommandGetKeysAsync` | COMMAND GETKEYS | 2.8.13 |
| `ModuleListAsync` | MODULE LIST | 4.0 |
| `ConfigRewriteAsync` | CONFIG REWRITE | 2.8 |
| `ConfigResetStatisticsAsync` | CONFIG RESETSTAT | 2.0 |
| `SaveAsync` | SAVE | 1.0 |
| `BackgroundSaveAsync` | BGSAVE | 1.0; `schedule: true` requires 3.2.2 |
| `BackgroundRewriteAofAsync` | BGREWRITEAOF | 1.0 |

Unsupported commands, missing ACL permissions, and server failures remain
`RespireServerException`. No fallback substitutes another operation. Existing INFO,
COMMAND COUNT/LIST, CONFIG GET/SET, and LASTSAVE methods remain available.

## Command and module inspection

```csharp
var commands = await redis.Server.CommandInfoAsync([RespireCommands.String.GET, "CONFIG GET"]);
foreach (var command in commands)
    Console.WriteLine(command is null ? "Unknown command" : $"{command.Name}: arity {command.Arity}");

var docs = await redis.Server.CommandDocsAsync(["GET", "CONFIG GET"]);
var keys = await redis.Server.CommandGetKeysAsync("MSET", ["one", "value", "two", "value"]);
var modules = await redis.Server.ModuleListAsync();
```

[COMMAND INFO](https://redis.io/docs/latest/commands/command/) preserves request order
and null entries for unknown commands. Negative arity means a minimum argument count.
Legacy key indexes include command words. Optional categories, tips, key specifications,
and recursive subcommands reflect the queried server version. Structured key
specifications and future positional elements remain owned `RespireResult` values.

[COMMAND DOCS](https://redis.io/docs/latest/commands/command-docs/) omits unknown names.
It returns typed documentation, change history, recursive argument descriptions and
subcommands, preserving unknown fields. Empty requests ask for all commands; this can
produce a large response. Prefer selecting specific names when appropriate.

For INFO and DOCS, `"CONFIG GET"` becomes one `CONFIG|GET` lookup token. GETKEYS instead
sends `CONFIG` and `GET` as separate words before the supplied argument tokens. Binary
arguments are snapshotted before asynchronous work, including fan-out discovery.
GETKEYS does not execute the command and never adds a client key prefix. Supply the
actual key bytes you want inspected. Keyless or malformed requests retain the server's
error semantics. See [COMMAND GETKEYS](https://redis.io/docs/latest/commands/command-getkeys/).

[Module results](https://redis.io/docs/latest/commands/module-list/) include the module
name, integer version, optional binary path and argument tokens, and future fields.
All models own their storage and remain valid after reply/client disposal. Arrays are
caller-owned and mutable; record equality compares array references. Nested
`RespireResult` values are GC-owned, so disposal is optional but invalidates their views.

## Configuration and persistence

All five mutations require `AllowAdmin` before I/O, including fan-out. Server ACL rules
still apply. Configuration and files belong to each node independently.
[CONFIG REWRITE](https://redis.io/docs/latest/commands/config-rewrite/) requires a
writable configuration file supplied when the server started; it can fail for servers
started with command-line options alone. RESETSTAT clears local server statistics.

SAVE blocks the Redis server while writing its snapshot. Its successful return means
that SAVE completed on that node. It can delay unrelated clients and should be used
deliberately. Background save and AOF rewrite return
`RespireBackgroundPersistenceResult`, which distinguishes `Started`, `Scheduled`, and
`Unknown` success messages while preserving the original message. Acceptance does not
prove completion or persistence success. Inspect `InfoAsync("persistence")` and the
existing `LastSaveAsync` as appropriate. See
[BGSAVE](https://redis.io/docs/latest/commands/bgsave/) and
[BGREWRITEAOF](https://redis.io/docs/latest/commands/bgrewriteaof/).

```csharp
// The client must be configured with AllowAdmin = true.
var accepted = await redis.Server.BackgroundSaveAsync(schedule: true);
Console.WriteLine($"{accepted.State}: {accepted.Message}");
Console.WriteLine(await redis.Server.InfoAsync("persistence"));
```

Cancellation before sending prevents I/O. Cancellation or timeout after sending does
not undo a configuration change, stop a SAVE, or cancel a background persistence job.
Do not infer rollback from a failed client wait or automatically repeat mutations.

For all-node calls, discovery cancellation throws. After discovery, each node retains
its own success or failure, including cancellation; inspect every result. Partial
success is possible, and no rollback or cluster-wide atomicity is promised. Standalone
clients return one result. Discovery in Cluster mode requires CLUSTER NODES permission.
Explicit all-node SAVE can block several servers at once.

These 18 methods extend `IServerCommands` under the repository's pre-release API policy.
Custom implementations, decorators, and mocks must implement or forward them. They
are immediate Server operations, with no batch or transaction variants.
