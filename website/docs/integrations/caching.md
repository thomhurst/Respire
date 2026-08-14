---
title: Microsoft caching
description: Use Respire as an IDistributedCache, IBufferDistributedCache, or HybridCache backend.
---

# Microsoft caching

Respire integrates with Microsoft caching abstractions through two companion projects.

## Distributed cache

`Respire.Extensions.Caching` provides `IDistributedCache` and `IBufferDistributedCache`:

```csharp
builder.Services.AddRespireDistributedCache(
    "redis://localhost",
    instanceName: "myapp:");
```

Use `ClientOptions` when the cache needs its own fully configured client. The factory receives
the service provider, and takes precedence if `ConnectionString` is also set:

```csharp
builder.Services.AddRespireDistributedCache(options =>
{
    options.ClientOptions = services => new RespireOptions
    {
        Endpoints = { new RespireEndpoint("redis.internal") },
        Serializer = services.GetRequiredService<IRespireSerializer>(),
        CommandTimeout = TimeSpan.FromSeconds(2),
    };
    options.InstanceName = "myapp:";
});
```

`RespireDistributedCache` uses atomic Lua reads to implement sliding expiration, so RESP3
client-side caching does not apply to `IDistributedCache` operations. Applications can still
enable it on a separately registered `IRespireClient` used for direct `GET` and `MGET` calls.

The cache owns and disposes clients created from `ClientOptions` or `ConnectionString`. If neither
is set, it uses a separately registered `IRespireClient` without taking ownership.

Inject the framework abstraction into application code:

```csharp
public sealed class ProductCache(IDistributedCache cache)
{
    public Task<byte[]?> GetAsync(string id, CancellationToken ct) =>
        cache.GetAsync($"product:{id}", ct);
}
```

## HybridCache

`Respire.Extensions.Caching.Hybrid` adds Respire as the L2 backend for `HybridCache`:

```csharp
builder.Services.AddRespireHybridCache(
    "redis://localhost",
    instanceName: "myapp:");
```

This combines an in-process L1 with Redis-backed L2 storage.

## Migration compatibility

Cache entries use the same Redis layout as `Microsoft.Extensions.Caching.StackExchangeRedis`. You can switch providers without flushing existing entries. Sliding-expiration reads refresh TTL atomically in the same round trip.

## Redis ACL commands

Cache identities need these commands:

```text
EVALSHA EVAL SET UNLINK HSET HMGET PTTL PEXPIRE PERSIST EXISTS
```

Timeout- or cancellation-safe operations also require `CLIENT ID` and `CLIENT KILL`.
