---
title: Dependency injection
description: Register lazy Respire clients in ASP.NET Core and worker services.
---

# Dependency injection

`Respire.DependencyInjection` registers `IRespireClient` with lazy connection behavior. Application startup does not wait for Redis availability.

## Register a default client

Install [Respire.DependencyInjection from NuGet](https://www.nuget.org/packages/Respire.DependencyInjection), including prerelease versions. The package also brings in `Respire` as a dependency:

```bash
dotnet add package Respire.DependencyInjection
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

To build options from other services, pass a `Func<IServiceProvider, RespireOptions>` instead.
Both forms exist for `AddKeyedRespire` too. If the options do not set a `LoggerFactory`, the
client uses the container's `ILoggerFactory`.

Register the default client, and each service key, only once. A duplicate registration
throws `InvalidOperationException` immediately instead of silently keeping the first
configuration.

Several `Endpoints` require `UseCluster = true` or `SentinelPrimaryName`. A standalone
client with several endpoints throws `RespireConfigurationException` when the container
first resolves it. Endpoint order does not imply automatic failover; register separate named
clients for independent deployments.

`AddRespire` and `AddKeyedRespire` support Sentinel through their lazy clients. Set
`SentinelPrimaryName` and configure Sentinel endpoints; discovery happens on the first network
operation. Use `await RespireClient.ConnectAsync(options)` when startup must validate the
primary eagerly. Prefixed views and DI consumers keep the same client when Sentinel reports a
new primary.

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
