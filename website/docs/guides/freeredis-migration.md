---
title: Coming from FreeRedis
description: Map FreeRedis connections, commands, pipelines, subscriptions, and module APIs to Respire.
---

# Coming from FreeRedis

Move `FreeRedis.RedisClient` callers to a long-lived `RespireClient` and await commands throughout
the call chain. Respire has no synchronous command API. Keep the same Redis keys and wire values
during a gradual rollout; compare serialization and expiry behavior before switching writers.
Respire targets .NET 8 and .NET 10; applications on .NET Framework must retarget before migrating.

The FreeRedis column uses the API documented in its
[upstream README](https://github.com/2881099/FreeRedis/blob/b685beb08c4e547aa49e956aee5bf48c031fc411/README.md).
The examples below are native Respire code and compile in the documentation snippet harness.

## Concepts and common calls

Here `cli` is a FreeRedis client and `redis` is a Respire client or key-prefixed view.

| FreeRedis | Respire | Migration detail |
| --- | --- | --- |
| `new RedisClient(connectionString)` | `await RespireClient.ConnectAsync(options)` | Translate configuration; do not copy pool settings blindly. |
| `cli.GetDatabase(n)` | A client with `RespireOptions.Database = n` | Database selection is fixed for the client's lifetime. |
| `prefix=app:` | `client.WithKeyPrefix("app:")` | Pass logical keys without adding the prefix again. |
| `cli.Set(key, value)` | `await redis.Strings.SetAsync(key, value)` | A plain SET clears an existing expiry. |
| `cli.Get(key)` | `await redis.Strings.GetStringAsync(key)` | Missing strings return `null`. |
| `cli.IncrBy(key, 10)` | `await redis.Strings.IncrementAsync(key, 10)` | Returns the new integer value. |
| `cli.Del(key)` | `await redis.Keys.DeleteAsync(key)` | Returns the number deleted. |
| `cli.StartPipe()` / `pipe.EndPipe()` | `redis.CreateBatch()` / `await batch.ExecuteAsync()` | Retain typed pendings; read them only after execution. |
| `cli.Multi()` / `tran.Exec()` | `redis.CreateTransaction()` / `await transaction.CommitAsync()` | A transaction is atomic; a batch is not. |
| `cli.Subscribe(channel, ondata)` | `await redis.SubscribeAsync(channel)` | Replace the callback with `await foreach`; dispose the subscription. |
| `cli.Publish(channel, payload)` | `await redis.PublishAsync(channel, payload)` | Channels are separate from key prefixes. |
| `cli.FtSearch(...).Execute()` | `await redis.Search.SearchAsync(index, query)` | Install `Respire.Search` and import `using Respire.Search;`; `query` is a `RespireSearchQuery`. Translate the query and result projection; see the [complete module example](nredisstack-migration.md#search-json-and-time-series-together). |

## Connection, prefixes, and pipelining

```bash
dotnet add package Respire
```

This replaces a FreeRedis client configured with database 13 and the prefix `app:` on a
standalone server. Own the connection at application scope, rather than creating one per request.

```csharp
using Respire;

await using var client = await RespireClient.ConnectAsync(new RespireOptions
{
    Endpoints = [new("localhost", 6379)],
    Database = 13,
});
var redis = client.WithKeyPrefix("app:");

await redis.Strings.SetAsync("name", "Ada", expiry: TimeSpan.FromMinutes(1));
string? name = await redis.Strings.GetStringAsync("name");

await using var batch = redis.CreateBatch();
var visits = batch.Increment("visits", 10);
var storedName = batch.GetString("name");
await batch.ExecuteAsync();
Console.WriteLine($"{storedName.Result}: {visits.Result}");
```

Concurrent individual commands are automatically pipelined. Explicit batches return typed
`RespirePending<T>` values rather than an `object[]`; awaiting a pending before executing the
batch throws. For `Multi` / `Exec` callers, use the same queue methods on a transaction and
commit it, as shown in the [CSRedisCore transaction example](csredis-migration.md#transactions).

## Replace subscription callbacks

The subscription is active when `SubscribeAsync` completes. This bounded example publishes one
message and stops after receiving it; a service should use its shutdown token for the consumer.

```csharp
using Respire;

await using var redis = await RespireClient.ConnectAsync("localhost:6379");
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
await using var subscription = await redis.SubscribeAsync(
    "migration-events", cancellationToken: timeout.Token);
await redis.PublishAsync("migration-events", "hello", timeout.Token);

await foreach (var message in subscription.WithCancellation(timeout.Token))
{
    if (message.Kind == RespireMessageKind.Gap)
    {
        Console.Error.WriteLine($"Reload authoritative state after a delivery gap: {message.Gap}");
        continue;
    }
    Console.WriteLine(message.Text);
    break;
}
```

## Differences to check before switching

- **Configuration:** FreeRedis pool sizes, send/receive timeouts, retries, and database leases
  do not map one for one to a multiplexed Respire connection. Use
  [connection options](../fundamentals/connections.md), `CommandTimeout`, and an explicit
  [retry policy](command-retries.md). Configure Cluster or Sentinel deliberately; Cluster uses
  database 0. Review replica-read policy rather than assuming the old constructor's routing.
- **Serialization:** FreeRedis's `Serialize` / `Deserialize` delegates are not Respire options.
  Keep pre-serialized values as strings or bytes, or configure a matching
  [serializer](../fundamentals/values-and-serialization.md). Test old payloads, nulls, missing
  keys, and non-UTF-8 values before adopting typed reads. `GetAsync<int>` returns the default for
  a missing key; `TryGetAsync<T>` distinguishes absence from a stored default.
- **Pub/sub:** subscription disposal is asynchronous; use `await using`. A slow consumer can
  overflow its buffer and receive a gap marker. Pub/sub does not provide durable delivery.
- **Modules:** FreeRedis Search builders and document-repository attributes are not compatible
  with Respire's types. Rebuild mappings with [Search](search.md) or
  [generated schemas](generated-search-schemas.md). Use `Respire.Json`, `Respire.TimeSeries`, and
  `Respire.Probabilistic` for their respective server commands; see the
  [module migration example](nredisstack-migration.md#search-json-and-time-series-together).
  Installing a client package does not add server capabilities.
- **Higher-level helpers:** `SubscribeList`, `SubscribeStream`, and delay queues need a behavior
  review, including acknowledgement and retry rules. Start with [blocking queues](blocking-queues.md),
  [stream workers](stream-workers.md), or [job queues](job-queues.md); do not assume their stored
  metadata is interchangeable. Client-side caching also needs its own
  [Respire configuration](../fundamentals/client-side-caching.md).

For atomic operations and optimistic concurrency, see [batches and transactions](batches-and-transactions.md).
