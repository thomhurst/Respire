# Respire.DependencyInjection

Register shared Respire clients with .NET dependency injection.

## Install

```sh
dotnet add package Respire.DependencyInjection
dotnet add package Microsoft.Extensions.DependencyInjection
```

## Usage

```csharp
using Microsoft.Extensions.DependencyInjection;
using Respire;
using Respire.DependencyInjection;

var services = new ServiceCollection();
services.AddRespire("redis://localhost:6379");
await using var provider = services.BuildServiceProvider();
var redis = provider.GetRequiredService<IRespireClient>();
await redis.SetAsync("greeting", "hello");
```

The service provider owns clients it creates. Reuse the registered client rather than creating one per request.

[Read the guide](https://thomhurst.github.io/Respire/docs/integrations/dependency-injection) for configuration, requirements, and further examples.
