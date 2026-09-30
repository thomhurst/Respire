# Lua scripts and the script cache

Create a `RespireScript` once and reuse it. The default permits writes and tries `EVALSHA`, falling back to `EVAL` only for `NOSCRIPT`.

```csharp
var read = RespireScript.Create("return redis.call('GET', KEYS[1])", readOnly: true);
var value = await client.Scripts.ExecuteStringAsync(read, keys: ["key"]);
```

`readOnly: true` requires Redis 7 or later and selects `EVALSHA_RO` with `EVAL_RO` fallback. Redis rejects write commands inside the script. `IsReadOnly` exposes this execution contract; it does not currently select replica connections. Writable and read-only descriptors for the same source have the same SHA1. Keys receive the client's key prefix; arguments do not. Cluster scripts must access keys in one hash slot, normally through a shared hash tag.

The same selection applies to raw `ExecuteAsync`, generic `ExecuteAsync<T>`, `ExecuteIntegerAsync`, `ExecuteStringAsync`, and `ExecuteSpanAsync`. Dispose raw immediate results. Typed helpers dispose their replies automatically. Span inputs are consumed before the method returns its pending operation.

```csharp
var digest = await client.Scripts.LoadAsync(read);
var present = await client.Scripts.ExistsAsync(digest, read.Sha1);
await client.Scripts.FlushAsync(ScriptFlushMode.Sync);
```

`ExistsAsync` returns one Boolean per digest in input order, including duplicates. Its cancellation overload accepts a span followed by a `CancellationToken`. `FlushAsync` clears scripts, not keys. `Default` follows the server's `lazyfree-lazy-user-flush` setting; `Sync` and `Async` explicitly choose memory reclamation and require Redis 6.2 or later. Script execution still handles `NOSCRIPT` after a successful existence check because caches can change.

In Cluster, immediate `LoadAsync` and `FlushAsync` visit every discovered primary. `ExistsAsync` returns true for a digest only when every discovered primary reports it. These operations refresh topology before selecting primaries, fail when a selected primary cannot be contacted, and are not atomic across nodes. Existence results are a best-effort observation of that selected node set, not a guarantee across concurrent topology changes; cancellation or failure may leave a load or flush partially applied. Replica caches are not independently visited.

## Batches and transactions

Both expose `Scripts.Evaluate`, `Scripts.Load`, `Scripts.Exists`, and `Scripts.Flush`:

```csharp
await using var transaction = client.CreateTransaction();
var cleared = transaction.Scripts.Flush();
var pending = transaction.Scripts.Evaluate(read, keys: ["key"]);
await transaction.CommitAsync();
using var result = pending.Result;
var value = result.AsString();
```

Deferred evaluation sends source through `EVAL` or `EVAL_RO` directly. It does not retry `NOSCRIPT` after `EXEC`. Deferred results retain managed storage, so unread results do not hold pooled reply buffers; disposing a result invalidates its nested views.

Deferred cache commands affect only their execution node. A Cluster transaction executes them on the node selected by its keys (or the router's default node for a keyless transaction). A Cluster batch sends keyless cache commands to its keyless group, which can differ from the group executing a keyed script. Use the immediate cache methods when every primary must be visited. A flush does not prevent a later deferred script from running because evaluation carries its source.

Redis references: [EVAL_RO](https://redis.io/docs/latest/commands/eval_ro/), [EVALSHA_RO](https://redis.io/docs/latest/commands/evalsha_ro/), [SCRIPT EXISTS](https://redis.io/docs/latest/commands/script-exists/), [SCRIPT FLUSH](https://redis.io/docs/latest/commands/script-flush/).

External implementations of `IScriptCommands` or `IBatchScriptCommands` remain source-compatible: the new cache members have default implementations that throw `NotSupportedException`. Decorators should forward these members to their underlying implementation to expose cache management. Unsupported Redis versions return their original server error, preserving the command name and error code; Respire does not silently substitute a writable command for a read-only request.
