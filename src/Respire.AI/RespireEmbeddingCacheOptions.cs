namespace Respire.AI;

/// <summary>Settings for exact-text embedding caching.</summary>
public sealed class RespireEmbeddingCacheOptions
{
    /// <summary>Application and model deployment namespace. Change it when the underlying model changes.</summary>
    /// <remarks>Must be nonempty, non-whitespace, and well-formed UTF-16 (no unpaired surrogates).</remarks>
    public string CacheName { get; init; } = "default";

    /// <summary>Absolute lifetime from insertion, or null for no expiry. Hits do not renew expiry.</summary>
    public TimeSpan? TimeToLive { get; init; }

    /// <summary>Returns provider results on non-cancellation Redis failures. Defaults to true.</summary>
    public bool FailOpen { get; init; } = true;

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(CacheName);
        if (!EmbeddingCacheIdentity.IsWellFormed(CacheName))
            throw new ArgumentException("The cache namespace must be well-formed UTF-16.", nameof(CacheName));
        if (TimeToLive is { Ticks: < TimeSpan.TicksPerMillisecond })
            throw new ArgumentOutOfRangeException(nameof(TimeToLive));
    }
}
