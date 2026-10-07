# Lua scripts and the script cache

Create a `RespireScript` once and reuse it. The default permits writes and tries `EVALSHA`, falling back to `EVAL` only for `NOSCRIPT`.

```csharp
await using var client = await RespireClient.ConnectAsync("redis://localhost:6379");
var read = RespireScript.Create("return redis.call('GET', KEYS[1])", readOnly: true);
var value = await client.Scripts.ExecuteStringAsync(read, keys: ["key"]);
```

`readOnly: true` requires Redis 7 or later and selects `EVALSHA_RO` with `EVAL_RO` fallback. Redis rejects write commands inside the script. `IsReadOnly` exposes this execution contract. Read-only scripts follow the client's `ReadFrom` policy and can use a replica when that policy selects one. Writable and read-only descriptors for the same source have the same SHA1. Keys receive the client's key prefix; arguments do not. Cluster scripts must access keys in one hash slot, normally through a shared hash tag.

The same selection applies to raw `ExecuteAsync`, generic `ExecuteAsync<T>`, `ExecuteIntegerAsync`, `ExecuteStringAsync`, and `ExecuteSpanAsync`. Dispose raw immediate results. Typed helpers dispose their replies automatically. Span inputs are consumed before the method returns its pending operation.

```csharp
await using var client = await RespireClient.ConnectAsync("redis://localhost:6379");
var read = RespireScript.Create("return redis.call('GET', KEYS[1])", readOnly: true);
var digest = await client.Scripts.LoadAsync(read);
var present = await client.Scripts.ExistsAsync(digest, read.Sha1);
await client.Scripts.FlushAsync(ScriptFlushMode.Sync);
```

`ExistsAsync` returns one Boolean per digest in input order, including duplicates. Its cancellation overload accepts a span followed by a `CancellationToken`. Digests must be nonblank; other strings are forwarded unchanged, so Redis reports false for a value that is not a cached SHA1 rather than Respire rejecting its format. `FlushAsync` clears scripts, not keys. `Default` follows the server's `lazyfree-lazy-user-flush` setting; `Sync` and `Async` explicitly choose memory reclamation and require Redis 6.2 or later. Script execution still handles `NOSCRIPT` after a successful existence check because caches can change. For read-only scripts, source fallback uses `EVAL_RO` on the selected connection; it does not reload the script on a primary.

In Cluster, immediate `LoadAsync` and `FlushAsync` visit every discovered primary. `ExistsAsync` returns true for a digest only when every discovered primary reports it. These operations refresh topology before selecting primaries, send to those primaries concurrently, and observe every send before returning. They fail when a selected primary cannot be contacted and are not atomic across nodes. Existence results are a best-effort observation of that selected node set, not a guarantee across concurrent topology changes; cancellation or failure may leave a load or flush partially applied. Replica caches are not independently visited. Loading on primaries does not promise that a replica has the script; execution on a replica can still require the `NOSCRIPT` source fallback on that same selected server.

## Batches and transactions

Both expose `Scripts.Evaluate`, `Scripts.Load`, `Scripts.Exists`, and `Scripts.Flush`:

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

Deferred evaluation sends source through `EVAL` or `EVAL_RO` directly. It does not retry `NOSCRIPT` after `EXEC`. Deferred results retain managed storage, so unread results do not hold pooled reply buffers; disposing a result invalidates its nested views.

