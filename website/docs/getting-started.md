---
sidebar_position: 2
title: Getting started
description: Install Respire and send your first commands.
---

# Getting started

Install Respire, start a server, and send typed commands.

You need .NET 8 or later (Respire ships `net8.0` and `net10.0` builds) and Redis, Valkey, KeyDB, or another RESP2-compatible server.

## Install Respire

```bash
dotnet add package Respire
```

The package is on [NuGet](https://www.nuget.org/packages/Respire); its API may change before 1.0. Optional features and integrations ship as [separate packages](./packages).

## Start a server

No local server? Use Docker:

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

- `ConnectAsync` connects immediately and fails fast if the endpoint is unavailable.
- The client is `IAsyncDisposable`: use `await using` or dispose it at shutdown.
- Create one client and share it. It multiplexes concurrent callers over one connection.

## Store a typed object

The default serializer uses `System.Text.Json`:

<!-- doc-test-declaration: split-before=await redis -->
```csharp
public sealed record User(string Name, int LoginCount);

await redis.SetAsync("user:ada", new User("Ada", 7));
User? user = await redis.GetAsync<User>("user:ada");
```

Strings, byte arrays, numbers, and Booleans bypass the serializer. See [values and serialization](./fundamentals/values-and-serialization).

## Explore commands by data type

```csharp
await redis.Hashes.SetAsync("user:ada", "role", "admin");
string? role = await redis.Hashes.GetStringAsync("user:ada", "role");

await redis.Sets.AddAsync("online", "ada", "grace");
bool online = await redis.Sets.ContainsAsync("online", "ada");
```

Root methods cover common operations. Facets group the rest: `Strings`, `Keys`, `Hashes`, `Lists`, `Sets`, `SortedSets`, `Streams`, `Bitmaps`, `HyperLogLog`, `Geo`, `VectorSets`, `Arrays`, `Locks`, `Scripts`, `Functions`, and `Server`.

## Connection URI

```text
redis://[username:password@]host[:port][/database]
```

- Use `rediss://` for TLS. The port defaults to `6379`.
- Query parameters such as `?clientName=api&commandTimeoutMs=2000` set common options. See [URI query options](./fundamentals/connections#uri-query-options).
- For timeouts, protocol, serialization, or logging, use [RespireOptions](./fundamentals/connections).

## Next

- [Strings and keys](./commands/strings-and-keys)
- [Blocking queues](./guides/blocking-queues)
- [Dependency injection](./integrations/dependency-injection)
