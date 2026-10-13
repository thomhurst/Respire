# Server metadata and persistence

Use the Server facet to inspect command and module metadata, and to run configuration and persistence operations. Ordinary methods query one node. Each method below also has an `OnAllNodesAsync` variant that returns a `RespireServerResult<T>` value or failure per endpoint, including replicas and slotless members. These calls do not reconcile metadata, change routing, or replicate configuration.

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

Unsupported commands, missing ACL permissions, and server failures throw `RespireServerException`; there is no fallback. For INFO, COMMAND COUNT/LIST, CONFIG GET/SET, and LASTSAVE, use `InfoAsync`, `CommandCountAsync`, `CommandListAsync`, `ConfigAsync`, `SetConfigAsync`, and `LastSaveAsync`.

## Command and module inspection

```csharp
var commands = await redis.Server.CommandInfoAsync([RespireCommands.String.GET, "CONFIG GET"]);
foreach (var command in commands)
    Console.WriteLine(command is null ? "Unknown command" : $"{command.Name}: arity {command.Arity}");

var docs = await redis.Server.CommandDocsAsync(["GET", "CONFIG GET"]);
var keys = await redis.Server.CommandGetKeysAsync("MSET", ["one", "value", "two", "value"]);
var modules = await redis.Server.ModuleListAsync();
```

[COMMAND INFO](https://redis.io/docs/latest/commands/command/):

- Keeps request order, with null for unknown commands.
- Negative arity means a minimum argument count. Legacy key indexes include command words.
- Categories, tips, key specifications, and subcommands depend on the server version. Key specifications and future positional elements are `RespireResult` values.

[COMMAND DOCS](https://redis.io/docs/latest/commands/command-docs/):

- Omits unknown names.
- Returns typed docs, history, and recursive arguments and subcommands, keeping unknown fields.
- An empty request returns all commands, which can be a large response. Prefer naming specific commands.

[COMMAND GETKEYS](https://redis.io/docs/latest/commands/command-getkeys/):

- INFO and DOCS send `"CONFIG GET"` as one `CONFIG|GET` token. GETKEYS sends `CONFIG` and `GET` as separate words before your arguments.
- Does not run the command and never adds a key prefix. Pass the actual key bytes.
- Keyless or malformed requests return the server's error.
- Binary arguments are snapshotted before async work, including fan-out discovery.

[Module results](https://redis.io/docs/latest/commands/module-list/) include the name, integer version, optional binary path and arguments, and future fields.

All models own their storage and stay valid after the reply and client are disposed. Arrays are caller-owned and mutable; record equality compares array references. Disposing nested `RespireResult` values is optional and invalidates their views.

## Configuration and persistence

All five mutations require `AllowAdmin`, checked before I/O (including fan-out). Server ACL rules still apply. Configuration and files are per node.

- [CONFIG REWRITE](https://redis.io/docs/latest/commands/config-rewrite/) needs the writable configuration file the server started with. It can fail for servers started with command-line options only.
- RESETSTAT clears the node's statistics.
- SAVE blocks the server until the snapshot is written, which can delay other clients. A successful return means SAVE completed on that node.
- [BGSAVE](https://redis.io/docs/latest/commands/bgsave/) and [BGREWRITEAOF](https://redis.io/docs/latest/commands/bgrewriteaof/) return `RespireBackgroundPersistenceResult`, with `State` (`Started`, `Scheduled`, or `Unknown`) and the original `Message`. Acceptance does not prove the job succeeded; check `InfoAsync("persistence")` or `LastSaveAsync`.

```csharp
// The client must be configured with AllowAdmin = true.
var accepted = await redis.Server.BackgroundSaveAsync(schedule: true);
Console.WriteLine($"{accepted.State}: {accepted.Message}");
Console.WriteLine(await redis.Server.InfoAsync("persistence"));
```

Cancellation:

- Before sending, it prevents I/O.
- After sending, cancellation or timeout does not undo a configuration change, stop a SAVE, or cancel a background job. Do not assume rollback, and do not repeat mutations automatically.

All-node calls:

- Discovery cancellation throws. After discovery, each node has its own success or failure, including cancellation. Check every result.
- Partial success is possible, with no rollback or atomicity. Standalone returns one result.
- Cluster discovery needs CLUSTER NODES permission.
- All-node SAVE can block several servers at once.

These 18 methods (nine single-node methods and their all-node counterparts) are `IServerCommands` members. Custom implementations, decorators, and mocks must implement or forward them. They are immediate Server operations, not available in batches or transactions.
