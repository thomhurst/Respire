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

> **Status:** Respire is pre-release, so its API may still change. Automatic Sentinel failover and
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
- **Modern async patterns.** Pub/sub, stream consumer groups, and the `SCAN` family use
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
cleanup—no delegate bookkeeping required. `SubscribeAsync` returns once the server has
acknowledged the SUBSCRIBE, so the next publish is guaranteed to reach it.

```csharp
await using var subscription = await redis.SubscribeAsync("orders", token);

await foreach (var message in subscription.WithCancellation(token))
{
    Console.WriteLine($"{message.Channel}: {message.Text}");
}
```

Redis 7 sharded pub/sub uses `SSUBSCRIBE` and `SPUBLISH`. Run this as a separate consumer:

```csharp
await using var shard = await redis.SubscribeShardedAsync("orders:europe", token);
await using var shardMessages = shard.GetAsyncEnumerator(token);

await redis.PublishShardedAsync("orders:europe", "ready", token);

if (await shardMessages.MoveNextAsync())
{
    Console.WriteLine(shardMessages.Current.Text);
}
```

### Batches and transactions

Batch commands share one flush. Transactions use one connection and return typed pending
results. Both carry the same facets as the client — `batch.Lists.RightPush` mirrors
`redis.Lists.RightPushAsync` — but return a `RespirePending<T>` instead of awaiting.

```csharp
var batch = redis.CreateBatch();
var name = batch.GetString("name");
var visits = batch.Increment("visits");
var profile = batch.Hashes.GetAll("user:1");
RespireBatchResult batchResult = await batch.ExecuteAsync();
batchResult.ThrowIfAnyFailed();

Console.WriteLine($"{name.Result}: {visits.Result} ({profile.Result.Count} fields)");

await using var transaction = redis.CreateTransaction();
var balance = transaction.Increment("balance", -100);
transaction.Lists.RightPush("audit", "withdraw:100");
bool committed = await transaction.CommitAsync();
```

Always commit or dispose a transaction so its pooled buffer and any dedicated WATCH connection
are released. `await using` protects early-return and command-queuing failure paths; disposal is
a no-op after a successful commit.

Use `CreateTransactionAsync(["balance"])` for optimistic concurrency with `WATCH`. Read the
current value, queue the conditional update, then retry when `CommitAsync` returns `false`:

```csharp
bool applied;
do
{
    await using var watched = await redis.CreateTransactionAsync(["balance"]);
    long current = long.Parse((await redis.GetStringAsync("balance"))!);
    watched.Set("balance", current - 100);
    applied = await watched.CommitAsync();
}
while (!applied);
```

### Distributed locks

`AcquireAsync` generates the owner token and returns a non-null attempt. Check `Acquired`, then use
the `Lock` handle; disposing the attempt also releases an acquired lock.

```csharp
await using var attempt = await redis.Locks.AcquireAsync("locks:report", TimeSpan.FromSeconds(30));
if (!attempt.Acquired)
{
    return; // someone else holds it
}

var mutex = attempt.Lock;
await RunReportAsync();
```

A lock is a lease, not a mutex: it disappears on its own when its `Duration` elapses, even
mid-work. `RemainingEstimate` and `ExpiresAtEstimate` provide local best-effort deadlines. For
longer work, start a keep-alive and pass its cancellation token into the protected operation;
renewal failure cancels the token immediately:

```csharp
await using var keepAlive = await mutex.KeepAliveAsync(cancellationToken);
await RunReportAsync(keepAlive.CancellationToken);
if (keepAlive.OwnershipLost)
{
    // Do not publish protected output; another owner may be active.
}
```

You can instead call `mutex.ExtendAsync(...)` directly and stop protected writes when it returns
`false`. `ReleaseAsync` returns `LockReleaseOutcome`, distinguishing `Released`,
`AlreadyReleased`, and `NotOwned`. Every operation compares the token on the server, so an expired
handle never extends or deletes the next owner's lock.

When contention is exceptional, `AcquireOrThrowAsync` returns the handle directly and throws
`RespireLockNotAcquiredException` after the optional wait budget:

```csharp
await using var mutex = await redis.Locks.AcquireOrThrowAsync(
    "locks:report",
    TimeSpan.FromSeconds(30),
    wait: TimeSpan.FromSeconds(5)); // retries every 50 ms by default
```

`TryTakeAsync`, `ExtendAsync`, `ReleaseAsync`, and `GetOwnerTokenAsync` are the raw-token APIs for
callers that must share ownership between processes or outlive the acquiring process:

```csharp
var token = Guid.NewGuid().ToString("N");

if (await redis.Locks.TryTakeAsync("locks:report", token, TimeSpan.FromSeconds(30)))
{
    try
    {
        await RunReportAsync();
    }
    finally
    {
        await redis.Locks.ReleaseAsync("locks:report", token);
    }
}
```

