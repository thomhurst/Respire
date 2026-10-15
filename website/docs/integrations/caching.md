---
title: Microsoft caching
description: Use Respire as an IDistributedCache, IBufferDistributedCache, or HybridCache backend.
---

# Microsoft caching

Use these packages to back Microsoft caching abstractions with Redis:

| Package | Provides |
| --- | --- |
| `Respire.Caching` | `IDistributedCache` and `IBufferDistributedCache` |
| `Respire.Caching.Hybrid` | `HybridCache` L2 backend, with opt-in L1 coherence |

## Distributed cache

When backed by a concrete `RespireClient`, [command hooks](../guides/command-hooks.md#cache-operation-owners)
cover each adapter operation: `GET`, `SET`, `REFRESH`, or `UNLINK`. Admission precedes
codecs and tracking setup; completion includes conversion, foreground TTL correction,
and resource cleanup. Internal script attempts do not create separate callbacks.

```bash
dotnet add package Respire.Caching
```

Register a cache with its own client:

```csharp
builder.Services.AddRespireDistributedCache(
    "redis://localhost",
    instanceName: "myapp:");
```

Inject the framework abstraction:

<!-- doc-test-declaration -->
```csharp
public sealed class ProductCache(IDistributedCache cache)
{
    public Task<byte[]?> GetAsync(string id, CancellationToken ct) =>
        cache.GetAsync($"product:{id}", ct);
}
```

For a fully configured client, set `ClientOptions`. The factory receives the service provider
and takes precedence over `ConnectionString`:

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

Client ownership:

- The cache owns and disposes clients created from `ClientOptions` or `ConnectionString`.
- With neither set, it uses the registered `IRespireClient` without owning it, and throws
  `RespireConfigurationException` if none is registered.
- The registration replaces any earlier `IDistributedCache`, such as
  `AddDistributedMemoryCache`.

Sliding expiration uses atomic Lua reads, so RESP3 client-side caching does not apply to
`IDistributedCache` operations. You can still enable it on a separate `IRespireClient` for
direct reads. The [HybridCache bridge](#opt-in-l1-coherence) adds its own tracked read for L1
invalidation.

### Adapt an existing client

```csharp
using Respire.Caching;

await using var client = await RespireClient.ConnectAsync("redis://localhost");
await using var cache = client.AsDistributedCache(new RespireCacheOptions
{
    InstanceName = "myapp:",
});
var cachedBytes = await cache.GetAsync("product:42");
```

- Each call creates a new adapter, without network I/O. `new RespireDistributedCache(client,
  options)` does the same.
- `InstanceName` is appended to any existing client key prefix. `ValueCodec` sets the payload
  encoding.
- You keep ownership of the client; disposing the adapter does not dispose it.
- `RespireCacheOptions` holds only cache settings (`InstanceName`, `ValueCodec`). The
  registration methods take `RespireCacheRegistrationOptions`, which adds `ConnectionString`
  and `ClientOptions`; `AsDistributedCache` ignores those.

## HybridCache

`Respire.Caching.Hybrid` makes Redis the L2 backend for `HybridCache`, behind its in-process L1:

```bash
dotnet add package Respire.Caching.Hybrid
```

```csharp
builder.Services.AddRespireHybridCache(
    "redis://localhost",
    instanceName: "myapp:");
```

Another overload takes a `configureCache` callback (`RespireCacheRegistrationOptions`) and an
optional `configureHybridCache` callback (`HybridCacheOptions`). Without a connection string or
`ClientOptions`, it uses the registered `IRespireClient`.

### Opt-in L1 coherence

By default, when another process updates Redis, a provider keeps serving its earlier L1 value
until local expiry. To invalidate L1 from the server, opt in:

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

- Works with a registered `IRespireClient`, key-prefix views, `InstanceName`, value codecs, and
  the buffer L2 API. The underlying client must be a `RespireClient`; custom wrappers can use
  L2-only mode.
- The bridge owns a separate RESP3 tracking client with the same connection, authentication,
  and database settings. Tracking reads always use the primary.
- Before admitting a key to L1, it subscribes to the key's **physical** Redis name and does a
  tracked `HEXISTS` read of the hash's `data` field.
- Requires RESP3, server tracking support, and permission for the tracking handshake and hash
  read.

`TrackingOptions` defaults to OPTIN for all physical keys. To use Broadcast with narrow
physical prefixes:

```csharp
builder.Services.AddRespireHybridCache("redis://localhost", instanceName: "myapp:")
    .WithRespireClientSideCoherence(coherence =>
    {
        coherence.TrackingOptions = new()
        {
            TrackingMode = RespireClientTrackingMode.Broadcast,
            KeyPrefixes = ["myapp:"], // physical InstanceName prefix for this client
        };
    });
```

When L1 is skipped:

- Keys outside the tracking prefixes, failed tracking reads, and keys beyond `MaxObservedKeys`
  use L2 only.
- Tracking-client shutdown retires L1 and stops further local admission.
- Explicit clears and continuity losses on an observed registered client retire the matching
  local entries.

:::warning Eventual invalidation
Invalidation is asynchronous. A read in progress can return its earlier value, and failure
detection takes time. After an invalidation is processed, later reads cannot use the old local
value, and late factory or `SetAsync` completions cannot publish into L1. The bridge never
deletes or rewrites L2. It is not a distributed lock: concurrent factories can still overwrite
each other's L2 results, as with any `HybridCache`. Use application-level coordination when
ordering matters.
:::

Costs and bounds:

- Each observed key adds a tracking subscription, an initial tracked read, and its own
  `HybridCache` context. L2 keys, payload format, serializers, and factory coalescing are
  unchanged.
- Size `MaxObservedKeys` for your working set. Expired or evicted entries release idle
  observations; a sweep runs every `ObservationSweepInterval` (default 30 seconds). Active
  requests keep observations until they finish.
- The provider's own L2 writes also notify the tracker, so a miss or `SetAsync` can retire its
  first L1 entry. The next read reloads L2, then L1 serves until the next invalidation.
- Local `RemoveByTagAsync` writes the shared tag marker once. `MaxRememberedTagInvalidations`
  bounds remembered tag history; exhausting it permanently disables L1 for that provider (L2
  and tags keep working).
- Use tags consistently across reads and writes. Tags in a serialized L2 payload validate it;
  they do not replace the caller's tag list on the L1 entry.

Disposing the provider stops the bridge's timer, subscriptions, and tracker. It never
disposes an externally registered client. The default L2-only registration creates no bridge.

### Cross-instance tag invalidation

Enable tag propagation on every participating instance:

```csharp
builder.Services.AddRespireHybridCache("redis://localhost", instanceName: "myapp:")
    .WithRespireClientSideCoherence(options =>
    {
        options.TagInvalidationChannel = "myapp:hybrid-tags:v1";
        options.TagInvalidationNamespace = "myapp:cache:v1";
        options.MaxTagInvalidationMessageBytes = 4_096;
        options.TagInvalidationBufferSize = 256;
        options.MaxTagsPerEntry = 64;
    });
```

**Configuration**

- Both `TagInvalidationChannel` and `TagInvalidationNamespace` are required; set both to null
  for tracking-only mode.
- Share a namespace only between instances using the same L2 cache, database, key prefixes,
  codecs, and tags. Pub/sub channels span databases; the namespace prevents cross-cache replay.
- The channel is a literal physical name: pub/sub views, `PubSubPrefix`, key prefixes, and
  `InstanceName` are not added.
- Settings are captured when the extension is called; later changes to the options object
  have no effect.
- Requires `SUBSCRIBE`, `UNSUBSCRIBE`, and `PUBLISH`, plus the tracking permissions.

**Behavior**

- `RemoveByTagAsync("tag")`, the multiple-tag overload, and `RemoveByTagAsync("*")` propagate.
  `*` invalidates every entry, including untagged ones. Other strings are literal tags, not
  globs.
- Each tag writes the shared HybridCache marker once, then publishes its UTC timestamp.
  Receivers update local tag metadata only; they never republish, write markers, or delete
  shared values.
- Unrelated idle L1 entries stay cached. Active fills are retired conservatively: callers
  already waiting can get the earlier result, but later requests cannot join that fill.
- The first local admission waits for the subscriber acknowledgment and honors the request's
  cancellation token. The subscriber uses the client's `ConnectTimeout` and `CommandTimeout`
  (10 seconds each by default). Disabling `CommandTimeout` removes the acknowledgment deadline,
  so pass a cancellation token.
- Startup failure, terminal subscriber failure, or a replay or cleanup failure disables L1 for
  that provider; recreate it to retry.
- During detected reconnects, reads bypass L1. A reconnect, buffer discard, or oversized-message
  gap retires all local entries and remembered timestamps, so new reads refetch L2 tag
  metadata.
- Malformed frames and foreign namespaces are ignored.
- Prefer async disposal. Synchronous disposal waits for the subscriber and cleanup. Disposal
  unsubscribes and joins owned work, without disposing a registered client.

:::warning Delivery limits
Redis pub/sub does not replay lost messages.

- A removal returns after its marker write and publication, not after receivers process it.
- A publication failure is reported after the marker may already have changed.
- A disconnected instance can serve stale L1 data until it detects the failure. A silently
  lost message with no detected gap is not recovered.
- Every tag writer must participate.
- Synchronize instance clocks. Clock skew can invalidate early, or leave values from a fast
  clock valid after a slower clock's removal.

For durable invalidation or linearizable reads, add application coordination.
:::

**Limits**

- `MaxTagInvalidationMessageBytes` covers a 32-byte header plus the UTF-8 namespace and tag.
  An oversized local tag is rejected before its marker is written. Multiple-tag removal is
  per tag and can partially complete.
- Oversized inbound messages are discarded before queueing.
- The queue holds at most `TagInvalidationBufferSize` messages; overflow invalidates
  conservatively instead of queueing more.
- `MaxTagsPerEntry` caps tags per entry; a local entry that collects more distinct tags starts
  fresh.
- These bounds limit bridge memory, not caller or serializer memory. Use stable,
  low-cardinality tags, not request IDs or user-generated tag sets.

:::warning Security
Tag names, namespaces, timestamps, and a sender ID travel as plaintext; the value codec does
not protect them. Keep secrets and personal data out of tags, namespaces, and channel names.
Anyone allowed to publish on the channel can invalidate local cache state, so restrict it
with Redis ACLs, and use TLS when required.
:::

### Disposal during local cleanup

The bridge waits for active local-memory cleanup before disposal completes.

- Do not dispose the coherent cache from inside a custom `IMemoryCache.Remove` or its
  synchronous cleanup callback. `Dispose` and `DisposeAsync` throw
  `InvalidOperationException` there, and do not schedule disposal later.
- The check follows `ExecutionContext`, including `Task.Run` and nested caches. A
  fire-and-forget task started from the callback is also rejected while cleanup is active;
  observe its exception. Once cleanup completes, that task can dispose normally.
- Disposal from an unrelated context waits for cleanup and reports any cleanup failure.
- Suppressing `ExecutionContext` flow or using `UnsafeQueueUserWorkItem` hides the cleanup
  identity, so that disposal waits. Never wait for such disposal from a cleanup callback: it
  deadlocks. Dispose under application shutdown control instead.

## Opt-in payload compression

Set `RespireCacheOptions.ValueCodec` to encode the hash's `data` field. The default, null,
stores raw bytes. The client's serializer is separate: `RespireValueCodecSerializer` on the
client does not compress cache entries.

```csharp
using Respire.Compression;

builder.Services.AddRespireDistributedCache(options =>
{
    options.ConnectionString = "redis://localhost";
    options.InstanceName = "myapp:compressed-v1:";
    options.ValueCodec = new RespireBrotliValueCodec(new RespireValueCodecOptions
    {
        MinimumLength = 1024,
        MaximumDecodedLength = 4 * 1024 * 1024,
    });
});
```

For HybridCache, set it in the `configureCache` callback:

```csharp
using Respire.Compression;

builder.Services.AddRespireHybridCache(options =>
{
    options.ConnectionString = "redis://localhost";
    options.InstanceName = "myapp:hybrid-compressed-v1:";
    options.ValueCodec = new RespireBrotliValueCodec();
});
```

**Setup**

- Without `ConnectionString` or `ClientOptions`, these use the registered `IRespireClient`.
  Direct construction accepts `new RespireCacheOptions { ValueCodec = codec }`.
- The codec is captured at construction; later option changes do not affect existing caches.
- Codecs must be thread-safe. The cache does not dispose a shared codec.

**Behavior**

- Sync and async, array and buffer APIs all apply the codec. HybridCache applies it only to
  serialized L2 payloads.
- Buffer reads decode into the supplied `IBufferWriter<byte>`; built-in codecs skip an
  intermediate array. Multi-segment input is combined before encoding.
- Compression is synchronous CPU work on the calling thread, not streaming or
  allocation-free.
- Cancellation is checked before encoding and before sending. It cannot interrupt a running
  codec call.
- For Brotli, start at the default quality `4`. Measure CPU time and stored size before
  raising it.
- Values below `MinimumLength`, empty, or incompressible still get an uncompressed frame.
- `MaximumDecodedLength` bounds decoded payload bytes, not total workspace. Oversized writes
  fail before they are stored.
- `RespireDistributedCache` throws for corrupt, oversized, or incompatible frames instead of
  returning a miss. HybridCache controls its own handling of backend exceptions.
- Built-in buffer decoding advances the writer only on success, but a failure can change
  uncommitted buffer memory.
- Expiry (`absexp`, `sldexp`), sliding refresh, and removal are unchanged and never call the
  codec. Reads refresh sliding TTL before decoding, so even a corrupt entry is refreshed.
  Compression adds no Redis commands.

See [value codecs](../guides/value-codecs.md) for frame layout and resource costs.

## Migration compatibility

With `ValueCodec = null`, entries use the same Redis layout and bytes as
`Microsoft.Extensions.Caching.StackExchangeRedis`, so existing entries are interchangeable.
Sliding-expiration reads refresh TTL atomically in the same round trip.

:::warning
Enabling a codec changes the stored payload format.

- Built-in decoders reject unframed legacy entries; raw readers return encoded bytes.
- Every process sharing a namespace, including HybridCache instances, must use a compatible
  codec. Enabling a reader first does not make legacy entries readable.
- Use a new `InstanceName`, or rewrite old entries, preserving expiry.
- Changing algorithms also needs compatible decoding or migration. There is no automatic
  fallback to raw bytes.
:::

## Redis ACL commands

Cache identities need these commands:

```text
EVALSHA EVAL SET UNLINK HSET HMGET PTTL PEXPIRE PERSIST EXISTS
```

| Feature | Additional commands |
| --- | --- |
| Timeout- or cancellation-safe operations | `CLIENT ID`, `CLIENT KILL` |
| HybridCache coherence | `HEXISTS`, `CLIENT TRACKING`, `CLIENT CACHING` (OPTIN), plus RESP3 handshake commands |
| Cross-instance tag invalidation | `SUBSCRIBE`, `UNSUBSCRIBE`, `PUBLISH` |
