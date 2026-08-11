# Respire API Design Spec

Greenfield API design for a modern .NET Redis/RESP client. Pre-release — nothing here is
constrained by the current public surface. The wire layer (multiplexed connections, FIFO
inflight ring, auto-pipelining, persistent flush task) stays as-is; this spec is about what
users touch.

> **Status:** implemented, with these deltas from the original draft:
> - Connection sizing is one `Connections` knob (minimum 1), not `Min`/`MaxConnections` — the
>   multiplexer pool is fixed-size, and the option should say what the code does.
> - Lazy connect is `RespireClient.Create(...)` (used by the DI package) rather than a
>   `lazy:` flag on `ConnectAsync`.
> - TLS is supported through `rediss://` and `RespireOptions.UseTls`; custom certificate and
>   authentication settings use `RespireOptions.TlsOptions`.
> - Raw `ExecuteAsync` throws on *top-level* server errors like the friendly layer (one error
>   model everywhere); `RespireResult.IsError` still exposes nested error elements.
> - Watched transactions shipped in v1 as `CreateTransactionAsync(watchKeys)` on a dedicated
>   connection (§6 marked it v2).
> - The timeout exception's queue-depth diagnostic snapshot (§13) is still roadmap; the message
>   covers cause and next steps.
> - Open questions resolved: plural facet names; root shortcuts as in §2; `GetStringAsync` +
>   `GetAsync<T>` (no non-generic string-returning `GetAsync`).

## Design principles

1. **The 90% path is one line.** Connect with a URI, `GetAsync`/`SetAsync` on the client root.
   No multiplexer/database/server split to learn before the first command works.
2. **Return real .NET types.** `string?`, `long`, `bool`, `TimeSpan?`, `T?` — never a
   protocol union struct the user must interrogate and dispose. Missing key = `null`.
   Pooled/zero-copy access exists, but as an explicit opt-in lease API.
3. **Async-only, cancellation-honest.** Every command takes a `CancellationToken`.
   Cancellation abandons the *wait*, never the *send* — a cancelled command may still
   execute server-side, and the docs say so. No sync command API.
4. **Discoverable by data type.** Commands grouped into facets (`redis.Hashes`,
   `redis.SortedSets`, …) so IntelliSense shows ~15 relevant methods, not 400. Human names
   (`Hashes.GetAsync`), with the Redis command name in every XML doc (`/// Redis: HGET`) so
   searching "HGET" still finds it.
5. **Modern C# as the feature.** `IAsyncEnumerable` pub/sub and streams, `TimeSpan`
   everywhere, init-only options records, nullable annotations as the null-key contract,
   spans/leases for zero-copy, interpolated-string raw commands.
6. **Observability built in.** `ActivitySource` + `Meter` following OTel semantic
   conventions, not an afterthought package.
7. **Escape hatches, not dead ends.** Raw command execution, byte-level args, and lease
   reads are first-class so no one has to fork the client for a missing command.

---

## 1. Connecting

```csharp
// The hero path — URI, redis:// or rediss:// (TLS)
await using var redis = await RespireClient.ConnectAsync("redis://localhost");

// Full control — options record, init-only
await using var redis = await RespireClient.ConnectAsync(new RespireOptions
{
    Endpoints = { new("cache.example.com", 6379) },
    Password = builder.Configuration["Redis:Password"],
    ClientName = "checkout-api",
    Database = 0,
    ConnectTimeout = TimeSpan.FromSeconds(5),
    CommandTimeout = TimeSpan.FromSeconds(2),
    Connections = 4,
    Serializer = SystemTextJsonSerializer.FromContext(AppJsonContext.Default),
    LoggerFactory = loggerFactory,
});
```

- `ConnectAsync` connects eagerly. `Create(...)` defers connection to the first command for
  hosts that start before Redis. `ConnectTimeout` bounds socket and TLS setup; the Redis
  handshake and non-blocking commands use `CommandTimeout`. Blocking commands use their
  explicit wait timeout, and caller cancellation applies throughout. Standalone clients surface
  setup exceptions directly, while cluster clients wrap seed failures in
  `RespireConnectionException`. A later command starts a new connection attempt.
