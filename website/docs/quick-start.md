---
sidebar_position: 2
title: Quick start
description: Connect to Redis and run your first Respire commands in two minutes.
---

# Quick start

Two minutes, one file. You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) or later and a
Redis-compatible server. Respire runs on .NET 10 and .NET 11.

## 1. Start Redis

If you don't have a server running, start one with Docker:

```bash
docker run --name respire-redis --rm -d -p 6379:6379 redis:8
```

Valkey, KeyDB, and managed services such as Azure Managed Redis, Amazon ElastiCache, and
Google Cloud Memorystore work too. Pass their connection string in step 3.

## 2. Create an app

```bash
dotnet new console -n HelloRespire
cd HelloRespire
dotnet add package Respire
```

## 3. Write some Redis code

Replace `Program.cs` with:

<!-- doc-test-top-level-tail-declaration: split-before=record Player -->
```csharp title="Program.cs"
using Respire;

await using var redis = await RespireClient.ConnectAsync("redis://localhost");

// Strings, with an expiry
await redis.SetAsync("greeting", "Hello from Respire", expiry: TimeSpan.FromMinutes(5));
Console.WriteLine(await redis.GetStringAsync("greeting"));

// Your own types, serialized as JSON
await redis.SetAsync("player:ada", new Player("Ada", Level: 7));
Player? ada = await redis.GetAsync<Player>("player:ada");
Console.WriteLine($"{ada?.Name} is level {ada?.Level}");

// Counters
long visits = await redis.IncrementAsync("visits");
Console.WriteLine($"Visit number {visits}");

// Other data types live behind facets: Hashes, Lists, Sets, SortedSets, Streams...
await redis.SortedSets.AddAsync("leaderboard", ("ada", 98.5), ("grace", 97.5));
string[] top = await redis.SortedSets.RangeAsync("leaderboard", 0, 2, descending: true);
Console.WriteLine($"Leader: {top[0]}");

record Player(string Name, int Level);
```

## 4. Run it

```bash
dotnet run
```

```text
Hello from Respire
Ada is level 7
Visit number 1
Leader: ada
```

That's it: no multiplexer, no `RedisValue`, no configuration. The client pipelines
concurrent calls automatically and reconnects if the connection drops. Create one client
when your app starts and share it everywhere.

:::tip Connection strings

`ConnectAsync` accepts Redis URIs (`rediss://user:password@host:6380` for TLS), connection
strings copied from the Azure portal, and StackExchange.Redis connection strings. Add
`?cluster=true` for Redis Cluster or `?serviceName=mymaster` for Sentinel. See
[Connections](./fundamentals/connections).

:::

## Next steps

- **[Build an ASP.NET Core app](./getting-started).** Dependency injection, typed endpoints,
  pub/sub, and batches.
- **[Make hot reads local](./fundamentals/client-side-caching).** Turn on client-side caching
  and cache-aside with one option.
- **[Recipes](./guides/recipes).** Leaderboards, sessions, an outbox, and leader election.
- **[Distributed locks](./guides/distributed-locks)** and **[job queues](./guides/job-queues)**.
- **[Test without Docker](./guides/in-memory-testing).** Use the in-memory server.
