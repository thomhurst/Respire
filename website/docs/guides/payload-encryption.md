---
title: Client-side payload encryption
description: Encrypt cache values, near-cache map entries, job arguments, and stream payloads before Redis receives them.
---

# Client-side payload encryption

`RespireDataProtectionValueCodec` in `Respire.DataProtection` implements `IRespireValueCodec`
using ASP.NET Core `IDataProtector`. It authenticates and encrypts bytes before sending them
to Redis. Enable it independently for each feature through existing codec or serializer hooks.
Without it, existing code paths, allocations, and stored formats are unchanged.

Install `Respire.DataProtection`. Create a protector from your configured
`IDataProtectionProvider`, using a stable purpose for each feature and tenant boundary.
Readers and writers need the same application name, purpose, key ring, and compression mode.

<!-- doc-test-declaration -->
```csharp
using Microsoft.AspNetCore.DataProtection;
using Respire.Compression;
using Respire.DataProtection;

static RespireDataProtectionValueCodec CreatePayloadCodec(IDataProtectionProvider provider)
    => new(provider.CreateProtector("my-app:cache:tenant-42:v1"),
        new RespireBrotliValueCodec(new RespireValueCodecOptions
        {
            MaximumDecodedLength = 1024 * 1024,
        }));
```

The optional second argument compresses **before** encryption. Reads authenticate and decrypt
before decompressing. Omit it for encryption without compression. The codec adds versioned
purposes, including the compression mode, so changing between compressed and raw mode fails
authentication. Changing algorithms follows the [codec migration rules](value-codecs.md).
Compression bounds do not bound ciphertext buffering or Data Protection's decryption
allocation. Limit input sizes at the application boundary.

## Distributed cache and HybridCache

Set `RespireCacheOptions.ValueCodec`. This encrypts the cache's `data` field, including raw
bytes and `IBufferDistributedCache` operations. The client serializer does not control this
field. HybridCache L2 uses this setting on its Respire distributed-cache backend; HybridCache
L1 still holds ordinary application values in process memory.

<!-- doc-test-declaration -->
```csharp
using Microsoft.AspNetCore.DataProtection;
using Respire;
using Respire.Caching;
using Respire.DataProtection;

static RespireDistributedCache CreateEncryptedCache(IRespireClient client, IDataProtectionProvider provider)
    => new(client, new RespireCacheOptions
    {
        InstanceName = "encrypted:",
        ValueCodec = new RespireDataProtectionValueCodec(provider.CreateProtector("app:cache:v1")),
    });
```

Dispose the returned cache when finished. See [cache registration](../integrations/caching.md)
for DI and HybridCache configuration.

## Near-cache maps

Wrap the map's value delegates. These run for every stored map value, including primitives.
The server-assisted near cache retains ciphertext; each read decrypts it. Loaders,
persistence callbacks, and returned objects see plaintext.

<!-- doc-test-declaration -->
```csharp
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Respire;
using Respire.DataProtection;

static RespireMap<string, string> CreateEncryptedMap(IRespireClient client, IDataProtectionProvider provider)
{
    var codec = new RespireDataProtectionValueCodec(provider.CreateProtector("app:map:v1"));
    return new RespireMap<string, string>(client, "private-map", key => key,
        value => codec.Encode(Encoding.UTF8.GetBytes(value)),
        bytes => Encoding.UTF8.GetString(codec.Decode(bytes.Span)));
}
```

Enable `ClientSideCache` on the client and dispose the map before the client.
See [near-cache maps](near-cache-map.md) for loading and persistence.

## Job arguments and stream worker payloads

Compose encryption with the producer and consumer delegates. For objects, replace UTF-8
conversion with your serializer, such as source-generated JSON. Job queues use the same
producer delegate for immediate, scheduled, recurring, and flow arguments. Retries and
dead-letter copies retain encoded arguments.

