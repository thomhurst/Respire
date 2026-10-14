---
title: Complete command catalog
description: Execute every documented command with pre-encoded, discoverable descriptors.
---

# Complete command catalog

`RespireCommands` has a descriptor for every command in the audited Redis 8.10 and Valkey 9.1 references, Redis's integrated modules, Valkey's documented optional modules, and documented KeyDB and Dragonfly extensions. Use it when a typed facet lacks the command or option you need.

## Using descriptors

```csharp
using RespireResult result = await redis.ExecuteAsync(
    RespireCommands.Json.JSON_SET,
    "document:42",
    "$",
    "{\"message\":\"hello world\"}");

Console.WriteLine(RespireCommands.Json.JSON_SET.Name);    // JSON.SET
Console.WriteLine(RespireCommands.Json.JSON_SET.Sources); // RedisAndValkey
```

Descriptors expose the canonical command name and the references that list it. Command words are encoded once, at static initialization.

The catalog is grouped by feature for IDE completion: `Array`, `Bitmap`, `Bloom`, `Cluster`, `Connection`, `CountMinSketch`, `Cuckoo`, `Dragonfly`, `Geo`, `Hash`, `HyperLogLog`, `Json`, `Key`, `KeyDb`, `List`, `PubSub`, `Scripting`, `Search`, `Sentinel`, `Server`, `Set`, `SortedSet`, `Stream`, `String`, `TDigest`, `TimeSeries`, `TopK`, `Transaction`, and `VectorSet`. See [Dragonfly and KeyDB extensions](server-extensions.md) for vendor commands.

Blocking descriptors, such as `BLPOP` or an `XREAD` with `BLOCK`, use a dedicated connection automatically. When the server-side timeout can be unbounded, pass a cancellation token before the individual arguments:

```csharp
using RespireResult popped = await redis.ExecuteAsync(
    RespireCommands.List.BLPOP,
    cancellationToken,
    "jobs", 0);
```

`ExecuteFireAndForgetAsync(command, cancellationToken, args...)` uses the same
ordering. The array overload remains available when supplying command flags:
`ExecuteAsync(command, args, flags, cancellationToken)`.

Commands that need or change connection state, such as `MULTI`, `WAIT`, `SELECT`, `SUBSCRIBE`, `AUTH`, and `CLIENT TRACKING`, throw `NotSupportedException`, because multiplexing cannot keep their connection affinity. Use transactions, subscription APIs, or `RespireOptions` instead.

## Finding typed APIs

Prefer a typed API when one exists: it validates arguments and returns owned .NET results. Each guide lists server versions, routing, and result semantics. Redis-only commands do not work on Valkey just because the client exposes them.

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

- Single-command data APIs have batch and transaction forms where queuing is supported.
- Blocking reads and list moves use dedicated connections; queued list moves do not block.
- Hash import queues belong to their import session.
- Explicit-node lifecycle operations are immediate-only, and mutations require `AllowAdmin`.
- Valkey's internal `SYNCSLOTS` protocol is not exposed; see the [migration boundary](server-node-lifecycle.md#valkey-slot-migration-and-deletion).

