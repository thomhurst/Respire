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

```csharp
builder.Services.AddRespire("sessions", "redis://sessions-host");
builder.Services.AddRespire("jobs", "redis://jobs-host");

public sealed class CartService(
    [FromKeyedServices("sessions")] IRespireClient sessions);
```

## Configure options

Use the options overload when you need explicit timeouts, connection counts, serialization, or logging. Keep secrets in configuration providers; do not embed credentials in source.

For ASP.NET Core cache abstractions, continue to [caching integrations](./caching).
