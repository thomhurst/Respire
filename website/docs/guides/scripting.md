---
title: Lua scripts and functions
description: Run Lua scripts through the script cache and call Redis Functions, with read-only variants, Cluster fan-out, and deferred execution.
---

# Lua scripts and functions

- `client.Scripts` runs `RespireScript` objects with `EVALSHA`/`EVAL`. Use it for server-side logic that ships with your application.
- `client.Functions` calls named Redis 7+ functions with `FCALL` and manages libraries. Use it when the logic is deployed to the server as a named library.

## Run a script

Create a `RespireScript` once and reuse it. Execution tries `EVALSHA` and falls back to `EVAL` only on `NOSCRIPT`.

```csharp
await using var client = await RespireClient.ConnectAsync("redis://localhost:6379");
var read = RespireScript.Create("return redis.call('GET', KEYS[1])", readOnly: true);
var value = await client.Scripts.ExecuteStringAsync(read, keys: ["key"]);
```

- Keys go to `KEYS[...]` and get the client's key prefix. Arguments go to `ARGV[...]` unchanged.
- In Cluster, every key a script touches must map to one hash slot. Use a shared hash tag such as `{order:42}`.

### Execution methods

| Method | Result |
| --- | --- |
| `ExecuteAsync` | A GC-owned `RespireResult`. Disposal is optional. |
| `ExecuteSpanAsync` | A GC-owned `RespireResult` from span-based keys and arguments. |
| `ExecuteLeaseAsync` / `ExecuteLeaseSpanAsync` | A pooled `RespireResult`. Dispose it with `using`. |
| `ExecuteAsync<T>` | The reply deserialized with the client serializer. |
| `ExecuteIntegerAsync` | The reply as `long`. |
| `ExecuteStringAsync` | The reply as `string`, or `null`. |

Typed helpers dispose their replies for you. `ExecuteSpanAsync` copies the span collections before returning, so you can reuse the span storage immediately. Binary key and argument buffers stay borrowed: keep them unchanged until the operation completes.

### Read-only scripts

`RespireScript.Create(source, readOnly: true)` uses `EVALSHA_RO` with an `EVAL_RO` fallback (Redis 7+). Redis rejects writes inside the script. `IsReadOnly` reports the flag.

- Read-only scripts follow the client's `ReadFrom` policy, so they can run on a replica.
- The `EVAL_RO` fallback runs on the selected connection; Respire does not load the script on a primary first.
- Writable and read-only scripts with the same source share one SHA1.
- On a server without `EVAL_RO`, you get the server's error. Respire never substitutes a writable command.

### Retries

Scripts, including read-only ones, default to `RespireCommandRetryCategory.Never`. Call `WithRetryCategory` to opt in; immediate execution then retries when `RespireOptions.RetryPolicy` permits that category.

:::warning
A failed attempt may have run. Declare a write category only when repeating every effect of the script is safe. See [Command retry policy](command-retries.md).
:::

## Manage the script cache

```csharp
await using var client = await RespireClient.ConnectAsync("redis://localhost:6379");
var read = RespireScript.Create("return redis.call('GET', KEYS[1])", readOnly: true);
var digest = await client.Scripts.LoadAsync(read);
var present = await client.Scripts.ExistsAsync(digest, read.Sha1);
await client.Scripts.FlushAsync(ScriptFlushMode.Sync);
```

- `LoadAsync` sends `SCRIPT LOAD` and returns the SHA1.
- `ExistsAsync` returns one Boolean per digest, in input order, including duplicates. The cancellable overload takes a span then a `CancellationToken`. Pass at least one nonblank digest; an unknown string returns `false`.
- `FlushAsync` clears scripts, not keys. `ScriptFlushMode.Default` follows the server's `lazyfree-lazy-user-flush` setting. `Sync` and `Async` require Redis 6.2+.

The cache can change at any time, so execution still handles `NOSCRIPT` after `ExistsAsync` returns `true`.

