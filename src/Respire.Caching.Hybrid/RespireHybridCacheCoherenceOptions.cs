namespace Respire.Caching.Hybrid;

/// <summary>Bounds and server tracking for opt-in HybridCache local-cache coherence.</summary>
public sealed class RespireHybridCacheCoherenceOptions
{
    /// <summary>Maximum observed logical keys, including active fills. Defaults to 1,024.</summary>
    /// <remarks>Additional keys use L2 with local caching disabled until capacity becomes available.</remarks>
    public int MaxObservedKeys { get; set; } = 1_024;

    /// <summary>Maximum remembered local tag invalidations. Defaults to 1,024.</summary>
    /// <remarks>When exhausted, the provider permanently bypasses local caching. Remembered
    /// timestamps preserve local tag semantics when a key receives a new local generation.</remarks>
    public int MaxRememberedTagInvalidations { get; set; } = 1_024;

    /// <summary>How often expired local entries release idle subscriptions. Defaults to 30 seconds.</summary>
    public TimeSpan ObservationSweepInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Tracking settings for the bridge's owned connection. Defaults to OPTIN for all keys.</summary>
    /// <remarks>Prefixes are physical Redis prefixes, including client prefixes and InstanceName.
    /// Keys outside these prefixes bypass local caching. This connection caches only tracking reads;
    /// the distributed payload and its configured codec remain on the original cache connection.</remarks>
    public RespireClientSideCacheOptions TrackingOptions { get; set; } = new();
}
