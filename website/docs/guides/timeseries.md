---
title: RedisTimeSeries
---

The `Respire.TimeSeries` package (namespace `Respire.Extensions.TimeSeries`) adds typed RedisTimeSeries commands to an existing Respire client. Use Redis 8, or Redis Stack, with the TimeSeries module enabled.

```bash
dotnet add package Respire.TimeSeries
```

```csharp
using Respire.Extensions.TimeSeries;

await using var client = await RespireClient.ConnectAsync("localhost:6379");
var timeSeries = new RespireTimeSeriesClient(client);
RespireKey key = "sensor:room-1";

await timeSeries.CreateAsync(key, new RespireTimeSeriesOptions
{
    RetentionMilliseconds = 86_400_000,
    Labels = new Dictionary<string, string> { ["sensor"] = "temperature", ["room"] = "1" },
});

var timestamp = await timeSeries.AddAsync(key, 1_725_000_000_000, 21.5);
var recent = await timeSeries.RangeAsync(key, new(1_725_000_000_000, timestamp));
var matching = await timeSeries.MultiRangeAsync(
    new(RespireTimeSeriesTimestamp.Minimum, RespireTimeSeriesTimestamp.Maximum),
    new RespireTimeSeriesRangeOptions
    {
        Filters = ["sensor=temperature"],
        WithLabels = true,
        Aggregation = (RespireTimeSeriesAggregation.Avg, 60_000),
    });

Console.WriteLine($"Read {recent.Samples.Count} samples from {key}.");
```

## Writes

`CreateAsync` and `AlterAsync` set retention, chunk size, duplicate policy, IGNORE thresholds, and labels. Only `CreateAsync` sets encoding, and supplying labels to `AlterAsync` replaces every existing label. `AddAsync` creates the series on first write when needed and returns the assigned timestamp; its options also set `ON_DUPLICATE` for that write. `MultiAddAsync` writes key/timestamp/value triples to existing series and returns the timestamps in request order. If the server rejects some samples, for example because a series does not exist, it throws `RespireTimeSeriesMultiAddException`. The accepted samples are still written, and the exception's `Timestamps` and `Errors` report the outcome of each sample. The whole batch goes to Redis as one `TS.MADD` command, and Respire does not limit its size. Redis finishes the command before it serves other clients, so split very large batches, for example into chunks of a few thousand samples. `IncrementByAsync` and `DecrementByAsync` update the latest sample. Their `RespireTimeSeriesIncrementOptions` set an explicit `TIMESTAMP`, and the retention, encoding, chunk size, duplicate policy, IGNORE thresholds, and labels of a series the call creates.

Timestamps are checked before anything is sent. Writes take a non-negative millisecond timestamp or `RespireTimeSeriesTimestamp.Now`. Ranges, deletions, and `Align` take a non-negative millisecond timestamp, `Minimum`, or `Maximum`.

## Reads

`GetAsync` returns the latest sample, or `null` for an empty series. `RangeAsync` and `ReverseRangeAsync` read one series. Their options support exact timestamp and value filters, `COUNT`, `LATEST`, and aggregation with `ALIGN`, `BUCKETTIMESTAMP`, and `EMPTY`.

`MultiGetAsync`, `MultiRangeAsync`, and `MultiReverseRangeAsync` select series through label filters such as `sensor=temperature`. They also accept `WITHLABELS` or `SELECTED_LABELS`, and the multi-series ranges accept `GROUPBY`/`REDUCE`. Single-series ranges reject these options locally. `QueryIndexAsync` returns matching keys as `RespireKey` values, preserving arbitrary key bytes. Multi-series results expose the lossless key in `KeyValue`, and `Key` is its display string.

## Compaction rules and metadata

`CreateRuleAsync` creates a compaction rule from a source series into an existing destination series, with an optional alignment timestamp. The bucket duration is given in milliseconds or as a `TimeSpan`. `DeleteRuleAsync` removes the rule between a source and a destination.

`GetInfoAsync` returns a typed `RespireTimeSeriesInfo` with the sample count, first and last timestamps, retention, chunk settings, duplicate policy, labels, source key, and compaction rules. `GetRawInfoAsync` reads the raw `TS.INFO` or `TS.INFO DEBUG` response for fields the typed model does not cover. Pass a projection, such as `GetRawInfoAsync(key, static info => info.Count)`, and Respire disposes the response after the projection runs. The overload without a projection returns the response itself, and the caller must dispose it.

## Key prefixes and Cluster

:::warning Label-filter queries in Redis Cluster
`MultiGetAsync`, `MultiRangeAsync`, `MultiReverseRangeAsync`, and `QueryIndexAsync` name no keys, so in Redis Cluster Respire sends each call to one node. Whether the result covers every shard depends on the server's RedisTimeSeries cluster support. Without that support, you only see the series stored on the node that answered.
:::

The package uses Respire's generated command infrastructure and does not use reflection. On a `WithKeyPrefix` view, every series key is prefixed, including both keys of a compaction rule and every key passed to `MultiAddAsync`. Label-filter queries (`MultiGetAsync`, `MultiRangeAsync`, `MultiReverseRangeAsync`, and `QueryIndexAsync`) name no keys and would return series outside the prefix. A prefixed view therefore rejects them with `NotSupportedException`. Run them through an unprefixed client instead.

In Redis Cluster, `MultiAddAsync` and `CreateRuleAsync` require all their keys to share a hash slot. The caller owns the underlying client.
