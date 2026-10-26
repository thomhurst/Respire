# Respire.Aspire.OutputCaching

Use ASP.NET Core output caching with an Aspire-registered Respire client.

## Install

```sh
dotnet add package Respire.Aspire.OutputCaching
```

## Usage

```csharp
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder();
builder.Configuration["ConnectionStrings:cache"] = "redis://localhost:6379";
builder.AddRespireClientBuilder("cache").AddOutputCache();
await builder.Build().RunAsync();
```

Register this in the consuming service. An Aspire AppHost resource reference can supply ConnectionStrings:cache instead of the explicit local setting. The host owns the client. In a web application, also call UseOutputCache and apply CacheOutput to endpoints.

[Read the guide](https://respire.tomlonghurst.dev/docs/integrations/aspire) for configuration, requirements, and further examples.
