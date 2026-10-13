---
title: Semantic chat response cache
description: Cache Microsoft.Extensions.AI chat responses by cosine similarity, with exact scopes and expiring Redis hashes.
---

# Semantic chat response cache

`Respire.AI` adds `ChatClientBuilder.UseRespireSemanticCache` for `Microsoft.Extensions.AI` 10.10.0. Use it to reuse chat responses for prompts that mean the same thing. It embeds the final user prompt with your `IEmbeddingGenerator<string, Embedding<float>>` and searches a Redis Search FLOAT32 vector index. A hit within the cosine distance threshold returns the stored `ChatResponse`. A miss calls the inner chat client and stores eligible text responses with a TTL.

Requirements:

- An **unprefixed client on standalone Redis 8 with Query Engine**, or Redis Stack with Search. Redis Cluster and servers without Search are unsupported.
- .NET 8 or .NET 10, RESP2 or RESP3.

You own the Redis client and embedding generator. Disposing the chat pipeline disposes only its inner chat client.

```bash
dotnet add package Respire.AI
```

## Configure the pipeline

<!-- doc-test-declaration -->
```csharp
using Microsoft.Extensions.AI;
using Respire;
using Respire.AI;

public static class SemanticChatSetup
{
    public static IChatClient Build(
        IChatClient provider,
        IRespireClient redis,
        IEmbeddingGenerator<string, Embedding<float>> embeddings)
        => new ChatClientBuilder(provider)
            .UseRespireSemanticCache(redis, embeddings, new()
            {
                CacheName = "support-assistant:embedding-model-v1:deployment-v1",
                Dimensions = 1536, // Must match the embedding generator.
                DistanceThreshold = 0.1,
                TimeToLive = TimeSpan.FromMinutes(30),
                ScopeSelector = (_, options) =>
                    options?.AdditionalProperties?.TryGetValue("UserId", out var user) == true
                        ? user as string : null,
            })
            .Build();
}
```

| Option | Default | Meaning |
| --- | --- | --- |
| `CacheName` | `"default"` | Cache namespace |
| `Dimensions` | Required | Embedding size; must match the generator |
| `DistanceThreshold` | 0.1 | Maximum cosine distance for a hit (inclusive) |
| `TimeToLive` | 1 hour | Entry lifetime; at least 1 ms |
| `ScopeSelector` | Shared scope | Exact user/tenant boundary; null bypasses the cache |
| `ShouldBypass` | null | Return `true` to skip embedding, reads and writes |
| `FailOpen` | `true` | Report cache failures as Trace warnings instead of throwing |

**Use a new `CacheName` when you change** the embedding model, hidden provider defaults, deployment behavior, or downstream middleware that modifies requests. Dimensions and schema version are already part of the index name, and available provider metadata is part of the scope. Providers without metadata need distinct `CacheName` values when they behave differently.

Place the middleware where it sees the effective messages and options. Changes made inside the inner provider are invisible to the cache.

Indexes:

- Created lazily and shared by instances with the same namespace and dimensions. A dropped index is recreated and the lookup retried once.
- Do not modify the reserved `respire:semantic:v1:` index schemas or hashes.
- Entries expire, but empty indexes remain until you remove them through Search administration. A separate Redis database or namespace makes cleanup easier.
- Required commands: `FT.CREATE` and `FT.INFO` (creation), `FT.SEARCH` and `HGET` (lookups), `EVALSHA`/`EVAL`, `HSET` and `PEXPIRE` (writes).

## Scope and opt out

`ScopeSelector` returns an exact user or tenant boundary. **The default shared scope is safe only for responses every caller may see.** A null scope bypasses the cache.

Besides the scope, these must match exactly (including case): model/provider identity, generation options (including `Instructions` and request properties such as `UserId`), and **every message before the final user prompt**. Only the final user text matches semantically.

Set `RespireSemanticCacheOptions.DisableCacheKey` to `true` in `ChatOptions.AdditionalProperties` to bypass reads and writes for one request:

<!-- doc-test-declaration -->
```csharp
using Microsoft.Extensions.AI;
using Respire.AI;

public static class SemanticChatRequest
{
    public static Task<ChatResponse> GetFreshAnswer(IChatClient chat)
        => chat.GetResponseAsync([new(ChatRole.User, "What is my current balance?")],
            new ChatOptions
            {
                AdditionalProperties = new()
                {
                    ["UserId"] = "user-42",
                    [RespireSemanticCacheOptions.DisableCacheKey] = true,
                },
            });
}
```

Use `ShouldBypass` or `DisableCacheKey` for requests that depend on current time, account state, authorization or other changing data. A semantic hit can return an answer generated for different wording, so opt out for transactional operations and exact identifiers.

:::warning Scope is not authorization
Derive user and tenant identifiers from trusted application context, not from request input.
:::

## Threshold tuning

Cosine **distance** is lower for more similar prompts: 0 for identical directions, 1 for orthogonal, 2 for opposite. A hit needs `distance <= DistanceThreshold`. The 0.1 default is a starting point, not a quality guarantee. Embeddings must have the configured dimensions, finite values and a nonzero norm.

1. Collect representative paraphrases and hard negative pairs, especially questions that differ only in negation, names, dates, units or account IDs.
2. Embed them with the production model. Measure distances and label whether reusing each answer is acceptable within its scope.
3. Start small and raise the threshold only while false hits stay acceptable. Smaller means more provider calls; larger risks wrong answers. Re-tune after changing the model.

Lookups use exact FLAT KNN with one nearest neighbor, filtered by scope. Search cost grows with the scoped cache, so bound it with TTL and measure latency before scaling. Expired or deleted neighbors are misses. **Concurrent misses can each call the provider** and store separate entries; there is no request coalescing.

## TTL, response fidelity and bypass behavior

TTL starts at insertion, has millisecond precision and is not extended by hits. The entry and its expiry are written atomically. The cache uses `Respire.Search` hashes directly, not `Respire.VectorData` or vector sets.

**Streaming always bypasses the cache**: no embedding, reads or writes.

Requests that bypass caching:

- Conversations that are not text-only or do not end in a nonblank user prompt.
- Tools, provider-specific raw request representations, custom request types and final-prompt content metadata.
- Server-side conversation IDs and background/continuation requests.

Responses with tool or non-text content, conversation state or continuation tokens are not stored.

Each hit returns a new object deserialized with the upstream AI JSON metadata. Messages, model ID, response ID, finish reason, timestamps and usage are kept; `RawRepresentation` is dropped. Usage and IDs describe the original generation, not a new provider call. Additional properties need types the upstream AI JSON metadata supports.

Failures:

- With `FailOpen = true` (default), embedding, Redis and serialization failures become `System.Diagnostics.Trace` warnings. A failed lookup calls the provider without storing; a failed insert still returns the provider response. Add a Trace listener to see them.
- With `FailOpen = false`, cache failures propagate.
- Provider failures and cancellation always propagate, including cancellation during insertion. Failed or cancelled provider calls never store a response.
