---
title: .NET Aspire
description: Register Respire clients from Aspire connection strings, with configuration, health checks, telemetry, and cache companions.
---

# .NET Aspire

`Aspire.Respire` registers Respire clients from the connection strings that Aspire hosting
references inject. It also wires health checks, OpenTelemetry tracing and metrics, and host
logging. Use it in a service that an Aspire AppHost starts with a Redis, Valkey, or Azure
Managed Redis reference.

The client integration supports .NET 8 and .NET 10 applications. For a runnable AppHost
with Redis, Valkey, HTTP caches, and dashboard telemetry, see the
[.NET Aspire sample](aspire-sample.md).

## Install

```bash
dotnet add package Aspire.Respire
```

## Register a client

Register clients on an `IHostApplicationBuilder`, including `WebApplicationBuilder` and
`HostApplicationBuilder`:

```csharp
using Microsoft.Extensions.Hosting;
using Respire;

builder.AddRespireClient("cache");
builder.AddKeyedRespireClient("valkey");
```

The default singleton exposes both `IRespireClient` and `RespireClient`. The keyed singleton
uses the connection name as its key. Inject it with
`[FromKeyedServices("valkey")] IRespireClient`, or resolve it with
`GetRequiredKeyedService<IRespireClient>("valkey")`. A second default registration, or a
duplicate service key, throws. The host owns and disposes the clients.

Registration and host construction do not connect to Redis. The client connects on its
first command.

## Hosting resources

In the AppHost, use the normal Aspire hosting integrations and references:

<!-- doc-test-ignore: AppHost-only fragment requires generated Projects.Api metadata and Aspire hosting packages; the equivalent sample is built and run by Aspire.Respire.IntegrationTests. -->
```csharp
var cache = builder.AddRedis("cache");
var valkey = builder.AddValkey("valkey");
builder.AddProject<Projects.Api>("api")
    .WithReference(cache).WaitFor(cache)
    .WithReference(valkey).WaitFor(valkey);
```

`Aspire.Hosting.Redis` and `Aspire.Hosting.Valkey` inject `ConnectionStrings__cache` and
`ConnectionStrings__valkey`. The consuming application needs no hostnames, passwords, or
port settings. Azure Managed Redis hosting references use the same named connection-string
convention. Respire parses TLS, username, password, and database settings from the string.

## Configuration

Integration settings live under `Aspire:Respire`. Per-client overrides live under
`Aspire:Respire:<connectionName>`. Settings apply in this order:

1. Global integration settings.
2. Named integration settings.
3. `ConnectionStrings:<connectionName>`.
4. The `configureSettings` callback.
5. Parse the resulting connection string into `RespireOptions`.
6. Apply global `Aspire:Respire:Options`, then named `Aspire:Respire:<connectionName>:Options`.
7. Run `configureOptions` once, when the singleton is resolved.

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

### Integration settings

| Setting | Default | Effect |
| --- | --- | --- |
| `ConnectionString` | `null` | Used when `ConnectionStrings:<connectionName>` is absent. |
| `DisableHealthChecks` | `false` | Skips the health check registration. |
| `DisableTracing` | `false` | Skips adding the `Respire` activity source. |
| `DisableMetrics` | `false` | Skips adding the `Respire` meter. |
| `DisableLogging` | `false` | Gives the client a null logger factory. |

### Options section

The `Options` section covers scalar `RespireOptions` fields, endpoints, replica endpoints,
hedged reads, reconnect policy, client-side caching, and the scalar TLS settings.

- Endpoint arrays accept strings such as `"localhost:6379"` or objects such as
  `{ "Host": "localhost", "Port": 6379 }`.
- A named array replaces the global array. An absent named array inherits the global value.
  An explicit empty array (`[]`) clears the global endpoint, replica endpoint, or
  client-cache prefix array.
- Clearing all primary endpoints still requires an options callback that supplies usable
  endpoints before the client is resolved.