- URI carries the common knobs:
  `redis://user:pass@host:6379/2?clientName=api&commandTimeoutMs=2000`.
- Connection count, logger, TLS all live in `RespireOptions` — no five-parameter factory.
- `redis.IsConnected`, `redis.ConnectionStateChanged` event (`Connected`, `Reconnecting`,
  `Disconnected`) with endpoint and error context for health surfacing.

## 2. Core surface: root shortcuts + facets

The client root carries the string/key ops that dominate real usage. Everything else lives
on a facet property per Redis data type. Facets are singleton classes created once per
client (interface-friendly, no per-call allocation).

```csharp
public sealed class RespireClient : IRespireClient, IAsyncDisposable
{
    // Root shortcuts (delegate to Strings/Keys facets)
    ValueTask<string?> GetStringAsync(RespireKey key, CancellationToken ct = default);
    ValueTask<T?>      GetAsync<T>(RespireKey key, CancellationToken ct = default);
    ValueTask<bool>    SetAsync(RespireKey key, RespireValue value,
                                RespireExpiry expiry = default,   // none | In | At | Keep
                                SetWhen when = SetWhen.Always,
                                CancellationToken ct = default);
    ValueTask<bool>    SetAsync<T>(RespireKey key, T value, /* same options */);
    ValueTask<long>    DeleteAsync(params ReadOnlySpan<RespireKey> keys);
    ValueTask<long>    DeleteAsync(ReadOnlySpan<RespireKey> keys, CancellationToken ct);
    ValueTask<bool>    ExistsAsync(RespireKey key, CancellationToken ct = default);
    ValueTask<long>    IncrementAsync(RespireKey key, long by = 1, CancellationToken ct = default);
    ValueTask<bool>    ExpireAsync(RespireKey key, RespireExpiry expiry,
                                   ExpireWhen when = ExpireWhen.Always,
                                   CancellationToken ct = default);
    ValueTask<TimeSpan> PingAsync(CancellationToken ct = default);   // returns measured RTT

    // Facets
    IStringCommands    Strings    { get; }
    IKeyCommands       Keys       { get; }   // EXPIRE, TTL, TYPE, SCAN, RENAME, PERSIST…
    IHashCommands      Hashes     { get; }
    IListCommands      Lists      { get; }
    ISetCommands       Sets       { get; }
    ISortedSetCommands SortedSets { get; }
    IStreamCommands    Streams    { get; }
    IScriptCommands    Scripts    { get; }
    IServerCommands    Server     { get; }   // INFO, DBSIZE, FLUSHDB, CONFIG…
}
```

Naming inside facets drops the Redis prefix — the facet *is* the prefix:

```csharp
await redis.Hashes.SetAsync("user:1", "name", "Tom");          // HSET
string? name = await redis.Hashes.GetStringAsync("user:1", "name"); // HGET
long count = await redis.Lists.CountAsync("queue");            // LLEN
bool added = await redis.SortedSets.AddAsync("board", "tom", 42.0); // ZADD
```

Multi-key operations fit naturally on facets (they never fit key-scoped handle designs):

```csharp
long n = await redis.Sets.IntersectStoreAsync(destination: "both", "set:a", "set:b");
```

**Rejected alternatives**, recorded so we don't relitigate:

- *Flat prefixed methods* (`HashGetAsync`, SE.Redis style): 400-method IntelliSense wall,
  and the prefix is just a worse namespace.
- *Key-scoped handles as the primary API* (`redis.Hash("user:1").GetAsync("name")`):
  reads nicely but multi-key commands, batching, and cluster routing all fight it.
  May return later as an optional sugar layer on top of facets.

## 3. Value model

### Inputs: `RespireKey` and `RespireValue`

Two small readonly structs with implicit conversions kill the overload explosion:

```csharp
public readonly struct RespireKey    // from: string, byte[], ReadOnlyMemory<byte>
public readonly struct RespireValue  // from: key/text/binary, numeric primitives, bool,
                                     //       Guid, DateTimeOffset, TimeSpan, char
```

`RespireValue` is *input-only*. (The current parse-side `RespireValue` union becomes an
internal type; results surface as plain .NET types.) Equality compares the exact bulk-string
payload written to Redis, so equivalent text, bytes, and scalar values compare equal.

