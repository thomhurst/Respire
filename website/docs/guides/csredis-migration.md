---
title: Coming from CSRedisCore
description: Migrate CSRedisClient and RedisHelper callers while preserving keys, payloads, and transaction behavior.
---

# Coming from CSRedisCore

This guide covers the **CSRedisCore** NuGet package (`CSRedis.CSRedisClient` and `RedisHelper`),
using the APIs in its [upstream documentation](https://github.com/2881099/csredis/blob/7066ef789b59a358111c80bc0f3c8a961be2aef1/README.md).
Replace static helper calls with a shared Respire client, preferably injected as `IRespireClient`.
Use [dependency injection](../integrations/dependency-injection.md) to manage application lifetime.
Respire targets .NET 8 and .NET 10; retarget .NET Framework applications before replacing the client.

## Concepts and common calls

Here `redis` is a Respire client or a view with the old application's key prefix.
`RedisHelper` calls also have corresponding instance operations on `CSRedisClient`.

| CSRedisCore | Respire | Migration detail |
| --- | --- | --- |
| `new CSRedisClient(connectionString)` and `RedisHelper.Initialization(client)` | `await RespireClient.ConnectAsync(options)` | Share the client; there is no required global initialization. |
| `defaultDatabase=13` | `RespireOptions.Database = 13` | Use a separate client for each database; Cluster uses database 0. |
| `prefix=my_` | `client.WithKeyPrefix("my_")` | Preserve the exact prefix and pass unprefixed logical keys. |
| `RedisHelper.Set("key", "value", 60)` | `await redis.Strings.SetAsync("key", "value", expiry: TimeSpan.FromSeconds(60))` | Convert the seconds argument to an explicit duration. |
| `RedisHelper.Get(key)` | `await redis.Strings.GetStringAsync(key)` | Returns `null` for missing keys. |
| `RedisHelper.IncrBy(key, 1)` | `await redis.Strings.IncrementAsync(key, 1)` | Returns the new integer value. |
| `RedisHelper.Del(key)` | `await redis.Keys.DeleteAsync(key)` | Returns the number deleted. |
| `RedisHelper.StartPipe(p => p.Set("a", "1").Get("a"))` | `redis.CreateBatch()`, queue, then `await batch.ExecuteAsync()` | Read typed pendings after execution instead of indexing an `object[]`. |
| `RedisHelper.Subscribe((channel, handler))` | `await redis.SubscribeAsync(channel)` | Consume an async stream; see the [complete example](freeredis-migration.md#replace-subscription-callbacks). |
| `RedisHelper.PSubscribe(patterns, handler)` | `await redis.SubscribePatternAsync(pattern)` | Use pattern subscriptions explicitly. |
| `RedisHelper.Publish(channel, payload)` | `await redis.PublishAsync(channel, payload)` | Verify channel naming separately from key prefixes. |

## Preserve connection and value behavior

```bash
dotnet add package Respire
```

This standalone example keeps the database, prefix, string value, and 60-second TTL of a
`RedisHelper.Set` call. Supply credentials through your application's configuration when needed.

```csharp
using Respire;

await using var client = await RespireClient.ConnectAsync(new RespireOptions
{
    Endpoints = [new("localhost", 6379)],
    Database = 13,
});
var redis = client.WithKeyPrefix("my_");

await redis.Strings.SetAsync("test1", "123123", expiry: TimeSpan.FromSeconds(60));
string? value = await redis.Strings.GetStringAsync("test1");
Console.WriteLine(value ?? "missing");
```

Translate connection settings using the [connection guide](../fundamentals/connections.md).
`poolsize`, `preheat`, `asyncPipeline`, and `testcluster` are not settings to carry over unchanged.
Respire pipelines concurrent calls automatically and requires explicit topology configuration.
CSRedisCore client-side partitioning across independent servers is not Redis Cluster: preserve
the old key-to-server placement in an application routing layer or migrate the data first.

## Transactions

A CSRedisCore `StartPipe` pipeline is not a transaction. Use a Respire batch for pipelining;
use `CreateTransaction()` when porting code that actually uses Redis `MULTI` / `EXEC`, including
low-level CSRedis transaction callers. Queue methods return pendings and do not send immediately.

```csharp
using Respire;

await using var redis = await RespireClient.ConnectAsync("localhost:6379");
await using var transaction = redis.CreateTransaction();
var stored = transaction.Strings.Set("{account:42}:status", "active");
var count = transaction.Increment("{account:42}:updates");
await transaction.CommitAsync();
Console.WriteLine($"Stored: {stored.Result}; updates: {count.Result}");
```

The hash tag keeps these keys in one Cluster slot. Read pendings only after committing.
Redis transactions do not roll back commands that succeeded when another queued command has a
runtime error. For read/check/write logic, use a watched transaction and handle a `false` commit
result; see [optimistic concurrency](batches-and-transactions.md).

## Other differences to check

- **Async and cancellation:** replace synchronous calls throughout the call chain. Avoid
  `.Result` and `.Wait()`. A cancellation or timeout cannot undo a write already sent to Redis.
- **Serialization and cache helpers:** compare stored bytes before moving generic object reads
  to Respire's [typed serialization](../fundamentals/values-and-serialization.md).
  `RedisHelper.CacheShell` is a higher-level cache-aside helper, not just GET/SET; review factory
  concurrency, TTL, null caching, and hash-field behavior when replacing it. Do not assume
  `Caching.CSRedis` entries are compatible with [Respire.Caching](../integrations/caching.md);
  verify the entry format or use a new cache prefix and let old entries expire.
- **Subscriptions:** retain and asynchronously dispose the subscription, consume messages with
  a shutdown token, and handle `RespireMessageKind.Gap`. CSRedis message envelopes and Respire
  messages are different types; port access to payload, channel, and pattern explicitly.
- **Modules and raw commands:** replace custom module calls with [Respire.Search](search.md),
  [Respire.Json](json.md), or [Respire.TimeSeries](timeseries.md), following the
  [NRedisStack module mappings](nredisstack-migration.md). For commands without a typed wrapper,
  use [raw commands](raw-commands.md), check their routing rules, and review RESP2/RESP3 reply shapes and
  result ownership. Existing server modules and index schemas still need to match the commands.
- **Migration tooling:** the StackExchange.Redis code fix and adapter do not translate
  `CSRedisClient` or `RedisHelper`. Move one application boundary at a time and test old and new
  readers against the same payload fixtures before switching writers.