### Cluster behavior

In Cluster, immediate `LoadAsync` and `FlushAsync` run on every discovered primary. `ExistsAsync` returns `true` only when every primary has the digest.

- The call fails if a primary is unreachable. It is not atomic: cancellation or failure can leave a load or flush partly applied.
- Existence results can go stale when topology changes.
- Replicas are not visited, so execution on a replica can still need the `NOSCRIPT` fallback.

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

- `Evaluate` sends the source with `EVAL` or `EVAL_RO`, so it never needs a `NOSCRIPT` retry, even after a queued flush.
- Disposing a deferred result invalidates its nested views.
- A queued `Load`, `Exists`, or `Flush` runs only on the execution node. In a Cluster batch, keyless cache commands can run on a different node from a keyed script. Use the immediate methods to reach every primary.
- Batches and transactions never resend an individual script. An unwatched standalone or Sentinel transaction can replay as a whole when every member's retry category permits it.
- Queued commands always invalidate the client cache conservatively, even when read-only.

## Call a Redis Function

[FCALL](https://redis.io/docs/latest/commands/fcall/) (Redis 7+) calls named functions. Wrap the library source in a `RespireFunctionLibrary`, then reference its functions:

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

Use `RespireFunction.Create(name)` for a function you load separately. `readOnly: true` calls it with `FCALL_RO`; Redis then rejects functions that permit writes. Read-only functions follow the client's `ReadFrom` policy.

- Keys get the client's key prefix. Arguments, library names, and function names do not.
- All keys must share one Cluster slot, even for read-only functions. Keyless calls use the router's default node.
- `ExecuteAsync` returns a GC-owned `RespireResult`; disposal is optional. `ExecuteLeaseAsync` and `ExecuteLeaseSpanAsync` return pooled results that must be disposed. `ExecuteAsync<T>`, `ExecuteStringAsync`, and `ExecuteIntegerAsync` dispose the reply for you.
- `ExecuteSpanAsync` copies its span collections before returning. Binary key and argument memory stays borrowed until the call completes.
- Functions default to `RespireCommandRetryCategory.Never`. Use `WithRetryCategory` to opt in.

### Automatic library loading

A library created with `RespireFunctionLibrary.Create` lets immediate calls recover from a missing library:

1. Respire calls the function.
2. Only Redis's exact `ERR Function not found` reply triggers one reload and one retry. Other errors, and a second missing-function reply, are returned to you.
3. The reload checks the named library with `FUNCTION LIST LIBRARYNAME ... WITHCODE`, accepts identical source, and loads it if missing.

This also recovers from a flush or restart.

:::warning
Do not return `Function not found` from your own functions. Respire treats it as proof that the call did not run.
:::

When a read-only call on a replica triggers the reload, the load runs on the primary. The retry waits up to five seconds for replication, or less if the command timeout is smaller. Disabling command timeouts does not remove this limit.

Other loading rules:

- The header needs an unquoted, unescaped `name=library_name` token; Respire rejects quoted headers. `LoadAsync(source)` leaves header checks to Redis.
- Concurrent first calls share one reload. A later flush can trigger another.
- A different source under the same library name fails with the server's collision error.
- `RespireFunctionLibrary.Create(source, replace: true)` allows replacement while loading, but an already registered function is used as-is. Call `LoadAsync` to deploy a replacement.
- Automatic reload needs ACL permission to list and load libraries, not just to call functions.
- Concurrent deployments or flushes can still make the single retry fail. Apps that deploy different sources under one name with replacement enabled can keep replacing each other. Coordinate deployments or version library and function names.

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

- `ListAsync` returns library and function metadata, flags, and optionally source. Patterns use Redis glob syntax.
- `FlushAsync` accepts `Default`, `Sync`, or `Async`.
- `DumpAsync` returns binary library data. `RestoreAsync` accepts `FunctionRestorePolicy.Append` (default; collisions fail), `Flush`, or `Replace`. Do not change the payload bytes until the call completes.
- `StatsAsync` returns the running function, if any, and per-engine library and function counts for one server.

Fan-out operations are not atomic. Cancellation or failure can leave some nodes changed, and the exception does not list per-node outcomes. A failover during the call can leave a newly promoted primary without the library. To inspect divergent state, connect to each primary directly.

Function libraries are server-wide: key prefixes and database selection do not isolate them.

### Functions in batches and transactions

`batch.Functions` and `transaction.Functions` mirror the immediate methods without the `Async` suffix or cancellation parameters.

- Load libraries first. Deferred calls never reload a missing library or resend individually. An eligible transaction can replay as a whole under `RespireOptions.RetryPolicy`. When one command fails, later commands in the unit may already have taken effect.
- Administration runs only on the execution node. Keyless batch administration may run on a different node from a keyed call.
- Disposing a deferred `RespireResult` invalidates its nested views.

### Client-side cache

Immediate `FCALL_RO` and library administration keep cached data. Writable `FCALL` and all deferred calls invalidate conservatively, because Respire cannot tell which keys changed.

## Valkey scripting engines

In Valkey 9.1, Lua is a module an operator can unload. When a scripting command fails with Valkey's exact missing-engine error, Respire checks `INFO scriptingengines` on the same connection. If the engine is not listed, it throws `RespireScriptingEngineUnavailableException`:

- `Engine` names the missing engine.
- `Endpoint` identifies the server.
- `ServerError` holds the original `RespireServerException`, which is also the inner exception.

Respire does not load modules or replay the command; make the engine available, then retry. Locks, coordination primitives, and import sessions treat the exception as a definite server rejection. Fire-and-forget calls discard it like other command errors.

- Successful calls and ordinary script errors cause no extra I/O.
- `NOSCRIPT` and missing function libraries keep their normal handling.
- `EVALSHA` and `FCALL` errors are never classified from their text alone. For `EVAL` and source loads, the error must also match the engine named in the source.
- Detection covers `EVAL`/`EVAL_RO`, `SCRIPT LOAD`, `FUNCTION LOAD`/`RESTORE`, immediate calls, batches, and failed transaction members. A transaction is never replayed.
- Detection depends on Valkey's exact error wording. Otherwise you get the original `RespireServerException`.
- The probe needs `INFO` permission and has at most one second (less if the command has less time left). If it fails for any reason, you get the original error. Redis and older Valkey need no extra ACL permissions.

The `valkey/valkey:9.1.0-alpine` image allows `MODULE UNLOAD lua` when module administration is enabled, but cannot `MODULE LOAD lua` again. Restart the server to restore the engine.

## Custom implementations

In `IScriptCommands` and `IBatchScriptCommands`, the cache members (`ExistsAsync` with cancellation, `FlushAsync`, `Load`, `Exists`, `Flush`) are required implementations, as are `IRespireClient.Functions` and `IRespireCommandQueue.Functions`. Custom implementations and decorators must implement or forward them; omitting them is a compile error. Convenience overloads that forward to required members remain available. See [interface compatibility](../intro.md#interface-compatibility) for the versioning policy.

## Redis references

[EVAL_RO](https://redis.io/docs/latest/commands/eval_ro/), [EVALSHA_RO](https://redis.io/docs/latest/commands/evalsha_ro/), [SCRIPT EXISTS](https://redis.io/docs/latest/commands/script-exists/), [SCRIPT FLUSH](https://redis.io/docs/latest/commands/script-flush/), [FUNCTION LOAD](https://redis.io/docs/latest/commands/function-load/), [FUNCTION LIST](https://redis.io/docs/latest/commands/function-list/), [FUNCTION RESTORE](https://redis.io/docs/latest/commands/function-restore/), [FUNCTION STATS](https://redis.io/docs/latest/commands/function-stats/).
