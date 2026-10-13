---
title: Lua scripts and functions
description: Run Lua scripts through the script cache and call Redis Functions, with read-only variants, Cluster fan-out, and deferred execution.
---

# Lua scripts and functions

Respire runs Lua through two facets. `client.Scripts` executes `RespireScript` objects with `EVALSHA`/`EVAL`. `client.Functions` calls named Redis 7+ functions with `FCALL` and manages function libraries. Use scripts for ad hoc server-side logic that ships with your application. Use functions when the logic is deployed to the server as a named library.

## Run a script

Create a `RespireScript` once and reuse it. `Create` computes the SHA1 up front. Execution tries `EVALSHA` and falls back to `EVAL` only when the server replies `NOSCRIPT`.

```csharp
await using var client = await RespireClient.ConnectAsync("redis://localhost:6379");
var read = RespireScript.Create("return redis.call('GET', KEYS[1])", readOnly: true);
var value = await client.Scripts.ExecuteStringAsync(read, keys: ["key"]);
```

Keys go to `KEYS[...]` and receive the client's key prefix. Arguments go to `ARGV[...]` and are sent unchanged. In Cluster, every key a script touches must map to one hash slot. Use a shared hash tag such as `{order:42}` for this.

### Execution methods

| Method | Result |
| --- | --- |
| `ExecuteAsync` | A leased `RespireResult`. Dispose it. |
| `ExecuteSpanAsync` | A leased `RespireResult` from span-based keys and arguments. Dispose it. |
| `ExecuteAsync<T>` | The reply deserialized with the client serializer. |
| `ExecuteIntegerAsync` | The reply as `long`. |
| `ExecuteStringAsync` | The reply as `string`, or `null`. |

Typed helpers dispose their replies for you. `ExecuteSpanAsync` copies its span inputs before it returns the pending operation, so you can reuse that storage immediately.

### Read-only scripts

`RespireScript.Create(source, readOnly: true)` selects `EVALSHA_RO` with an `EVAL_RO` fallback. These commands require Redis 7 or later, and Redis rejects write commands inside the script. `IsReadOnly` reports this contract.

- Read-only scripts follow the client's `ReadFrom` policy. They can run on a replica when that policy selects one.
- The `EVAL_RO` fallback runs on the selected connection. Respire does not reload the script on a primary first.
- Writable and read-only descriptors for the same source share one SHA1.
- On a server without `EVAL_RO`, Respire returns the server's error with its command name and error code. It never substitutes a writable command.

### Retries

Scripts default to `RespireCommandRetryCategory.Never`, including read-only scripts, because Respire cannot audit Lua source. Call `WithRetryCategory` to opt in. Immediate execution then retries when `RespireOptions.RetryPolicy` permits that category. A failed attempt counts as possibly executed, so declare a write category only when repeating every effect of the script is safe. See [Command retry policy](command-retries.md).

## Manage the script cache

```csharp
await using var client = await RespireClient.ConnectAsync("redis://localhost:6379");
var read = RespireScript.Create("return redis.call('GET', KEYS[1])", readOnly: true);
var digest = await client.Scripts.LoadAsync(read);
var present = await client.Scripts.ExistsAsync(digest, read.Sha1);
await client.Scripts.FlushAsync(ScriptFlushMode.Sync);
```

- `LoadAsync` sends `SCRIPT LOAD` and returns the SHA1.
- `ExistsAsync` returns one Boolean per digest, in input order, including duplicates. The overload with cancellation takes a span followed by a `CancellationToken`. At least one digest is required, and each must be nonblank. Respire does not validate the format, so a string that is not a cached SHA1 returns `false`.
- `FlushAsync` clears scripts, not keys. `ScriptFlushMode.Default` follows the server's `lazyfree-lazy-user-flush` setting. `Sync` and `Async` choose memory reclamation explicitly and require Redis 6.2 or later.

Execution still handles `NOSCRIPT` after a successful existence check, because the cache can change at any time.

### Cluster behavior

In Cluster, immediate `LoadAsync` and `FlushAsync` visit every discovered primary. `ExistsAsync` reports a digest as present only when every discovered primary has it.

- Respire refreshes topology, sends to the selected primaries concurrently, and waits for every send before returning.
- The call fails when a selected primary cannot be reached. It is not atomic across nodes. Cancellation or failure can leave a load or flush partially applied.
- Existence results describe that set of primaries at that moment. Concurrent topology changes can make them stale.
- Replica caches are not visited. A replica may not have a script loaded on its primary, so execution there can still use the `NOSCRIPT` fallback.

## Scripts in batches and transactions

`batch.Scripts` and `transaction.Scripts` expose `Evaluate`, `Load`, `Exists`, and `Flush`.

```csharp
await using var client = await RespireClient.ConnectAsync("redis://localhost:6379");
var read = RespireScript.Create("return redis.call('GET', KEYS[1])", readOnly: true);
await using var transaction = client.CreateTransaction();
var cleared = transaction.Scripts.Flush();
var pending = transaction.Scripts.Evaluate(read, keys: ["key"]);
await transaction.CommitAsync();
using var result = pending.Result;
var value = result.AsString();
```

