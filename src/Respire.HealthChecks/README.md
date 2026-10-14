# Respire.HealthChecks

ASP.NET Core health checks that probe an existing Respire client connection.

## Install

```sh
dotnet add package Respire.HealthChecks
dotnet add package Respire.DependencyInjection
```

## Usage

Use this example in an ASP.NET Core project created with `dotnet new web`.

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Respire.DependencyInjection;
using Respire.HealthChecks;

var builder = WebApplication.CreateBuilder();
builder.Services.AddRespire("redis://localhost:6379");
builder.Services.AddHealthChecks().AddRespire(new RespireHealthCheckOptions
{
    ProbeTimeout = TimeSpan.FromSeconds(2),
}, tags: ["ready"]);
var app = builder.Build();
app.MapHealthChecks("/health/ready");
app.Run();
```

The check opens no connections; a lazy client that has never connected reports unhealthy. Connect or send a bounded PING during startup when initial readiness is required.

[Read the guide](https://thomhurst.github.io/Respire/docs/integrations/health-checks) for configuration, requirements, and further examples.
