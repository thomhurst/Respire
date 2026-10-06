# .NET Aspire

`Aspire.Respire` supports .NET 8 and .NET 10 applications. The sample AppHost and its
hosting tests use .NET 10 and Aspire 13.6. Register clients on an
`IHostApplicationBuilder`, including `WebApplicationBuilder` and `HostApplicationBuilder`:

```csharp
using Microsoft.Extensions.Hosting;
using Respire;

builder.AddRespireClient("cache");
builder.AddKeyedRespireClient("valkey");
```

The default singleton exposes both `IRespireClient` and `RespireClient`. The keyed
singleton uses the connection name as its key. Inject it with
`[FromKeyedServices("valkey")] IRespireClient`, or resolve it with
`GetRequiredKeyedService<IRespireClient>("valkey")`. Duplicate default registrations
or duplicate service keys throw. The host owns and disposes the clients.

## Hosting resources

Use the normal Aspire hosting integrations and references:

```csharp
var cache = builder.AddRedis("cache");
var valkey = builder.AddValkey("valkey");
builder.AddProject<Projects.Api>("api")
    .WithReference(cache).WaitFor(cache)
    .WithReference(valkey).WaitFor(valkey);
```

`Aspire.Hosting.Redis` and `Aspire.Hosting.Valkey` inject `ConnectionStrings__cache`
and `ConnectionStrings__valkey`. No hostnames, passwords, or client port settings
are needed in the consuming application. Azure Managed Redis hosting references
use the same named connection-string convention; Respire parses TLS, username,
password, and database settings from the supplied string.

## Configuration and callbacks

Integration settings live under `Aspire:Respire`, with per-client overrides under
`Aspire:Respire:<connectionName>`. Settings are applied in this order:

1. Global integration settings.
2. Named integration settings.
3. `ConnectionStrings:<connectionName>`.
4. `configureSettings`.
5. Parse the resulting connection string into `RespireOptions`.
6. Apply global `Aspire:Respire:Options`, then named `Aspire:Respire:<connectionName>:Options`.
7. Run `configureOptions` once when the singleton is resolved.

```json
{
  "Aspire": {
    "Respire": {
      "Options": { "CommandTimeout": "00:00:05", "Connections": 1 },
      "cache": {
        "DisableTracing": false,
        "Options": { "ClientName": "web", "ReconnectPolicy": { "MaxAttempts": 4 } }
      }
    }
  }
}
```

Options configuration covers scalar `RespireOptions` fields, endpoints, replica
endpoints, hedged reads, reconnect policy, client-side caching, and the scalar TLS
authentication settings. Endpoint arrays accept strings such as `"localhost:6379"`
or objects such as `{ "Host": "localhost", "Port": 6379 }`. Named arrays replace
global arrays. Client-side cache key prefixes are strings. Time spans use the
invariant .NET format; an empty string explicitly clears a nullable time span or
integer (for example, `CommandTimeout: ""` disables its cap).

Service objects, serializers, binary key prefixes, certificate objects, and TLS
callbacks are configured in code. Use the immutable options callback to preserve
parsed and bound values while resolving services:

```csharp
builder.AddRespireClient("cache", configureOptions: (services, options) => options with
{
    ClientName = "web",
    CredentialProvider = services.GetRequiredService<IRespireCredentialProvider>(),
});
```

A callback can supply endpoints when no connection string is configured. Resolving
a client with no endpoints, or returning null from the callback, throws a clear
configuration error. Registration and host construction do not connect to Redis.

## Health checks, logging, and OpenTelemetry

All four integrations are enabled by default. Configure `DisableHealthChecks`,
`DisableTracing`, `DisableMetrics`, or `DisableLogging` globally, per connection,
or in `configureSettings`:

```csharp
builder.AddRespireClient("cache", settings => settings.DisableTracing = true);
```

The health check is named `respire_<connectionName>` and carries the `ready` tag.
It reuses the selected client's existing connections. A client that has never
connected reports unhealthy; health checks do not create connections. Applications
requiring readiness immediately after startup should issue a bounded startup PING,
as the sample does.

