---
title: Coming from StackExchange.Redis
description: How Respire compares with StackExchange.Redis, and how to move existing code across.
---

# Coming from StackExchange.Redis

This page compares Respire with StackExchange.Redis and maps common StackExchange.Redis code to Respire.
Both cover multiplexed connections, automatic pipelining, Cluster, Sentinel, pub/sub, transactions,
and scripting. Respire has a different API shape and some extra capabilities.

The opt-in [migration analyzer and code fix](./guides/stackexchange-migration-tool) converts
supported `IDatabase` methods to native Respire calls and flags other calls for manual review.

To migrate gradually, use the [incremental migration adapter](./guides/stackexchange-interop). It
accepts `RedisKey`, `RedisValue`, and `RedisResult`, and provides a limited `IConnectionMultiplexer`
for the official distributed cache, DataProtection, and [Hangfire](./guides/hangfire-acceptance).
It is not a general StackExchange.Redis replacement. It does not support Microsoft's SignalR
backplane; use [`Respire.SignalR`](./integrations/signalr).

For applications using RedLock.net or madelson's DistributedLock.Redis, see
[migrating Redis coordination libraries](./guides/coordination-migration.md) for API mappings,
renewal and timeout handling, and the differences between quorum leases and fenced locks.

Verified on **14 October 2026** against StackExchange.Redis
[3.4.5 (latest stable release)](https://github.com/StackExchange/StackExchange.Redis/releases/tag/3.4.5)
and [4.0.127-alpha (latest published 4.0 preview)](https://www.nuget.org/packages/StackExchange.Redis/4.0.127-alpha).
The 4.0 column describes that preview, not a stable 4.0 release or a promise about its final API.
Earlier 3.x releases may lack features added in 3.4. The published [benchmarks](./benchmarks)
used 3.3.1 and do not measure 4.0.

See the [roadmap](./roadmap) for supported features and the
[experimental API policy](./experimental-apis) for compatibility limits.

## Why switch

Respire gives you:

- [server-assisted client-side caching](./fundamentals/client-side-caching) for hot reads;
- [blocking commands](./guides/blocking-queues) such as `BLPOP`, `BLMOVE`, and `XREADGROUP BLOCK`
  without stalling other traffic;
- typed results (`string?`, `long`, `T?`) instead of a protocol value union;
- `IAsyncEnumerable` pub/sub, stream reads, and scans;
- an [in-memory test server](./guides/in-memory-testing) and built-in OpenTelemetry.

See the [benchmarks](./benchmarks) and [stress tests](./stress-tests) for measurements against
StackExchange.Redis. The published 3.3.1 results do not establish performance against 4.0.

## Feature comparison

| Capability | StackExchange.Redis 3.x (3.4.5) | StackExchange.Redis 4.0.127-alpha | Respire |
| --- | --- | --- | --- |
| Target frameworks | .NET Framework 4.6.1/4.7.2, `netstandard2.0`, .NET 8/10 | Same targets | .NET 8 and .NET 10 |
| API style | Sync methods and async `Task` methods | New command groups return `ValueTask`; legacy sync/`Task` methods remain supported | Asynchronous only (`ValueTask`) |
| Command surface | Flat methods on `IDatabase` (`StringGetAsync`) | Groups on the database (`db.Strings.GetAsync`), alongside the flat API | Facets per data type (`redis.Strings.GetStringAsync`) |
| Values | `RedisValue` union | `RedisValue` for ordinary string results; groups also expose pooled `ReadOnlyLease<T>` results | Typed results; `RespireValue` for arguments and pooled result APIs |
| Per-call cancellation | No token on ordinary `IDatabaseAsync` commands | Command groups accept `CancellationToken` | Most methods accept `CancellationToken` |
| Automatic pipelining | Yes | Yes | Yes |
| Protocol | RESP3 attempted when `HELLO` is enabled and configured server version is 6+; RESP2 fallback/override | Same negotiation; client-side caching requires RESP3 | RESP3 preferred, with RESP2 fallback |
| Server-assisted client-side cache | No built-in cache | Opt-in `ClientCache`; covers both legacy calls and groups | Opt-in `ClientSideCache` |
| Blocking Redis commands (`BLPOP`, `BLMOVE`, ...) | No typed APIs | No typed APIs in this preview | Typed APIs on dedicated pooled connections |
| Pub/sub | Handlers or `ChannelMessageQueue`; separate pub/sub connection by default under RESP3 since 3.4 | Same subscription API and connection default | `IAsyncEnumerable` subscriptions with delivery-gap markers |
| Sharded pub/sub (Redis 7) | Yes | Yes | Yes |
| Transactions | `AddCondition` checks | Same checks; batches and transactions also expose groups | `WATCH`-based optimistic concurrency |
| Distributed locks | `LockTake`, `LockExtend`, `LockRelease` | Same helpers on the legacy `IDatabase` surface | Raw-token APIs plus managed lock handles with keep-alive |
| Read from replicas | `CommandFlags.PreferReplica` / `DemandReplica` | Same per-command flags | `ReadFrom` or `WithReadFrom`, with hedged and zone-aware reads |
| Fire-and-forget | `CommandFlags.FireAndForget` | Same flag, including on groups | `ExecuteFireAndForgetAsync` for raw commands |
| Multiple databases | `GetDatabase(n)` | Same | One database per client (`Database` option) |
| Health-checked failover between deployments | `ConnectGroupAsync` connection groups | Same, with group configuration in `MultiGroupOptions` | [`RespireFailoverGroup`](./guides/failover-groups); read `ActiveClient` for each operation |
| Smart client handoffs | Maintenance notifications and endpoint handoff; automatic for Redis Enterprise and Azure Managed Redis since 3.4; disabled inside connection groups | Same availability and group restriction | Opt-in [maintenance handoff](./fundamentals/connections#maintenance-notifications), including [failover group candidates](./guides/failover-groups#maintenance-notifications) |
| Command retry policy | Async `WithRetry(...)` wrapper with `RetryPolicy` and `CommandRetry*` categories | `WithRetry(...)` also exposes groups; configurable connection/group policy | [`RetryPolicy`](./guides/command-retries) with category limits, plus whole-unit batch and transaction retries |
| Diagnostics | `RegisterProfiler` profiling sessions | Same profiling API | OpenTelemetry `ActivitySource` and `Meter` |
| Analyzers | Transaction and sync-over-async checks (`SER305`–`SER308`) | Those checks remain available | Pooled-result and batch-read checks (`RESP001`, `RESP002`); source-generator checks (`RESP003`–`RESP006`); sync-over-async (`RESP009`); client lifetime, unbounded reads, and blocking subscription consumers (`RESP010`–`RESP012`) |
| `IDistributedCache` | `Microsoft.Extensions.Caching.StackExchangeRedis` | Legacy `IConnectionMultiplexer` / `IDatabase` API remains available for adapters | [`Respire.Caching`](./integrations/caching); entries interchangeable while `ValueCodec` is unset |
| Module/custom commands | Raw `Execute` | Raw commands plus extensible groups and `Context.SendAsync<T>` | Typed `Json`, `Search`, `TimeSeries`, and `Probabilistic` packages |
| In-memory test server | None shipped in the client package | None shipped in the client package | [`Respire.Testing`](./guides/in-memory-testing) |

The 3.x entries use the tagged [configuration][ser3-config], [failover/retry][ser3-failover],
[maintenance][ser3-maintenance], and [sync/async][ser3-async] documentation. For 4.0, see the
preview's [command groups][ser4-groups], [legacy API mapping][ser4-legacy],
[client-side cache][ser4-cache], [protocol][ser4-protocol], and [failover/retry][ser4-failover]
documentation. These links are pinned to the compared versions.

Both compared StackExchange.Redis versions also offer opt-in `DedicatedThreads` to keep Redis
traffic off the global thread pool; see the [3.x][ser3-async] and [4.0][ser4-async] threading guidance.

### What changes when coming from 4.0

Async command groups, `ValueTask`, cancellation, and client-side caching are also available in
the 4.0 preview. Compare their behavior and configuration before choosing a client. Its legacy
`IDatabase` methods are still supported; upgrading StackExchange.Redis does not require a
migration to Respire or a rewrite to groups.

Similar method names do not imply identical result types: StackExchange.Redis
`db.Strings.GetAsync(key)` returns `RedisValue`; Respire's `redis.Strings.GetStringAsync(key)`
returns `string?`, while `GetAsync<T>` uses the selected codec. Both offer pooled results that
must be disposed. Review binary values, missing values, and ownership when porting callers.

StackExchange.Redis 4.0's `Context.Blocking()` blocks the calling thread; it does not add typed
Redis blocking queue commands such as `BLPOP`. A command group's cancellation token prevents
a command from being sent if it has not yet been written to the connection. Once written,
the command still runs on Redis; cancellation ends the caller's wait and discards the reply.

The examples below map the flat 3.x API, which is also available in 4.0. For callers already
using 4.0 groups, use the upstream [mapping][ser4-legacy] to identify the Redis operation,
then check the Respire result and options rather than applying a mechanical rename.

[ser3-config]: https://github.com/StackExchange/StackExchange.Redis/blob/3.4.5/docs/Configuration.md
[ser3-failover]: https://github.com/StackExchange/StackExchange.Redis/blob/3.4.5/docs/Failover.md
[ser3-maintenance]: https://github.com/StackExchange/StackExchange.Redis/blob/3.4.5/docs/ServerMaintenanceEvent.md
[ser3-async]: https://github.com/StackExchange/StackExchange.Redis/blob/3.4.5/docs/SyncOverAsync.md
[ser4-groups]: https://github.com/StackExchange/StackExchange.Redis/blob/4.0.127-alpha/docs/Groups.md
[ser4-legacy]: https://github.com/StackExchange/StackExchange.Redis/blob/4.0.127-alpha/docs/LegacyApi.md
[ser4-cache]: https://github.com/StackExchange/StackExchange.Redis/blob/4.0.127-alpha/docs/ClientSideCaching.md
[ser4-protocol]: https://github.com/StackExchange/StackExchange.Redis/blob/4.0.127-alpha/docs/Resp3.md
[ser4-failover]: https://github.com/StackExchange/StackExchange.Redis/blob/4.0.127-alpha/docs/Failover.md
[ser4-async]: https://github.com/StackExchange/StackExchange.Redis/blob/4.0.127-alpha/docs/SyncOverAsync.md

## Behavior differences

Review these before you switch; they can change application behavior.

- **No synchronous API.** Every command returns `ValueTask` or `ValueTask<T>`. Replace sync calls
  with `await`; do not block on `.Result`.
- **Missing values are `null` or `default`.** `GetStringAsync` returns `string?` and
  `GetAsync<T>` returns `T?`. There is no `RedisValue.IsNull` check. For a value type,
  `GetAsync<int>` returns `0` for a missing key. Use `TryGetAsync<T>` and check `Found` when a
  missing key must differ from a stored default.
- **Server errors throw.** A Redis error reply throws `RespireServerException`; its `Code`
  carries the Redis error class.
- **Timeouts are longer by default.** `CommandTimeout` defaults to 10 seconds; StackExchange.Redis
  defaults to 5 seconds. A timed-out command may still run on the server, as with
  StackExchange.Redis. `asyncTimeout` and `syncTimeout` in a connection string set
  `CommandTimeout`. See [timeouts and thread-pool starvation](./guides/timeouts.md)
  for timeout stages, diagnosis, and a mapping of StackExchange.Redis v3 message fields.
- **Cancellation is supported.** Most methods accept a `CancellationToken`. Cancellation abandons
  the wait only; a command that was already written still runs.
- **Standalone reconnect waits are bounded.** After initial connection, definitely-unsent
  standalone primary commands wait in a FIFO backlog by default, including raw and
  fire-and-forget commands, whole batches and unwatched transactions. `BacklogCapacity` defaults
  to 1024 waiting operations; each keeps its original timeout and cancellation. Set
  `BacklogPolicy = RespireBacklogPolicy.FailFast` to opt out. This does not replay accepted
  commands and does not require `RetryPolicy`. Cluster, Sentinel and replica-routing backlog
  support remains tracked in [#1717](https://github.com/thomhurst/Respire/issues/1717).
  See [connection recovery](./guides/reconnect-policy.md#standalone-command-backlog) for exclusions
  and the distinction between backlog, reconnect and command retry policies.
- **Batch results throw before the batch runs.** Reading `RespirePending<T>.Result` before
  `ExecuteAsync` throws instead of deadlocking.
- **RESP3 changes raw reply shapes.** Typed methods hide the difference. Raw `RespireResult`
  callers can receive maps, sets, and doubles. Set `Protocol = RespProtocol.Resp2` if raw callers
  need RESP2 shapes. Client-side caching always uses RESP3, so use a separate client without
  `ClientSideCache` for those callers. See [protocol negotiation](./fundamentals/connections#protocol-negotiation).
- **One database per client.** Create one client per database index, or use key prefixes.
  `SELECT` cannot switch a shared connection.
- **Multiple endpoints need a mode.** A comma-delimited string with several endpoints must set
  `cluster=true` or `serviceName=...`. Respire rejects an ambiguous list instead of guessing.
- **Failover groups do not move existing calls.** `RespireFailoverGroup` selects a deployment for
  new operations only. Read `group.ActiveClient` for each operation instead of keeping a client
  reference. In-flight calls on the old deployment are not replayed.
- **Key scans cover the whole deployment.** `server.KeysAsync` scans one server. `Keys.ScanAsync`
  scans the client's configured database, and every primary in Cluster mode.
- **Subscriptions report delivery gaps.** A subscription stream yields `RespireMessageKind.Gap`
  items after a reconnect or buffer overflow. Check `message.Kind` before reading the payload.

## Connecting

Respire accepts most StackExchange.Redis connection strings:

```csharp
await using var redis = await RespireClient.ConnectAsync(
    "cache-a:6380,password=secret,ssl=true,defaultDatabase=2");
```

Azure portal strings including `abortConnect=False` work as pasted. Unsupported options
are ignored with one warning per key per client when `LoggerFactory` is configured.
Invalid mapped values throw `RespireConfigurationException`. See
[StackExchange.Redis connection strings](./fundamentals/connections#stackexchangeredis-connection-strings)
for mappings and explicit rejections. URIs such as `rediss://user:pass@cache-a:6380/2` and `RespireOptions` also work.

| StackExchange.Redis | Respire |
| --- | --- |
| `ConnectionMultiplexer.ConnectAsync(config)` | `RespireClient.ConnectAsync(config)` |
| `ConnectionMultiplexer.Connect(config)` with `abortConnect=false` | `RespireClient.Create(config)` (connects lazily; accepts `abortConnect=false`) |
| `multiplexer.GetDatabase()` | The client itself (`IRespireClient`) |
| `multiplexer.GetDatabase(2)` | A separate client with `Database = 2` |
| `multiplexer.GetSubscriber()` | `redis.SubscribeAsync` and `redis.PublishAsync` on the client |
| `multiplexer.GetServer(endpoint)` | `redis.Server.OnNode(endpoint)` for [one explicit node](./guides/server-node-lifecycle). `redis.Server` otherwise uses normal routing; `*OnAllNodesAsync` methods run on every node |
| `ConnectionFailed`, `ConnectionRestored` | `ConnectionStateChanged` |
| `multiplexer.IsConnected` | `redis.IsConnected` |
| `services.AddSingleton<IConnectionMultiplexer>(...)` | [`services.AddRespire(...)`](./integrations/dependency-injection) |

## Types

| StackExchange.Redis | Respire |
| --- | --- |
| `RedisKey` | `RespireKey` (implicit from `string`, `byte[]`, `ReadOnlyMemory<byte>`) |
| `RedisValue` (arguments) | `RespireValue` (implicit from strings, bytes, numbers, `bool`, `Guid`, and more) |
| `RedisValue` (results) | `string?`, `byte[]?`, `long`, `bool`, `double`, or `T?` |
| `RedisResult` | `RespireResult` — GC-owned by default; dispose results from explicit lease APIs |
| `RedisChannel` | `RespireChannel` |
| `TimeSpan? expiry` | `RespireExpiry` (implicit from `TimeSpan` and `DateTimeOffset`) |
| `When.NotExists`, `When.Exists` | `SetWhen.NotExists`, `SetWhen.Exists` |
| `KeyTimeToLive` result `TimeSpan?` | `RespireTtl`, which separates a missing key from a key without expiry |

## Commands

Common string and key commands are on the client. Other commands are grouped into facets:
`Strings`, `Keys`, `Hashes`, `Lists`, `Sets`, `SortedSets`, `Streams`, `Bitmaps`, `HyperLogLog`,
`Geo`, `VectorSets`, `Scripts`, `Functions`, `Locks`, and `Server`.

<!-- doc-test-ignore: StackExchange.Redis is not referenced by the documentation test project. -->
```csharp
// StackExchange.Redis
IDatabase db = multiplexer.GetDatabase();
await db.StringSetAsync("user:1:name", "Ada", TimeSpan.FromMinutes(5), When.NotExists);
string? name = await db.StringGetAsync("user:1:name");
long visits = await db.StringIncrementAsync("visits");
await db.HashSetAsync("user:1", "email", "ada@example.com");
await db.SortedSetAddAsync("leaderboard", "ada", 42);
TimeSpan? ttl = await db.KeyTimeToLiveAsync("user:1:name");
```

```csharp
// Respire
await redis.SetAsync("user:1:name", "Ada", TimeSpan.FromMinutes(5), SetWhen.NotExists);
string? name = await redis.GetStringAsync("user:1:name");
long visits = await redis.IncrementAsync("visits");
await redis.Hashes.SetAsync("user:1", "email", "ada@example.com");
await redis.SortedSets.AddAsync("leaderboard", "ada", 42);
RespireTtl ttl = await redis.Keys.ExpiryAsync("user:1:name");
```

| StackExchange.Redis | Respire |
| --- | --- |
| `StringGetAsync`, `StringSetAsync` | `GetStringAsync`, `GetAsync<T>`, `SetAsync` on the client |
| `StringIncrementAsync`, `StringDecrementAsync` | `IncrementAsync`, `DecrementAsync` |
| `KeyDeleteAsync`, `KeyExistsAsync`, `KeyExpireAsync` | `DeleteAsync`, `ExistsAsync`, `ExpireAsync` |
| `HashGetAsync`, `HashSetAsync`, `HashGetAllAsync` | `Hashes.GetStringAsync` or `Hashes.GetBytesAsync`, `Hashes.SetAsync`, `Hashes.GetAllAsync` or `Hashes.GetAllAsync<byte[]>` |
| `ListLeftPushAsync`, `ListRightPopAsync` | `Lists.LeftPushAsync`, `Lists.RightPopAsync` |
| `SetAddAsync`, `SetMembersAsync` | `Sets.AddAsync`, `Sets.MembersAsync` |
| `SortedSetAddAsync`, `SortedSetRangeByScoreAsync` | `SortedSets.AddAsync`, `SortedSets.RangeByScoreAsync` |
| `StreamAddAsync`, `StreamReadGroupAsync` | `Streams.AddAsync`; `Streams.ReadGroupOnceAsync` for one batch; single-key `Streams.ReadGroupAsync` reads continuously |
| `server.KeysAsync(pattern)` | `Keys.ScanAsync(pattern)` (`IAsyncEnumerable`; scans every primary in Cluster mode) |
| `StringGetLeaseAsync` | `Strings.GetLeaseAsync` |
| `ExecuteAsync("CMD", args)` | `ExecuteAsync("CMD", args)` or the generated `RespireCommands` catalog |
| `CommandFlags.FireAndForget` | `ExecuteFireAndForgetAsync` |

**Binary data.** Non-generic read methods return UTF-8 strings and replace invalid bytes. This
applies to strings, hashes, lists, sets, sorted sets, and `Keys.ScanAsync`. For binary data, use
`GetBytesAsync` or a `<byte[]>` overload, such as `GetAsync<byte[]>`, `Hashes.GetAllAsync<byte[]>`,
`Lists.RangeAsync<byte[]>`, `Sets.MembersAsync<byte[]>`, or `SortedSets.RangeAsync<byte[]>`.
Hash field names, stream field names, and scanned keys are always strings; use raw `HGETALL`,
`XADD`, stream read, or `SCAN` commands when they are binary. In Cluster mode, a raw `SCAN` reaches one node only.

**Stream consumer groups.** StackExchange.Redis `StreamReadGroupAsync` returns one batch; the equivalent
is `Streams.ReadGroupOnceAsync`. The single-key `Streams.ReadGroupAsync` reads continuously on a
dedicated connection until its cancellation token is cancelled. The multi-stream `Streams.ReadGroupAsync`
overload returns one page across streams. `StreamReadOptions` supports Redis 8.10 reply limits,
`NoAck`, and Redis 8.4 `ClaimMinIdle`.

See [strings and keys](./commands/strings-and-keys), [collections](./commands/collections), and
[raw commands](./guides/raw-commands) for the full surface.

## Batches and transactions

Respire batch methods return `RespirePending<T>` instead of tasks, and drop the `Async` suffix:

```csharp
using var batch = redis.CreateBatch();
var name = batch.GetString("user:1:name");
var visits = batch.Increment("visits");
await batch.ExecuteAsync();

Console.WriteLine($"{name.Result}: {visits.Result}");
```

`AddCondition` has no direct equivalent. Watch keys with `CreateTransactionAsync`, read the
current state, and retry when `CommitAsync` returns `false`:

```csharp
bool applied;
do
{
    await using var watched = await redis.CreateTransactionAsync(["balance"]);
    long current = long.Parse(await redis.GetStringAsync("balance") ?? "0");
    watched.Set("balance", current - 100);
    applied = await watched.CommitAsync();
}
while (!applied);
```

If the client can read from replicas, read the input through `redis.WithReadFrom(RespireReadFrom.Primary)`.
`WATCH` cannot detect a replica value that was already stale before the watch started.

For single-key compare-and-set on Redis 8.4+, use `Strings.SetConditionalAsync` or
`Strings.DeleteConditionalAsync` with a `RespireValueCondition` instead. See
[batches and transactions](./guides/batches-and-transactions) and
[compare values before writing or deleting](./commands/strings-and-keys#compare-values-before-writing-or-deleting).

## Pub/sub

A Respire subscription is an async stream, not a handler or `ChannelMessageQueue`.
`SubscribeAsync` returns after the server acknowledges the subscription:

```csharp
await using var subscription = await redis.SubscribeAsync("orders", cancellationToken: token);

await foreach (var message in subscription.WithCancellation(token))
{
    if (message.Kind == RespireMessageKind.Gap)
    {
        // Messages may have been lost. Reload authoritative state before continuing.
        Console.Error.WriteLine($"Delivery gap: {message.Gap}");
        continue;
    }

    Console.WriteLine($"{message.Channel}: {message.Text}");
}
```

A `Gap` item has no channel or payload. `message.Text` decodes the payload as UTF-8. Use `message.Payload` or
`message.As<byte[]>()` for binary payloads. Disposing the subscription unsubscribes. See
[pub/sub](./guides/pub-sub#detecting-delivery-gaps).

## Locks, scripts, and key prefixes

| StackExchange.Redis | Respire |
| --- | --- |
| `LockTakeAsync(key, token, expiry)` | `Locks.TryTakeAsync(key, token, expiry)` |
| `LockExtendAsync` | `Locks.ResetExpiryAsync` |
| `LockReleaseAsync` | `Locks.ReleaseAsync` |
| `LockQueryAsync` | `Locks.GetOwnerTokenAsync` |
| — | `Locks.AcquireAsync(key, expiry, keepAlive: true)` for a managed handle that renews itself; keep-alive is opt-in |
| `LuaScript.Prepare(source)` | `RespireScript.Create(source)`; rewrite `@name` parameters as `KEYS[n]` and `ARGV[n]` |
| `ScriptEvaluateAsync(script, keys, values)` | `Scripts.ExecuteAsync(script, keys, args)` (`EVALSHA` with `EVAL` fallback) |
| `db.WithKeyPrefix("tenant:")` | `redis.WithKeyPrefix("tenant:")` |

Respire sends script source unchanged and has no named `@parameter` binding. Rewrite each `@key`
as `KEYS[n]` and each `@value` as `ARGV[n]`, then pass keys and arguments as arrays in that order.

See [distributed locks](./guides/distributed-locks) and
[Lua scripting](guides/scripting.md).

## Replicas, Cluster, and Sentinel

| StackExchange.Redis | Respire |
| --- | --- |
| `CommandFlags.PreferReplica` | `ReadFrom = RespireReadFrom.ReplicaPreferred`, or `redis.WithReadFrom(...)` |
| `CommandFlags.DemandReplica` | `RespireReadFrom.Replica` |
| Cluster endpoints in the connection string | `cluster=true`, or `UseCluster = true` |
| `serviceName=mymaster` | `serviceName=mymaster`, or `SentinelPrimaryName` |

Respire routes only read-only commands to replicas. See
[read from replicas](./fundamentals/connections#read-from-replicas) and
[Redis Sentinel](./fundamentals/connections#redis-sentinel).

## Exceptions

| StackExchange.Redis | Respire |
| --- | --- |
| `RedisConnectionException` | `RespireConnectionException` (`RespireAuthenticationException` for authentication failures) |
| `RedisTimeoutException` | `RespireTimeoutException` |
| `RedisServerException` | `RespireServerException` |
| `RedisException` | `RespireException` |

## Observability

Respire has no profiling-session API. It emits OpenTelemetry traces and metrics from an
`ActivitySource` and a `Meter`, both named `Respire`:

<!-- doc-test-ignore: Fragment of an OpenTelemetry builder configuration. -->
```csharp
tracing.AddSource("Respire");
metrics.AddMeter("Respire");
```

See [observability](./integrations/observability).
