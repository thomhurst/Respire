---
title: Dependency injection
description: Register lazy Respire clients in ASP.NET Core and worker services.
---

# Dependency injection

`Respire.Extensions.DependencyInjection` registers `IRespireClient` with lazy connection behavior. Application startup does not wait for Redis availability.

## Register a default client

Add project references while Respire remains pre-release:

```bash
dotnet add reference path/to/Respire/src/Respire/Respire.csproj
dotnet add reference path/to/Respire/src/Respire.Extensions.DependencyInjection/Respire.Extensions.DependencyInjection.csproj
```

Register a connection string:

```csharp
builder.Services.AddRespire(
    builder.Configuration.GetConnectionString("redis")!);
```

Inject `IRespireClient`:

<!-- doc-test-declaration -->
```csharp
public sealed class SessionStore(IRespireClient redis)
{
    public ValueTask<Session?> GetAsync(string id, CancellationToken ct) =>
        redis.GetAsync<Session>($"session:{id}", ct);
}
```

The container owns disposal.

## Named clients

Use keyed services when an application talks to separate endpoints:

<!-- doc-test-tail-declaration: split-before=public sealed class CartService -->
```csharp
builder.Services.AddKeyedRespire("sessions", "redis://sessions-host");
builder.Services.AddKeyedRespire("jobs", "redis://jobs-host");

public sealed class CartService(
    [FromKeyedServices("sessions")] IRespireClient sessions)
{
    public ValueTask<string?> GetAsync(string id, CancellationToken ct) =>
        sessions.GetStringAsync($"cart:{id}", ct);
}
```

## Configure options

Use the options overload when you need explicit timeouts, connection counts, serialization, or logging. Keep secrets in configuration providers; do not embed credentials in source.

```csharp
builder.Services.AddRespire(options =>
{
    options.Endpoints.Add(new RespireEndpoint("redis.internal"));
    options.CommandTimeout = TimeSpan.FromSeconds(2);
    options.Connections = 2;
    options.UseClientSideCaching();
});
```

Default registrations and each service key may be added only once. A duplicate registration
throws immediately instead of silently retaining the first configuration.

For these lazy DI registrations, multiple `Endpoints` require `UseCluster = true`.
A standalone client with several endpoints throws `RespireConfigurationException` when the
container first resolves it. Register separate named clients for independent deployments;
endpoint order does not imply automatic failover.

Sentinel configurations require `await RespireClient.ConnectAsync(options)` during application
startup because discovery performs network I/O. `AddRespire` and `AddKeyedRespire` create lazy
clients through `RespireClient.Create`, which rejects `SentinelPrimaryName`; setting that option
on the DI builder does not enable Sentinel discovery. Applications can register an already
connected Sentinel client as a singleton and retain responsibility for disposing it at shutdown.

For ASP.NET Core cache abstractions, continue to [caching integrations](./caching).
