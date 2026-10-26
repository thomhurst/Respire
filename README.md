# Respire

**A fast, modern Redis client for .NET. Plain C# types in, plain C# types out, and the hard parts
of Redis are one call each**

Works with Redis, Valkey, KeyDB, and other RESP-compatible servers.

[Documentation](https://respire.tomlonghurst.dev/) ·
[Getting started](https://respire.tomlonghurst.dev/docs/getting-started) ·
[Benchmarks](https://respire.tomlonghurst.dev/docs/benchmarks) ·
[Coming from StackExchange.Redis?](https://respire.tomlonghurst.dev/docs/stackexchange-redis)

```bash
dotnet add package Respire
```

```csharp
await using var redis = await RespireClient.ConnectAsync("redis://localhost");

await redis.SetAsync("user:1", new User("Ada", 36), expiry: TimeSpan.FromMinutes(5));
User? user = await redis.GetAsync<User>("user:1");
```

That is the whole setup. No multiplexer to configure, no `RedisValue` to unwrap.

> The public API may change before the first supported major release, **2.0.0**. See
> [release candidates](https://respire.tomlonghurst.dev/docs/getting-started#release-candidates),
> the [roadmap](https://respire.tomlonghurst.dev/docs/roadmap), and the
> [experimental API policy](https://respire.tomlonghurst.dev/docs/experimental-apis).

## Why Respire?

- **Real return types.** `string?`, `long`, `bool`, `TimeSpan`, or your own `T?`.
- **Reads from memory.** Server-assisted client-side caching: a cached string `GET` takes
  about 150 ns and allocates nothing. Redis tells Respire when to evict.
- **Pipelined by default.** Concurrent calls share socket writes. No batching switch.
- **Blocking commands that work.** `BLPOP` and blocking stream reads get their own connections,
  so they never stall other calls.
- **Coordination built in.** Locks, rate limiters, semaphores, leader election, queues, and
  stream workers. No extra library to glue on.
- **Tests without Docker.** An in-memory server with a fake clock runs your real client code.
- **Every command.** Typed facets per data type, plus a generated catalog of every Redis 8.10 and
  Valkey 9.1 command.

## The hard parts, one call each

**Cache-aside with stampede protection.** Memory hit, else Redis, else your factory. Concurrent
misses share one factory call.

```csharp
await using var redis = await RespireClient.ConnectAsync(new RespireOptions
{
    Endpoints = { new RespireEndpoint("localhost") },
    ClientSideCache = new() { CoalesceConcurrentMisses = true },
});

User? user = await redis.GetOrSetAsync<User>("user:42", LoadUserAsync, TimeSpan.FromMinutes(5));

static ValueTask<User?> LoadUserAsync(CancellationToken cancellationToken) => new(new User("Ada", 36));
```

**A distributed lock that renews itself.** Disposing the attempt releases the lock.

```csharp
await using var attempt = await redis.Locks.AcquireAsync(
    "locks:nightly-report", TimeSpan.FromSeconds(30), keepAlive: true);

if (attempt.Acquired)
{
    await RunReportAsync(attempt.Lock.KeepAliveCancellationToken);
}
```

**A rate limit shared by every server.** A standard .NET `RateLimiter`, so the ASP.NET Core
middleware accepts it.

```csharp
using Respire.Coordination;

await using var limiter = redis.Coordination.RateLimiters.SlidingWindow(
    $"limits:api:{userId}", permitLimit: 100, window: TimeSpan.FromMinutes(1), segments: 6);

using var lease = await limiter.AcquireAsync(1, cancellationToken);
if (!lease.IsAcquired)
{
    return; // Over the limit: reject the request, for example with HTTP 429.
}
```

**Optimistic transactions with retries.** Read, decide, write. If another client changes the key
first, Respire runs the callback again.

```csharp
await redis.RunTransactionWithReadsAsync(["stock:42"], async (reads, transaction, token) =>
{
    long stock = long.Parse(await reads.GetStringAsync("stock:42", token) ?? "0");
    if (stock > 0)
        transaction.Set("stock:42", stock - 1);
});
```

**A reliable queue.** Deduplicated sends, visibility timeouts, and redelivery when a worker dies.

```csharp
using System.Text;
using Respire.Streaming;

var queue = new RespireReliableQueue<string>(redis, "emails", Encoding.UTF8.GetBytes, Encoding.UTF8.GetString);
await queue.SendAsync("welcome:ada@example.com", deduplicationId: "welcome:42");

foreach (var message in await queue.ReceiveAsync(visibilityTimeout: TimeSpan.FromSeconds(30)))
{
    await HandleAsync(message.Payload);
    await queue.AckAsync(message);
}
```

**Pub/sub as `await foreach`.**

```csharp
await using var orders = await redis.SubscribeAsync("orders");

await foreach (var message in orders)
{
    Console.WriteLine(message.Text);
}
```

**Many commands, one round trip.** Results are typed.

```csharp
await using var batch = redis.CreateBatch();
var name = batch.GetString("name");
var visits = batch.Increment("visits");
await batch.ExecuteAsync();

Console.WriteLine($"{name.Result}: {visits.Result}");
```

**Tests without Docker.** Expire a key by moving the clock, not by waiting.

```csharp
using Respire.Testing;

var clock = new RespireFakeClock();
await using var server = new RespireFakeServer(clock);
await using var client = await RespireClient.ConnectAsync(server.CreateOptions());

await client.SetAsync("session", "abc", expiry: TimeSpan.FromMinutes(20));
clock.Advance(TimeSpan.FromMinutes(21));
string? expired = await client.GetStringAsync("session"); // null
```

## More in the box

| You need | With Respire |
| --- | --- |
| [ASP.NET Core registration](https://respire.tomlonghurst.dev/docs/integrations/dependency-injection) | `services.AddRespire(connectionString)` |
| [`IDistributedCache` and `HybridCache`](https://respire.tomlonghurst.dev/docs/integrations/caching) | `services.AddRespireHybridCache(...)` |
| [.NET Aspire](https://respire.tomlonghurst.dev/docs/integrations/aspire) (not yet published on NuGet) | `builder.AddRespireClient("cache")` |
| [Stream consumer workers](https://respire.tomlonghurst.dev/docs/guides/stream-workers) | `services.AddRespireStreamWorker<THandler>(stream, group)` |
| [Job queues with retries and scheduling](https://respire.tomlonghurst.dev/docs/guides/job-queues) | `services.AddRespireJobQueue<THandler, TJob>(...)` |
| [Leader election with fencing tokens](https://respire.tomlonghurst.dev/docs/guides/coordination) | `new RespireLeaderElection(...)` |
| [Semaphores, read-write locks, latches, Redlock](https://respire.tomlonghurst.dev/docs/guides/coordination) | `redis.Coordination.CreateSemaphore(key, capacity)` |
| [A blocking work queue](https://respire.tomlonghurst.dev/docs/guides/blocking-queues) | `redis.Lists.LeftPopAsync(key, waitFor: timeout)` |
| [Cluster and Sentinel](https://respire.tomlonghurst.dev/docs/fundamentals/connections) | `?cluster=true` for Cluster, `?serviceName=mymaster` for Sentinel |
| [OpenTelemetry](https://respire.tomlonghurst.dev/docs/integrations/observability) | Traces and metrics built in |
| [NativeAOT](https://respire.tomlonghurst.dev/docs/fundamentals/values-and-serialization#nativeaot-and-trimming) | Pass a source-generated `JsonSerializerContext` |

## Packages

| Package | Adds |
| --- | --- |
| `Respire` | The client, locks, pub/sub, streams, batches, Cluster, and Sentinel |
| `Respire.Coordination` | Rate limiters, semaphores, read-write locks, latches, leader election, and Redlock |
| `Respire.Streaming` | Reliable queues, job queues, and stream workers |
| `Respire.DependencyInjection` | `AddRespire` and keyed clients |
| `Respire.Caching`, `Respire.Caching.Hybrid` | `IDistributedCache` and `HybridCache` |
| `Respire.Json`, `.Search`, `.TimeSeries`, `.Probabilistic` | Typed module APIs such as `redis.Json` |
| `Respire.Testing`, `Respire.Testing.Containers` | An in-memory server, or Redis in a container |

Plus SignalR, DataProtection, output caching, health checks, FusionCache, and more:
see [all packages](https://respire.tomlonghurst.dev/docs/packages).

## License

[MIT](https://github.com/thomhurst/Respire/blob/main/LICENSE)
