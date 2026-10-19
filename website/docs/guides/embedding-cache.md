---
title: Embedding cache
description: Cache exact-text Microsoft.Extensions.AI embeddings in Redis and batch provider misses.
---

:::warning Experimental API

`Respire.AI` is experimental (`RSP0002`). See the [experimental API policy](../experimental-apis.md).

:::

`UseRespireEmbeddingCache` decorates an `IEmbeddingGenerator<string, Embedding<float>>`.
Repeated text reuses its stored vector. Mixed batches retain input order and send
only unique misses to the provider in one call. This works with ordinary Redis,
Valkey and Redis Cluster; no Search module or vector index is required.

```sh
dotnet add package Respire.AI
```

<!-- doc-test-declaration -->
```csharp
using Microsoft.Extensions.AI;
using Respire;
using Respire.AI;

public static class EmbeddingSetup
{
    public static IEmbeddingGenerator<string, Embedding<float>> Build(
        IEmbeddingGenerator<string, Embedding<float>> provider, IRespireClient redis)
        => new EmbeddingGeneratorBuilder<string, Embedding<float>>(provider)
            .UseRespireEmbeddingCache(redis, new()
            {
                CacheName = "search:embedding-model-v1:deployment-v1",
                TimeToLive = TimeSpan.FromHours(24),
            })
            .Build();
}
```

The caller owns the Redis client. Disposing the built pipeline disposes its inner
embedding generator. You can also pass the cached generator to
[semantic chat caching](semantic-cache.md) to reuse prompt embeddings.

## Identity and lifetime

Keys contain SHA-256 hashes of the application namespace, provider name and URI,
effective model ID, effective dimensions, and exact input text. Text is not stored
in keys or values. Hashes are not encryption: use Redis access controls and TLS
for sensitive data. Change `CacheName` whenever a model deployment changes without
a new model ID, or when generators with different configuration share Redis.
Use separate namespaces or prefixed Redis clients for tenant isolation.

Request `ModelId` and `Dimensions` override generator metadata for key identity.
The effective dimensions also validate provider responses and cached vectors.
Custom option subclasses, `RawRepresentationFactory`, and nonempty request
`AdditionalProperties` bypass all caching, since they may change the vector.
Embeddings with provider-specific `AdditionalProperties` are returned but not stored.

Vectors use lossless, little-endian FLOAT32 bytes with a compact header preserving
`ModelId`, `CreatedAt`, and vector length. Lookups and writes use explicit pipelines, allowing
keys in different cluster slots. Each result position owns its vector, even for
duplicate inputs. Concurrent requests may generate the same miss independently;
there is no distributed single-flight lock.

`TimeToLive = null` (the default) stores entries without expiry. A positive TTL
must be at least one millisecond. It starts on insertion; hits do not extend it.
The reserved namespace is `respire:embedding:v1:`. Truncated or structurally invalid
values are misses and are replaced after successful generation.

## Failures and metrics

`FailOpen = true` (the default) reports Redis failures through `Trace` and returns
provider results. Set it to `false` to propagate Redis errors. Cancellation,
provider errors, and invalid provider result counts or dimensions always propagate.
Invalid provider batches are not stored. Cache write failures can leave a partially
stored batch, since the pipeline is not a transaction.

The `Respire.AI` meter exposes `respire.ai.embedding_cache.hits` and
`respire.ai.embedding_cache.misses` counters in input positions. A repeated uncached
input counts as multiple misses but reaches the provider once per batch. Bypassed
requests are excluded. These counters measure completed cache lookups, not successful
generation: misses still count when the provider later fails or the request is cancelled.
Fail-open Redis read failures count all input positions as misses. Counters have no
text, model or tenant tags.

`GeneratedEmbeddings.Usage` and batch properties describe only the provider call
for unique misses. An all-hit response has no usage. Cached results preserve
vectors, model IDs and creation timestamps; provider-specific batch properties
are not replayed from Redis.

See the runnable [sample](https://github.com/thomhurst/Respire/tree/main/samples/Respire.Samples.AI)
for cold, mixed, and warm batches without a paid provider.
