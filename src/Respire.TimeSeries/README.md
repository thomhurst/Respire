# Respire.TimeSeries

Typed Redis time-series commands for samples, retention, labels, and aggregation.

## Install

```sh
dotnet add package Respire.TimeSeries
```

## Usage

```csharp
using Respire;
using Respire.TimeSeries;

await using var redis = await RespireClient.ConnectAsync("redis://localhost:6379");
var timeSeries = new RespireTimeSeriesClient(redis);
await timeSeries.CreateAsync("sensor:room-1", new RespireTimeSeriesOptions
{
    RetentionMilliseconds = 86_400_000,
});
var timestamp = await timeSeries.AddAsync("sensor:room-1", 1_725_000_000_000, 21.5);
var samples = await timeSeries.RangeAsync("sensor:room-1", new(0, timestamp));
```

Requires a server with Redis time-series support. Create the series once before adding samples.

[Read the guide](https://respire.tomlonghurst.dev/docs/guides/timeseries) for configuration, requirements, and further examples.