- Client-side cache key prefixes are strings. `KeyPrefix` takes a text prefix; see
  [default key namespaces](dependency-injection.md#default-key-namespaces-with-aspire).
- Time spans use the invariant .NET format. An empty string clears a nullable time span or
  integer. For example, `"CommandTimeout": ""` removes the command timeout cap.
- An invalid value throws `InvalidOperationException` that names the configuration path.

The returned client builder's `Settings` property is a detached snapshot of the values
applied during registration. Changing that copy, or a settings object kept from the
callback, does not change the registered client or its integration flags.

### Options callback

Configure service objects, serializers, binary key prefixes, certificate objects, and TLS
callbacks in code. The immutable options callback keeps parsed and bound values and can
resolve services:

```csharp
builder.AddRespireClient("cache", configureOptions: (services, options) => options with
{
    ClientName = "web",
    CredentialProvider = services.GetRequiredService<IRespireCredentialProvider>(),
});
```

A callback can supply endpoints when no connection string is configured. Without an options
callback, missing endpoints throw `InvalidOperationException` during registration. With a
callback, the result is validated when the singleton is resolved. Missing endpoints or a
`null` result throw `InvalidOperationException`.

## Health checks, logging, and OpenTelemetry

Health checks, tracing, metrics, and logging are enabled by default. Set
`DisableHealthChecks`, `DisableTracing`, `DisableMetrics`, or `DisableLogging` globally, per
connection, or in `configureSettings`:

```csharp
builder.AddRespireClient("cache", settings => settings.DisableTracing = true);
```

### Health checks

Health checks are named `respire_default_<connectionName>` or
`respire_keyed_<connectionName>` and carry the `ready` tag. Default and keyed clients can
share a connection name without colliding. Each check reuses the client's existing
connections and does not create new ones, so a client that has never connected reports
unhealthy. If you need readiness right after startup, send a bounded `PING` at startup, as
the sample does. See [health checks](health-checks.md) for how the check behaves.

### Tracing and metrics

Tracing adds the activity source `Respire` to the host's OpenTelemetry provider. Metrics add
the meter `Respire`. Configure exporters in the application, for example
`builder.Services.AddOpenTelemetry().UseOtlpExporter()` for the Aspire dashboard.

The integration does not change process-wide metric group selection. To export command
duration, call
`RespireMetrics.Configure(new RespireMetricsOptions { Groups = RespireMetricGroups.Default | RespireMetricGroups.Command })`
before you register providers or create clients. See [observability](observability.md).

Repeated client registrations share one telemetry builder per host. The integration adds
each source or meter when a client first enables it, so a later client can enable a signal
that earlier clients disabled. OpenTelemetry uses one provider of each kind per host.

The tracing and metrics flags only turn off this integration's automatic provider wiring.
They do not suppress listeners registered elsewhere in the process. A provider that listens
to `Respire` observes every client's telemetry.

### Logging

Clients use the host's logger factory by default. `DisableLogging` installs a null logger
factory for the selected client, even when options supply another factory. Configure
logging exporters in application code.

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

Use `AddKeyedRespireClientBuilder` to select a keyed client. The companions reuse that exact
host-owned client, and disposing a distributed cache does not dispose it.

- HybridCache uses the distributed cache as its L2 backend. See
  [Microsoft caching](caching.md).
- Output caching keeps the store's validation and hosted tag cleanup. See
  [output caching](output-caching.md).
- `IDistributedCache`, `HybridCache`, and `IOutputCacheStore` remain ordinary unkeyed
  services. Choose one backend for each in an application.
- The base `Aspire.Respire` package has no ASP.NET Core framework dependency.

Repeated `AddDistributedCache` calls follow ordinary unkeyed DI rules: resolving a single
`IDistributedCache` selects the last registration, and its options and client win together.
This also applies when `AddHybridCache` registers its distributed backend. Keyed client
registrations do not make the cache service keyed.

## Azure Managed Redis with Microsoft Entra

Add `Respire.Azure` and your chosen Azure credential package. Grant the identity Redis
access, and use its object ID as the Redis username:

```csharp
using global::Azure.Core;
using global::Azure.Identity;
using Respire.Azure;

builder.Services.AddSingleton<TokenCredential>(new DefaultAzureCredential());
builder.AddRespireClient("cache", configureOptions: (services, options) => options with
{
    CredentialProvider = new AzureManagedRedisCredentialProvider(
        services.GetRequiredService<TokenCredential>(),
        builder.Configuration["RedisIdentityObjectId"]!),
});
```

The hosting reference still supplies the endpoint and TLS settings. The `Respire.Azure`
provider supplies and refreshes Entra credentials; the credential stays caller-owned. See
[Azure Managed Redis](../guides/azure-managed-redis.md) for provider details.

Aspire 13.6 emits `host:10000,ssl=true` for the Entra connection string, which Respire
accepts directly
([hosting source](https://github.com/microsoft/aspire/blob/56f3e9c0d216c0c7069dabb49dd0464e4827744f/src/Aspire.Hosting.Azure.Redis/AzureManagedRedisExtensions.cs)).