### Redis Cluster

Enable cluster routing and provide one or more seed nodes. Respire loads `CLUSTER SLOTS`, follows
`MOVED`/`ASK` redirects, and caches learned routes. Batches may span nodes; transactions must keep
all keys in one slot, so use Redis hash tags for related keys. `WATCH` transactions are not
supported in cluster mode—use a same-slot Lua script instead. Sharded pub/sub is also unavailable
in cluster mode; `SSUBSCRIBE` subscriptions require a non-cluster client.

```csharp
await using var cluster = await RespireClient.ConnectAsync(new RespireOptions
{
    UseCluster = true,
    Endpoints =
    {
        new("redis-1", 6379),
        new("redis-2", 6379),
    },
});

await cluster.SetAsync("{account:42}:name", "Ada");
await cluster.SetAsync("{account:42}:balance", 100);
```

A single seed can also be enabled with `redis://redis-1?cluster=true`.

### Zero-copy reads and custom commands

Normal reads favor convenient .NET values. For large payloads, opt into a disposable lease:

```csharp
using RespireLease blob = await redis.Strings.GetLeaseAsync("blob:4mb");
Process(blob.Span);
```

Every command in the Redis 8.10 and Valkey 9.1 references is available through the generated,
discoverable `RespireCommands` catalog. It also includes Redis's integrated JSON, Search,
probabilistic, time-series, and vector commands, Valkey modules, and documented KeyDB and
Dragonfly extensions. Command words are pre-encoded once; only arguments are written per call:

```csharp
using var document = await redis.ExecuteAsync(
    RespireCommands.Json.JSON_SET, "user:1", "$", payload);
using var encoding = await redis.ExecuteAsync(
    RespireCommands.Key.OBJECT_ENCODING, "user:1");
```

Catalog descriptors do not encode key positions, so catalog execution is rejected on
`WithKeyPrefix` views; use the typed facets there to preserve key isolation.

Strings convert implicitly to `RespireCommand` for experimental or server-specific commands.
Interpolated values are encoded as single arguments, so spaces stay safe. Format strings and
alignment are honored with invariant culture; holes use `IFormattable` or `ToString()` and do not
pass through a Respire serializer.

## App integration

### Dependency injection

```csharp
builder.Services.AddRespire(builder.Configuration.GetConnectionString("redis")!);

// Named clients are supported too.
builder.Services.AddKeyedRespire("sessions", "redis://sessions-host");
public sealed class CartService(
    [FromKeyedServices("sessions")] IRespireClient redis);
```

Registration is lazy, so Redis availability never blocks application startup. `ConnectTimeout`
bounds socket and TLS setup; the Redis handshake and command use `CommandTimeout` and caller
cancellation. Standalone clients surface setup exceptions directly, while cluster clients wrap
seed failures in `RespireConnectionException`. The next command starts a new connection attempt.

### NativeAOT and trimming

Typed values use reflection-based System.Text.Json metadata by default. For a trimmed or NativeAOT
application, generate metadata for every stored type and pass that context to Respire:

```csharp
using System.Text.Json.Serialization;
using Respire;
using Respire.Serialization;

var options = new RespireOptions
{
    Endpoints = { new RespireEndpoint("localhost") },
    Serializer = SystemTextJsonSerializer.FromContext(AppJsonContext.Default),
};

await using var redis = await RespireClient.ConnectAsync(options);

// The generic APIs are conservatively annotated because IRespireSerializer can be
// reflection-based. This configured context makes these two calls AOT-safe.
#pragma warning disable IL2026, IL3050
await redis.SetAsync("user:1", new User("Ada", 36));
User? user = await redis.GetAsync<User>("user:1");
#pragma warning restore IL2026, IL3050

[JsonSerializable(typeof(User))]
internal partial class AppJsonContext : JsonSerializerContext
{
}
```

Add a `[JsonSerializable]` entry for each non-primitive type. Strings, byte arrays, Boolean values,
and numeric values use Respire's built-in codecs and do not need generated JSON metadata. Custom
serializers can also override the `Type`-based `IRespireSerializer` members for polymorphic adapters.

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
- Redis Sentinel primary discovery when connecting
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

See [command coverage](docs/COMMAND_COVERAGE.md) for audited sources and regeneration details,
and [API design](docs/API_DESIGN.md) for design decisions, wire architecture, and roadmap.
Reproducible comparisons with StackExchange.Redis live in
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
dotnet build Respire.slnx
dotnet test tests/Respire.Tests
dotnet test tests/Respire.IntegrationTests # Requires Docker
```

## License

[MIT](LICENSE)