Tracing adds activity source `Respire` to the host's OpenTelemetry provider.
Metrics add meter `Respire`. Configure exporters in the application, for example
`builder.Services.AddOpenTelemetry().UseOtlpExporter()` for the Aspire dashboard.
The integration does not change process-wide metric group selection. To export
command duration, configure `RespireMetricsOptions.Groups` to include
`RespireMetricGroups.Command` before registering providers or creating clients.

Tracing and metrics flags disable this integration's automatic provider wiring.
They do not suppress listeners registered elsewhere in the same process, and a
provider listening to `Respire` observes every client's telemetry. Logging uses the
host's logger factory by default. `DisableLogging` installs a null logger factory
for the selected client, including when options explicitly supply another factory.
Application code configures logging exporters separately.

## Cache companions

Install the companion package for each cache API you need:

| Package | Registration on an existing client builder |
| --- | --- |
| `Aspire.Respire.DistributedCaching` | `.AddDistributedCache(...)` |
| `Aspire.Respire.HybridCaching` | `.AddHybridCache(configureCache, configureHybrid)` |
| `Aspire.Respire.OutputCaching` | `.AddOutputCache(...)` |

```csharp
builder.AddRespireClientBuilder("cache")
    .AddHybridCache(options => options.InstanceName = "app:cache:")
    .AddOutputCache(options => options.InstanceName = "app:output:");
```

Use `AddKeyedRespireClientBuilder` to select a keyed client. The helpers reuse that
exact host-owned client. Disposing a distributed cache does not dispose the client.
HybridCache uses the distributed cache as its L2 backend. Output caching retains
the existing store's validation and hosted tag cleanup. `IDistributedCache`,
`HybridCache`, and `IOutputCacheStore` remain ordinary unkeyed application services;
choose one application backend for each. The base client package has no ASP.NET
Core framework dependency.

## Azure Managed Redis with Microsoft Entra

Add `Respire.Azure` and your chosen Azure credential package. Grant the identity
Redis access, and use its object ID as the Redis username:

```csharp
using Azure.Core;
using Azure.Identity;
using Respire.Azure;

builder.Services.AddSingleton<TokenCredential>(new DefaultAzureCredential());
builder.AddRespireClient("cache", configureOptions: (services, options) => options with
{
    CredentialProvider = new AzureManagedRedisCredentialProvider(
        services.GetRequiredService<TokenCredential>(),
        builder.Configuration["RedisIdentityObjectId"]!),
});
```

The hosting reference still supplies the endpoint and TLS settings. The existing
`Respire.Azure` provider supplies and refreshes Entra credentials; the credential
remains caller-owned. The local tests verify credential resolution and the Redis
token scope without provisioning Azure resources.

Aspire 13.6 emits `host:10000,ssl=true` for the Entra connection string, which
Respire accepts directly ([hosting source](https://github.com/microsoft/aspire/blob/56f3e9c0d216c0c7069dabb49dd0464e4827744f/src/Aspire.Hosting.Azure.Redis/AzureManagedRedisExtensions.cs)).

## Sample and acceptance test

From `samples/Respire.Samples.Aspire.AppHost`, run `aspire start --non-interactive`
(add `--isolated` in a worktree). Wait for `api` using `aspire wait api`, then open
its dashboard endpoint. The AppHost starts Redis and Valkey, passes both references,
and starts the API with PING, distributed cache, HybridCache, output cache, health,
and OTLP telemetry. Endpoints and dashboard ports are assigned dynamically.
Stop it with `aspire stop` when finished.

`tests/Aspire.Respire.IntegrationTests` starts this AppHost through
`Aspire.Hosting.Testing`, waits for health, verifies both injected clients and all
three HTTP caches, and disposes its resources. A container runtime is required.
`tests/Aspire.Respire.Tests` covers configuration, keyed registration, telemetry
flags, logging, health reuse, cache ownership, and Entra credentials on both client
target frameworks.
