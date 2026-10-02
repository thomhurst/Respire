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
Console.WriteLine(RespireCommands.Json.JSON_SET.Sources); // Redis, Valkey
```

The catalog is grouped by feature, so IDE completion can guide discovery: `Bitmap`, `Bloom`,
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
`SUBSCRIBE`, `AUTH`, and `CLIENT TRACKING`—are rejected by `ExecuteAsync`. Multiplexing cannot
safely preserve their connection affinity. Use transactions, subscription APIs, or
`RespireOptions` instead.

## Cluster key validation

With `UseCluster`, immediate catalog, string, interpolated, and fire-and-forget execution
validate every declared key in supported layouts before connecting or sending the command.
Different slots throw `RespireServerException` with code `CROSSSLOT`, including when
`NoRedirect` is set. The client does not split these raw requests across nodes. This changes
previous first-key-only routing for these layouts; standalone execution still leaves argument
validation to the server.
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
dynamic key discovery such as SORT patterns, retain their existing routing and server-side
validation; a catalog entry alone does not guarantee complete key discovery. Use typed facets
where available and supply compatible keys for other raw commands. No caller-provided layout
API is required or inferred. Administrative commands retain their existing node-local scope.
Prefixed views reject immediate catalog execution unless the command's layout is registered as
prefixable, meaning it names every key position. Today that covers the RedisTimeSeries single-key,
compaction-rule, and `TS.MADD` commands and the probabilistic (`BF.*`, `CF.*`, `CMS.*`, `TOPK.*`,
`TDIGEST.*`) commands. Label-filter queries such as `TS.MGET` stay rejected because they could reach
keys outside the prefix. Otherwise, use typed facets or supported deferred raw execution when the
client should apply a key prefix. The deferred allowlist is unchanged.

Key-layout references: [KeyDB 6.3.4 command table](https://github.com/Snapchat/KeyDB/blob/v6.3.4/src/server.cpp),
[MSETEX](https://redis.io/docs/latest/commands/msetex/),
[XREAD](https://redis.io/docs/latest/commands/xread/),
[MIGRATE](https://redis.io/docs/latest/commands/migrate/), and
[JSON.MGET](https://redis.io/docs/latest/commands/json.mget/).

## Dynamic commands

Use a string when targeting an experimental command absent from the audited references.

## Explicit arguments

```csharp
using RespireResult result = await redis.ExecuteAsync(
    "OBJECT",
    "ENCODING",
    "user:42");
```

Arguments are encoded independently. Space-separated command words are split, but argument
values are never split.

## Interpolated commands

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
Keep the result lifetime short and dispose it after parsing.
