---
title: Coming from StackExchange.Redis
description: How Respire compares with StackExchange.Redis, and how to move existing code across.
---

# Coming from StackExchange.Redis

StackExchange.Redis is the established .NET Redis client. Respire covers the same core ground —
multiplexed connections, automatic pipelining, Cluster, Sentinel, pub/sub, transactions, and
scripting — with a different API shape and some capabilities StackExchange.Redis does not provide.
This page compares the two and maps common StackExchange.Redis code to Respire.

Comparisons describe StackExchange.Redis 3.3.1, the version used by Respire's comparison
benchmarks. Recheck the StackExchange.Redis claims on this page when that version changes.
The upstream [configuration](https://github.com/StackExchange/StackExchange.Redis/blob/3.3.1/docs/Configuration.md)
and [failover/retry documentation](https://github.com/StackExchange/StackExchange.Redis/blob/3.3.1/docs/Failover.md)
describe its RESP3 defaults, smart client handoffs, connection groups, and retry categories.

:::note Pre-release
Respire's public API may still change before a stable release. See the [roadmap](./roadmap).
:::

## Why switch

Respire gives you:

- [server-assisted client-side caching](./fundamentals/client-side-caching) for hot reads;
- [blocking commands](./guides/blocking-queues) such as `BLPOP`, `BLMOVE`, and `XREADGROUP BLOCK`
  without stalling other traffic;
- typed results (`string?`, `long`, `T?`) instead of a protocol value union;
- `IAsyncEnumerable` pub/sub, stream reads, and scans;
- an [in-memory test server](./guides/in-memory-testing) and built-in OpenTelemetry.

Uncached wire performance is comparable. See the [benchmarks](./benchmarks) and
[stress tests](./stress-tests) for current measurements against StackExchange.Redis.

## Feature comparison

| Capability | StackExchange.Redis | Respire |
| --- | --- | --- |
| Target frameworks | .NET Framework 4.6.1+, `netstandard2.0`, .NET 8+ | .NET 8 and .NET 10 |
| API style | Synchronous and asynchronous methods | Asynchronous only (`ValueTask`) |
| Command surface | Flat methods on `IDatabase` (`HashGet`, `ListLeftPush`) | Facets per data type (`redis.Hashes.GetStringAsync`) |
| Values | `RedisValue` union | Typed results; `RespireValue` for arguments |
| Automatic pipelining | Yes | Yes |
| Protocol | RESP3 attempted by default when `HELLO` is enabled and the configured server version is 6+; RESP2 fallback and explicit override | RESP3 preferred by default, with RESP2 fallback |
| Server-assisted client-side cache | No | Yes, opt-in |
| Blocking commands (`BLPOP`, `BLMOVE`, ...) | No typed APIs; they would stall the shared connection | Typed APIs on dedicated pooled connections |
| Pub/sub | Handlers or `ChannelMessageQueue` | `IAsyncEnumerable` subscriptions with delivery-gap markers |
| Sharded pub/sub (Redis 7) | Yes | Yes |
| Transactions | `AddCondition` checks | `WATCH`-based optimistic concurrency |
| Distributed locks | `LockTake`, `LockExtend`, `LockRelease` | Raw-token APIs plus managed lock handles with keep-alive |
| Read from replicas | `CommandFlags.PreferReplica` / `DemandReplica` per command | `ReadFrom` option or `WithReadFrom` view, with hedged and zone-aware reads |
| Fire-and-forget | `CommandFlags.FireAndForget` on any command | `ExecuteFireAndForgetAsync` for raw commands |
| Multiple databases | `GetDatabase(n)` per call | One database per client (`Database` option) |
| Health-checked failover between deployments | `ConnectGroupAsync` connection groups | [`RespireFailoverGroup`](./guides/failover-groups); read `ActiveClient` for each operation |
| Smart client handoffs | Opt-in maintenance notifications and endpoint handoff; maintenance notifications are disabled for connection-group members in 3.3.1 | Opt-in [maintenance notifications and handoff](./fundamentals/connections#maintenance-notifications); combining handoff with failover groups is tracked in [#893](https://github.com/thomhurst/Respire/issues/893) |
| Command retry policy | Async `WithRetry(...)` wrapper with `RetryPolicy` and `CommandRetry*` categories | Connection recovery and specific routing retries; a general per-command retry policy is tracked in [#862](https://github.com/thomhurst/Respire/issues/862) |
| Diagnostics | `RegisterProfiler` profiling sessions | OpenTelemetry `ActivitySource` and `Meter` |
| `IDistributedCache` | `Microsoft.Extensions.Caching.StackExchangeRedis` | [`Respire.Caching`](./integrations/caching); entries are interchangeable while `ValueCodec` is unset |
| Module commands | Raw `Execute` | Typed `Json`, `Search`, `TimeSeries`, and `Probabilistic` packages |
| Test double | None built in | [`Respire.Testing`](./guides/in-memory-testing) in-memory server |

## Behavior differences

These differences can change application behavior after a migration. Review them before you
switch.

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
  `CommandTimeout`.
- **Cancellation is supported.** Most methods accept a `CancellationToken`. Cancellation abandons
  the wait only; a command that was already written still runs.
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

Respire accepts most StackExchange.Redis connection strings, so existing configuration can stay
in place:

```csharp
await using var redis = await RespireClient.ConnectAsync(
    "cache-a:6380,password=secret,ssl=true,defaultDatabase=2");
```

Respire rejects unknown options with `ArgumentException` instead of ignoring them. Remove
StackExchange.Redis-only options such as `abortConnect` before you pass the string to Respire. See
[StackExchange.Redis connection strings](./fundamentals/connections#stackexchangeredis-connection-strings)
for the supported options. URIs such as `rediss://user:pass@cache-a:6380/2` and `RespireOptions`
also work.

| StackExchange.Redis | Respire |
| --- | --- |
| `ConnectionMultiplexer.ConnectAsync(config)` | `RespireClient.ConnectAsync(config)` |
| `ConnectionMultiplexer.Connect(config)` with `abortConnect=false` | `RespireClient.Create(config)` without `abortConnect` (connects lazily) |
| `multiplexer.GetDatabase()` | The client itself (`IRespireClient`) |
| `multiplexer.GetDatabase(2)` | A separate client with `Database = 2` |
| `multiplexer.GetSubscriber()` | `redis.SubscribeAsync` and `redis.PublishAsync` on the client |
| `multiplexer.GetServer(endpoint)` | No per-endpoint selection. `redis.Server` uses normal routing; `*OnAllNodesAsync` methods run on every node |
| `ConnectionFailed`, `ConnectionRestored` | `ConnectionStateChanged` |
| `multiplexer.IsConnected` | `redis.IsConnected` |
| `services.AddSingleton<IConnectionMultiplexer>(...)` | [`services.AddRespire(...)`](./integrations/dependency-injection) |

## Types

| StackExchange.Redis | Respire |
| --- | --- |
| `RedisKey` | `RespireKey` (implicit from `string`, `byte[]`, `ReadOnlyMemory<byte>`) |
| `RedisValue` (arguments) | `RespireValue` (implicit from strings, bytes, numbers, `bool`, `Guid`, and more) |
| `RedisValue` (results) | `string?`, `byte[]?`, `long`, `bool`, `double`, or `T?` |
| `RedisResult` | `RespireResult` — owns pooled memory, so dispose it |
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
| `StreamAddAsync`, `StreamReadGroupAsync` | `Streams.AddAsync`; `Streams.ReadGroupAsync` reads continuously (raw `XREADGROUP` for one batch) |
| `server.KeysAsync(pattern)` | `Keys.ScanAsync(pattern)` (`IAsyncEnumerable`; scans every primary in Cluster mode) |
| `StringGetLeaseAsync` | `Strings.GetLeaseAsync` |
| `ExecuteAsync("CMD", args)` | `ExecuteAsync("CMD", args)` or the generated `RespireCommands` catalog |
| `CommandFlags.FireAndForget` | `ExecuteFireAndForgetAsync` |

`RedisValue` results can hold arbitrary bytes. Respire's non-generic read methods return strings,
which decode UTF-8 and replace invalid byte sequences. This applies to strings, hashes, lists,
sets, sorted sets, and `Keys.ScanAsync`. For binary data, use `GetBytesAsync` or the generic
`<byte[]>` overloads, such as `GetAsync<byte[]>`, `Hashes.GetAllAsync<byte[]>`,
`Lists.RangeAsync<byte[]>`, `Sets.MembersAsync<byte[]>`, and `SortedSets.RangeAsync<byte[]>`.
Hash field names, stream field names, and scanned keys are always strings. Use raw `HGETALL`,
`XADD`, stream read, or `SCAN` commands when field names or keys are binary. In Cluster mode, a
raw `SCAN` reaches one node only, so it does not enumerate keys on every primary.

`StreamReadGroupAsync` returns one batch. `Streams.ReadGroupAsync` is a continuous consumer: it
keeps issuing blocking `XREADGROUP` calls on a dedicated connection and ends only when its
cancellation token is canceled. Use a raw `XREADGROUP` for a single batch, or for options such as
`NOACK`.

See [strings and keys](./commands/strings-and-keys), [collections](./commands/collections), and
[raw commands](./guides/raw-commands) for the full surface.

## Batches and transactions

StackExchange.Redis batch and transaction methods return tasks that complete when the batch
runs. Respire returns `RespirePending<T>`, and the batch methods drop the `Async` suffix:

```csharp
using var batch = redis.CreateBatch();
var name = batch.GetString("user:1:name");
var visits = batch.Increment("visits");
await batch.ExecuteAsync();

Console.WriteLine($"{name.Result}: {visits.Result}");
```

StackExchange.Redis `AddCondition` checks have no direct equivalent. Use `WATCH` through
`CreateTransactionAsync`, read the current state with the client, and retry when `CommitAsync`
returns `false`:

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

StackExchange.Redis delivers messages to a handler or a `ChannelMessageQueue`. Respire returns
a subscription that is an async stream. `SubscribeAsync` returns after the server acknowledges
the subscription:

```csharp
await using var subscription = await redis.SubscribeAsync("orders", token);

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

A `Gap` item means messages may have been lost during a reconnect or buffer overflow; it has no
channel or payload. `message.Text` decodes the payload as UTF-8. Use `message.Payload` or
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

Respire sends script source unchanged. It does not support StackExchange.Redis named `@parameter`
binding. Rewrite each `@key` reference as `KEYS[n]` and each `@value` reference as `ARGV[n]`, then
pass the keys and arguments as arrays in the same order.

See [distributed locks](./guides/distributed-locks) and
[Lua scripting](https://github.com/thomhurst/Respire/blob/main/docs/SCRIPTING.md).

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
