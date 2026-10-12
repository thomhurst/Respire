---
title: Semantic chat response cache
description: Cache Microsoft.Extensions.AI chat responses by cosine similarity, with exact scopes and expiring Redis hashes.
---

# Semantic chat response cache

`Respire.AI` adds `ChatClientBuilder.UseRespireSemanticCache` for
`Microsoft.Extensions.AI` 10.10.0. It embeds the final user prompt with your
`IEmbeddingGenerator<string, Embedding<float>>`, searches a Redis Search FLOAT32
vector index, and returns a stored `ChatResponse` when cosine distance is within
the configured threshold. On a miss it calls the inner chat client and stores
eligible text responses with an absolute TTL.

Install `Respire.AI`. Use an **unprefixed client connected to standalone Redis 8
with Query Engine**, or Redis Stack with Search. Redis Cluster and servers without
Search are unsupported. The caller owns the Redis client and embedding generator;
disposing the built chat pipeline disposes its inner chat client only.

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

Create a new cache namespace when changing the embedding model, hidden provider
defaults, deployment behavior, or middleware that modifies requests downstream.
Dimension and schema version are part of the index name. Available chat and
embedding provider metadata also participates in the scope. Providers without
metadata need distinct `CacheName` values when their behavior differs. Place this
middleware where it sees the effective messages and generation options; changes
made inside the inner provider are not visible to the cache.

Indexes are created lazily and shared by middleware instances with the same
namespace and dimensions. Do not modify the reserved `respire:semantic:v1:` index
schemas or hashes. Entries expire; empty indexes remain until you remove them
through Search administration. Use a separate Redis database or namespace for
cache lifecycle management. Cache creation needs `FT.CREATE`; lookups need
`FT.SEARCH` and `HGET`; writes use `EVALSHA`/`EVAL`, `HSET` and `PEXPIRE`.
Concurrent index creation also uses `FT.INFO` to confirm an existing index.
If a lookup finds that the index was dropped, the middleware recreates it and
retries the search once. Other failures follow the configured failure policy.

## Scope and opt out

`ScopeSelector` returns an exact user or tenant boundary. The default is a shared
scope, suitable only for responses that every caller may see. A null scope bypasses
the cache. The middleware additionally hashes the model/provider identity,
generation options (including `Instructions`), and **every message preceding the
final user prompt**. System prompts and conversation history therefore match
exactly, including case. Only the final user text uses semantic matching.
Request properties such as `UserId` remain part of the exact options scope.
The scope hash cannot inject Search query syntax.

Set `RespireSemanticCacheOptions.DisableCacheKey` to Boolean `true` in
`ChatOptions.AdditionalProperties` to bypass both reads and writes. The flag is
excluded from the scope when false; caller options are not modified.

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

`ShouldBypass` can also exclude requests that depend on current time, account
state, authorization, or other changing data. Scope selection is not authorization:
derive user and tenant identifiers from trusted application context. A semantic
match can return an answer generated for different wording, so opt out for
transactional operations and exact identifiers.

## Threshold tuning

Cosine **distance** is lower for more similar prompts: identical directions have
distance 0, orthogonal vectors 1, and opposite directions 2. The boundary is
inclusive: `distance <= DistanceThreshold`. The default is 0.1; it is a starting
point, not a quality guarantee. Embeddings must have the configured dimensions,
finite values and a nonzero norm.

1. Collect representative paraphrases and difficult negative pairs, especially
   questions that differ only in negation, names, dates, units or account IDs.
2. Embed them with the production model. Measure cosine distances and label
   whether reusing each answer is acceptable within its scope.
3. Start with a small threshold and increase it only while false hits remain
   acceptable. A smaller threshold means more misses and provider calls; a larger
   threshold risks incorrect reuse. Re-evaluate after changing the model.

The index uses exact FLAT KNN with one nearest neighbor, filtered by scope before
ranking. Expired or concurrently deleted neighbors become misses. Concurrent
misses can call the provider more than once and create separate entries; there is
no distributed request coalescing. FLAT search cost grows with the scoped cache,
so bound retention with TTL and measure production latency before scaling it.

## TTL, response fidelity and bypass behavior

TTL starts when the response is inserted. A Lua script writes the complete hash
and applies `PEXPIRE` in one atomic operation. TTL has millisecond precision and
must be at least one millisecond. Hits do not extend it. Each insertion gets a new
key, so replacing an entry cannot clear its expiry. This uses `Respire.Search`
directly: the small cache-specific schema can apply TTL atomically without
changing the complete-record upsert contract of `Respire.VectorData`. It does not
use vector sets or depend on their AOF replacement behavior.

**Streaming always bypasses embedding, cache reads and cache writes.** Neither a
streaming miss nor a streaming call after a cached response populates/replays the
cache. Non-streaming caching accepts text-only conversations ending in a nonblank
user prompt. Non-text content, tools, provider-specific raw request representations,
custom request types, final-prompt content metadata, server-side conversation IDs
and background/continuation requests bypass caching. Responses containing tool or
non-text content, conversation state or continuation tokens are not stored.

Responses use the upstream AI JSON metadata and return a new deserialized object
on each hit. Portable response messages, model ID, response ID, finish reason,
timestamps and usage are retained. `RawRepresentation` fields are omitted, as in
upstream JSON serialization. Usage and IDs describe the original generation;
they do not represent a new provider call on a cache hit. Arbitrary additional
properties need types supported by upstream AI JSON metadata. By default,
`FailOpen = true` reports non-cancellation embedding, Redis and serialization
failures through `System.Diagnostics.Trace` warnings. A failed lookup calls the
provider without storing the response; a failed insertion returns the successful
provider response. Configure a Trace listener to capture these warnings. Set
`FailOpen = false` to propagate cache failures instead. Provider failures and
cancellation always propagate, including cancellation during insertion. Cancelled
or failed provider calls never insert responses.

The real Redis contracts use a fake embedding generator and chat provider on
RESP2 and RESP3. They cover hits, misses, the inclusive distance boundary, exact
scope/model/system/history isolation, opt-out, expiry, streaming bypass,
invalid embeddings/options, stateful/tool bypass and dependency ownership on
.NET 8 and .NET 10.
