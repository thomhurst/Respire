namespace Respire.Caching.Hybrid;

/// <summary>Bounds and server tracking for opt-in HybridCache local-cache coherence.</summary>
public sealed class RespireHybridCacheCoherenceOptions
{
    /// <summary>Literal Redis channel for cross-instance tag invalidation. Null disables propagation.</summary>
    public string? TagInvalidationChannel { get; set; }

    /// <summary>Explicit cache namespace carried in tag messages. Required when a channel is configured.</summary>
    /// <remarks>Use the same value only for instances sharing the same logical L2 cache and tag contract.</remarks>
    public string? TagInvalidationNamespace { get; set; }

    /// <summary>Maximum encoded tag message size, including its namespace. Defaults to 4,096 bytes.</summary>
    public int MaxTagInvalidationMessageBytes { get; set; } = 4_096;

    /// <summary>Maximum buffered tag messages. Defaults to 256. A discard conservatively retires all L1 state.</summary>
    public int TagInvalidationBufferSize { get; set; } = 256;

    /// <summary>Maximum tags supplied per entry with tag propagation enabled. Defaults to 64.</summary>
    /// <remarks>A local generation also retains at most this many distinct caller tags; exceeding
    /// that cumulative bound starts a new generation. Each tag must fit the message-size limit.</remarks>
    public int MaxTagsPerEntry { get; set; } = 64;

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

    internal RespireHybridCacheCoherenceOptions Snapshot() => new()
    {
        MaxObservedKeys = MaxObservedKeys,
        MaxRememberedTagInvalidations = MaxRememberedTagInvalidations,
        ObservationSweepInterval = ObservationSweepInterval,
        TrackingOptions = TrackingOptions,
        TagInvalidationChannel = TagInvalidationChannel,
        TagInvalidationNamespace = TagInvalidationNamespace,
        MaxTagInvalidationMessageBytes = MaxTagInvalidationMessageBytes,
        TagInvalidationBufferSize = TagInvalidationBufferSize,
        MaxTagsPerEntry = MaxTagsPerEntry,
    };
}