<!-- doc-test-declaration -->
```csharp
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Respire.DataProtection;
using Respire.Streaming;

static void AddEncryptedJobs<THandler>(IServiceCollection services, IDataProtectionProvider provider)
    where THandler : class, IRespireStreamHandler<RespireJob<string>>
{
    var codec = new RespireDataProtectionValueCodec(provider.CreateProtector("app:jobs:v1"));
    services.AddRespireJobQueue<THandler, string>("private-jobs",
        value => codec.Encode(Encoding.UTF8.GetBytes(value)),
        bytes => Encoding.UTF8.GetString(codec.Decode(bytes)));
}
```

For ordinary streams, encrypt each sensitive field on the producer and decode the same field
in the worker. Register `IRespireClient` separately as described in [stream workers](stream-workers.md).

<!-- doc-test-declaration -->
```csharp
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Respire;
using Respire.DataProtection;
using Respire.Streaming;

static async Task ConfigureEncryptedStream<THandler>(IServiceCollection services,
    IRespireClient client, IDataProtectionProvider provider, string message)
    where THandler : class, IRespireStreamHandler<string>
{
    var codec = new RespireDataProtectionValueCodec(provider.CreateProtector("app:stream:v1"));
    services.AddRespireStreamWorker<THandler, string>("private-events", "readers",
        entry => Encoding.UTF8.GetString(codec.Decode(entry["payload"])));
    await client.Streams.AddAsync("private-events",
        ("payload", codec.Encode(Encoding.UTF8.GetBytes(message))));
}
```

## Typed client serialization

Set `RespireOptions.Serializer` to `new RespireValueCodecSerializer(yourSerializer, encryptionCodec)`
to encrypt **serializer-backed** values. Primitive and raw paths (strings, numbers, byte arrays,
and `RespireValue`) bypass the serializer. For these, call `Encode` and `Decode` explicitly
or use the feature delegates above. See [where codecs apply](value-codecs.md#where-codecs-apply).

## Authentication failures and migration

Tampered, truncated, plaintext, wrong-purpose, and unavailable-key payloads throw
`CryptographicException` with a payload-authentication message and the original exception.
Decompression and deserialization never run on unauthenticated bytes. Destination overloads
append nothing until the complete decode succeeds. Invalid authenticated compression
frames can instead throw `InvalidDataException`.

There is no plaintext fallback. Use a new key namespace or migrate old values with the old
reader and new writer. All producers and consumers must agree on configuration. Encryption
is randomized: ciphertext equality cannot support set membership, compare-and-swap, or
deduplication. Job IDs continue to control job deduplication.

## Key rotation and storage

Data Protection writes using its current active key and reads with retained expired keys.
Rotation does not rewrite existing payloads. Retain keys for the lifetime of cached values,
queued and scheduled jobs, retries, and dead letters. Deleted or revoked keys cannot decrypt
their payloads; changing application names or purposes also prevents reads.
Key-ring changes take effect as Data Protection refreshes each process's cached ring;
revocation is not an instantaneous distributed kill switch.
See Microsoft's [key management documentation](https://learn.microsoft.com/aspnet/core/security/data-protection/implementation/key-management).

Persist and share the key ring across instances and restarts; do not use an ephemeral
provider for durable data. If you [store keys in Redis](../integrations/data-protection.md),
protect key material separately, for example with `ProtectKeysWithCertificate`, and keep
the private certificate outside Redis. Unencrypted keys beside ciphertext do not protect
against someone who can read the whole Redis database.

## What remains visible

Redis keys, hash and stream field names, TTLs, lengths, access patterns, job IDs, schedules,
groups, tracing fields, progress/results, and other metadata remain visible. Only bytes
passed through the codec are encrypted. Protect job completion results and progress
messages separately if they contain secrets. Search indexes, JSON fields, and vector
attributes remain plaintext unless separately transformed; server-side search and computation
cannot operate on encrypted contents. Ciphertext length can reveal compression ratios;
omit compression when that leakage is unsuitable for your payloads.

Handlers receive plaintext, and this codec does not protect application memory. Use TLS
and avoid logging sensitive values. Valid ciphertext can be replayed or moved between keys
sharing a purpose: the codec does not authenticate the Redis key or provide freshness.
