---
sidebar_position: 1
slug: /
title: Why Respire
description: A fast, modern Redis client for .NET. Plain C# types in and out, and the hard parts of Redis built in.
---

# Redis code that feels like .NET

Respire is a fast, modern client for Redis, Valkey, KeyDB, and other RESP servers.
You pass plain C# types in and get plain C# types back. Caching, locks, queues, and rate
limits each take one call.

```csharp
await using var redis = await RespireClient.ConnectAsync("redis://localhost");

await redis.SetAsync("user:1", new User("Ada", 36), expiry: TimeSpan.FromMinutes(5));
User? user = await redis.GetAsync<User>("user:1");
```

No multiplexer to tune, no `RedisValue` to unwrap, no extra libraries to glue together.

**[Try it in two minutes →](./quick-start)**

## Why teams choose Respire

### It reads like normal C#

Commands return `string?`, `long`, `bool`, `TimeSpan`, or your own `T?`, serialized with
System.Text.Json by default. Each data type has its own facet, such as `redis.Hashes`,
`redis.Streams`, or `redis.SortedSets`, so IntelliSense shows you what's available.
Pub/sub, stream reads, and key scans are `await foreach` loops.

```csharp
await redis.Hashes.SetAsync("user:1:profile", "city", "London");
await redis.SortedSets.AddAsync("leaderboard", "ada", 98.5);

await foreach (var key in redis.Keys.ScanAsync(match: "user:*"))
{
    Console.WriteLine(key);
}
```

### It's fast without tuning

- **Hot reads come from memory.** With [server-assisted client-side caching](./fundamentals/client-side-caching),
  a cached `GET` takes about 150 ns and allocates nothing. Redis tells Respire when a key
  changes, so you never write invalidation code.
- **Pipelined by default.** Concurrent calls share socket writes automatically, with no
  batching switch.
- **Blocking commands just work.** `BLPOP` and blocking stream reads get their own pooled
  connections, so they never stall your other calls.

See [Performance](./performance) for measured comparisons with StackExchange.Redis.

### The hard parts are built in

Most Redis projects end up adding a lock library, a rate limiter, a job queue, and a
caching layer on top of the client. Respire includes them, tested together:

| Instead of adding… | Use |
| --- | --- |
| A distributed lock library | [`redis.Locks.AcquireAsync(...)`](./guides/distributed-locks): renewing, fenced locks |
| A rate limiter package | [`redis.Coordination.RateLimiters`](./guides/coordination): a standard .NET `RateLimiter` shared by every server |
| A background job framework | [Job queues](./guides/job-queues): retries, scheduling, cron, and a dashboard |
| Hand-written stream consumers | [Stream workers](./guides/stream-workers) and [reliable queues](./guides/reliable-queues) |
| A cache-aside helper | [`GetOrSetAsync`](./fundamentals/client-side-caching#populate-missing-values-with-a-factory) with stampede protection |
| A Redis module client | Typed [JSON](./guides/json), [Search](./guides/search), [TimeSeries](./guides/timeseries), and [vector](./guides/vector-sets) APIs |

### It keeps running when the network doesn't

Respire reconnects automatically and holds commands briefly while it does. It follows
[Cluster](./fundamentals/connections#redis-cluster-endpoint-identity) slot moves and
[Sentinel](./fundamentals/connections#redis-sentinel) failovers, and resubscribes pub/sub
channels after a reconnect. When you need more, you can add
[safe retries](./guides/command-retries), [circuit breakers](./guides/circuit-breakers), and
[failover groups](./guides/failover-groups) across deployments. Timeout errors say what was
happening at the time, so you don't have to guess. See the [timeouts guide](./guides/timeouts).

### It's easy to test

[`Respire.Testing`](./guides/in-memory-testing) is an in-memory server with a fake clock. It
runs your real client code without Docker. You expire a key by moving the clock, not by
waiting.

```csharp
using Respire.Testing;

var clock = new RespireFakeClock();
await using var server = new RespireFakeServer(clock);
await using var client = await RespireClient.ConnectAsync(server.CreateOptions());

await client.SetAsync("session", "abc", expiry: TimeSpan.FromMinutes(20));
clock.Advance(TimeSpan.FromMinutes(21));
string? expired = await client.GetStringAsync("session"); // null
```

### It fits the .NET stack

Respire works with [dependency injection](./integrations/dependency-injection),
[`IDistributedCache` and `HybridCache`](./integrations/caching),
[output caching](./integrations/output-caching), [ASP.NET Core rate limiting](./integrations/rate-limiting),
[SignalR](./integrations/signalr), [Data Protection](./integrations/data-protection),
[health checks](./integrations/health-checks), [OpenTelemetry](./integrations/observability),
[.NET Aspire](./integrations/aspire), and NativeAOT. It also authenticates with Azure, AWS,
and Google Cloud managed Redis.

## Design choices

| Respire chooses | Instead of | So that |
| --- | --- | --- |
| Real return types and a serializer for `T` | A protocol union type (`RedisValue`) | Your code never handles protocol details |
| Facets per data type (`redis.Hashes.GetAsync`) | Hundreds of flat, prefixed methods | IntelliSense acts as the documentation |
| A server-assisted local cache | A network round trip for every read | Hot reads are local and stay correct |
| Dedicated connections for blocking commands | Forbidding them | Lists and streams work as queues |
| Batch results read after the batch runs | Awaitable results that deadlock before the flush | You can't write the await-before-flush deadlock |
| Cancellation that abandons the wait | Cancelling half-written frames | A cancelled call never corrupts the shared connection |

## Where to go next

- **[Quick start](./quick-start).** Connect and run your first commands in two minutes.
- **[Build an ASP.NET Core app](./getting-started).** Dependency injection, typed values,
  pub/sub, and batches.
- **[Recipes](./guides/recipes).** Leaderboards, sessions, outboxes, and leader election.
- **[Coming from StackExchange.Redis](./stackexchange-redis).** What changes, and how to
  migrate step by step.
- **[Choose packages](./packages).** Pick the add-ons for what you're building.
