---
sidebar_position: 2
title: Getting started
description: Install Respire and send your first commands.
---

# Getting started

Connect to a local RESP server and send typed commands in a few lines.

## Prerequisites

- An application targeting .NET 8 or later (Respire ships `net8.0` and `net10.0` builds)
- Redis, Valkey, KeyDB, or another RESP2-compatible server

## Install Respire

Respire is available on [NuGet](https://www.nuget.org/packages/Respire). Its public API may still change before 1.0.

```bash
dotnet add package Respire
```

Optional Redis features and .NET integrations ship as separate packages. See [Packages and namespaces](./packages).

## Start a server

If you do not have a server running locally, Docker is the quickest option:

```bash
docker run --name respire-redis --rm -p 6379:6379 redis:7-alpine
```

## Connect and write a value

```csharp
using Respire;

await using var redis = await RespireClient.ConnectAsync("redis://localhost");

await redis.SetAsync(
    "session:42",
    "active",
    expiry: TimeSpan.FromMinutes(30));

string? state = await redis.GetStringAsync("session:42");
Console.WriteLine(state);
```

`ConnectAsync` connects immediately and fails fast when the endpoint is unavailable. The client is `IAsyncDisposable`; use `await using` or dispose it during application shutdown. Create one client and share it: a single client multiplexes concurrent callers over one connection.

## Store a typed object

The default serializer uses `System.Text.Json`:

<!-- doc-test-declaration: split-before=await redis -->
```csharp
public sealed record User(string Name, int LoginCount);

await redis.SetAsync("user:ada", new User("Ada", 7));
User? user = await redis.GetAsync<User>("user:ada");
```

Typed strings, byte arrays, numeric primitives, and Boolean values bypass the object serializer. Other values use the configured serializer. See [values and serialization](./fundamentals/values-and-serialization).

## Explore commands by data type

```csharp
await redis.Hashes.SetAsync("user:ada", "role", "admin");
string? role = await redis.Hashes.GetStringAsync("user:ada", "role");

await redis.Sets.AddAsync("online", "ada", "grace");
bool online = await redis.Sets.ContainsAsync("online", "ada");
```

Root shortcuts cover frequent operations. Facets keep IntelliSense focused: `Strings`, `Keys`, `Hashes`, `Lists`, `Sets`, `SortedSets`, `Streams`, `Bitmaps`, `HyperLogLog`, `Geo`, `VectorSets`, `Arrays`, `Locks`, `Scripts`, `Functions`, and `Server`.

## Connection URI

The common form is:

```text
redis://[username:password@]host[:port][/database]
```

Use `rediss://` for TLS. The port defaults to `6379`. Query parameters such as `?clientName=api&commandTimeoutMs=2000` set common options; see [URI query options](./fundamentals/connections#uri-query-options).

For connection timeouts, protocol selection, serialization, or logging, use [RespireOptions](./fundamentals/connections).

## Next

- [Strings and keys](./commands/strings-and-keys)
- [Blocking queues](./guides/blocking-queues)
- [Dependency injection](./integrations/dependency-injection)
