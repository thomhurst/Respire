namespace Respire.OutputCaching;

/// <summary>Options for the Redis output-cache namespace and expired tag cleanup.</summary>
public sealed class RespireOutputCacheOptions
{
    /// <summary>Prefix matching the Microsoft Redis output cache's InstanceName.</summary>
    public string? InstanceName { get; set; }

    /// <summary>Interval between hosted cleanup passes. Defaults to five minutes.</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Clock used for tag expiration scores. All participating servers should synchronize clocks.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
}
