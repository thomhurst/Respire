# Respire.Probabilistic

Typed Bloom, Cuckoo, Count-Min Sketch, Top-K, and t-digest commands.

## Install

```sh
dotnet add package Respire.Probabilistic
```

## Usage

```csharp
using Respire;
using Respire.Probabilistic;

await using var redis = await RespireClient.ConnectAsync("redis://localhost:6379");
var probabilistic = new RespireProbabilisticClient(redis);
await probabilistic.BloomReserveAsync("seen:users", errorRate: 0.01, capacity: 100_000);
await probabilistic.BloomAddAsync("seen:users", "user:42");
bool maybeSeen = await probabilistic.BloomExistsAsync("seen:users", "user:42");
```

Requires the corresponding probabilistic commands on the server. Reserve the filter once; a positive membership result can be a false positive.

[Read the guide](https://respire.tomlonghurst.dev/docs/guides/probabilistic) for configuration, requirements, and further examples.