- `Evaluate` sends the source with `EVAL` or `EVAL_RO`. It is valid inside `MULTI`/`EXEC` and never needs a `NOSCRIPT` retry. A queued flush therefore does not stop a later queued evaluation.
- Deferred results use managed memory. Unread results hold no pooled reply buffers. Disposing a result invalidates its nested views.
- A queued `Load`, `Exists`, or `Flush` runs only on the execution node. A Cluster transaction runs on the node its keys select, or on the router's default node when it has no keys. A Cluster batch sends keyless cache commands to its keyless group, which can be a different node from the one running a keyed script. Use the immediate methods to reach every primary.
- Batches and transactions never resend an individual script. An eligible unwatched standalone or Sentinel transaction can replay as a whole when every member's retry category permits it.
- Queued commands keep the conservative client-cache invalidation that batches and transactions always use, even when every command is read-only.

## Call a Redis Function

Redis 7 added [FCALL](https://redis.io/docs/latest/commands/fcall/) for named functions. Wrap the library source in a `RespireFunctionLibrary`, then reference its functions:

```csharp
await using var client = await RespireClient.ConnectAsync("redis://localhost:6379");
var library = RespireFunctionLibrary.Create("""
    #!lua name=example
    redis.register_function{function_name='read_value', callback=function(keys,args)
        return redis.call('GET', keys[1])
    end, flags={'no-writes'}}
    """);
var read = library.Function("read_value", readOnly: true);
await client.SetAsync("key", "value");
var value = await client.Functions.ExecuteStringAsync(read, keys: ["key"]);
```

Use `RespireFunction.Create(name)` for a function that you load separately. Pass `readOnly: true` to call it with `FCALL_RO`. Redis then rejects functions that permit writes. `IsReadOnly` reports this contract, and read-only functions follow the client's `ReadFrom` policy.

- Keys receive the client's key prefix. Arguments, library names, and function names do not.
- All resolved keys must share one Cluster slot, even for read-only functions. Keyless calls use the router's default node.
- `ExecuteAsync` returns a leased `RespireResult` to dispose. `ExecuteAsync<T>`, `ExecuteStringAsync`, and `ExecuteIntegerAsync` convert and dispose the reply.
- `ExecuteSpanAsync` copies its span collections before returning. Binary key and argument memory stays borrowed until the call completes.
- Functions default to `RespireCommandRetryCategory.Never`. Use `WithRetryCategory` to opt in, as for scripts.

### Automatic library loading

A library created with `RespireFunctionLibrary.Create` lets immediate calls recover from a missing library:

1. Respire calls the function first.
2. Only Redis's exact `ERR Function not found` reply triggers one reload and one retry. Timeouts, connection failures, and other errors are returned unchanged. A second missing-function reply is returned to you.
3. The reload inspects only the named library with `FUNCTION LIST LIBRARYNAME ... WITHCODE`. It accepts identical source and loads a missing library.

This also covers a flush or restart that removed the library. Do not return the reserved `Function not found` error from your own functions: Respire treats it as proof that the call did not run.

When a read-only call on a replica triggers the reload, the load runs on the primary. The retry then waits for replication with a five-second budget, shortened by a smaller command timeout. Disabling command timeouts does not disable this budget.

Other loading rules:

- A reusable library needs an unquoted, unescaped `name=library_name` header token. Respire rejects quoted headers locally. `LoadAsync(source)` leaves header validation to Redis.
- Reloads for one library and one logical client are serialized, including across prefixed views. Concurrent first calls share one reload. The library is not marked as permanently loaded, so a later flush can trigger another reload.
- A different source under the same library name fails with the server's collision error. If another client loads the library between inspection and `LOAD`, Respire inspects once more and accepts only identical source.
- `RespireFunctionLibrary.Create(source, replace: true)` permits replacement while loading. It does not check source equality on every call: an already registered function is used as-is. Call `LoadAsync` to deploy a replacement before calling a function that already exists.
- Automatic reload needs ACL permission to list and load libraries, not just to call functions.
- Concurrent external deployments or flushes can still make the single retry fail. Applications that deploy different sources under one name with replacement enabled can keep replacing each other. Coordinate deployments or version library and function names.

### Function administration

| Method | Redis command | Scope in Cluster |
| --- | --- | --- |
| `LoadAsync(source, replace)`, `LoadAsync(library)` | FUNCTION LOAD | All discovered primaries. Returns the library name. |
| `DeleteAsync(name)` | FUNCTION DELETE | All discovered primaries. |
| `FlushAsync(FunctionFlushMode)` | FUNCTION FLUSH | All discovered primaries. |
| `RestoreAsync(payload, policy)` | FUNCTION RESTORE | All discovered primaries. |
| `ListAsync(libraryPattern, withCode)` | FUNCTION LIST | One routing node. |
| `DumpAsync()` | FUNCTION DUMP | One routing node. |
| `StatsAsync()` | FUNCTION STATS | One routing node. |

- `ListAsync` returns owned library and function metadata, descriptions, flags, and optionally source. Patterns use Redis glob syntax.
- `FlushAsync` accepts `Default`, `Sync`, or `Async`.
- `DumpAsync` returns owned binary library data. `RestoreAsync` accepts `FunctionRestorePolicy.Append` (the default; collisions fail), `Flush`, or `Replace`. Keep the payload bytes unchanged until the call completes. Redis validates the format and collision policy.
- `StatsAsync` returns the running function, if any, with owned binary arguments, and per-engine library and function counts for one server at one moment.

Fan-out operations are not atomic. Respire waits for every send before it reports a failure, but cancellation or failure can leave some nodes changed, and inconsistent replies are detected only after the mutations ran. The exception does not list per-node outcomes. The set of primaries is a topology snapshot that is not rechecked, so a failover can leave a newly promoted primary without the library. To inspect or back up divergent state, connect directly to each primary.

Function libraries are server-wide. They are not isolated by key prefix or selected database, and they replicate like any other Redis library.

### Functions in batches and transactions

`batch.Functions` and `transaction.Functions` mirror the immediate methods without the `Async` suffix or cancellation parameters.

- Load libraries before execution. Deferred calls never reload a missing library and never resend individually. An eligible transaction can replay as a whole under `RespireOptions.RetryPolicy`. Later commands in the same unit may already have taken effect when one fails.
- Administration runs only on the execution node. Keyless batch administration may run on a different node from a keyed call.
- Deferred `RespireResult` values use managed memory and hold no pooled buffers. Disposing one invalidates its nested views. Other replies are owned arrays and records.

### Client-side cache

Immediate `FCALL_RO` and library administration keep cached key data. Writable `FCALL` invalidates conservatively, because Respire cannot infer which keys a function changed. Deferred calls keep the conservative invalidation that batches and transactions always use.

## Valkey scripting engines

Valkey 9.1 provides Lua as a module-backed scripting engine that an operator can unload. When a scripting command fails with Valkey's exact missing-engine error, Respire runs `INFO scriptingengines` on the same connection. If the complete inventory does not list that engine, Respire throws `RespireScriptingEngineUnavailableException`:

- `Engine` names the missing engine.
- `Endpoint` identifies the server.
- `ServerError` holds the original `RespireServerException`, which is also the inner exception.

Respire does not load modules or replay the command. Make the engine available before you retry. The exception keeps the original error's meaning as a definitive server reply, so locks, coordination primitives, and import sessions handle it like any other rejection. Transport failures remain uncertain. Fire-and-forget calls discard it wherever they discard ordinary command errors.

Detection rules:

- Detection is lazy. Successful calls and ordinary script errors cause no extra I/O.
- `NOSCRIPT` still triggers the `EVAL` fallback, and a missing function library still follows the reload policy. Neither is treated as a missing engine.
- `EVALSHA` and `FCALL` errors are never classified from their text alone. For `EVAL` and source loads, the error must match both the command's error format and the engine named in the submitted source. Ambiguous quoted or escaped engine headers keep the server error.
- The same policy applies to `EVAL`/`EVAL_RO`, `SCRIPT LOAD`, `FUNCTION LOAD`/`RESTORE`, immediate calls, batches, and failed transaction members. A transaction is never replayed.
- Detection depends on Valkey's exact error wording. If a server changes it, you get the original `RespireServerException`.

The probe needs `INFO` permission. If it is denied, unknown, malformed, timed out, or disconnected, you get the original command error. Redis and older Valkey servers need no extra ACL permissions. The probe has at most one second, shortened to the command's remaining time. It never moves to a replacement connection, and Respire keeps no cached inventory, so reconnects and module changes cannot reuse stale results. Within one `EXEC` reply, failed operations share one observation per engine. A caller who cancels during an immediate probe still sees cancellation.

The `valkey/valkey:9.1.0-alpine` image loads its Lua module at startup and allows `MODULE UNLOAD lua` when module administration is enabled. It ships no separate Lua library for `MODULE LOAD lua`; restart the server to restore the engine.

## Custom implementations

`IScriptCommands` and `IBatchScriptCommands` give the cache members (`ExistsAsync` with cancellation, `FlushAsync`, `Load`, `Exists`, `Flush`) default implementations that throw `NotSupportedException`. `IRespireClient.Functions` and `IRespireCommandQueue.Functions` default the same way. Decorators should forward these members to the wrapped implementation.

## Redis references

[EVAL_RO](https://redis.io/docs/latest/commands/eval_ro/), [EVALSHA_RO](https://redis.io/docs/latest/commands/evalsha_ro/), [SCRIPT EXISTS](https://redis.io/docs/latest/commands/script-exists/), [SCRIPT FLUSH](https://redis.io/docs/latest/commands/script-flush/), [FUNCTION LOAD](https://redis.io/docs/latest/commands/function-load/), [FUNCTION LIST](https://redis.io/docs/latest/commands/function-list/), [FUNCTION RESTORE](https://redis.io/docs/latest/commands/function-restore/), [FUNCTION STATS](https://redis.io/docs/latest/commands/function-stats/).
