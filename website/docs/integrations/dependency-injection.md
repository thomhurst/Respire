---
title: Dependency injection
description: Register lazy Respire clients in ASP.NET Core and worker services.
---

# Dependency injection

`Respire.DependencyInjection` registers a lazy `IRespireClient` in ASP.NET Core and worker
services. Startup does not wait for Redis.

## Register a default client

Install [Respire.DependencyInjection](https://www.nuget.org/packages/Respire.DependencyInjection) (include prerelease versions). It brings in `Respire`:

```bash
dotnet add package Respire.DependencyInjection
```

Register a connection string, then inject `IRespireClient`:

```csharp
builder.Services.AddRespire(
    builder.Configuration.GetConnectionString("redis")!);
```

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

Use keyed services for separate endpoints:

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

Use the options overload for timeouts, connection counts, serialization, or logging. Keep
credentials in configuration providers, not in source.

```csharp
builder.Services.AddRespire(options =>
{
    options.Endpoints.Add(new RespireEndpoint("redis.internal"));
    options.CommandTimeout = TimeSpan.FromSeconds(2);
    options.Connections = 2;
    options.UseClientSideCaching();
});
```

- To build options from other services, pass a `Func<IServiceProvider, RespireOptions>`.
  `AddKeyedRespire` has both forms too.
- Without `LoggerFactory`, the client uses the container's `ILoggerFactory`.
- Register the default client, and each key, once. A duplicate throws
  `InvalidOperationException` immediately.
- Several `Endpoints` require `UseCluster = true` or `SentinelPrimaryName`; otherwise the
  container throws `RespireConfigurationException` on first resolve. Endpoint order is not
  failover: register separate named clients for independent deployments.
- For Sentinel, set `SentinelPrimaryName` and the Sentinel endpoints. Discovery happens on the
  first network operation. To validate the primary at startup, use
  `await RespireClient.ConnectAsync(options)` instead. Consumers and prefixed views keep the
  same client when the primary changes.

For ASP.NET Core cache abstractions, continue to [caching integrations](./caching).
For readiness endpoints, see [health checks](./health-checks).

## Default key namespaces with Aspire

`Aspire.Respire` reads a key prefix from `Aspire:Respire:Options:KeyPrefix`, overridden per
client by `Aspire:Respire:<connectionName>:Options:KeyPrefix`. `AddRespireClient` and
`AddKeyedRespireClient` both apply it.

- Values are literal text, not percent-decoded.
- An absent value keeps the connection-string or global prefix; an empty value clears it.
- The `configureOptions` callback runs last and can set a binary prefix through
  `RespireOptions.KeyPrefix`.

See
[default root namespaces](../commands/strings-and-keys.md#default-root-namespace)
for key, scan, cache and disposal behavior.
