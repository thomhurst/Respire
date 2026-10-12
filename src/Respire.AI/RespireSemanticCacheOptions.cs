using Microsoft.Extensions.AI;

namespace Respire.AI;

/// <summary>Settings for hash-backed semantic response caching.</summary>
public sealed record RespireSemanticCacheOptions
{
    /// <summary>The additional-properties key that bypasses both cache reads and writes when set to true.</summary>
    public const string DisableCacheKey = "Respire.SemanticCache.Disabled";

    /// <summary>Application and embedding-model namespace. Change it when the embedding model or deployment changes.</summary>
    public string CacheName { get; init; } = "default";

    /// <summary>Required FLOAT32 embedding dimensions.</summary>
    public required int Dimensions { get; init; }

    /// <summary>Maximum inclusive cosine distance for a hit, from 0 to 2. Defaults to 0.1.</summary>
    public double DistanceThreshold { get; init; } = 0.1;

    /// <summary>Absolute lifetime from insertion. Hits do not renew expiry. Defaults to one hour.</summary>
    public TimeSpan TimeToLive { get; init; } = TimeSpan.FromHours(1);

    /// <summary>Selects an exact user/tenant scope. Null bypasses caching. Defaults to a shared scope.</summary>
    /// <remarks>Model, generation options and all messages preceding the final user prompt are isolated automatically.</remarks>
    public Func<IReadOnlyList<ChatMessage>, ChatOptions?, string?> ScopeSelector { get; init; } = static (_, _) => "";

    /// <summary>An additional request predicate. True bypasses embedding, cache reads and writes.</summary>
    public Func<IReadOnlyList<ChatMessage>, ChatOptions?, bool>? ShouldBypass { get; init; }

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(CacheName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Dimensions);
        if (!double.IsFinite(DistanceThreshold) || DistanceThreshold < 0 || DistanceThreshold > 2)
            throw new ArgumentOutOfRangeException(nameof(DistanceThreshold));
        // Redis PEXPIRE takes a signed integer in milliseconds; reject sub-millisecond lifetimes.
        if (TimeToLive.Ticks < TimeSpan.TicksPerMillisecond)
            throw new ArgumentOutOfRangeException(nameof(TimeToLive));
        ArgumentNullException.ThrowIfNull(ScopeSelector);
    }
}
