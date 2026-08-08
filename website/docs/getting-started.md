---
sidebar_position: 2
title: Getting started
description: Build Respire and send your first commands.
---

# Getting started

Connect to a local RESP server and send typed commands in a few lines.

## Prerequisites

- .NET 9 SDK or later (Respire targets both `net8.0` and `net9.0`)
- Redis, Valkey, KeyDB, or another RESP2-compatible server

Respire is pre-release and is not currently published to NuGet. Clone the repository and add a project reference while evaluating it:

```bash
git clone https://github.com/thomhurst/Respire.git
dotnet add reference path/to/Respire/src/Respire/Respire.csproj
```

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

`ConnectAsync` connects immediately and fails fast when the endpoint is unavailable. The client is `IAsyncDisposable`; use `await using` or dispose it during application shutdown.

## Store a typed object

The default serializer uses `System.Text.Json`:

```csharp
public sealed record User(string Name, int LoginCount);

await redis.SetAsync("user:ada", new User("Ada", 7));
User? user = await redis.GetAsync<User>("user:ada");
```

Typed strings and byte arrays bypass the object serializer. Other values passed to generic typed APIs, including numeric and Boolean primitives, use the configured serializer. See [values and serialization](./fundamentals/values-and-serialization).

## Explore commands by data type

```csharp
await redis.Hashes.SetAsync("user:ada", "role", "admin");
string? role = await redis.Hashes.GetStringAsync("user:ada", "role");

await redis.Sets.AddAsync("online", "ada", "grace");
bool online = await redis.Sets.ContainsAsync("online", "ada");
```

Root shortcuts cover frequent operations. Facets—`Strings`, `Keys`, `Hashes`, `Lists`, `Sets`, `SortedSets`, `Streams`, `Scripts`, and `Server`—keep IntelliSense focused.

## Connection URI

The common form is:

```text
redis://[username:password@]host[:port][/database]
```

For connection timeouts, protocol selection, serialization, or logging, use [RespireOptions](./fundamentals/connections).

## Next

- [Strings and keys](./commands/strings-and-keys)
- [Blocking queues](./guides/blocking-queues)
- [Dependency injection](./integrations/dependency-injection)
