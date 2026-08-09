# Respire

Respire is a fast, modern RESP client for .NET. It works with Redis, Valkey, KeyDB, and other
RESP-compatible servers while keeping the API familiar to C# developers.

[Read the documentation](https://thomhurst.github.io/Respire/)

```csharp
await using var redis = await RespireClient.ConnectAsync("redis://localhost");

await redis.SetAsync("greeting", "hello", expiry: TimeSpan.FromMinutes(5));
string? greeting = await redis.GetStringAsync("greeting");

await redis.SetAsync("user:1", new User("Ada", 36));
User? user = await redis.GetAsync<User>("user:1");
```

> **Status:** Respire is pre-release, so its API may still change. TLS, cluster support, and
> RESP3 client-side caching are on the [roadmap](docs/API_DESIGN.md#18-roadmap-designed-for-not-v1).

## Why Respire?

- **Natural .NET APIs.** Get `string?`, `long`, `bool`, `TimeSpan?`, or `T?` directly—no
  protocol wrapper to unpack. Nullability tells you when a result can be missing.
- **Fast by default.** Respire coalesces commands from concurrent callers into fewer socket
  writes, parses replies from pooled buffers, and spreads work across multiplexed connections.
  No batching switch is required.
- **Blocking commands that do not block everything else.** Commands such as `BLPOP` use a
  dedicated pooled connection, leaving normal traffic free to flow.
- **An API that is easy to explore.** Commands are grouped by data type (`redis.Hashes`,
  `redis.Streams`, `redis.SortedSets`, and more), while common string operations remain on the
  client itself.
- **Modern async patterns.** Pub/sub, stream consumer groups, and `SCAN` use
  `IAsyncEnumerable`. Expiries use `TimeSpan` and `DateTimeOffset`.
- **Safer failure modes.** Early batch awaits fail immediately instead of deadlocking.
  Cancellation abandons the wait without leaving a partial RESP frame on the connection.
- **Production-friendly.** Built-in reconnection, resubscribing pub/sub, OpenTelemetry,
  dependency injection, typed serialization, and testable interfaces.

## Everyday patterns

### Blocking list reads

Set `waitFor` and Respire automatically uses a dedicated connection:

```csharp
string? job = await redis.Lists.LeftPopAsync(
    "jobs",
    waitFor: TimeSpan.FromSeconds(30));
```

### Pub/sub

Subscriptions are async streams. Leaving the loop and disposing the subscription handles
cleanup—no delegate bookkeeping required.

```csharp
await using var subscription = redis.Subscribe("orders");

await foreach (var message in subscription.WithCancellation(token))
{
    Console.WriteLine($"{message.Channel}: {message.Text}");
}
```

### Batches and transactions

Batch commands share one flush. Transactions use one connection and return typed pending
results.

```csharp
var batch = redis.CreateBatch();
var name = batch.GetStringAsync("name");
var visits = batch.IncrementAsync("visits");
await batch.SendAsync();

Console.WriteLine($"{name.Result}: {visits.Result}");

var transaction = redis.CreateTransaction();
var balance = transaction.IncrementAsync("balance", -100);
transaction.ListRightPushAsync("audit", "withdraw:100");
bool committed = await transaction.CommitAsync();
```

Use `CreateTransactionAsync(["balance"])` for optimistic concurrency with `WATCH`. Read the
current value, queue the conditional update, then retry when `CommitAsync` returns `false`:

```csharp
bool applied;
do
{
    await using var watched = await redis.CreateTransactionAsync(["balance"]);
    long current = long.Parse((await redis.GetStringAsync("balance"))!);
    watched.SetAsync("balance", current - 100);
    applied = await watched.CommitAsync();
}
while (!applied);
```

### Zero-copy reads and custom commands

Normal reads favor convenient .NET values. For large payloads, opt into a disposable lease:

```csharp
using RespireLease blob = await redis.Strings.GetLeaseAsync("blob:4mb");
Process(blob.Span);
```

Commands without a typed wrapper remain one call away. Interpolated values are encoded as
single arguments, so spaces and other content are safe:

```csharp
using var reply = await redis.ExecuteAsync($"SET {key} {payload} EX {60}");
using var encoding = await redis.ExecuteAsync("OBJECT", "ENCODING", "user:1");
```

## App integration

### Dependency injection

```csharp
builder.Services.AddRespire(builder.Configuration.GetConnectionString("redis")!);

// Named clients are supported too.
builder.Services.AddRespire("sessions", "redis://sessions-host");
public sealed class CartService(
    [FromKeyedServices("sessions")] IRespireClient redis);
```

Registration is lazy, so Redis availability never blocks application startup.

### IDistributedCache and HybridCache

`Respire.Extensions.Caching` provides `IDistributedCache` and `IBufferDistributedCache`.
`Respire.Extensions.Caching.Hybrid` adds Respire as the L2 backend for `HybridCache`.

```csharp
builder.Services.AddRespireDistributedCache(
    "redis://localhost",
    instanceName: "myapp:");

// L1 memory + L2 Redis
builder.Services.AddRespireHybridCache(
    "redis://localhost",
    instanceName: "myapp:");
```

Cache entries use the same layout as `Microsoft.Extensions.Caching.StackExchangeRedis`, so you
can switch without flushing existing entries. Sliding-expiration reads also refresh their TTL
atomically in the same round trip.

> **Redis ACL note:** Cache users need `EVALSHA`, `EVAL`, `SET`, `UNLINK`, `HSET`, `HMGET`,
> `PTTL`, `PEXPIRE`, `PERSIST`, and `EXISTS`. Timeout- or cancellation-safe calls also require
> `CLIENT ID` and `CLIENT KILL`.

## More capabilities

- Lua scripts with automatic `EVALSHA` to `EVAL` fallback
- Streams and consumer groups with per-entry acknowledgement
- Key-prefixed client views for multi-tenant applications
- Sharded pub/sub for Redis 7
- Automatic reconnect and pub/sub resubscribe
- OpenTelemetry spans and metrics through `ActivitySource` and `Meter`, both named `Respire`
- Custom `IRespireSerializer` support with a System.Text.Json default
- `IRespireClient` and per-feature interfaces for straightforward testing

Redis telemetry follows OpenTelemetry database semantic conventions. `db.namespace` reports
the database index configured when the connection was established; raw `SELECT` commands are
not tracked. Query text is not collected because arbitrary Redis command values cannot be
reliably sanitized. Operation latency uses the stable `db.client.operation.duration` histogram
in seconds; pipelines and transactions are recorded as single operations.

See [API design](docs/API_DESIGN.md) for the full surface, design decisions, wire architecture,
and roadmap. Reproducible comparisons with StackExchange.Redis live in
[`benchmarks/`](benchmarks/).

## Documentation

Read the [Respire documentation](https://thomhurst.github.io/Respire/) or run it locally:

```bash
cd website
npm install
npm start
```

## Build and test

```bash
dotnet build Respire.sln
dotnet test tests/Respire.Tests
dotnet test tests/Respire.IntegrationTests # Requires Docker
```

## License

MIT
