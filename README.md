# Respire

**A fast, modern Redis client for .NET, with distributed locks, rate limiters, and even server assisted client cache.**

Works with Redis, Valkey, KeyDB, and other RESP-compatible servers.

[Documentation](https://thomhurst.github.io/Respire/) ·
[Getting started](https://thomhurst.github.io/Respire/docs/getting-started) ·
[Benchmarks](https://thomhurst.github.io/Respire/docs/benchmarks) ·
[Coming from StackExchange.Redis?](https://thomhurst.github.io/Respire/docs/stackexchange-redis)

```bash
dotnet add package Respire
```

```csharp
await using var redis = await RespireClient.ConnectAsync("redis://localhost");

await redis.SetAsync("user:1", new User("Ada", 36), expiry: TimeSpan.FromMinutes(5));
User? user = await redis.GetAsync<User>("user:1");
```

> **Status:** Respire is pre-release, so its API may still change. See the
> [roadmap](https://thomhurst.github.io/Respire/docs/roadmap).

## Why Respire?

- **Plain .NET types.** Get `string?`, `long`, `bool`, or your own `T?`, not protocol wrappers.
- **Fast by default.** Concurrent calls are pipelined automatically. Blocking commands get their
  own connection, so they never stall other calls.
- **Hot reads from memory.** Turn on client-side caching and Redis tells Respire when to evict.
- **Every command.** Typed APIs for each data type, and a generated catalog of every Redis 8.10
  and Valkey 9.1 command.
- **Ready for production.** Cluster, Sentinel, reconnection, OpenTelemetry, and dependency
  injection are built in.

## Batteries included

| You need | With Respire |
| --- | --- |
| [A distributed lock](https://thomhurst.github.io/Respire/docs/guides/distributed-locks) | `redis.Locks.AcquireAsync(key, expiry)` |
| [A shared rate limit](https://thomhurst.github.io/Respire/docs/guides/coordination#redis-backed-rate-limits) | `redis.Coordination.RateLimiters.SlidingWindow(...)` |
| [A cross-process semaphore](https://thomhurst.github.io/Respire/docs/guides/coordination#distributed-semaphores) | `redis.Coordination.CreateSemaphore(key, capacity)` |
| [A work queue](https://thomhurst.github.io/Respire/docs/guides/blocking-queues) | `redis.Lists.LeftPopAsync(key, waitFor: timeout)` |
| [Pub/sub](https://thomhurst.github.io/Respire/docs/guides/pub-sub) | `await foreach` over `redis.SubscribeAsync(channel)` |
| [Unix domain sockets](docs/UNIX_SOCKETS.md) | `RespireClient.ConnectAsync("unix:///run/redis/redis.sock")` |
| [Stream consumer groups](https://thomhurst.github.io/Respire/docs/commands/collections#consumer-groups) | `await foreach` over `redis.Streams.ReadGroupAsync(...)` |
| [Hot reads without a round trip](https://thomhurst.github.io/Respire/docs/fundamentals/client-side-caching) | `ClientSideCache = new()` |
| [`IDistributedCache` and `HybridCache`](https://thomhurst.github.io/Respire/docs/integrations/caching) | `services.AddRespireHybridCache(connectionString)` |
| [.NET Aspire](docs/ASPIRE.md) | `builder.AddRespireClient("cache")` |
| [Tests without Docker](https://thomhurst.github.io/Respire/docs/guides/in-memory-testing) | `new RespireFakeServer()` |

### Distributed locks

Run a job on one instance only. Disposing the attempt releases the lock:

```csharp
await using var attempt = await redis.Locks.AcquireAsync("locks:nightly-report", TimeSpan.FromSeconds(30));
if (attempt.Acquired)
{
    await RunReportAsync();
}
```

Waiting and keep-alive renewal are one call each. See the
[locks guide](https://thomhurst.github.io/Respire/docs/guides/distributed-locks).

### Rate limiting

`Respire.Coordination` returns standard .NET `RateLimiter` instances, shared by every process
that uses the same key. They also work with the ASP.NET Core rate-limiting middleware.

```csharp
using System.Threading.RateLimiting;
using Respire.Coordination;

await using var limiter = redis.Coordination.RateLimiters.SlidingWindow(
    $"limits:api:{userId}", permitLimit: 100, window: TimeSpan.FromMinutes(1), segments: 6);

using var lease = await limiter.AcquireAsync(1, cancellationToken);
if (!lease.IsAcquired && lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
{
    Console.WriteLine($"Slow down. Try again in {retryAfter}.");
}
```

### Semaphores and more

Allow at most four exports at once, across all your servers:

```csharp
using Respire.Coordination;

var exports = redis.Coordination.CreateSemaphore("permits:exports", capacity: 4);
await using var attempt = await exports.TryAcquireAsync(TimeSpan.FromSeconds(30));
if (attempt.Acquired)
{
    await RunReportAsync();
}
```

The same package has read-write locks, countdown latches, leases, and Redlock. See the
[coordination guide](https://thomhurst.github.io/Respire/docs/guides/coordination).

### Client-side caching

Set one option. Repeated reads then come from local memory, and Redis sends an invalidation
when the key changes:

```csharp
await using var cachedRedis = await RespireClient.ConnectAsync(new RespireOptions
{
    Endpoints = { new RespireEndpoint("localhost") },
    ClientSideCache = new(),
});

string? name = await cachedRedis.GetStringAsync("user:42:name"); // Cached after the first read.
```

A cache hit takes hundreds of nanoseconds instead of a network round trip.

### Tests without Docker

`Respire.Testing` runs your real client code against an in-memory server with a fake clock:

```csharp
using Respire.Testing;

var clock = new RespireFakeClock();
await using var server = new RespireFakeServer(clock);
await using var client = await RespireClient.ConnectAsync(server.CreateOptions());

await client.SetAsync("session", "abc", expiry: TimeSpan.FromMinutes(20));
clock.Advance(TimeSpan.FromMinutes(21));
string? expired = await client.GetStringAsync("session"); // null
```

### Batches and Cluster

Queue commands, send them in one flush, and read typed results:

```csharp
using var batch = redis.CreateBatch();
var name = batch.GetString("name");
var visits = batch.Increment("visits");
await batch.ExecuteAsync();

Console.WriteLine($"{name.Result}: {visits.Result}");
```

For Redis Cluster, add `?cluster=true` to the connection string. Respire handles slot routing
and redirects. See [transactions](https://thomhurst.github.io/Respire/docs/guides/batches-and-transactions)
and [connections](https://thomhurst.github.io/Respire/docs/fundamentals/connections).

For explicit-node slot and topology operations, see [Cluster administration](docs/CLUSTER_ADMINISTRATION.md).

### ASP.NET Core

```csharp
builder.Services.AddRespire(builder.Configuration.GetConnectionString("redis")!);
builder.Services.AddRespireHybridCache("redis://localhost", instanceName: "myapp:");
```

The client connects lazily, so startup never waits for Redis. The cache uses the same layout as
`Microsoft.Extensions.Caching.StackExchangeRedis`, so existing entries keep working.

## Packages

| Package | Adds |
| --- | --- |
| `Respire` | The client, locks, pub/sub, streams, batches, Cluster, and Sentinel |
| `Respire.Coordination` | Rate limiters, semaphores, read-write locks, latches, and Redlock |
| `Respire.DependencyInjection` | `AddRespire` and keyed clients |
| `Respire.HealthChecks` | ASP.NET Core health checks using existing client connections |
| `Respire.Caching`, `Respire.Caching.Hybrid` | `IDistributedCache` and `HybridCache` |
| `Respire.FusionCache` | FusionCache backplane on a shared Respire client |
| [`Respire.DataProtection`](docs/DATA_PROTECTION.md) | ASP.NET Core DataProtection keys in a Redis list |
| `Respire.OutputCaching` | [ASP.NET Core output caching](docs/OUTPUT_CACHING.md), including buffer APIs and tag eviction |
| `Respire.Json`, `.Search`, `.TimeSeries`, `.Probabilistic` | Typed module APIs such as `redis.Json` |
| `Respire.Testing`, `Respire.Testing.Containers` | An in-memory server, or Redis in a container |

See [all packages](https://thomhurst.github.io/Respire/docs/packages).

## Learn more

- [Documentation](https://thomhurst.github.io/Respire/)
- [Benchmarks](https://thomhurst.github.io/Respire/docs/benchmarks)
- [NativeAOT and custom serializers](https://thomhurst.github.io/Respire/docs/fundamentals/values-and-serialization#nativeaot-and-trimming)
- [StackExchange.Redis comparison and migration](https://thomhurst.github.io/Respire/docs/stackexchange-redis)

## License

[MIT](LICENSE)
