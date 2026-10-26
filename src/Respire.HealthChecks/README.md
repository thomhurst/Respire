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
    ConnectIfDisconnected = true,
    ProbeTimeout = TimeSpan.FromSeconds(2),
}, tags: ["ready"]);
var app = builder.Build();
app.MapHealthChecks("/health/ready");
app.Run();
```

`ConnectIfDisconnected = true` lets the check send a normal PING to establish a lazy client's first connection within `ProbeTimeout`, so readiness does not depend on application traffic. Connection setup and node probes share that timeout; probe providers retain up to 250 milliseconds of completion grace. The shared client remains owned by DI.

The default is `false`: checks reuse existing connections, and a lazy client that has never connected reports unhealthy until application work connects it.

[Read the guide](https://respire.tomlonghurst.dev/docs/integrations/health-checks) for configuration, requirements, and further examples.
