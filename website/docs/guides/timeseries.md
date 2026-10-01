---
title: RedisTimeSeries
---

`Respire.TimeSeries` adds typed RedisTimeSeries commands to an existing Respire client. Use Redis 8, or Redis Stack, with the TimeSeries module enabled.

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

`CreateAsync` and `AlterAsync` set retention, chunk size, duplicate policy, IGNORE thresholds, and labels. Only `CreateAsync` sets encoding, and supplying labels to `AlterAsync` replaces every existing label. `AddAsync` creates the series on first write when needed and returns the assigned timestamp; its options also set `ON_DUPLICATE` for that write. `MultiAddAsync` writes key/timestamp/value triples to existing series. `IncrementByAsync` and `DecrementByAsync` update the latest sample.

## Reads

`GetAsync` returns the latest sample, or `null` for an empty series. `RangeAsync` and `ReverseRangeAsync` read one series. Their options support exact timestamp and value filters, `COUNT`, `LATEST`, and aggregation with `ALIGN`, `BUCKETTIMESTAMP`, and `EMPTY`.

`MultiGetAsync`, `MultiRangeAsync`, and `MultiReverseRangeAsync` select series through label filters such as `sensor=temperature`. They also accept `WITHLABELS` or `SELECTED_LABELS`, and the multi-series ranges accept `GROUPBY`/`REDUCE`. Single-series ranges reject these options locally. `QueryIndexAsync` returns matching keys as `RespireKey` values, preserving arbitrary key bytes. Multi-series results expose a display string in `Key` and the lossless key in `KeyValue`.

## Compaction rules and metadata

`CreateRuleAsync` creates a compaction rule from a source series into an existing destination series, with an optional alignment timestamp. `DeleteRuleAsync` removes the rule between a source and a destination. `GetInfoAsync` returns the raw `TS.INFO` response, which the caller must dispose.

## Key prefixes and Cluster

The package uses Respire's generated command infrastructure and does not use reflection. On a `WithKeyPrefix` view, every series key is prefixed, including both keys of a compaction rule and every key passed to `MultiAddAsync`. Label-filter queries (`MultiGetAsync`, `MultiRangeAsync`, `MultiReverseRangeAsync`, and `QueryIndexAsync`) name no keys and would return series outside the prefix. A prefixed view therefore rejects them with `NotSupportedException`.

In Redis Cluster, `MultiAddAsync` and `CreateRuleAsync` require all their keys to share a hash slot. Label-filter queries are sent to one node, and whether they cover every shard depends on the server's RedisTimeSeries cluster support. The caller owns the underlying client.
