# Respire.FusionCache

A FusionCache backplane and distributed locker that share an existing Respire client.

## Install

```sh
dotnet add package Respire.FusionCache
```

## Usage

```csharp
using Microsoft.Extensions.DependencyInjection;
using Respire;
using Respire.FusionCache;
using ZiggyCreatures.Caching.Fusion;

await using var redis = await RespireClient.ConnectAsync("redis://localhost:6379");
var services = new ServiceCollection();
services.AddSingleton<IRespireClient>(redis);
services.AddFusionCache()
    .WithOptions(options => options.BackplaneChannelPrefix = "myapp:cache")
    .WithRespireBackplane()
    .WithRespireDistributedLocker();
await using var provider = services.BuildServiceProvider();
var cache = provider.GetRequiredService<IFusionCache>();
await cache.GetOrSetAsync("product:42", _ => Task.FromResult(42));
```

Add Respire.Caching and a FusionCache serializer for distributed L2 storage; see the shared-client setup in the guide. Dispose the service provider before the supplied client.

[Read the guide](https://thomhurst.github.io/Respire/docs/integrations/fusioncache) for configuration, requirements, and further examples.
