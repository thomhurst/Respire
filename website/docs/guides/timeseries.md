---
title: Redis time series
---

`Respire.TimeSeries` provides typed Redis time series commands. Time series is built into Redis Open Source 8+; older deployments need Redis Stack or the RedisTimeSeries module.

```bash
dotnet add package Respire.TimeSeries
```

With C# 14 or later, import `Respire.TimeSeries` and use `client.TimeSeries` on `RespireClient` or `IRespireClient`. It returns one wrapper per client (a `WithKeyPrefix` view gets its own; see [Key prefixes and Cluster](#key-prefixes-and-cluster)), does no network I/O, and never disposes the client. On earlier C# versions, use `new RespireTimeSeriesClient(client)`.

On concrete clients, [logical command hooks](command-hooks.md#timeseries-result-owners)
include typed reply parsing and `GetRawInfoAsync` projections. A chunked `MultiAddAsync`
reports one completion after all chunks and any partial-progress exception translation.
`FollowAsync` reports each underlying `TS.READ` separately.

```csharp
using Respire;
using Respire.TimeSeries;

await using var client = await RespireClient.ConnectAsync("localhost:6379");
var timeSeries = client.TimeSeries;
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

- `CreateAsync` and `AlterAsync` set retention, chunk size, duplicate policy, IGNORE thresholds and labels. Only `CreateAsync` sets encoding; `AlterAsync` throws `ArgumentException` if `Encoding` is set.
- `AlterAsync` labels replace all existing labels; `ClearLabels = true` removes them all.
- `AddAsync` creates the series if needed and returns the assigned timestamp. `RespireTimeSeriesAddOptions` sets `ON_DUPLICATE` for that write.
- `IncrementByAsync` and `DecrementByAsync` update the latest sample. `RespireTimeSeriesIncrementOptions` sets an explicit `TIMESTAMP`, plus retention, encoding, chunk size, duplicate policy, IGNORE thresholds and labels for a series the call creates.

### Timestamps

Timestamps are validated before anything is sent. Writes take a non-negative millisecond timestamp or `RespireTimeSeriesTimestamp.Now`. Ranges, deletions and `Align` take a non-negative millisecond timestamp, `Minimum` or `Maximum`.

### Multi-series writes

`MultiAddAsync` writes `RespireTimeSeriesWrite` (key, timestamp, value) triples to existing series and returns timestamps in request order. If the server rejects some samples (for example, a missing series), it throws `RespireTimeSeriesMultiAddException`. Accepted samples are still written; the exception's `Timestamps` and `Errors` report each sample.

By default the whole batch is one unbounded `TS.MADD`, and Redis serves no other client until it finishes. For large batches, pass `maxBatchSize`, as in `MultiAddAsync(samples, maxBatchSize: 2_000)`. Respire then sends sequential `TS.MADD` chunks and still reports every sample in request order.

:::warning Chunked writes are not atomic
Other clients can run commands between chunks, and earlier chunks stay written if a later one fails or the call is cancelled. A lost reply does **not** prove a write failed. Respire does not replay uncertain chunks; do not replay them or the whole batch automatically yourself, because duplicate policies and server-assigned timestamps can change the result.
:::

After at least one confirmed chunk, an interrupted call throws:

| Exception | When |
| --- | --- |
| `RespireTimeSeriesMultiAddInterruptedException` | Transport, server or malformed-reply failure; original in `InnerException` |
| `RespireTimeSeriesMultiAddCanceledException` | Cancellation; an `OperationCanceledException` that keeps its token |

Failures before the first confirmed chunk, and programming or resource failures (such as client disposal), keep their original exception type. Per-sample rejections still accumulate and throw `RespireTimeSeriesMultiAddException` after every chunk finishes.

Both progress exceptions expose `Progress` (also available via `RespireTimeSeriesMultiAddProgress.FromException(error)`), which splits the request into three consecutive parts:

| Part | Meaning |
| --- | --- |
| `CompletedSampleCount` (across `CompletedChunkCount` chunks) | Fully decoded replies. `Timestamps` and `Errors` cover only this part, including per-sample rejections. |
| `UncertainSampleCount` | The attempted chunk without a complete reply. Conservative; zero if cancellation was detected before the next chunk. |
| `UnattemptedSampleCount` | Never attempted. |

## Client-side cache

With client-side caching enabled, each `AddAsync`, `IncrementByAsync`, `DecrementByAsync`, `DeleteRangeAsync` or `MultiAddAsync` call flushes the local cache, because a write can also update compaction destinations the command does not name. High-volume ingestion therefore gets no cache reuse between writes. `CreateRuleAsync` and `DeleteRuleAsync` invalidate their source and destination keys.

## Reads

- `GetAsync` returns the latest sample, or `null` for an empty series.
- `RangeAsync` and `ReverseRangeAsync` read one series, with exact timestamp and value filters, `COUNT`, `LATEST`, and aggregation with `ALIGN`, `BUCKETTIMESTAMP` and `EMPTY`.
- `MultiGetAsync`, `MultiRangeAsync` and `MultiReverseRangeAsync` select series by label filters such as `sensor=temperature`, and accept `WITHLABELS` or `SELECTED_LABELS`. Multi-series ranges also accept `GROUPBY`/`REDUCE`; single-series ranges reject these locally.
- `QueryIndexAsync` returns matching keys as `RespireKey`, preserving arbitrary bytes. Multi-series results expose the lossless key in `KeyValue`; `Key` is its display string.
- `RespireTimeSeriesAggregation.CountNaN` and `CountAll` require Redis 8.10 in any range method.

## Redis 8.10 reads

`RangeKeysAsync` (`TS.NRANGE`) and `ReverseRangeKeysAsync` (`TS.NREVRANGE`) combine named series into timestamp rows. `RespireTimeSeriesRow.Values` follows key order, including duplicates. Missing values are `double.NaN`, indistinguishable from stored NaN. All keys must share a Cluster hash slot; prefixes and binary keys are preserved.

```csharp
using Respire;
using Respire.TimeSeries;

await using var client = await RespireClient.ConnectAsync("localhost:6379");
var timeSeries = client.TimeSeries;
var rows = await timeSeries.RangeKeysAsync(
    ["{weather}:temperature", "{weather}:humidity"],
    new(0, RespireTimeSeriesTimestamp.Maximum),
    new RespireTimeSeriesKeyRangeOptions
    {
        Aggregators = [[RespireTimeSeriesAggregation.Min, RespireTimeSeriesAggregation.Max],
                      [RespireTimeSeriesAggregation.Avg]],
        BucketMilliseconds = 60_000,
    });
```

Each key contributes its aggregators in order, so each row here is temperature min, temperature max, humidity average. Options also support `LATEST`, exact timestamps, value bounds, `COUNT` (limits rows), alignment, bucket timestamps and empty buckets.

`QueryLabelsAsync(filters)` returns distinct label names and `QueryLabelValuesAsync(label, filters)` distinct values, in no defined order. Omit filters to query all indexed series. Both reject prefixed views and share the Cluster limits of other label queries.

`ReadAsync` (`TS.READ`) returns samples at or after an inclusive timestamp. `RespireTimeSeriesReadOptions` sets `MaximumCount` and optional `BlockMilliseconds`/`MinimumCount`; zero milliseconds waits indefinitely. A timeout returns available samples (possibly none); deletion while blocked returns an empty list. Blocking calls use the dedicated pool, so ordinary commands stay responsive.

```csharp
using Respire;
using Respire.TimeSeries;

await using var client = await RespireClient.ConnectAsync("localhost:6379");
var timeSeries = client.TimeSeries;
using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
await foreach (var sample in timeSeries.FollowAsync(
    "sensor:temperature", RespireTimeSeriesTimestamp.New,
    batchSize: 256, cancellationToken: cts.Token))
{
    Console.WriteLine($"{sample.Timestamp}: {sample.Value}");
}
```

`FollowAsync` blocks between samples and advances past the last emitted timestamp.

| Start | Reads |
| --- | --- |
| `Minimum` | All history |
| `Maximum` | The latest existing sample onward |
| `New` (`$`) | Future samples only. Resolved once, at start, to the timestamp after the latest sample (zero if none), so no samples are skipped later. |

- Backfills and updates below the cursor are not replayed.
- Empty replies trigger cancellable backoff from 100 ms to 1 second, reset when samples arrive.
- Cancellation stops the reader and releases its connection.
- Enumeration ends after `long.MaxValue`. Transport failures, including the initial read for `New`, propagate so you can choose a restart policy.
- To stop after repeated empty replies, use `FollowAsync(new RespireTimeSeriesFollowOptions { MaximumConsecutiveEmptyReads = 3 }, key, timestamp, cancellationToken)`. The count resets after any sample; omitted, it retries until cancellation. This is not an idle timeout: a quiet `BLOCK 0` read can wait indefinitely.

See Redis's [explicit-key range semantics](https://redis.io/docs/latest/commands/ts.nrange/) and [blocking read semantics](https://redis.io/docs/latest/commands/ts.read/).

## Compaction rules and metadata

`CreateRuleAsync` creates a compaction rule from a source into an existing destination series, with an optional alignment timestamp. The bucket is a `TimeSpan` or milliseconds: `CreateRuleAsync(source, destination, RespireTimeSeriesAggregation.Avg, 5)` makes 5 ms buckets; use `TimeSpan.FromMinutes(5)` for minutes. `DeleteRuleAsync` removes a rule.

`GetInfoAsync` returns `RespireTimeSeriesInfo`: sample count, first and last timestamps, retention, chunk settings, duplicate policy, labels, source key and rules. For other fields, `GetRawInfoAsync` reads raw `TS.INFO` or `TS.INFO DEBUG` through a projection, such as `GetRawInfoAsync(key, static info => info.Count)`. The response is disposed after the projection runs, so copy out what you need.

## Key prefixes and Cluster

:::warning Label-filter queries in Redis Cluster
`MultiGetAsync`, `MultiRangeAsync`, `MultiReverseRangeAsync`, `QueryIndexAsync`, `QueryLabelsAsync` and `QueryLabelValuesAsync` name no keys, so in Cluster each call goes to one node. Without server-side RedisTimeSeries cluster support, you see only that node's series. [Redis 8.4 documents partial results or duplicates](https://redis.io/docs/latest/develop/whats-new/8-4/#known-limitations) from `TS.MGET`, `TS.MRANGE`, `TS.MREVRANGE` and `TS.QUERYINDEX` during atomic slot migration; do not treat a reply as a complete snapshot then.
:::

- On a `WithKeyPrefix` view, every series key is prefixed, including both rule keys and every `MultiAddAsync` key.
- Label queries could expose data outside the prefix, so a prefixed view rejects them (even without filters) with `NotSupportedException`. Use an unprefixed client.
- In Cluster, `MultiAddAsync` and `CreateRuleAsync` keys must share a hash slot.
- The package uses no reflection.