### Outputs: real types, serializer for objects

| Redis reply | .NET type | Missing key |
|---|---|---|
| bulk string | `string?` / `byte[]?` / `T?` | `null` |
| integer | `long` | n/a |
| ok/condition | `bool` | `false` |
| double (RESP3) | `double` | n/a |
| TTL | `RespireTtl` (readonly struct: `Exists`, `HasExpiry`, `TimeToLive`) | `Exists == false` |

`GetAsync<T>` / `SetAsync<T>` run through `RespireOptions.Serializer`
(`IRespireSerializer`: `Serialize<T>(T, IBufferWriter<byte>)` /
`Deserialize<T>(ReadOnlySequence<byte>)`). Default: `System.Text.Json` with source-gen
context support. `string`, `byte[]`, and primitives bypass the serializer.

### Zero-copy: the lease API

The friendly layer allocates (`string?`, `byte[]?`). Hot paths opt into pooled buffers
explicitly, so the disposal obligation is visible at the call site:

```csharp
using RespireLease lease = await redis.Strings.GetLeaseAsync("blob:4mb");
if (!lease.IsNull)
    Process(lease.Span);   // pooled memory, valid until Dispose
```

No API returns pooled memory without `Lease` in its name.

## 4. Command conventions

- **Time is `TimeSpan`/`DateTimeOffset`.** Expiry inputs use `RespireExpiry.In(TimeSpan)` or
  `.At(DateTimeOffset)` (both also convert implicitly), plus `.Keep`/`.Persist`. Never `int seconds`.
- **Options with more than ~3 knobs become an options struct** (e.g. `SetWhen.Always /
  NotExists / Exists`, `GetExAsync` variants), but common cases stay optional parameters.
