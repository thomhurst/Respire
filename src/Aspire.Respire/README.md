# Aspire.Respire

Register Respire clients from Aspire connection strings, with health checks, logging, and OpenTelemetry.

## Install

```sh
dotnet add package Aspire.Respire
dotnet add package Microsoft.Extensions.Hosting
```

## Usage

```csharp
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder();
builder.Configuration["ConnectionStrings:cache"] = "redis://localhost:6379";
builder.AddRespireClient("cache");
await builder.Build().RunAsync();
```

Register this in the consuming service. An Aspire AppHost resource reference can supply ConnectionStrings:cache instead of the explicit local setting. The host owns the client.

[Read the guide](https://respire.tomlonghurst.dev/docs/integrations/aspire) for configuration, requirements, and further examples.
