---
title: .NET Aspire
description: Register Respire clients from Aspire connection strings, with configuration, health checks, telemetry, and cache companions.
---

# .NET Aspire

`Respire.Aspire` registers Respire clients from Aspire connection strings, with health checks,
OpenTelemetry, and logging. Use it in a .NET 8 or .NET 10 service that an AppHost starts with a
Redis, Valkey, or Azure Managed Redis reference. For a runnable AppHost, see the
[.NET Aspire sample](aspire-sample.md).

## Install

:::warning Not yet published

As of October 26, 2026, `Respire.Aspire`, `Respire.Aspire.DistributedCaching`,
`Respire.Aspire.HybridCaching`, and `Respire.Aspire.OutputCaching` are implemented
in this repository but are not yet published on NuGet. The install command below
requires the first release containing these packages; it cannot restore them today.
The former `Aspire.Respire*` names were replaced by `Respire.Aspire*` in
[PR #2193](https://github.com/thomhurst/Respire/pull/2193).

To try the integration now, build the [repository sample](aspire-sample.md), which
uses project references. The release pipeline already includes all four packages;
ordinary pushes validate them, while publication requires an explicit release.

:::

```bash
dotnet add package Respire.Aspire
```

## Register a client

Register clients on any `IHostApplicationBuilder`:

```csharp
using Microsoft.Extensions.Hosting;
using Respire;

builder.AddRespireClient("cache");
builder.AddKeyedRespireClient("valkey");
```

- The default singleton exposes `IRespireClient` and `RespireClient`.
- A keyed client uses the connection name as its key: inject
  `[FromKeyedServices("valkey")] IRespireClient`.
- A second default registration or a duplicate key throws.
- The host owns and disposes the clients. They connect in the background when the host starts;
  host startup does not wait for Redis. See
  [connect at startup](dependency-injection.md#connect-at-startup).

## Hosting resources

In the AppHost, use the normal Aspire hosting integrations and references:

<!-- doc-test-ignore: AppHost-only fragment requires generated Projects.Api metadata and Aspire hosting packages; the equivalent sample is built and run by Respire.Aspire.IntegrationTests. -->
```csharp
var cache = builder.AddRedis("cache");
var valkey = builder.AddValkey("valkey");
builder.AddProject<Projects.Api>("api")
    .WithReference(cache).WaitFor(cache)
    .WithReference(valkey).WaitFor(valkey);
```

The hosting integrations (Redis, Valkey, and Azure Managed Redis) inject `ConnectionStrings__cache`
and `ConnectionStrings__valkey`, so the service needs no host, port, or password settings. Respire
reads TLS, username, password, and database from the string.

## Configuration

Settings live under `Aspire:Respire`, with per-client overrides under
`Aspire:Respire:<connectionName>`. They apply in this order:

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

`Options` binds scalar `RespireOptions` fields, endpoints, replica endpoints, hedged reads,
reconnect policy, client-side caching, and scalar TLS settings.

- Endpoint arrays accept strings such as `"localhost:6379"` or objects such as
  `{ "Host": "localhost", "Port": 6379 }`.
- A named array replaces the global one; an absent one inherits it. An empty array (`[]`)
  clears the global endpoints, replica endpoints, or client-cache prefixes.
- If you clear all primary endpoints, an options callback must supply them.
- Client-side cache key prefixes are strings. `KeyPrefix` takes a text prefix; see
  [default key namespaces](dependency-injection.md#default-key-namespaces-with-aspire).
- Time spans use the invariant .NET format. An empty string clears a nullable value:
  `"CommandTimeout": ""` removes the command timeout.
- An invalid value throws `InvalidOperationException` naming the configuration path.

The builder's `Settings` property is a read-only snapshot; changing it does not affect the client.

### Options callback

Set services, serializers, binary key prefixes, certificates, and TLS callbacks in code. The
callback receives the parsed and bound options and can resolve services:

```csharp
builder.AddRespireClient("cache", configureOptions: (services, options) => options with
{
    ClientName = "web",
    CredentialProvider = services.GetRequiredService<IRespireCredentialProvider>(),
});
```

A callback can supply endpoints when there is no connection string. Missing endpoints throw
`InvalidOperationException`: at registration without a callback, or when the client is resolved
with one (as does a `null` result).

## Health checks, logging, and OpenTelemetry

All four are on by default. Turn one off globally, per connection, or in `configureSettings`:

```csharp
builder.AddRespireClient("cache", settings => settings.DisableTracing = true);
```

### Health checks

Checks are named `respire_default_<connectionName>` or `respire_keyed_<connectionName>`, with
the `ready` tag. A check reuses existing connections and opens none. The client connects in the
background when the host starts, so the check turns healthy without application traffic. See
[health checks](health-checks.md).

### Tracing and metrics

The integration adds the `Respire` activity source and meter to the host's OpenTelemetry
providers. Configure exporters yourself, for example
`builder.Services.AddOpenTelemetry().UseOtlpExporter()` for the Aspire dashboard.

To export command duration, call
`RespireMetrics.Configure(new RespireMetricsOptions { Groups = RespireMetricGroups.Default | RespireMetricGroups.Command })`
before you register providers or create clients. See [observability](observability.md).

Providers are per host, not per client: if any client enables a signal, every client's telemetry
for it is observed. The flags only skip this integration's wiring; they do not suppress listeners
registered elsewhere.

### Logging

Clients use the host's logger factory. `DisableLogging` gives the client a null logger factory,
even if options supply another.

## Cache companions

Install the companion package for each cache API you need:

| Package | Registration on an existing client builder |
| --- | --- |
| `Respire.Aspire.DistributedCaching` | `.AddDistributedCache(...)` |
| `Respire.Aspire.HybridCaching` | `.AddHybridCache(configureCache, configureHybrid)` |
| `Respire.Aspire.OutputCaching` | `.AddOutputCache(...)` |

```csharp
builder.AddRespireClientBuilder("cache")
    .AddHybridCache(options => options.InstanceName = "app:cache:")
    .AddOutputCache(options => options.InstanceName = "app:output:");
```

Use `AddKeyedRespireClientBuilder` for a keyed client. Companions reuse the host-owned client
and never dispose it.

- HybridCache uses the distributed cache as its L2. See [Microsoft caching](caching.md) and
  [output caching](output-caching.md).
- `IDistributedCache`, `HybridCache`, and `IOutputCacheStore` stay unkeyed, even with a keyed
  client. Choose one backend for each. If you register several, the last one wins, with its
  options and client.
- The base `Respire.Aspire` package has no ASP.NET Core dependency.

## Azure Managed Redis with Microsoft Entra

Add `Respire.Azure` and an Azure credential package. Grant the identity Redis access. The provider
derives the Redis username from the access token's `oid` claim, so deployment configuration does not
need to supply the identity's object ID:

```csharp
using global::Azure.Core;
using global::Azure.Identity;
using Respire.Azure;

builder.Services.AddSingleton<TokenCredential>(new DefaultAzureCredential());
builder.AddRespireClient("cache", configureOptions: (services, options) => options with
{
    CredentialProvider = new RespireAzureManagedRedisCredentialProvider(
        services.GetRequiredService<TokenCredential>()),
});
```

The hosting reference supplies the endpoint and TLS (Aspire 13.6 emits `host:10000,ssl=true`,
which Respire accepts). The provider refreshes Entra credentials; you own the credential. See
[Azure Managed Redis](../guides/azure-managed-redis.md).
