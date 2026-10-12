using Microsoft.Extensions.AI;

namespace Respire.AI;

/// <summary>Builds a Respire semantic cache into a chat client pipeline.</summary>
public static class RespireSemanticCacheExtensions
{
    /// <summary>Caches non-streaming, stateless text responses using Redis Search and expiring hashes.</summary>
    /// <remarks>The caller owns the unprefixed Redis client and embedding generator. Streaming always bypasses the cache.</remarks>
    public static ChatClientBuilder UseRespireSemanticCache(
        this ChatClientBuilder builder,
        IRespireClient client,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        RespireSemanticCacheOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(embeddingGenerator);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return builder.Use(inner => new RespireSemanticCachingChatClient(inner, client, embeddingGenerator, options));
    }
}
