# Respire.Aspire

Register Respire clients from Aspire connection strings, with health checks, logging, and OpenTelemetry.

## Install

```sh
dotnet add package Respire.Aspire
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

The readiness check connects a lazy client on its first probe, so readiness does not depend on application traffic. Connection setup and node probes share the two-second probe timeout, with up to 250 milliseconds of provider completion grace.

[Read the guide](https://respire.tomlonghurst.dev/docs/integrations/aspire) for configuration, requirements, and further examples.
