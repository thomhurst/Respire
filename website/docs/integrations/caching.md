---
title: Microsoft caching
description: Use Respire as an IDistributedCache, IBufferDistributedCache, or HybridCache backend.
---

# Microsoft caching

Respire integrates with Microsoft caching abstractions through two companion projects.

## Distributed cache

`Respire.Caching` provides `IDistributedCache` and `IBufferDistributedCache`:

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
The [opt-in HybridCache bridge](#opt-in-l1-coherence) uses a separate tracked hash read to
invalidate HybridCache's L1 while retaining these atomic distributed-cache reads.

The cache owns and disposes clients created from `ClientOptions` or `ConnectionString`. If neither
is set, it uses a separately registered `IRespireClient` without taking ownership.

To adapt an existing client directly, use `AsDistributedCache`:

```csharp
using Respire.Caching;

await using var client = await RespireClient.ConnectAsync("redis://localhost");
await using var cache = client.AsDistributedCache(new RespireCacheOptions
{
    InstanceName = "myapp:",
});
var cachedBytes = await cache.GetAsync("product:42");
```

Each call creates a new adapter without network I/O. `InstanceName` adds to any existing
client key prefix, and `ValueCodec` configures the adapter's payload encoding. The caller
retains ownership of the client; disposing the adapter does not dispose it. `RespireCacheOptions`
holds only cache settings. A `RespireCacheRegistrationOptions` instance can also be passed here,
but its connection settings are ignored because the adapter uses the supplied client. The registration
methods take `RespireCacheRegistrationOptions`, which adds `ConnectionString` and `ClientOptions`.
The existing `RespireDistributedCache` constructor remains available.

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

`Respire.Caching.Hybrid` adds Respire as the L2 backend for `HybridCache`:

```csharp
builder.Services.AddRespireHybridCache(
    "redis://localhost",
    instanceName: "myapp:");
```

This combines an in-process L1 with Redis-backed L2 storage.

### Opt-in L1 coherence

The default registration provides L2 storage. A different process can update Redis while an
existing `HybridCache` provider continues to serve its earlier L1 value until local expiry.
Enable server-assisted local invalidation explicitly:

```csharp
builder.Services.AddRespireHybridCache(
    "redis://localhost",
    instanceName: "myapp:")
    .WithRespireClientSideCoherence(options =>
    {
        options.MaxObservedKeys = 1_024;
        options.MaxRememberedTagInvalidations = 1_024;
    });
```

The same builder extension works when `AddRespireHybridCache` uses a registered
`IRespireClient` instead of a connection string, including client key-prefix views,
`InstanceName`, configured value codecs, and the buffer-oriented L2 API. The underlying
client must be a `RespireClient`; custom client wrappers remain usable with the L2-only mode.

The bridge owns a separate RESP3 tracking client with the distributed client's connection,
authentication, database, and routing settings. Before admitting a logical key to L1, it
subscribes to that key's **physical** Redis name and performs a tracked `HEXISTS` read of the
hash's `data` field. A subscription alone does not establish Redis OPTIN tracking. The
separate client avoids treating the distributed cache's Lua reads as local mutations and
does not duplicate the serialized payload in its tracking cache.

`TrackingOptions` defaults to OPTIN for all physical keys. Configure Broadcast and narrow
physical prefixes in the builder callback when appropriate:

```csharp
options.TrackingOptions = new()
{
    TrackingMode = RespireClientTrackingMode.Broadcast,
    KeyPrefixes = ["tenant:myapp:"], // client prefix + InstanceName
};
```

Redis tracking requires RESP3 and server support, plus permission for the tracking handshake
and hash read. Keys outside the configured tracking prefixes, failed tracking reads, and keys
beyond `MaxObservedKeys` use L2 with local caching disabled. A tracking-client shutdown also
retires L1 and disables further local admission. Explicit clears and continuity losses from
an observed registered client's cache retire its corresponding local generations.

Invalidation is **eventual**, delivered asynchronously. A read already in progress can return
its earlier value; connection-failure detection and callback delivery also take time. After
the bridge processes an invalidation, later reads cannot join the old fill or use its local
value. Late factory or `SetAsync` completions cannot publish into the new local generation,
including when the final factory caller cancels. Observers only retire local state: they do
not delete or rewrite shared L2 values. This is not a distributed factory lock or a conditional
L2 write; concurrent factories can still overwrite each other's L2 results under the normal
`HybridCache` contract. Use application-level coordination when that ordering matters.

Each observed key has its own Microsoft `HybridCache` context and local memory namespace.
The logical L2 key, payload format, serializers, mutable-value copies, entry flags, and normal
factory cancellation and coalescing behavior stay intact. This opt-in mode adds a tracking
client, an initial tracked read, subscriptions, and context/metadata allocations per observed
key. Choose the observation bound for the application's working set. Expired or evicted local
entries release idle observations; a sweep checks expired entries every 30 seconds by default.
Active requests can keep observations until they complete or cancel.

Local `RemoveByTagAsync` writes the shared tag marker once and preserves its timestamp across
local generations. `MaxRememberedTagInvalidations` bounds this additional history: exhausting
it permanently disables L1 for that provider, preserving ordinary L2/tag behavior. Use tags
consistently across calls, as with ordinary `HybridCache`. Propagating another process's tag
removal to already populated L1 remains separate work tracked by
[#1225](https://github.com/thomhurst/Respire/issues/1225).

Disposing the provider stops the bridge's timer, subscriptions, and owned tracker. It never
disposes an externally registered client; the distributed cache retains its existing ownership
rules. The default L2-only registration creates no bridge or tracking client.

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
For Brotli, start with the default quality `4`; measure CPU time and stored size before increasing
quality, especially for large values. The cache does not schedule codec work onto another thread.

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
Opt-in HybridCache coherence additionally needs `HEXISTS`, `CLIENT TRACKING`, and, for OPTIN,
`CLIENT CACHING`, together with the client's normal RESP3 handshake commands.
