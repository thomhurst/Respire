---
title: Raw commands
description: Execute commands that do not yet have typed wrappers.
---

# Raw commands

Typed facets cover common Redis operations. `ExecuteAsync` is the escape hatch for modules, new server commands, or uncommon operations.

## Explicit arguments

```csharp
using RespireResult result = await redis.ExecuteAsync(
    "OBJECT",
    "ENCODING",
    "user:42");
```

Arguments are encoded independently. Respire does not split a command string on spaces.

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

## Prefer a typed wrapper when reused

Raw execution is ideal for exploration and isolated commands. For a command used throughout an application, wrap parsing and validation in one extension method so protocol assumptions have one home.