Batches and transactions retain their existing conservative client-cache invalidation policy, even when every queued command is read-only. Deferred cache commands affect only their execution node. A Cluster transaction executes them on the node selected by its keys (or the router's default node for a keyless transaction). A Cluster batch sends keyless cache commands to its keyless group, which can differ from the group executing a keyed script. Use the immediate cache methods when every primary must be visited. A flush does not prevent a later deferred script from running because evaluation carries its source.

Redis references: [EVAL_RO](https://redis.io/docs/latest/commands/eval_ro/), [EVALSHA_RO](https://redis.io/docs/latest/commands/evalsha_ro/), [SCRIPT EXISTS](https://redis.io/docs/latest/commands/script-exists/), [SCRIPT FLUSH](https://redis.io/docs/latest/commands/script-flush/).

External implementations of `IScriptCommands` or `IBatchScriptCommands` remain source-compatible: the new cache members have default implementations that throw `NotSupportedException`. Decorators should forward these members to their underlying implementation to expose cache management. Unsupported Redis versions return their original server error, preserving the command name and error code; Respire does not silently substitute a writable command for a read-only request.

## Redis Functions

Redis 7+ [FCALL](https://redis.io/docs/latest/commands/fcall/) invokes named functions.
Use `RespireFunction.Create(name, readOnly: true)` for `FCALL_RO`; Redis rejects functions
that permit writes. `IsReadOnly` records this contract. Read-only functions follow the client's `ReadFrom` policy and can use a replica when that policy selects one.

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

Function keys receive the view prefix; arguments and library/function names do not.
All resolved keys must share one Cluster slot, even for read-only functions. Keyless calls
use the router's default node. `ExecuteAsync` returns a leased `RespireResult` to dispose;
`ExecuteAsync<T>`, `ExecuteStringAsync`, and `ExecuteIntegerAsync` convert and dispose
replies automatically. `ExecuteSpanAsync` consumes span collections before returning its
pending operation. Binary key/argument memory remains borrowed until completion.

### Loading and reloads

A library holds source and an explicit replacement policy. Reusable libraries require an
unquoted, unescaped `name=library_name` header token; quoted headers are rejected locally
rather than approximating Redis escape rules. Direct `LoadAsync(source)` leaves full header
validation to Redis. Immediate execution tries the
function first. Only Redis's exact `ERR Function not found` reply permits one reload and
one retry; timeouts, connection failures, and other execution errors escape unchanged.
Application functions must not manufacture that reserved reply, because it is interpreted
as proof that invocation did not execute. A second missing-function reply is returned to
the caller. This also handles a flush or restart that removed the library.

Reloads for one reusable library and logical client are serialized, including prefixed
views. Concurrent first-use calls share a reload; this does not permanently mark the
library as loaded, so a later missing-function call can reload after a flush. The loader
inspects only the named library using an escaped `FUNCTION LIST LIBRARYNAME` pattern with
`WITHCODE`, accepts identical
source, and loads missing libraries. A different source with the same library name produces
a server collision error by default. If another client loads between inspection and LOAD,
the exact library-already-exists error triggers one more source inspection; only identical
source is accepted, without another LOAD. `Create(source, replace: true)` explicitly permits
replacement during loading. Existing registered functions are used as-is; this option does
not enforce source equality on every invocation. Use `LoadAsync` to deploy a replacement
before calling a function that already exists. Concurrent external deployment or flush can
still cause the bounded retry to fail. Automatic reload needs permission to inspect and
load libraries in addition to calling functions. Applications deploying different sources
under one name with replacement enabled can repeatedly replace each other; use a coordinated
deployment or versioned library/function names.

### Administration and scope

- `LoadAsync(source, replace)` or `LoadAsync(library)` returns the library name.
- `ListAsync(libraryPattern, withCode)` returns owned library/function metadata, descriptions,
  flags, and optionally source. Patterns follow Redis glob syntax.
- `DeleteAsync(name)` removes a library. `FlushAsync(Default/Sync/Async)` clears libraries.
- `DumpAsync()` returns owned binary library data. `RestoreAsync(payload, policy)` supports
  `Append` (default, collisions fail), `Flush`, and `Replace`. Keep input bytes unchanged
  until completion; Redis validates the serialized format and collision policy.
- `StatsAsync()` returns the running function, if any, with owned binary command arguments
  and per-engine library/function counts. Statistics describe one server at one moment.

Immediate LOAD, DELETE, FLUSH, and RESTORE visit all discovered Cluster primaries. Reload
also visits discovered primaries and accepts matching libraries already present on some
nodes. All sends are observed before reporting a failure. These operations are not atomic;
cancellation or failure can leave some nodes changed. Inconsistent successful replies are
also detected only after the mutations have run. All tasks complete before an exception
is surfaced, but the thrown exception does not aggregate per-node outcomes. The primary
set is a best-effort topology snapshot and is not rechecked after sending; concurrent
failover can leave a newly promoted primary unloaded. LIST, DUMP, and STATS inspect one
routing node only, not a merged cluster view. Connect directly to each primary to inspect
or back up divergent state. Function libraries are server-wide, not isolated by key prefix
or selected database. Replication follows Redis's normal library behavior.

Batch and transaction `Functions` mirror these methods without `Async` or cancellation
parameters. Load libraries before execution. Deferred calls never reload or replay after
an error; later successful commands can still have taken effect. Administration affects
only the execution node; keyless batch administration may run on a different node from a
keyed invocation. Deferred `RespireResult` values use owned managed memory; unread results
retain no pooled reply buffers, and disposal invalidates nested views. Other replies are
owned arrays/records. Deferred execution retains its existing conservative cache invalidation.
Immediate `FCALL_RO` and library administration preserve cached key data; writable `FCALL`
uses conservative mutation invalidation because function effects cannot be inferred.

Existing `IRespireClient` and `IRespireCommandQueue` implementations get default `Functions`
properties that throw `NotSupportedException`. Decorators must forward the facet to expose
it. Redis version/ACL errors remain server errors.

Redis references: [FUNCTION LOAD](https://redis.io/docs/latest/commands/function-load/),
[FUNCTION LIST](https://redis.io/docs/latest/commands/function-list/),
[FUNCTION RESTORE](https://redis.io/docs/latest/commands/function-restore/),
[FUNCTION STATS](https://redis.io/docs/latest/commands/function-stats/).

## Valkey scripting engines

Valkey 9.1 registers Lua as a module-backed scripting engine. If an applicable scripting
command returns the exact missing-engine error, Respire checks `INFO scriptingengines`
on the same physical connection. A complete inventory that omits the named engine produces
`RespireScriptingEngineUnavailableException`. Its `Engine`, `Endpoint`, and `ServerError`
properties identify the missing engine, selected server, and original command error.
The original error is also the inner exception. Respire does not load modules or replay
commands after this exception; an operator must make the engine available before retrying.

Detection depends on Valkey's exact missing-engine error wording. If a server version
changes that wording, Respire returns the original `RespireServerException` without
probing or translating it. An unrecognized error does not prove an engine is available.

Detection is lazy: successful calls and ordinary script errors perform no extra I/O.
`NOSCRIPT` still triggers the existing EVAL fallback, and a registered function's missing
library still follows the existing reload policy. An EVALSHA-only `NOSCRIPT` or an FCALL
`Function not found` error does not identify an engine and is not classified as engine
absence. EVALSHA and FCALL execution errors are never classified from their text alone.
For EVAL and source loads, the error must match both the operation's error format and the
engine identified by the submitted source. Ambiguous quoted/escaped engine headers retain
the server error. Application errors naming a different engine do not trigger a probe.
EVAL/EVAL_RO, SCRIPT LOAD, FUNCTION LOAD/RESTORE, immediate calls, batches, and
executed transaction errors share the same detection policy. A transaction is never replayed.

The optional probe needs INFO permission. A denied, unknown, malformed, timed-out, or
disconnected probe preserves the original command error. Redis and older Valkey servers
do not need additional ACL permissions for working scripting calls. A caller cancelling
during an immediate probe still observes cancellation. Classification after EXEC does not
cancel completion of already-executed transaction results.

Each diagnostic has a maximum one-second deadline, shortened to the command's remaining
budget. There is no persistent inventory or negative cache: each immediate failure checks
its own connection, and the probe cannot reroute to a replacement connection. One EXEC
reply shares its observation (including unknown results) per engine across its failed
operations, then discards it. Transactions use the connection that accepted their frame,
including after a pre-admission maintenance handoff. Reconnects,
failover, and module changes therefore cannot reuse stale absence evidence. The inventory
is an observation at probe time; a concurrent module change can still race with it.

The released `valkey/valkey:9.1.0-alpine` image loads its built-in Lua module at startup
and supports `MODULE UNLOAD lua` when module administration is enabled. That image does
not provide a separate Lua shared library for `MODULE LOAD lua`; restarting the server
restores its startup engine. Deployment-specific module loading remains an operator task.

Released source: [engine inventory](https://github.com/valkey-io/valkey/blob/c9e8005e9d0ec817e26c7db318861cb821409249/src/server.c),
[EVAL errors](https://github.com/valkey-io/valkey/blob/c9e8005e9d0ec817e26c7db318861cb821409249/src/eval.c),
and [function errors](https://github.com/valkey-io/valkey/blob/c9e8005e9d0ec817e26c7db318861cb821409249/src/functions.c).