- **Variadic where Redis is variadic**: `DeleteAsync(params ReadOnlySpan<RespireKey> keys)`
  (C# 13 params-span, zero alloc), `Hashes.SetAsync(key, [("name","Tom"), ("age","34")])`.
  A `params` parameter must come last, so each variadic command also has a sibling
  `DeleteAsync(ReadOnlySpan<RespireKey> keys, CancellationToken ct)` — non-params items and a
  required token. The token is required, not optional, so the two forms never overlap.
- **`Async` suffix stays.** Analyzer ecosystem and reader expectation beat the saved
  keystrokes.
- **SCAN-family returns `IAsyncEnumerable`**, cursor handled internally:

```csharp
await foreach (var key in redis.Keys.ScanAsync(
    match: "user:*", countHint: 250, type: "hash", cancellationToken: ct))
{
    Console.WriteLine(key);
}

await foreach (var field in redis.Hashes.ScanAsync("user:1", match: "profile:*", cancellationToken: ct))
{
    Console.WriteLine($"{field.Key} = {field.Value}");
}
```

Hashes yield field/value pairs, sets yield members, and sorted sets yield members with scores.

## 5. Batching (explicit pipeline)

Auto-pipelining already happens under concurrency; `CreateBatch` exists for the
sequential-code case where you want N commands in one flush:

```csharp
var batch = redis.CreateBatch();
RespirePending<string?> a = batch.GetString("a");
RespirePending<long>    n = batch.Increment("hits");
RespirePending<long>    q = batch.Lists.RightPush("queue", "job-1");
RespireBatchResult result = await batch.ExecuteAsync(ct);
result.ThrowIfAnyFailed();

string? av = a.Result;   // valid only after ExecuteAsync
```

A batch carries the same facets as the client (`Strings`, `Keys`, `Hashes`, `Lists`, `Sets`,
`SortedSets`, `Bitmaps`, `HyperLogLog`, `Geo`) with matching command names minus the `Async`
suffix and the same parameter shapes. The return type is deferred, and cancellation belongs to
`ExecuteAsync`. Blocking
(`waitFor`) and streaming (`ScanAsync`, `GetLeaseAsync`) members have no deferred form.

`RespirePending<T>` is awaitable *and* has `.Result`, but both throw
`RespirePendingNotReadyException` if touched before `ExecuteAsync`. The synchronous queueing names
make accidental early awaits conspicuous, while the exception prevents a deadlock. `Status`, `HasResult`, `Error`,
and `TryGetResult` expose pending, successful, faulted, and aborted outcomes without try/catch.
`ExecuteAsync` returns the batch-wide `Count`, `FailureCount`, and `FirstError`; command and
connection-acquisition failures fault their pendings and do not throw unless the caller invokes
`ThrowIfAnyFailed`.

## 6. Transactions

Same pending-value shape as batch, plus watch keys. `CommitAsync` returns whether EXEC
won:

```csharp
await using var tx = await redis.CreateTransactionAsync(["balance"], ct);
var newBal = tx.Increment("balance", -100);
var log    = tx.Lists.RightPush("audit", "withdraw:100");
bool committed = await tx.CommitAsync(ct);
```

When WATCH aborts EXEC, `committed` is false, each pending reports
`RespirePendingStatus.Aborted`, and reading one throws `RespireTransactionAbortedException`.

Interactive WATCH → read → decide → MULTI (true CAS) uses
`CreateTransactionAsync(watchKeys)`. Create the watched transaction, read current values through
the client (transaction reads are deferred and unavailable for decisions), queue writes, then
commit. If `CommitAsync` returns false, dispose that attempt and recreate the watched transaction,
including its reads and writes. Keep retries bounded. Scripts/functions remain preferable when
the operation can be expressed server-side because they avoid round trips and retries.

## 7. Pub/Sub: `IAsyncEnumerable`

Subscriptions are async streams. Unsubscribe = dispose/cancel. No delegate soup, no
handler-ordering questions:

```csharp
await using var sub = await redis.SubscribeAsync("orders");  // also: patterns, sharded
await foreach (RespireMessage msg in sub.WithCancellation(ct))
{
    Console.WriteLine($"{msg.Channel}: {msg.Text}");
    var order = msg.As<Order>();                            // serializer-backed
}
```

- `SubscribeAsync(channel | channels)`, `SubscribePatternAsync(pattern)`,
  `SubscribeShardedAsync(channel)` (RESP3 SSUBSCRIBE). Subscribing is always awaited: the
  task completes once the server has acknowledged, so the next publish reaches the stream.
- Backed by a bounded `Channel<T>`; overflow policy is `DropOldest` (default) or
  `DropNewest`. Blocking and throwing policies are intentionally omitted because either would
  stop the shared pub/sub reader and affect unrelated subscriptions.
- `RespireMessage` exposes `Channel`, `Pattern`, `Text`, `Memory`, `As<T>()`.
- Publish is just `redis.PublishAsync(channel, value)` on the root.

## 8. Streams

Same async-stream philosophy for consumer groups; XREADGROUP blocking loop, ack on the
entry:

```csharp
await redis.Streams.CreateGroupAsync("events", "processors", createStream: true);

await foreach (var entry in redis.Streams.ReadGroupAsync(
    "events", group: "processors", consumer: Environment.MachineName, ct))
{
    Handle(entry["type"]);          // field access on the entry
    await entry.AckAsync();
}
```

`AddAsync(key, [("type", "click"), ...])` returns the generated `RespireStreamId`
(comparable struct, not string).

## 9. Blocking commands are supported, transparently

BLPOP/BRPOP/BLMOVE/XREAD-block are *forbidden* in SE.Redis because of multiplexing. We
have a connection pool — blocking commands automatically route to a dedicated pooled
connection:

```csharp
string? job = await redis.Lists.LeftPopAsync("jobs", waitFor: TimeSpan.FromSeconds(30), ct);
```

`waitFor: null` (default) = non-blocking LPOP; a value = BLPOP on a dedicated connection.
One method, one mental model. This is a headline capability — spec it early, market it.

## 10. Raw commands and the interpolated escape hatch

```csharp
// Complete generated catalog — discoverable and pre-encoded
using RespireResult catalogResult = await redis.ExecuteAsync(
    RespireCommands.Key.OBJECT_ENCODING, "user:1");

// Explicit args — RespireValue params, no string-splitting surprises
using RespireResult rawResult = await redis.ExecuteAsync("OBJECT", "ENCODING", "user:1");

// Interpolated-string handler: literal text splits on whitespace into args;
// each hole is exactly one argument and supports format/alignment syntax.
using RespireResult interpolatedResult = await redis.ExecuteAsync($"SET {key} {payload} EX {60}");
```

Strings convert implicitly to `RespireCommand`, so raw and catalog calls share the same two result
method shapes and two fire-and-forget shapes. Interpolation holes use invariant `IFormattable`
formatting or `ToString()`; they are not routed through a Respire serializer.

`RespireResult` is the one public protocol-shaped type: `Kind`, `AsString()`,
`AsInteger()`, serializer-backed `As<T>()`, `AsSpan()`, and allocation-free array enumeration.
It owns pooled memory and must be disposed (`using`); `IsDisposed` exposes its lifetime state and
access after disposal throws `ObjectDisposedException`. It exists only on the raw layer. The
generated catalog covers every audited Redis and Valkey command plus
documented module, KeyDB, and Dragonfly commands; string execution remains available for
experimental server extensions.

## 11. Scripts and functions

```csharp
static readonly RespireScript RateLimit = RespireScript.Create("""
    local n = redis.call('INCR', KEYS[1])
    if n == 1 then redis.call('PEXPIRE', KEYS[1], ARGV[1]) end
    return n
    """);

long count = await redis.Scripts.ExecuteIntegerAsync(RateLimit,
    keys: [$"rl:{userId}"], args: [60_000]);
```

Scripts also expose `ExecuteAsync<T>()` and `ExecuteStringAsync()` conveniences that dispose the
pooled raw result after conversion. `ExecuteSpanAsync()` accepts `ReadOnlySpan<T>` inputs; both raw
entry points return a `RespireResult` that the caller must dispose.

- SHA1 computed once at `Create`; `ExecuteAsync` tries EVALSHA, falls back to EVAL on
  NOSCRIPT, transparently.
- FUNCTION and FCALL commands are available through `RespireCommands.Scripting`.

## 12. Key-prefixed views

```csharp
IRespireClient tenant = redis.WithKeyPrefix($"t:{tenantId}:");
await tenant.SetAsync("cart", cart);     // key = "t:42:cart"
```

Cheap decorator over the same connections; composes (`WithKeyPrefix` on a prefixed view
concatenates). Also the natural seam for a future `WithLocalCache(...)` view.

## 13. Resilience

- **Reconnect**: automatic, policy from options; commands issued while reconnecting wait
  (bounded by `CommandTimeout`) rather than failing instantly; `ConnectionStateChanged`
  fires transitions.
- **Timeouts**: `CommandTimeout` default per client; per-call `CancellationToken` for
  tighter control. Timeout throws `RespireTimeoutException` whose message includes the
  diagnostic snapshot (queue depth, inflight count, last successful flush age) — the
  anti-SE.Redis-cryptic-timeout feature.
- **No automatic command retry in v1.** Retrying non-idempotent commands is a data bug;
  document the pattern (Polly around idempotent calls) instead of shipping a footgun.
  `ReconnectPolicy` ≠ retry policy.
- Cancellation cancels the wait, never an in-flight send (wire invariant).

## 14. Observability

- `ActivitySource("Respire")` — span per logical operation using OTel Redis semantic
  conventions (`db.system.name = redis`, `db.namespace`, `db.operation.name`, endpoint and
  error attrs). Query text stays excluded because arbitrary Redis command values cannot be
  reliably sanitized. Pipelines and transactions emit one span with `db.operation.batch.size`.
- `Meter("Respire")` — stable `db.client.operation.duration` histogram in seconds.
- `Respire.Extensions.OpenTelemetry`: `AddRespireInstrumentation()` one-liners.

## 15. Errors

```
RespireException
├── RespireConnectionException     // can't connect / connection lost mid-command
├── RespireConfigurationException  // valid input cannot configure the requested API
├── RespireProtocolException       // malformed or invalid RESP data
├── RespireTimeoutException        // command name and elapsed timeout
└── RespireServerException         // .Code, .CommandName, and .IsTransient
```

Server errors always throw at the friendly layer — no error-as-value inspection. Only
`RespireResult` (raw layer) exposes `Kind == Error` for people who asked for the wire.

## 16. Dependency injection (`Respire.Extensions.DependencyInjection`)

```csharp
builder.Services.AddRespire(builder.Configuration.GetConnectionString("redis")!);

// Multiple clients via keyed services
builder.Services.AddKeyedRespire("cache",    o => o.Endpoints.Add(new("cache-host")));
builder.Services.AddKeyedRespire("sessions", o => o.Endpoints.Add(new("sess-host")));

public sealed class CartService([FromKeyedServices("cache")] IRespireClient redis) { }
```

- Registers `IRespireClient` + `RespireClient` singleton; connection happens on first use.
  `ConnectTimeout` bounds socket and TLS setup; the Redis handshake uses `CommandTimeout`, as do
  non-blocking commands. Blocking commands use their explicit wait timeout, and caller
  cancellation applies throughout. Cluster seed failures are wrapped in
  `RespireConnectionException`, and the next command retries connection.
- Configuration accepts a connection string, an `Action<RespireOptionsBuilder>`, or a
  service-provider factory returning `RespireOptions`; the package does not bind `IOptions`.
- Health integrations can inspect `IsConnected` and subscribe to `ConnectionStateChanged`;
  the package does not register a health check.

## 17. Testing story

- All facets and the client are interfaces (`IRespireClient`, `IHashCommands`, …);
  implementations sealed. Mocking works with any framework.
- Roadmap: `Respire.Testing` — in-memory `IRespireClient` fake for unit tests without a
  container; integration tests keep using real Redis via Testcontainers.

## 18. Roadmap (designed-for, not v1)

1. **RESP3-first internals**: HELLO 3, maps/doubles/booleans natively, push messages —
   prerequisite for the next two.
2. **Client-side caching**: `redis.WithLocalCache(options)` view using CLIENT TRACKING
   invalidation pushes. Killer feature; the view seam (§12) already accommodates it.
3. **Sentinel**: automatic primary discovery and failover. Redis Cluster already uses
   `Endpoints` as seeds and handles `CLUSTER SLOTS`, MOVED, ASK, and hash-slot validation.
4. **Source-generated custom commands** (Refit-style) for modules (RedisJSON, Search):

```csharp
[RespireCommands]
public partial interface IJsonCommands
{
    [Command("JSON.GET")] ValueTask<string?> GetAsync(RespireKey key, string path = "$");
}
var json = redis.As<IJsonCommands>();
```

5. **Interactive WATCH transactions** on dedicated connections (§6).

## 19. What this deletes from today's surface

| Today | Becomes |
|---|---|
| `RespireClient.CreateAsync(host, port, connectionCount, logger, options)` | `ConnectAsync(uri \| options)`; everything else inside `RespireOptions` |
| Public disposable `RespireValue` returned from `GetAsync` | Internal; results are `string?`/`T?`/`long`…; leases for zero-copy; `RespireValue` name reused for the *input* arg struct |
| `ExpireAsync(key, int seconds)` | `ExpireAsync(key, TimeSpan)` |
| `PingWithResponseAsync` | Gone; `PingAsync` returns RTT `TimeSpan` |
| Flat `HGetAsync`/`LPushAsync`/`SAddAsync`… | Facets: `Hashes.GetAsync`, `Lists.PushAsync`, `Sets.AddAsync` |
| `RespireTransaction.Add<TCommand>` public generic | Internal; typed methods only |
| `RespireSubscriber` + `RespireMessageHandler` delegate | `await redis.SubscribeAsync(...)` → `IAsyncEnumerable<RespireMessage>` |
| `IRespireClientFactory` | Keyed DI registrations |

## Open questions

1. **Facet naming**: plural (`Hashes`, `Lists`, this spec) vs singular (`Hash`, `List`).
   Plural reads better as a collection-of-commands property; singular matches Redis doc
   group names. Spec says plural.
2. **Root shortcut set**: spec includes Get/Set/Delete/Exists/Increment/Expire/Ping.
   Draw the line tighter (Get/Set only) or wider (append TTL, GetSet)?
3. **`GetStringAsync` vs `GetAsync` returning `string?`**: spec uses `GetAsync<T>` for
   serialized objects and `GetStringAsync` for the raw-string common case, so that
   `GetAsync<string>` vs `GetStringAsync` never ambiguity-trap users. Alternative: make
   non-generic `GetAsync` return `string?` and require `<T>` for objects.
