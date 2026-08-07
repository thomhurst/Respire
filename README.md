# Respire

A modern, high-performance Redis/RESP client for .NET — for Redis, Valkey, KeyDB, and anything
else that speaks RESP.

```csharp
await using var redis = await RespireClient.ConnectAsync("redis://localhost");

await redis.SetAsync("greeting", "hello", expiry: TimeSpan.FromMinutes(5));
string? greeting = await redis.GetStringAsync("greeting");

await redis.SetAsync("user:1", new User("Ada", 36));       // System.Text.Json under the hood
User? user = await redis.GetAsync<User>("user:1");
```

> **Status:** pre-release. The API is new and still allowed to change. TLS, cluster, and
> RESP3 client-side caching are on the [roadmap](docs/API_DESIGN.md#18-roadmap-designed-for-not-v1).

## Where Respire shines

**Blocking commands actually work.** `BLPOP`-style waits are forbidden or hazardous in
multiplexing clients — one blocking command would stall every pipelined command behind it.
Respire routes them to dedicated pooled connections automatically, so they're just an argument:

```csharp
// LPOP when waitFor is omitted; BLPOP on a dedicated connection when it's set.
string? job = await redis.Lists.LeftPopAsync("jobs", waitFor: TimeSpan.FromSeconds(30));
```

**Results are real .NET types.** Commands return `string?`, `long`, `bool`, `TimeSpan?`, `T?` —
missing key means `null`, and the nullability annotations tell you which calls can miss. There
is no protocol union struct to interrogate, and nothing to remember to dispose on the common
path. Zero-copy reads exist, but as an explicit opt-in lease so the obligation is visible:

```csharp
using RespireLease blob = await redis.Strings.GetLeaseAsync("blob:4mb");
Process(blob.Span);   // pooled memory, no copy, freed on dispose
```

**Discoverable by data type.** Commands are grouped into facets — `redis.Hashes`,
`redis.SortedSets`, `redis.Streams`, … — so IntelliSense shows you fifteen relevant methods
instead of four hundred. Every method's doc comment carries the Redis command name
(`/// Redis: HGET`), so searching by the name you know still finds it. The everyday string ops
also live directly on the client root.

**Modern C# is the interface.** Pub/sub is an `IAsyncEnumerable` — subscribe is a
`foreach`, unsubscribe is a `Dispose`, and no delegate bookkeeping exists. Stream consumer
groups read the same way. SCAN hides its cursor behind `IAsyncEnumerable<string>`. Expiries are
`TimeSpan`/`DateTimeOffset`, never `int seconds`.

```csharp
await using var sub = redis.Subscribe("orders");
await foreach (var msg in sub.WithCancellation(token))
{
    Console.WriteLine($"{msg.Channel}: {msg.Text}");
}
```

**Footguns are designed out.** Batch results are unreadable until the batch is sent — awaiting
early throws immediately instead of deadlocking. Timeout exceptions say what actually happened
(the wait was abandoned; the command may still run) and where to look next. Cancellation
cancels the *wait*, never a partially-written frame — the protocol stream can't desync.

```csharp
var batch = redis.CreateBatch();
var a = batch.GetStringAsync("a");
var n = batch.IncrementAsync("hits");
await batch.SendAsync();               // one flush for the whole batch
Console.WriteLine($"{a.Result}, {n.Result}");
```

**Transactions with typed results.** MULTI/EXEC blocks are written as one atomic append on a
single connection, so concurrent multiplexed traffic can't interleave into them. Each queued
command hands back a typed pending; `CommitAsync` tells you whether EXEC ran:

```csharp
var tx = redis.CreateTransaction();
var balance = tx.IncrementAsync("balance", -100);
tx.ListRightPushAsync("audit", "withdraw:100");
bool committed = await tx.CommitAsync();

// Optimistic concurrency: WATCH on a dedicated connection.
await using var watched = await redis.CreateTransactionAsync(["balance"]);
watched.IncrementAsync("balance", -100);
bool won = await watched.CommitAsync();   // false if "balance" changed underneath you
```

**An escape hatch, not a dead end.** Any command Respire hasn't wrapped is one call away —
including as an interpolated string, where each hole is exactly one argument (never
re-tokenized, so values with spaces are safe) and writes straight into the RESP frame:

```csharp
using var reply = await redis.ExecuteAsync($"SET {key} {payload} EX {60}");
using var enc = await redis.ExecuteAsync("OBJECT", "ENCODING", "user:1");
```

**Observability built in.** An `ActivitySource` and `Meter` (both named `"Respire"`) follow the
OpenTelemetry database conventions — spans per command, command counters, duration histograms.
Zero cost until a listener subscribes:

```csharp
tracing.AddSource("Respire");
metrics.AddMeter("Respire");
```

**Performance is the foundation, not a feature flag.** The wire layer was built
allocation-free first: commands from all callers coalesce into one buffer and go out in a
single syscall (auto-pipelining, no batching API required); replies parse out of pooled buffers
with exactly one copy off the socket; completion sources, buffers, and async state machines are
pooled. Multiple multiplexed connections spread load across cores. See
`benchmarks/` for the numbers against StackExchange.Redis.

**And the rest:**

- **Lua scripts** with automatic `EVALSHA` → `EVAL` fallback: `redis.Scripts.ExecuteAsync(script, keys, args)`.
- **Streams**, including consumer groups as an endless `await foreach` with per-entry `AckAsync()`.
- **Key-prefix views** for multi-tenancy: `var tenant = redis.WithKeyPrefix($"t:{id}:");` — same
  connections, every key prefixed, `ScanAsync` transparently scoped.
- **Sharded pub/sub** (Redis 7): `SubscribeSharded` / `PublishShardedAsync`.
- **Reconnects handled**: dead connections are replaced in the background; pub/sub reconnects
  *and resubscribes* by itself; `ConnectionStateChanged` tells you it happened.
- **Typed serialization** you control: `IRespireSerializer`, with a source-generator-friendly
  System.Text.Json default.
- **Testable**: everything is behind `IRespireClient` and per-facet interfaces; implementations
  are sealed.

## Getting started

```csharp
// URI (redis://user:password@host:port/db) or "host:port"
await using var redis = await RespireClient.ConnectAsync("redis://localhost");

// Full control
await using var redis2 = await RespireClient.ConnectAsync(new RespireOptions
{
    Endpoints = { new RespireEndpoint("cache.example.com", 6379) },
    Password = secret,
    ClientName = "checkout-api",
    CommandTimeout = TimeSpan.FromSeconds(2),
    Protocol = RespProtocol.Resp3,
});
```

### Dependency injection

```csharp
builder.Services.AddRespire(builder.Configuration.GetConnectionString("redis")!);

// Multiple clients, keyed:
builder.Services.AddRespire("sessions", "redis://sessions-host");
public sealed class CartService([FromKeyedServices("sessions")] IRespireClient redis);
```

Registration is lazy — nothing connects until the first command, so app startup never blocks
on Redis.

### IDistributedCache and HybridCache

`Respire.Extensions.Caching` provides a Redis-backed `IDistributedCache` (implementing
`IBufferDistributedCache`, so HybridCache reads and writes through pooled buffers), and
`Respire.Extensions.Caching.Hybrid` wires it up as HybridCache's distributed backend:

```csharp
// IDistributedCache only:
builder.Services.AddRespireDistributedCache("redis://localhost", instanceName: "myapp:");

// HybridCache (L1 in-memory + L2 Redis):
builder.Services.AddRespireHybridCache("redis://localhost", instanceName: "myapp:");

// Or reuse the AddRespire client instead of a connection string:
builder.Services.AddRespire(connectionString);
builder.Services.AddRespireHybridCache(configureCache: o => o.InstanceName = "myapp:");
```

Entries use the same hash layout (`absexp`/`sldexp`/`data`) as
Microsoft.Extensions.Caching.StackExchangeRedis, so the two implementations can read each
other's entries — swapping in Respire needs no cache flush. Unlike the Microsoft
implementation, a read of a sliding-expiration entry re-arms its TTL atomically in the same
round trip via a Lua script.

## Design

The full API design — principles, per-facet surface, conventions, rejected alternatives, and
roadmap (TLS, cluster/sentinel, RESP3-first internals, client-side caching, source-generated
module commands) — lives in [docs/API_DESIGN.md](docs/API_DESIGN.md).

The wire layer is documented in the code: one socket per connection with a coalescing
double-buffered write path, a single persistent flush loop, a FIFO in-flight ring pairing
pipelined commands with replies, and pooled everything. Sends are never cancelled (a partial
frame would desync the stream permanently); failures abort the connection, which is replaced in
the background.

## Building and testing

```bash
dotnet build Respire.sln
dotnet test tests/Respire.Tests               # wire-level tests, no Docker needed
dotnet test tests/Respire.IntegrationTests    # real Redis via Testcontainers (needs Docker)
```

## License

MIT
