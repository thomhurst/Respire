namespace Respire.HealthChecks;

/// <summary>Settings for probes that reuse existing Respire command connections.</summary>
public sealed record RespireHealthCheckOptions
{
    /// <summary>Probe every known data node, including replicas. The default probes one primary.</summary>
    /// <remarks>Known nodes without an open usable connection fail the check. No connection is opened.</remarks>
    public bool ProbeAllNodes { get; init; }

    /// <summary>Maximum time for a probe round. Defaults to two seconds.</summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Report degraded when any successful PING reaches this latency. Null disables the threshold.</summary>
    public TimeSpan? DegradedLatency { get; init; }

    /// <summary>Include a snapshot of client-side cache statistics when enabled. Defaults to true.</summary>
    public bool IncludeClientSideCache { get; init; } = true;

    /// <summary>Report degraded when a failover candidate is unhealthy. Defaults to true.</summary>
    public bool DegradeOnFailover { get; init; } = true;

    internal void Validate()
    {
        if (ProbeTimeout <= TimeSpan.Zero || ProbeTimeout.TotalMilliseconds > uint.MaxValue - 1d)
            throw new ArgumentOutOfRangeException(nameof(ProbeTimeout));
        if (DegradedLatency is { } latency && latency <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(DegradedLatency));
    }
}

/// <summary>Owned observation of one data node at the time of a health probe.</summary>
/// <param name="Endpoint">The data endpoint in the captured routing snapshot.</param>
/// <param name="IsConnected">Whether the captured connection accepted commands before PING.</param>
/// <param name="Latency">PING round-trip latency, or null when PING failed.</param>
/// <param name="ErrorType">Failure type name, or null on success.</param>
public sealed record RespireNodeHealth(RespireEndpoint Endpoint, bool IsConnected, TimeSpan? Latency, string? ErrorType);
