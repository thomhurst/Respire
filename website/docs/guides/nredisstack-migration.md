---
title: Coming from NRedisStack
description: Move NRedisStack search, JSON, and time series calls to Respire's typed packages.
---

# Coming from NRedisStack

NRedisStack adds module commands to StackExchange.Redis. Replace its connection and ordinary
commands using the [StackExchange.Redis migration guide](../stackexchange-redis.md), then port
each module call to the corresponding Respire package. Existing Redis data and indexes can
stay on the same server; changing the client does not install server modules.

The source API names below follow the upstream
[NRedisStack documentation](https://github.com/redis/NRedisStack/blob/d363aa662b567df22e3f1a0c8900903c402d9fb3/README.md).
They are a manual migration map, not a promise that the StackExchange.Redis adapter or code
fix supports NRedisStack module wrappers.

## Concepts and common calls

In this table, `db` is an NRedisStack application's StackExchange.Redis `IDatabase`, `ft`,
`json`, and `ts` come from `db.FT()`, `db.JSON()`, and `db.TS()`, and `redis` is a Respire client.
Source calls use the synchronous spelling; port asynchronous callers to the same Respire methods.

| NRedisStack / StackExchange.Redis | Respire | Migration detail |
| --- | --- | --- |
| `ConnectionMultiplexer.Connect(...)`, then `GetDatabase(n)` | `await RespireClient.ConnectAsync(options)` | Set `options.Database = n`; keep one long-lived client per database. |
| `db.StringSet(key, value)` | `await redis.Strings.SetAsync(key, value)` | Use explicit serialization for object values. |
| `db.StringGet(key)` | `await redis.Strings.GetStringAsync(key)` | Returns `string?`, with `null` for a missing key. |
| `db.FT()` | `redis.Search` (`Respire.Search`) | Index names are not keys; set document prefixes on the index. |
| `ft.Create(index, parameters, schema)` | `await redis.Search.CreateIndexAsync(index, definition)` | Translate fields, aliases, data type, and prefixes explicitly. |
| `ft.Search(index, query)` | `await redis.Search.SearchAsync(index, query)` | Build a `RespireSearchQuery`; result types differ. |
| `db.JSON()` | `redis.Json` (`Respire.Json`) | Choose typed metadata or pre-serialized JSON. |
| `json.Set(key, "$", value)` | `await redis.Json.SetAsync(key, value, typeInfo, RespireJsonPath.JsonPathRoot)` | Supply `JsonTypeInfo<T>` for the value. |
| `json.Get(key, path: "$")` | `await redis.Json.GetJsonAsync(key, options)` | Set `options.Paths = [RespireJsonPath.JsonPathRoot]` to retain the JSONPath response shape. |
| `db.TS()` | `redis.TimeSeries` (`Respire.TimeSeries`) | Use typed timestamp, range, and retention options. |
| `ts.Add(key, timestamp, value)` | `await redis.TimeSeries.AddAsync(key, timestamp, value)` | Timestamps are milliseconds, not seconds. |
| `ts.Range(key, from, to)` | `await redis.TimeSeries.RangeAsync(key, new(from, to))` | Read the returned `Samples`. |
| `multiplexer.GetSubscriber().Subscribe(...)` | `await redis.SubscribeAsync(channel)` | Consume an async stream and handle delivery-gap messages. |
| `db.CreateBatch()` / `db.CreateTransaction()` | `redis.CreateBatch()` / `redis.CreateTransaction()` | Queue members, then await `ExecuteAsync()` / `CommitAsync()` before reading results. |

## Search, JSON, and time series together

Install the packages for the modules you use:

```bash
dotnet add package Respire
dotnet add package Respire.Search
dotnet add package Respire.Json
dotnet add package Respire.TimeSeries
```

This complete Respire example uses raw JSON text to keep the stored document shape explicit.
Run it on a server with JSON, Search, and TimeSeries support and an unused `migration-products`
index name. For an existing index, skip creation and keep its existing schema and name.
The initial scan of existing documents runs in the background, so the example waits for
`FT.INFO` to report indexing complete, with a 30-second deadline, before searching.

```csharp
using Respire;
using Respire.Json;
using Respire.Search;
using Respire.TimeSeries;

await using var redis = await RespireClient.ConnectAsync("localhost:6379");

await redis.Json.SetJsonAsync("product:42", "{\"name\":\"tea\",\"price\":3.5}",
    RespireJsonPath.JsonPathRoot);
string? document = await redis.Json.GetJsonAsync("product:42", new RespireJsonGetOptions
{
    Paths = [RespireJsonPath.JsonPathRoot],
});

await redis.Search.CreateIndexAsync("migration-products", new RespireSearchIndexDefinition
{
    Source = RespireSearchSource.Json,
    Prefixes = ["product:"],
    Fields = [new("$.name", RespireSearchFieldType.Text) { Alias = "name" }],
});
using var indexingDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
while ((await redis.Search.GetIndexInfoAsync("migration-products", indexingDeadline.Token))
    .Properties["indexing"].Scalar != "0")
{
    await Task.Delay(50, indexingDeadline.Token);
}
var matches = await redis.Search.SearchAsync("migration-products", new RespireSearchQuery(
    RespireSearchExpression.FromRaw("@name:tea"),
    new RespireSearchQueryOptions { Dialect = 2, Limit = (0, 10) }));

long timestamp = await redis.TimeSeries.AddAsync(
    "temperature:room-1", RespireTimeSeriesTimestamp.Now, 21.5);
var samples = await redis.TimeSeries.RangeAsync("temperature:room-1", new(timestamp, timestamp));
Console.WriteLine($"{document}; {matches.Total} search matches; {samples.Samples.Count} time series samples");
```

The extension properties require C# 14. Earlier language versions can construct
`RespireSearchClient`, `RespireJsonClient`, and `RespireTimeSeriesClient` with the same client.
These wrappers do not own the connection.

## Differences to check before switching

- **Search dialect and results:** NRedisStack's documented default from 1.0.0-beta1 is dialect 2.
  Set `Dialect = 2` explicitly when preserving that behavior, including aggregation options.
  Respire's ordinary queries omit `DIALECT` when unset. Port result projections, paging, aliases,
  and aggregation reducers rather than casting NRedisStack result objects.
- **JSON paths and serialization:** Respire defaults to the legacy root `.`; `$` selects JSONPath
  and can produce multiple matches. `GetAsync<T>` returns a result with `Found` and `Value` and
  rejects multiple matches; use `GetManyAsync<T>` for those. Preserve property names and converter
  settings when moving to explicit `System.Text.Json` metadata. A RedisJSON document and a Redis
  string containing JSON are different Redis data types.
- **Time series:** preserve retention, duplicate policy, labels, aggregation alignment, and
  timestamp units. Use `RespireTimeSeriesTimestamp.Now` for `*`, and `Minimum` / `Maximum` for
  unbounded range endpoints. Do not replace these with negative integer timestamps.
- **Transactions:** the core queued facets do not queue typed module wrappers, and the
  [deferred raw-command API](deferred-raw-commands.md) rejects module commands. Keep module
  transactions on the existing client until you can replace the entire atomic operation with
  a supported design; separate awaited module calls do not preserve transaction atomicity.
  Core transactions need all keys in one Cluster slot and do not roll back runtime errors.
- **Pub/sub and cancellation:** replace callbacks with an owned subscription and an async consumer;
  handle `RespireMessageKind.Gap` after reconnect or overflow. Cancellation stops the caller's
  wait but cannot undo a command already sent.

See the [Search](search.md), [JSON](json.md), [time series](timeseries.md),
[pub/sub](pub-sub.md), and [transaction](batches-and-transactions.md) guides for complete APIs.
