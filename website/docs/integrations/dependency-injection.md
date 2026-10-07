---
title: Dependency injection
description: Register lazy Respire clients in ASP.NET Core and worker services.
---

# Dependency injection

`Respire.DependencyInjection` registers `IRespireClient` with lazy connection behavior. Application startup does not wait for Redis availability.

## Register a default client

Install [Respire.DependencyInjection from NuGet](https://www.nuget.org/packages/Respire.DependencyInjection), including prerelease versions. The package also brings in `Respire` as a dependency:

```bash
dotnet add package Respire.DependencyInjection --prerelease
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

`AddRespire` and `AddKeyedRespire` support Sentinel through their lazy clients. Set
`SentinelPrimaryName` and configure Sentinel endpoints; discovery occurs on the first network
operation. Use `await RespireClient.ConnectAsync(options)` when startup must validate the
primary eagerly. Prefixed views and DI consumers retain the same client across reactive
Sentinel primary changes.

For ASP.NET Core cache abstractions, continue to [caching integrations](./caching).
For readiness endpoints, see [health checks](./health-checks).

## Default key namespaces with Aspire

`Aspire.Respire` accepts a text prefix at `Aspire:Respire:Options:KeyPrefix`, with
a per-client override at `Aspire:Respire:<connectionName>:Options:KeyPrefix`.
Configuration values are literal text; they are not percent-decoded. An absent value
preserves the connection-string or global prefix, and an empty value clears it.
The `configureOptions` callback runs last and can supply arbitrary binary prefixes
through `RespireOptions.KeyPrefix`. Both `AddRespireClient` and `AddKeyedRespireClient`
apply these rules when creating the lazy root client. See
[default root namespaces](../commands/strings-and-keys.md#default-root-namespace)
for key, scan, cache and disposal behavior.
