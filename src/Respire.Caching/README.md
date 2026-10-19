# Respire.Caching

Redis-backed IDistributedCache and IBufferDistributedCache implementations.

## Install

```sh
dotnet add package Respire.Caching
dotnet add package Microsoft.Extensions.DependencyInjection
```

## Usage

```csharp
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Respire.Caching;

var services = new ServiceCollection();
services.AddRespireDistributedCache("redis://localhost:6379", instanceName: "myapp:");
await using var provider = services.BuildServiceProvider();
var cache = provider.GetRequiredService<IDistributedCache>();
await cache.SetStringAsync("greeting", "hello");
Console.WriteLine(await cache.GetStringAsync("greeting"));
```

[Read the guide](https://respire.tomlonghurst.dev/docs/integrations/caching) for configuration, requirements, and further examples.
