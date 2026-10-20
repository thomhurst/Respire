using Microsoft.Extensions.AI;

namespace Respire.AI;

/// <summary>Builds an exact-text embedding cache into an embedding generator pipeline.</summary>
public static class RespireEmbeddingCacheExtensions
{
    /// <summary>Caches FLOAT32 embeddings in Redis, batching lookups and generation of unique misses.</summary>
    /// <remarks>
    /// The caller owns the Redis client. Disposing the pipeline disposes the inner generator.
    /// Result usage describes only generation of unique misses; all-hit responses have no usage.
    /// </remarks>
    public static EmbeddingGeneratorBuilder<string, Embedding<float>> UseRespireEmbeddingCache(
        this EmbeddingGeneratorBuilder<string, Embedding<float>> builder,
        IRespireClient client,
        RespireEmbeddingCacheOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return builder.Use(inner => new RespireCachingEmbeddingGenerator(inner, client, options));
    }
}
