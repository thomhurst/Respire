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
