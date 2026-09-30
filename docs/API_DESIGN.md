# Respire API Design Spec

Current API design and explicitly marked roadmap for a modern .NET Redis/RESP client.
The public surface is pre-release. The wire layer (multiplexed connections, FIFO
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
> - Obsolete compatibility aliases are removed before the first release. Custom facet
>   implementations must implement the canonical methods directly, including bitmap `Set`,
>   combined string/hash reads, and lock `ResetExpiryAsync`.

## Pre-release alias migration

The first release exposes only the canonical names. Replace the removed names below in both
immediate (`Async`) and deferred calls where applicable:

| Removed member | Replacement |
| --- | --- |
| String `GetDelete` | `GetAndDelete` |
| Hash `GetDelete` | `GetAndRemove` |
| String/hash `GetExpire` | `GetAndExpire` |
| Bitmap `GetAndSet` | `Set` |
| Lock commands and lock handles `ExtendAsync` | `ResetExpiryAsync` |
| Lock commands `IsHeldByAsync(mutex)` | `mutex.VerifyStillHeldAsync()` |
| Subscription `Channels` | `Targets` |

Custom facet implementations must provide the canonical members directly; the compatibility
forwarders are no longer default interface implementations.

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
await using var configuredRedis = await RespireClient.ConnectAsync(new RespireOptions
{
    Endpoints = { new("cache.example.com", 6379) },
    Password = builder.Configuration["Redis:Password"],
    ClientName = "checkout-api",
    Database = 0,
    ConnectTimeout = TimeSpan.FromSeconds(5),
    CommandTimeout = TimeSpan.FromSeconds(2),
    Connections = 4,
    Serializer = new SystemTextJsonSerializer(),
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

Facet operations require explicit implementations. Interfaces must not supply placeholder
implementations that throw `NotSupportedException`; missing support in a client, decorator,
or test double must be caught at compile time. Functional forwarding aliases are allowed.
Custom `IRespireSerializer` implementations likewise provide both generic and runtime-type
serialization and deserialization.

Pre-release migration for custom implementations: implement the newly required sorted-set range,
stream claim/replay, typed GETDEL/GETEX, subscription-options, and runtime-type serializer members.
Decorators should forward each member to the wrapped implementation; test doubles should provide
an explicit implementation. Existing implementations that inherited the removed throwing defaults
now fail to compile until those members are supplied. Client calls keep the same signatures.

```csharp
// The connected client also exposes these APIs through its interface.
IRespireClient client = redis;
string? text = await client.GetStringAsync("user:1:name", cancellationToken);
User? user = await client.GetAsync<User>("user:1", cancellationToken);
bool stored = await client.SetAsync("user:1:name", "Ada",
    expiry: TimeSpan.FromMinutes(5), cancellationToken: cancellationToken);
long removed = await client.DeleteAsync(["old:a", "old:b"], cancellationToken);
bool exists = await client.ExistsAsync("user:1", cancellationToken);
long hits = await client.IncrementAsync("hits", cancellationToken: cancellationToken);
bool expires = await client.ExpireAsync("user:1", TimeSpan.FromMinutes(5),
    cancellationToken: cancellationToken);
TimeSpan roundTrip = await client.PingAsync(cancellationToken);
```

The facet properties are `Strings`, `Keys`, `Hashes`, `Lists`, `Sets`, `SortedSets`, `Streams`,
`Bitmaps`, `HyperLogLog`, `Geo`, `Scripts`, `Locks`, and `Server`. See `IRespireClient` for
complete signatures; the snippets use application-defined model types such as `User`.

Naming inside facets drops the Redis prefix — the facet *is* the prefix:

```csharp
await redis.Hashes.SetAsync("user:1", "name", "Tom");          // HSET
string? name = await redis.Hashes.GetStringAsync("user:1", "name"); // HGET
long count = await redis.Lists.CountAsync("queue");            // LLEN
bool added = await redis.SortedSets.AddAsync("board", "tom", 42.0); // ZADD
```

New membership APIs must follow the [membership naming rule](../website/docs/commands/collections.md): `Exists` for named keys or fields, `Contains` for member values. The command guide is the source for examples and deferred-facet usage.

`Delete` removes whole keys; `Remove` removes entries within a key (hash fields, stream entries, set members, sorted-set members, or list elements). Hash `GetAndRemoveAsync` follows the same rule; string `GetAndDeleteAsync` removes the whole key. Stream group and consumer lifecycle methods retain `DeleteGroupAsync` and `DeleteConsumerAsync`: they delete consumer-group metadata, rather than stream entries.

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
RespireKey textKey = "user:42";
RespireKey binaryKey = new byte[] { 0xff, 0x00, 0x42 };
RespireValue numericValue = 42;
RespireValue binaryValue = new byte[] { 0xff, 0x00 };
```

`RespireValue` is *input-only*. The parse-side `RespValue` is internal; friendly results
surface as plain .NET types. Equality compares the exact bulk-string
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
(`IRespireSerializer`: `Serialize<T>(IBufferWriter<byte>, T)` /
`Deserialize<T>(ReadOnlySpan<byte>)`, plus runtime-type overloads). Default: `System.Text.Json` with source-gen
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
  NotExists / Exists`, `GetAndExpireAsync` variants), but common cases stay optional parameters.
- **Variadic where Redis is variadic**: `DeleteAsync(params ReadOnlySpan<RespireKey> keys)`
  (C# 13 params-span, zero alloc), `Hashes.SetAsync(key, [("name","Tom"), ("age","34")])`.
  A `params` parameter must come last, so each variadic command also has a sibling
  `DeleteAsync(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken)` — non-params items and a
  required token. The token is required, not optional, so the two forms never overlap.
  This pair is the chosen convention: retain bare varargs such as `DeleteAsync("a", "b")`
  and use `DeleteAsync(["a", "b"], cancellationToken)` when cancellation is needed. Collapsing the pair to
  one optional-token span overload would remove bare-varargs calls. Both forms use the same
  span-based command path; the convenience overload forwards `CancellationToken.None`.
  Deferred facets omit per-command tokens because execution owns cancellation. The public
  surface regression checks the matching generic arity, return type, item types, and required token.
- **`Async` suffix stays.** Analyzer ecosystem and reader expectation beat the saved
  keystrokes.
- **SCAN-family returns `IAsyncEnumerable`**, cursor handled internally:

```csharp
await foreach (var key in redis.Keys.ScanAsync(
    match: "user:*", type: RespireKeyType.Hash, countHint: 250, cancellationToken: cancellationToken))
{
    Console.WriteLine(key);
}

await foreach (var field in redis.Hashes.ScanAsync("user:1", match: "profile:*", cancellationToken: cancellationToken))
{
    Console.WriteLine($"{field.Key} = {field.Value}");
}
```

Hashes yield field/value pairs, sets yield members, and sorted sets yield members with scores.

## 5. Batching (explicit pipeline)

Auto-pipelining already happens under concurrency; `CreateBatch` exists for the
sequential-code case where you want N commands in one flush:

```csharp
using var batch = redis.CreateBatch();
RespirePending<string?> a = batch.GetString("a");
RespirePending<long>    n = batch.Increment("hits");
RespirePending<long>    q = batch.Lists.RightPush("queue", "job-1");
await batch.ExecuteAsync(cancellationToken);

string? av = a.Result;   // valid only after ExecuteAsync
```

Always declare batches with `using var`. See [batch disposal guarantees](../website/docs/guides/batches-and-transactions.md)
for pending commands, completed results, and repeated disposal.

A batch carries the same facets as the client (`Strings`, `Keys`, `Hashes`, `Lists`, `Sets`,
`SortedSets`, `Bitmaps`, `HyperLogLog`, `Geo`) with matching command names minus the `Async`
suffix and the same parameter shapes. The return type is deferred, and cancellation belongs to
`ExecuteAsync`. Blocking
(`waitFor`) and streaming (`ScanAsync`, `GetLeaseAsync`) members have no deferred form.

`Keys.RenameAsync`, `Lists.TrimAsync`, `HyperLogLog.MergeAsync`, and `Strings.SetManyAsync`
return `ValueTask<bool>`, matching the `RespirePending<bool>` result of their batch and transaction
counterparts. Each returns `true` only after an `OK` confirmation; an unexpected reply or Redis
error faults the operation rather than returning `false`.

Migration: calls that simply await and ignore the result need no changes. Update custom facet
implementations, wrappers, delegates, and variables that explicitly use the previous non-generic
`ValueTask` return type to `ValueTask<bool>`.

`RespirePending<T>` is awaitable *and* has `.Result`, but both throw
`RespirePendingNotReadyException` if touched before `ExecuteAsync`. The synchronous queueing names
make accidental early awaits conspicuous, while the exception prevents a deadlock. `Status`, `HasResult`, `Error`,
and `TryGetResult` expose pending, successful, faulted, and aborted outcomes without try/catch.
`ExecuteAsync` completes all pendings, then throws the first failure in original queue order.
Successful pending results remain readable, and the returned summary describes a successful flush.
Use `TryExecuteAsync` to inspect `Count`, `FailureCount`, `FirstError`, and `Failures` without
rethrowing command or connection-acquisition errors. Both methods preserve timeout/cancellation
errors on their pendings and reject disposed or already-sent batches. **Breaking behavior change:**
pre-release callers that inspect failed summaries must migrate from `ExecuteAsync` to `TryExecuteAsync`.

## 6. Transactions

Same pending-value shape as batch. `CreateTransaction()` returns `RespireTransaction`, whose
`CommitAsync` is a `ValueTask` because an unwatched EXEC cannot abort. With watch keys,
`CreateTransactionAsync` returns `RespireWatchedTransaction`; its `CommitAsync` returns whether
EXEC won:

```csharp
await using var tx = await redis.CreateTransactionAsync(["balance"], cancellationToken);
var newBal = tx.Increment("balance", -100);
var log    = tx.Lists.RightPush("audit", "withdraw:100");
bool committed = await tx.CommitAsync(cancellationToken);
```

When WATCH aborts EXEC, `committed` is false, each pending reports
`RespirePendingStatus.Aborted`, and reading one throws `RespireTransactionAbortedException`.

Interactive WATCH → read → decide → MULTI (true CAS) uses
`CreateTransactionAsync(watchKeys)`. Create the watched transaction, read current values through
the client (transaction reads are deferred and unavailable for decisions), queue writes, then
commit. If `CommitAsync` returns false, dispose that attempt and recreate the watched transaction,
including its reads and writes. Keep retries bounded. Scripts/functions remain preferable when
the operation can be expressed server-side because they avoid round trips and retries.

Key metadata is available through `Keys.ExpiryTimeAsync` (PEXPIRETIME/EXPIRETIME),
`EncodingAsync`, `IdleTimeAsync`, `FrequencyAsync`, and `ReferenceCountAsync` (OBJECT).
The absolute-expiry result reuses `RespireExpiryTime`, preserving missing/persistent distinctions
and the raw Unix-millisecond timestamp. OBJECT metadata is nullable for missing keys and retains
server policy errors (FREQ requires LFU; IDLETIME excludes it). Batch and transaction Keys facets
mirror these operations. Existing ExpireWhen conditions map to NX/XX/GT/LT on PEXPIRE/PEXPIREAT.

## 7. Pub/Sub: `IAsyncEnumerable`

Subscriptions are async streams. Dispose the subscription to unsubscribe. Cancelling an
enumerator stops that reader; it does not dispose the subscription:

```csharp
await using var sub = await redis.SubscribeAsync("orders");  // also: patterns, sharded
await foreach (RespireMessage msg in sub.WithCancellation(cancellationToken))
{
    if (msg.Kind == RespireMessageKind.Gap)
    {
        Console.Error.WriteLine($"Delivery gap: {msg.Gap}. Reload order state before processing more messages.");
        continue;
    }
    Console.WriteLine($"{msg.Channel}: {msg.Text}");
    var order = msg.As<Order>();                            // serializer-backed
}
```

- `SubscribeAsync(channel | channels)`, `SubscribePatternAsync(pattern)`,
  `SubscribeShardedAsync(channel)` (Redis 7+ SSUBSCRIBE). Subscribing is always awaited: the
  task completes once the server has acknowledged, so the next publish reaches the stream.
- Backed by a bounded message buffer; overflow policy is `DropOldest` (default) or
  `DropNewest`. Blocking and throwing policies are intentionally omitted because either would
  stop the shared pub/sub reader and affect unrelated subscriptions.
- `RespireChannel` owns binary channel bytes. Text and byte inputs compare by encoded bytes;
  `Literal`, `Pattern`, and `Sharded` factories carry explicit subscription metadata.
  `SubscribeAsync(RespireChannel)` selects the command family from that metadata. Multi-target
  binary overloads require one kind and an explicit cancellation argument.
- **Pre-release API change:** `RespireMessage.Channel`, nullable `Pattern`, and subscription
  `Targets` use `RespireChannel` instead of strings. `.Bytes` is lossless, allocation-free access;
  `.ToString()` is UTF-8 display with replacement characters for invalid bytes. The message
  also exposes owned `Payload`, `Text`, and `As<T>()`.
- **Pre-release behavior change:** streams also yield `RespireMessageKind.Gap`. Check `Kind`
  before payload access. `Gap` includes reconnect/overflow reasons, the observed interval, and
  known local discard count. Reconnect markers precede resumed messages for each target;
  adjacent markers coalesce without consuming message capacity or losing the notification.
  `DeliveryGap` and `respire.pubsub.delivery.gaps` report individual detected gaps. Event handlers
  must not block the receive path. Reload dependent state from its source after a gap; pub/sub
  provides no replay. See the pub/sub guide for multi-target and overflow ordering.
- Publish is just `redis.PublishAsync(channel, value)` on the root.

## 8. Streams

Same async-stream philosophy for consumer groups; XREADGROUP blocking loop, ack on the
entry:

```csharp
await redis.Streams.CreateGroupAsync("events", "processors", createStream: true);

await foreach (var entry in redis.Streams.ReadGroupAsync(
    "events", group: "processors", consumer: Environment.MachineName, cancellationToken: cancellationToken))
{
    Console.WriteLine(entry.GetString("type")); // decoded field value
    await entry.AckAsync();
}
```

`AddAsync(key, [("type", "click"), ...])` returns the generated `RespireStreamId`
(comparable struct, not string).
Pass `StreamAddOptions` to select an id, trim with `MAXLEN`, or require an existing stream.
The options overload returns `null` when `CreateStream` is false and the stream is absent.
`RangeAsync(key, start, end, count, descending: true)` reads the newest matching entries first with `XREVRANGE`.
Streams and sorted sets use an optional `descending = false` parameter immediately before the cancellation token.

Consumer recovery is typed as well. `ClaimAsync` transfers known pending ids with `XCLAIM`;
`ClaimPendingAsync` scans idle entries with `XAUTOCLAIM` and returns the next scan position,
claimed entries, and ids Redis reports as deleted. Passing an explicit `startAt` to
`ReadGroupAsync` replays that consumer's own PEL from the id and then completes; leaving it null
keeps the blocking new-entry loop.

## 9. Blocking commands are supported, transparently

BLPOP/BRPOP/BLMOVE/XREAD-block are *forbidden* in SE.Redis because of multiplexing. We
have a connection pool — blocking commands automatically route to a dedicated pooled
connection:

```csharp
string? job = await redis.Lists.LeftPopAsync("jobs", waitFor: TimeSpan.FromSeconds(30), cancellationToken: cancellationToken);
```

`waitFor: null` (default) = non-blocking LPOP; a value = BLPOP on a dedicated connection.
One method, one mental model. This is a headline capability — spec it early, market it.

## 10. Raw commands and the interpolated escape hatch

```csharp
RespireKey key = "user:1";
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

`RespireResult` is the one public protocol-shaped type: `Type`, `AsString()`,
`AsInteger()`, serializer-backed `As<T>()`, `AsSpan()`, and allocation-free array enumeration.
It owns pooled memory and must be disposed (`using`); `IsDisposed` exposes its lifetime state and
access after disposal throws `ObjectDisposedException`. It exists only on the raw layer. The
generated catalog covers every audited Redis and Valkey command plus
documented module, KeyDB, and Dragonfly commands; string execution remains available for
experimental server extensions.

## 11. Scripts and functions

<!-- doc-test-declaration: split-before=long count -->
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
entry points return a `RespireResult` that the caller must dispose. The span member is the required
implementation core; the array convenience forwards spans without allocating input copies. The
built-in client consumes input spans synchronously and keeps one combined command tail for async
execution and NOSCRIPT fallback. That tail still allocates; the span path avoids separate key and
argument arrays. `ExecuteSpanAsync` retains its distinct name to keep array and collection-expression
calls unambiguous. Custom `IScriptCommands` implementations must implement the span member.

- SHA1 computed once at `Create`; `ExecuteAsync` tries EVALSHA, falls back to EVAL on
  NOSCRIPT, transparently.
- FUNCTION and FCALL commands are available through `RespireCommands.Scripting`.

## 12. Key-prefixed views

```csharp
var tenantId = "42";
var cart = new { Items = new[] { "book" } };
IRespireClient tenant = redis.WithKeyPrefix($"t:{tenantId}:");
await tenant.SetAsync("cart", cart);     // key = "t:42:cart"
```

Cheap decorator over the same connections; composes (`WithKeyPrefix` on a prefixed view
concatenates). Client-side caching is configured through `RespireOptions.ClientSideCache`, not a separate view.

## 13. Resilience

- **Reconnect**: failed multiplexed connections are replaced automatically. Failed replacement attempts retry on the next use. Pub/sub uses its own reconnect/resubscribe loop. `ConnectionStateChanged` reports
  transitions. There is no configurable `ReconnectPolicy` today; that is tracked in
  [#401](https://github.com/thomhurst/Respire/issues/401).
- **Timeouts**: `CommandTimeout` is the client default; each call accepts a `CancellationToken`
  for tighter control. `RespireTimeoutException` names the operation and explains that a sent
  command may still execute. A queue/inflight diagnostic snapshot is planned in
  [#404](https://github.com/thomhurst/Respire/issues/404), not part of the current exception.
- **No general automatic replay after connection failure.** Retrying non-idempotent commands
  can duplicate effects. Redis Cluster MOVED/ASK routing and script NOSCRIPT recovery are
  targeted protocol recovery paths; they are separate from a general application retry policy.
- Cancellation cancels the wait, never an in-flight send (wire invariant).

## 14. Observability

- `ActivitySource("Respire")` — span per logical operation using OTel Redis semantic
  conventions (`db.system.name = redis`, `db.namespace`, `db.operation.name`, endpoint and
  error attrs). Query text stays excluded because arbitrary Redis command values cannot be
  reliably sanitized. Pipelines and transactions emit one span with `db.operation.batch.size`.
- `Meter("Respire")` — stable `db.client.operation.duration` histogram in seconds.
- Register the built-in source and meter with OpenTelemetry's standard extensions. There is
  no `Respire.Extensions.OpenTelemetry` package or `AddRespireInstrumentation()` API.

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource("Respire"))
    .WithMetrics(metrics => metrics.AddMeter("Respire"));
```

## 15. Errors

```
RespireException
├── RespireConnectionException     // can't connect / connection lost mid-command
├── RespireConfigurationException  // valid input cannot configure the requested API
├── RespireProtocolException       // malformed or invalid RESP data
├── RespireTimeoutException        // command name and elapsed timeout
└── RespireServerException         // .Code, .CommandName, and .IsTransient
```

Top-level server errors throw `RespireServerException` from both the friendly APIs and
`ExecuteAsync`. A raw aggregate reply can contain nested error elements; inspect those with
`RespireResult.IsError`. `RespireResult` has no public `Kind` property.

## 16. Dependency injection (`Respire.Extensions.DependencyInjection`)

<!-- doc-test-tail-declaration: split-before=public sealed class CartService -->
```csharp
builder.Services.AddRespire(builder.Configuration.GetConnectionString("redis")!);

// The explicit parameter type selects the options-builder action overload.
// Multiple clients via keyed services
builder.Services.AddKeyedRespire("cache",    (RespireOptionsBuilder o) => o.Endpoints.Add(new("cache-host")));
builder.Services.AddKeyedRespire("sessions", (RespireOptionsBuilder o) => o.Endpoints.Add(new("sess-host")));

public sealed class CartService([FromKeyedServices("cache")] IRespireClient redis) { }
```

- Registers `IRespireClient` + `RespireClient` singleton; connection happens on first use.
  `ConnectTimeout` bounds socket and TLS setup; the Redis handshake uses `CommandTimeout`, as do
  non-blocking commands. Blocking commands use their explicit wait timeout, and caller
  cancellation applies throughout. Cluster seed failures are wrapped in
  `RespireConnectionException`, and the next command retries connection.
- `RespireOptionsBuilder.Endpoints` is a mutable `IList<RespireEndpoint>`; `Endpoints.Add(...)`
  is valid in the action overload. Configuration accepts a connection string, an `Action<RespireOptionsBuilder>`, or a
  service-provider factory returning `RespireOptions`; the package does not bind `IOptions`.
- Health integrations can inspect `IsConnected` and subscribe to `ConnectionStateChanged`;
  the package does not register a health check.

## 17. Testing story

- All facets and the client are interfaces (`IRespireClient`, `IHashCommands`, …);
  implementations sealed. Mocking works with any framework.
- Roadmap: `Respire.Testing` — in-memory `IRespireClient` fake for unit tests without a
  container; integration tests keep using real Redis via Testcontainers.

## 18. Delivery status and roadmap

1. **Delivered — client-side caching**: RESP3 `CLIENT TRACKING` with bounded local storage and
   invalidation pushes for deterministic, explicitly keyed Redis reads. The implementation design, including why options-level
   ownership supersedes the earlier
   `WithLocalCache` sketch, lives in [CLIENT_SIDE_CACHING_DESIGN.md](CLIENT_SIDE_CACHING_DESIGN.md).
2. **RESP3-first internals**: broader native RESP3 adoption for maps, doubles, and booleans.
3. **Sentinel**: automatic primary discovery and failover. Redis Cluster already uses
   `Endpoints` as seeds and handles `CLUSTER SLOTS`, MOVED, ASK, and hash-slot validation.
4. **Source-generated custom commands** for modules such as RedisJSON and Search are planned
   in [#417](https://github.com/thomhurst/Respire/issues/417). Attribute-based declarations and
   `redis.As<T>()` are not available; use the generated command catalog or raw `ExecuteAsync`.
5. **Interactive WATCH transactions** are already delivered on dedicated connections (§6).

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

### Single-member and multi-member pops

This breaking rename is recorded in the [unreleased release notes](RELEASE_NOTES.md#unreleased).

Sets and sorted sets use `PopAsync` for one member and `PopManyAsync(key, count, ...)`
for an array. Batch and transaction facets use `Pop` and `PopMany`. Sorted-set typed
operations follow the same naming. Lists retain `LeftPopManyAsync`/`RightPopManyAsync`
and their deferred forms. Missing keys return null for scalar pops and empty arrays
for multi-member pops. Rename pre-release count-based `PopAsync`/`Pop` calls to their
`Many` forms; no compatibility aliases remain. Redis commands and count semantics
are unchanged, including an empty result for a zero count.

### Dedicated pool retirement (internal)

`DedicatedConnectionPool.RetireAsync` stops new rentals and cancels unfinished handshakes,
closes idle connections, and waits for accepted borrowed leases to return or be discarded.
Those borrowed operations keep their connections until they finish. `DisposeAsync` escalates
an existing retirement by aborting borrowed operations, including indefinitely blocking reads.
Both methods await the same cleanup, including receive/flush work and pending acquisitions,
not merely removal from the rented set. Retirement completion reports cleanup failures; disposal
logs them and returns so client shutdown can continue disposing other resources. Ordinary returned connections remain
reusable until retirement begins; closing connections never reenter the idle pool. Retirement has no implicit timeout: a caller
can bound its own wait without cancelling accepted work, or explicitly dispose the pool to abort
borrowed sockets. Receive callbacks must remain nonblocking; disposal cannot forcibly terminate
a callback executing application code.

This is a lifecycle primitive for Cluster generation retirement. Router integration must retain
retiring pools until completion, protect owed correction barriers before retiring control pools,
and avoid handing a retired pool to a new generation. That integration is tracked by #466;
this primitive alone does not remove departed nodes from Cluster routing. Owners must observe
retirement completion, including cleanup failures. A handshake cancelled by retirement reports
`OperationCanceledException`; a later or rejected rental reports `ObjectDisposedException`.
Router retry decisions must also inspect its own generation state and the caller's cancellation
rather than treating every cancellation as retirement.
