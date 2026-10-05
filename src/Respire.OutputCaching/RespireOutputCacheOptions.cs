namespace Respire.OutputCaching;

/// <summary>Storage and invalidation contract for output-cache tags.</summary>
public enum RespireOutputCacheTaggingMode
{
    /// <summary>Raw values and tag indexes compatible with Microsoft's Redis output cache.</summary>
    MicrosoftCompatible,

    /// <summary>Isolated hash values and generation-qualified tags that protect replacements from obsolete memberships.</summary>
    GenerationAware,
}

/// <summary>Options for the Redis output-cache namespace and expired tag cleanup.</summary>
public sealed class RespireOutputCacheOptions
{
    /// <summary>Prefix matching the Microsoft Redis output cache's InstanceName.</summary>
    public string? InstanceName { get; set; }

    /// <summary>Tag invalidation contract. Defaults to MicrosoftCompatible.</summary>
    /// <remarks>GenerationAware uses a separate format and requires a nonempty Redis hash tag in InstanceName.
    /// Microsoft writers cannot participate in that format; switching modes starts a separate cache.
    /// GenerationAware buffer reads allocate an owned byte array for each nonempty payload.
    /// Tag metadata grows with tagged writes over the value lifetime, including overwrites.</remarks>
    public RespireOutputCacheTaggingMode TaggingMode { get; set; }

    /// <summary>Interval between hosted cleanup passes. Defaults to five minutes.</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Clock used for tag scores and tagged values' absolute expiry. All participating servers should synchronize clocks.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    internal bool HasValidTaggingMode => Enum.IsDefined(TaggingMode);

    internal bool HasValidGenerationNamespace
    {
        get
        {
            if (TaggingMode != RespireOutputCacheTaggingMode.GenerationAware) return true;
            var start = InstanceName?.IndexOf('{') ?? -1;
            return start >= 0 && InstanceName!.IndexOf('}', start + 1) > start + 1;
        }
    }
}
