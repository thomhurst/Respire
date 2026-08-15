---
sidebar_position: 1
slug: /
title: Meet Respire
description: A fast, modern RESP client for .NET.
---

# Redis code that feels like .NET

Respire is a fast, modern RESP client for .NET. It works with Redis, Valkey, KeyDB, and other RESP-compatible servers while keeping protocol details out of application code.

```csharp
await using var redis = await RespireClient.ConnectAsync("redis://localhost");

await redis.SetAsync("greeting", "hello", expiry: TimeSpan.FromMinutes(5));
string? greeting = await redis.GetStringAsync("greeting");
```

:::warning Pre-release

Respire's public API may change. Cluster routing, Sentinel primary discovery, and bounded RESP3
client-side caching for eligible Redis reads are available; automatic Sentinel failover remains a roadmap item. See the
[roadmap](./roadmap).

:::

## Why Respire exists

Serious Redis clients need multiplexed connections, pipelining, pooled buffers, reconnection, and careful cancellation. Application code should not need to think in protocol union types to benefit from that machinery.

Respire combines a performance-focused wire layer with an API designed for current .NET:

- **Server-assisted client caching.** Eligible hot reads complete from bounded process memory;
  Redis invalidation pushes keep entries coherent without application-owned refresh plumbing.
- **Real return types.** Commands return `string?`, `long`, `bool`, `TimeSpan`, or `T?`.
- **Async-first.** Pub/sub, stream consumers, and key scanning use `IAsyncEnumerable`.
- **Discoverable commands.** Data types live behind facets such as `redis.Hashes` and `redis.Streams`.
- **Automatic pipelining.** Concurrent callers coalesce into fewer socket writes without a batching switch.
- **First-class blocking commands.** `BLPOP` and blocking stream reads use dedicated pooled connections.
- **Production integrations.** Dependency injection, Microsoft caching abstractions, typed serialization, and OpenTelemetry are built in.

## A small, typed surface

Common string and key operations sit on the client. Other commands are grouped by data type:

```csharp
await redis.Hashes.SetAsync("user:1", "name", "Ada");
await redis.Lists.RightPushAsync("jobs", "resize:42");
await redis.SortedSets.AddAsync("leaderboard", "ada", 98.5);

await foreach (var key in redis.Keys.ScanAsync(match: "user:*"))
{
    Console.WriteLine(key);
}
```

## Choose your next step

- [Get connected](./getting-started) and run the first command.
- Make hot reads local with [server-assisted client-side caching](./fundamentals/client-side-caching).
- Learn how [values and serialization](./fundamentals/values-and-serialization) work.
- Build a [blocking work queue](./guides/blocking-queues).
- Add Respire to an ASP.NET Core app with [dependency injection](./integrations/dependency-injection).
