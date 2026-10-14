# Aspire.Respire.HybridCaching

Use HybridCache with an Aspire-registered Respire client as its Redis L2 backend.

## Install

```sh
dotnet add package Aspire.Respire.HybridCaching
```

## Usage

```csharp
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder();
builder.Configuration["ConnectionStrings:cache"] = "redis://localhost:6379";
builder.AddRespireClientBuilder("cache").AddHybridCache();
await builder.Build().RunAsync();
```

Register this in the consuming service. An Aspire AppHost resource reference can supply ConnectionStrings:cache instead of the explicit local setting. The host owns the client.

[Read the guide](https://thomhurst.github.io/Respire/docs/integrations/aspire) for configuration, requirements, and further examples.
