# Respire.Caching.Hybrid

Use Respire as the Redis L2 backend for HybridCache, with optional server-assisted L1 coherence.

## Install

```sh
dotnet add package Respire.Caching.Hybrid
dotnet add package Microsoft.Extensions.DependencyInjection
```

## Usage

```csharp
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Respire.Caching.Hybrid;

var services = new ServiceCollection();
services.AddRespireHybridCache("redis://localhost:6379", instanceName: "myapp:");
await using var provider = services.BuildServiceProvider();
var cache = provider.GetRequiredService<HybridCache>();
var greeting = await cache.GetOrCreateAsync(
    "greeting", _ => ValueTask.FromResult("hello"));
```

L1 coherence is opt-in; see the guide for tracking, invalidation, and supported configurations.

[Read the guide](https://respire.tomlonghurst.dev/docs/integrations/caching) for configuration, requirements, and further examples.