The [in-memory testing server](in-memory-testing.md#supported-subset) models the new core commands for the data types it supports, including sparse arrays. It does not emulate every module or administration command, or Dragonfly and KeyDB extensions; use real server fixtures for those and for version-specific behavior.

## Retry-risk metadata

Each descriptor has a `RetryCategory`. It does not retry anything by itself. When `RespireOptions.RetryPolicy` is set, the policy compares the category with its `MaxCommandRetryCategory`; see [command retries](command-retries.md).

```csharp
Console.WriteLine(RespireCommands.String.GET.RetryCategory);   // ReadOnly
Console.WriteLine(RespireCommands.String.SETNX.RetryCategory); // WriteChecked
Console.WriteLine(RespireCommands.String.SET.RetryCategory);   // WriteAccumulating
Console.WriteLine(RespireCommand.Create("GET").RetryCategory); // Never
```

Categories, in increasing risk: `Always`, `Connection`, `ReadOnly`, `WriteChecked`, `WriteLastWins`, `WriteAccumulating`, `ServerAdmin`. `Never` is zero, so default values fail closed; a numeric threshold alone is not retry permission.

These use `Never`:

- Unknown commands, default descriptors, implicit string conversions, and `RespireCommand.Create` descriptors, even for known names and explicit cache mutation policies.
- Read-only scripts and functions. Their read routing is separate: `EVAL_RO` can still read from replicas.
- Search cursor, aggregation, and profile commands, which can involve server-side cursors.
- Unlisted writes and extensions, until audited.

Categories do not inspect options, so option-sensitive `SET`, `ZADD`, and `BITFIELD` use the conservative `WriteAccumulating`. To declare a category for a whole invocation, call `WithRetryCategory` on the descriptor. Respire does not check that declaration.

:::warning
A category cannot tell whether a socket write reached the server, and it does not give exactly-once execution. Repeating even a conditional or replacement write can return a different reply and interact with other clients' changes.
:::

## Cluster key validation

With `UseCluster`, immediate catalog, string, interpolated, and fire-and-forget execution validate every declared key before connecting or sending.

| Problem | Cluster | Standalone |
|---|---|---|
| Keys in different slots | `RespireServerException` with code `CROSSSLOT`, even with `NoRedirect`. Requests are never split across nodes. | Sent; the server validates. |
| Malformed arguments for a known layout (missing GET key, bad EVAL key count) | `ArgumentException`, locally. | Sent; can produce `RespireServerException`. |
| Null declared key | `ArgumentNullException` before I/O. | — |

Key layouts are known for:

- The [deferred raw allowlist](deferred-raw-commands.md): all-key commands such as MGET/DEL, MSET/MSETNX pairs, source/destination pairs, BITOP, and declared key counts in scripts, functions, and sorted-set combinations.
- Immediate execution only: KEYDB.MEXISTS, blocking list and sorted-set pops and moves, MSETEX pairs, XREAD/XREADGROUP keys after STREAMS, MIGRATE's single key or KEYS form, and JSON.MGET keys before the path.

Argument values, script arguments, stream IDs, JSON paths, and MIGRATE credentials are not keys. Binary hash tags use their original bytes.

Unknown commands and undeclared layouts, including dynamic key discovery such as SORT patterns, fall back to built-in routing heuristics and server-side validation. A catalog entry does not guarantee full key discovery, and you cannot declare your own key layout. Use typed facets where available, and supply same-slot keys otherwise. Administrative commands run on one node, except that Cluster clients send `FUNCTION LOAD`/`DELETE`/`FLUSH`/`RESTORE` and `SCRIPT LOAD`/`FLUSH` to every primary.

### Key prefixes

- **Catalog descriptors:** a prefixed view rejects them unless the layout is prefixable (names every key position). Prefixable layouts cover RedisJSON key commands (including `JSON.MGET` and `JSON.MSET`); RedisTimeSeries single-key, compaction-rule, and `TS.MADD` commands; `BF.*`, `CF.*`, `CMS.*`, `TOPK.*`, and `TDIGEST.*`; `VADD`/`VREM`/`VSETATTR`; and `HSET`, `HDEL`, `HGETALL`, `HMGET`, `HSETEX`, and `HPEXPIRE`. Label-filter queries such as `TS.MGET` stay rejected, because they could reach keys outside the prefix.
- **Plain strings** (`ExecuteAsync("GET", ...)`): not rejected, but keys are sent unchanged unless the command is a prefixable `JSON.*`, `TS.*`, `BF.*`, `CF.*`, `CMS.*`, `TOPK.*`, or `TDIGEST.*` command.
- **Interpolated commands:** keys are always sent unchanged.

Pass physical keys to unprefixed commands, or use typed facets or [deferred raw execution](deferred-raw-commands.md) to have the client apply the prefix.

Key-layout references: [KeyDB 6.3.4 command table](https://github.com/Snapchat/KeyDB/blob/v6.3.4/src/server.cpp),
[MSETEX](https://redis.io/docs/latest/commands/msetex/),
[XREAD](https://redis.io/docs/latest/commands/xread/),
[MIGRATE](https://redis.io/docs/latest/commands/migrate/), and
[JSON.MGET](https://redis.io/docs/latest/commands/json.mget/).

## Dynamic commands

For a command missing from the audited references, pass a string; it converts implicitly to a `RespireCommand`. For a command you run often, keep a static `RespireCommand.Create("MYMODULE.COMMAND")` descriptor so its name is encoded once.

### Explicit arguments

```csharp
using RespireResult result = await redis.ExecuteAsync(
    "OBJECT",
    "ENCODING",
    "user:42");
```

Space-separated command words are split. Argument values are never split.

### Interpolated commands

```csharp
string key = "message:42";
string payload = "hello world";

using RespireResult result = await redis.ExecuteAsync(
    $"SET {key} {payload} EX {60}");
```

Each interpolation hole becomes exactly one RESP argument, so spaces or other content in `payload` cannot change the command's structure.

## Result lifetime

`ExecuteAsync` returns a `RespireResult` backed by GC-owned storage, including nested
arrays and payloads. You do not need to dispose it. Calling `Dispose` is still
supported and invalidates the root, its struct copies, and nested views.

Use `ExecuteLeaseAsync` when you want to avoid copying the reply. It supports the
same catalog, raw descriptor, interpolated command, flags, and cancellation arguments:

```csharp
using RespireResult lease = await redis.ExecuteLeaseAsync("GET", "large-value");
Process(lease.AsSpan());
```

Always dispose a leased result. Nested views borrow its storage and must not outlive
the root lease. RESP001 warns when an explicit lease is dropped without disposal.

Top-level Redis errors throw `RespireServerException`, as with typed commands. Nested error elements in compound replies stay inspectable through the result.

The catalog returns `RespireResult` because uncommon, administrative, module, and vendor commands have varied reply shapes. Typed facets parse replies and validate options, so prefer them when available.
