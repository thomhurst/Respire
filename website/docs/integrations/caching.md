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
enable it on a separately registered `IRespireClient` used for direct eligible reads.

The cache owns and disposes clients created from `ClientOptions` or `ConnectionString`. If neither
is set, it uses a separately registered `IRespireClient` without taking ownership.

Inject the framework abstraction into application code:

<!-- doc-test-declaration -->
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

## Opt-in payload compression

Set `RespireCacheOptions.ValueCodec` to encode the hash's `data` field. The default is
null: cache bytes stay raw. This setting is independent of the client's serializer;
`RespireValueCodecSerializer` on a shared or cache-owned client does not enable cache compression.

```csharp
using Respire.Compression;

builder.Services.AddRespireDistributedCache(options =>
{
    options.ConnectionString = "redis://localhost";
    options.InstanceName = "myapp:compressed-v1:";
    options.ValueCodec = new BrotliValueCodec(new RespireValueCodecOptions
    {
        MinimumLength = 1024,
        MaximumDecodedLength = 4 * 1024 * 1024,
    });
});
```

The same option applies to `AddRespireHybridCache` through its `configureCache` callback:

```csharp
using Respire.Compression;

builder.Services.AddRespireHybridCache(options =>
{
    options.ConnectionString = "redis://localhost";
    options.InstanceName = "myapp:hybrid-compressed-v1:";
    options.ValueCodec = new BrotliValueCodec();
});
```

With neither `ConnectionString` nor `ClientOptions`, these registrations use the registered
`IRespireClient`. Direct construction also accepts `new RespireCacheOptions { ValueCodec = codec }`.
The cache captures the codec reference at construction; changing the options afterward does not
switch existing instances. Codecs must support concurrent calls. The cache does not dispose a
shared codec or take ownership of a registered client.

Both synchronous and asynchronous array and buffer APIs apply the codec. HybridCache applies it
only to serialized L2 payloads; L1 behavior and serialization remain HybridCache's responsibility.
Buffer reads decode into the supplied `IBufferWriter<byte>`; built-in codecs avoid an intermediate
decoded array. Writes retain an owned encoded array through the asynchronous send. Multi-segment
input is combined before encoding. Compression is synchronous CPU work, not streaming or
allocation-free. Cancellation is checked before encoding and again before sending; it cannot
interrupt a codec call already executing. Existing accepted-send cancellation semantics still apply.

The threshold determines whether compression is attempted, not whether a frame is written. Small,
empty, or incompressible values still carry an uncompressed frame; those can coexist with compressed
frames. Built-in settings are validated by the codec constructor. Maximum decoded length bounds
original/decoded payload bytes; it does not cap total workspace. Oversized writes fail before
publication. RespireDistributedCache throws for corrupt, oversized, or incompatible frames instead
of returning a cache miss. HybridCache's handling of backend exceptions remains controlled by HybridCache.
Built-in buffer decoding advances the destination only on success, though a decompression failure
can modify uncommitted buffer memory. Custom codecs define their own decoding contract.

`absexp`, `sldexp`, TTLs, sliding refresh, and removal are unchanged. Refresh and removal never
invoke a codec. Reads refresh sliding TTL through the existing script before decoding, so even a
corrupt entry can have its sliding TTL refreshed. There is no extra Redis command for compression.
See [value codecs](../guides/value-codecs.md) for frame layout and resource costs.

## Migration compatibility

With `ValueCodec = null`, cache entries use the same Redis layout and payload bytes as
`Microsoft.Extensions.Caching.StackExchangeRedis`; existing entries remain interchangeable.
Sliding-expiration reads refresh TTL atomically in the same round trip.

Opting into a codec changes the `data` payload contract. Built-in decoders reject unframed legacy
entries; raw readers return encoded bytes and do not decode them. Every process sharing a namespace,
including HybridCache instances, must use a compatible codec. Use a new `InstanceName` namespace or
explicitly rewrite old entries with the appropriate reader and writer, preserving desired expiry.
Enabling a reader first does not make legacy entries readable. Changing compression algorithms also
requires compatible decoding or migration; there is no automatic detection/fallback to raw bytes.

## Redis ACL commands

Cache identities need these commands:

```text
EVALSHA EVAL SET UNLINK HSET HMGET PTTL PEXPIRE PERSIST EXISTS
```

Timeout- or cancellation-safe operations also require `CLIENT ID` and `CLIENT KILL`.
