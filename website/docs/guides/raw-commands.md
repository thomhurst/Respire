---
title: Complete command catalog
description: Execute every documented command with pre-encoded, discoverable descriptors.
---

# Complete command catalog

Typed facets cover common operations and provide natural .NET return types. `RespireCommands`
covers every command in the audited Redis 8.10 and Valkey 9.1 command references, Redis's
integrated modules, Valkey's documented optional modules, and documented KeyDB and Dragonfly
extensions.

See [Dragonfly and KeyDB extensions](server-extensions.md) for vendor command lists,
version provenance, and rate-limiting/member-expiry examples.

## Finding typed APIs

The audited Redis 8.10 / Valkey 9.1 command families also have typed APIs for the
operations below. Prefer these APIs for argument validation and owned .NET results;
use descriptors when you need an option or extension outside the typed surface.
Each linked guide describes server versions, routing, and result semantics. Redis-only
commands do not become available on Valkey merely because the client exposes them.

| Operations | Typed API guide |
| --- | --- |
| All 18 sparse-array commands, including scans, aggregation, insertion cursors, and ring buffers | [Sparse arrays](../commands/arrays.md) |
| `INCREX` bounded integer/floating counters; `LCS IDX`, match-length options; `COPY DB` | [Strings and keys](../commands/strings-and-keys.md) |
| `LMOVEM`/`BLMOVEM`, `SDIFFCARD`/`SUNIONCARD`, conditional `ZADD` and `INCR`, rank with score, field-only `HSCAN` | [Collections and streams](../commands/collections.md) |
| Connection-local `HIMPORT PREPARE`, `SET`, `DISCARD`, and `DISCARDALL` | [Hash import sessions](hash-import.md) |
| `XCFGSET`, `XREAD`/`XREADGROUP` count/size limits, multi-stream group reads, `NOACK`, `CLAIM`, pending-idle filters, and claim/recovery options | [Stream reads and recovery](../commands/collections.md#streams) |
| `TS.NRANGE`, `TS.NREVRANGE`, `TS.QUERYLABELS`, blocking `TS.READ`, and tail following | [TimeSeries](timeseries.md#redis-810-reads) |
| Search aliases, suggestions, spellcheck, dictionaries, synonyms, tag values, profiling, configuration, query/schema options, hybrid queries, and `COLLECT` | [Search](search.md) |
| `JSON.MERGE`, `JSON.ARRLEN`, and `JSON.NUMPOWBY` | [JSON](json.md#low-level-commands) |
| Typed Sentinel queries and administration with separate credentials and TLS | [Sentinel administration](sentinel-administration.md) |
| Classic Cluster administration, Redis atomic slot migration, and Valkey slot migration/deletion | [Node lifecycle and administration](server-node-lifecycle.md) |
| ACL, replication, modules, backup, script/function cancellation, and lifecycle operations on an explicit node | [Node lifecycle and administration](server-node-lifecycle.md) |
| `CLIENT LIST`/`CLIENT KILL` filters and `FLUSHDB`/`FLUSHALL` modes | [Client administration](client-administration.md#filtering-and-closing-clients) and [server flushes](batches-and-transactions.md#deferred-server-flushes) |

Single-command data APIs have batch/transaction counterparts where queuing is supported.
Blocking reads and list moves use dedicated connections; queued list moves are nonblocking.
Hash import queues belong to their import session. Explicit-node lifecycle operations are
immediate-only, and mutations require `AllowAdmin`. Valkey's internal `SYNCSLOTS` protocol
is intentionally outside the typed request/reply surface; see the
[migration boundary](server-node-lifecycle.md#valkey-slot-migration-and-deletion).

The [in-memory testing server](in-memory-testing.md#supported-subset) models the new core
commands for data types it supports, including sparse arrays. It does not emulate every
module or administration command; use native server fixtures for those operations and
for version-specific behavior. Dragonfly and KeyDB extension commands remain descriptors.

## Using descriptors

Descriptors expose the canonical command name and the references in which it was found. Their
command words are encoded once during static initialization, avoiding string splitting and
temporary token arrays on each execution.

```csharp
using RespireResult result = await redis.ExecuteAsync(
    RespireCommands.Json.JSON_SET,
    "document:42",
    "$",
    "{\"message\":\"hello world\"}");

Console.WriteLine(RespireCommands.Json.JSON_SET.Name);    // JSON.SET
Console.WriteLine(RespireCommands.Json.JSON_SET.Sources); // RedisAndValkey
```

The catalog is grouped by feature, so IDE completion can guide discovery: `Array`, `Bitmap`, `Bloom`,
`Cluster`, `Connection`, `CountMinSketch`, `Cuckoo`, `Dragonfly`, `Geo`, `Hash`, `HyperLogLog`,
`Json`, `Key`, `KeyDb`, `List`, `PubSub`, `Scripting`, `Search`, `Sentinel`, `Server`, `Set`,
`SortedSet`, `Stream`, `String`, `TDigest`, `TimeSeries`, `TopK`, `Transaction`, and `VectorSet`.

Blocking descriptors such as `BLPOP` and an `XREAD` containing `BLOCK` automatically use a
dedicated pooled connection. Supply a cancellation token with the array overload when the
server-side timeout can be unbounded. Flags and cancellation are optional arguments on that
overload, so pass the one you need by name:

```csharp
using RespireResult popped = await redis.ExecuteAsync(
    RespireCommands.List.BLPOP,
    ["jobs", 0],
    cancellationToken: cancellationToken);
```

Descriptors that require or alter connection state—such as `MULTI`, `WAIT`, `SELECT`,
`SUBSCRIBE`, `AUTH`, and `CLIENT TRACKING`—make `ExecuteAsync` throw `NotSupportedException`.
Multiplexing cannot safely preserve their connection affinity. Use transactions, subscription
APIs, or `RespireOptions` instead.

## Retry-risk metadata

Descriptors expose `RetryCategory`, an audited command-level risk classification. On its own it
does not retry anything. When `RespireOptions.RetryPolicy` is set, the policy compares a command's
category with its `MaxCommandRetryCategory`; see [command retries](command-retries.md). A category
does not prove whether a socket write reached the server and does not provide exactly-once
execution. Repeating even a conditional or replacement write can return a different reply and
interact with another client's changes.

```csharp
Console.WriteLine(RespireCommands.String.GET.RetryCategory);   // ReadOnly
Console.WriteLine(RespireCommands.String.SETNX.RetryCategory); // WriteChecked
Console.WriteLine(RespireCommands.String.SET.RetryCategory);   // WriteAccumulating
Console.WriteLine(RespireCommand.Create("GET").RetryCategory); // Never
```

The categories are `Always`, `Connection`, `ReadOnly`, `WriteChecked`, `WriteLastWins`,
`WriteAccumulating`, `ServerAdmin`, and `Never`. The non-`Never` values increase in risk.
`Never` is zero so default values fail closed; a numeric threshold alone is not retry permission.
Unknown commands, default descriptors, implicit string conversions, and `RespireCommand.Create`
descriptors use `Never`, even for known names and explicit cache mutation policies. To declare a
category for the complete invocation, call `WithRetryCategory` on the descriptor; Respire does
not validate that declaration against the command or its arguments.

Read-only scripts/functions and consuming Search cursors remain `Never`. Cache effects and
replica-read eligibility are independent: `EVAL_RO` can retain its existing read routing while
having no automatic retry permission. Option-sensitive `SET`, `ZADD`, and `BITFIELD` use the
conservative `WriteAccumulating` category. Search aggregation/profile commands remain `Never`
because their invocations can involve server-side cursors. Descriptor categories do not inspect
invocation options. Unlisted writes and extensions remain `Never` until audited.

## Cluster key validation

With `UseCluster`, immediate catalog, string, interpolated, and fire-and-forget execution
validate every declared key in supported layouts before connecting or sending the command.
Different slots throw `RespireServerException` with code `CROSSSLOT`, including when
`NoRedirect` is set. The client does not split these raw requests across nodes. Standalone
execution leaves argument validation to the server.
Malformed arguments for a known Cluster layout (for example, a missing GET key or an invalid
EVAL key count) throw `ArgumentException` locally. The same malformed standalone request reaches
the server and can instead produce `RespireServerException`; argument-error types therefore differ
between these modes. Null declared keys are rejected with `ArgumentNullException` before Cluster I/O.

The shared layout table covers the [deferred raw allowlist](deferred-raw-commands.md), including
all-key commands such as MGET/DEL, key/value pairs in MSET/MSETNX, source/destination pairs,
BITOP, and declared key counts in scripts/functions and sorted-set combinations. Immediate
execution additionally understands KEYDB.MEXISTS, blocking list/sorted-set pops and moves,
MSETEX pairs, XREAD/XREADGROUP keys after STREAMS, MIGRATE's fixed key or KEYS form, and
JSON.MGET keys before its path. Argument values, script arguments, stream IDs, JSON paths,
and MIGRATE credentials are not keys. Binary hash tags use their original bytes.

This table is deliberately explicit. Unknown commands and undeclared layouts, including
dynamic key discovery such as SORT patterns, use the client's built-in routing heuristics and
server-side validation; a catalog entry alone does not guarantee complete key discovery. Use typed facets
where available and supply compatible keys for other raw commands. There is no API for
declaring your own key layout. Administrative commands run on one node, except that Cluster
clients send `FUNCTION LOAD`/`DELETE`/`FLUSH`/`RESTORE` and `SCRIPT LOAD`/`FLUSH` to every primary.

Prefixed views reject immediate catalog execution unless the command's layout is registered as
prefixable, meaning it names every key position. Prefixable layouts cover the RedisJSON key
commands (including `JSON.MGET` and `JSON.MSET`), the RedisTimeSeries single-key, compaction-rule,
and `TS.MADD` commands, the probabilistic (`BF.*`, `CF.*`, `CMS.*`, `TOPK.*`, `TDIGEST.*`)
commands, `VADD`/`VREM`/`VSETATTR`, and `HSET`, `HDEL`, `HGETALL`, `HMGET`, `HSETEX`, and
`HPEXPIRE`. Label-filter queries such as `TS.MGET` stay rejected because they could reach keys
outside the prefix. Otherwise, use typed facets or supported deferred raw execution when the
client should apply a key prefix.

Plain string commands (`ExecuteAsync("GET", ...)`) are not rejected on a prefixed view, but their
keys are sent unchanged unless the command is a `JSON.*`, `TS.*`, `BF.*`, `CF.*`, `CMS.*`,
`TOPK.*`, or `TDIGEST.*` command with a prefixable layout. Interpolated commands always send their
keys unchanged. Pass physical keys to these commands.

Key-layout references: [KeyDB 6.3.4 command table](https://github.com/Snapchat/KeyDB/blob/v6.3.4/src/server.cpp),
[MSETEX](https://redis.io/docs/latest/commands/msetex/),
[XREAD](https://redis.io/docs/latest/commands/xread/),
[MIGRATE](https://redis.io/docs/latest/commands/migrate/), and
[JSON.MGET](https://redis.io/docs/latest/commands/json.mget/).

## Dynamic commands

Use a string when targeting an experimental command absent from the audited references.
A string converts implicitly to a caller-supplied `RespireCommand`. For a command you run
repeatedly, keep a static `RespireCommand.Create("MYMODULE.COMMAND")` descriptor so its name is
encoded once.

### Explicit arguments

```csharp
using RespireResult result = await redis.ExecuteAsync(
    "OBJECT",
    "ENCODING",
    "user:42");
```

Arguments are encoded independently. Space-separated command words are split, but argument
values are never split.

### Interpolated commands

```csharp
string key = "message:42";
string payload = "hello world";

using RespireResult result = await redis.ExecuteAsync(
    $"SET {key} {payload} EX {60}");
```

Each interpolation hole becomes exactly one RESP argument, so spaces and arbitrary content inside `payload` cannot change command structure.

## Result lifetime

`RespireResult` can own pooled protocol data and implements `IDisposable`. Keep its lifetime short and use `using`.

Top-level Redis errors throw `RespireServerException`, matching typed commands. Nested error elements remain inspectable through the result for compound replies.

## Prefer a typed facet when available

Typed facets parse replies and validate option combinations. The catalog returns `RespireResult`
because uncommon, administrative, module, and vendor commands have widely varying reply shapes.
