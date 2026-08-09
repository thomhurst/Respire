---
title: Complete command catalog
description: Execute every documented command with pre-encoded, discoverable descriptors.
---

# Complete command catalog

Typed facets cover common operations and provide natural .NET return types. `RespireCommands`
covers every command in the audited Redis 8.10 and Valkey 9.1 command references, Redis's
integrated modules, Valkey's documented optional modules, and documented KeyDB and Dragonfly
extensions.

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
server-side timeout can be unbounded:

```csharp
using RespireResult popped = await redis.ExecuteAsync(
    RespireCommands.List.BLPOP,
    ["jobs", 0],
    cancellationToken);
```

Descriptors that require or alter connection state—such as `MULTI`, `WAIT`, `SELECT`,
`SUBSCRIBE`, `AUTH`, and `CLIENT TRACKING`—are rejected by `ExecuteAsync`. Multiplexing cannot
safely preserve their connection affinity. Use transactions, subscription APIs, or
`RespireOptions` instead.

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
