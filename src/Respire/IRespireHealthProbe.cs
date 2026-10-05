namespace Respire;

/// <summary>Optional health-probe capability implemented by a client that can observe its existing data connections.</summary>
/// <remarks>
/// Implement alongside <see cref="IRespireClient"/> to support health integrations without exposing transport types.
/// A probe must not create clients, open connections, discover topology, reconnect, or dispose shared resources.
/// Capture node membership and connection identities before asynchronous probing; do not follow redirects or
/// substitute replacement connections during the round. Report unavailable or retired captured connections as failures.
/// A routing publication is a snapshot, not a guarantee that nodes remain current or reachable.
/// Implementations must support both primary-only and all-known-data-node selection, including known unavailable nodes.
/// </remarks>
public interface IRespireHealthProbe
{
    /// <summary>Probes a captured set of existing data connections and returns an owned result array in capture order.</summary>
    /// <remarks>
    /// The call must return promptly without synchronously blocking. Honor cancellation while waiting for admission
    /// and replies, and bound the whole round by <see cref="RespireHealthProbeOptions.Timeout"/>.
    /// Caller cancellation throws <see cref="OperationCanceledException"/>; timeout may throw or produce failed node results.
    /// Snapshot acquisition failures throw. Individual node failures are returned with their original exception.
    /// Results must not borrow pooled buffers or require disposal. The caller owns the array; the implementation must not mutate it after completion.
    /// PING latency excludes concurrency admission time. Endpoints identify data nodes, never Sentinel monitor sockets.
    /// </remarks>
    ValueTask<RespireHealthProbeResult[]> ProbeHealthAsync(
        RespireHealthProbeOptions? options = null, CancellationToken cancellationToken = default);
}

/// <summary>Immutable settings for one existing-connection health-probe round.</summary>
public sealed record RespireHealthProbeOptions
{
    /// <summary>Probe every known primary and replica. Otherwise select one primary, preferring an existing usable connection.</summary>
    public bool ProbeAllNodes { get; init; }
    /// <summary>Maximum simultaneous PING operations. Defaults to eight; must be positive.</summary>
    public int MaxConcurrentProbes { get; init; } = 8;
    /// <summary>Whole-round deadline, including admission waits. Defaults to two seconds; must be a positive finite timer duration.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(2);

    internal void Validate()
    {
        if (MaxConcurrentProbes <= 0) throw new ArgumentOutOfRangeException(nameof(MaxConcurrentProbes));
        if (Timeout <= TimeSpan.Zero || Timeout.TotalMilliseconds > uint.MaxValue - 1d)
            throw new ArgumentOutOfRangeException(nameof(Timeout));
    }
}

/// <summary>An owned observation of one captured data endpoint, with no transport or disposal lifetime.</summary>
public sealed record RespireHealthProbeResult
{
    /// <summary>Creates a successful result with a latency, or a failed result with an exception and no latency.</summary>
    public RespireHealthProbeResult(RespireEndpoint endpoint, bool isConnected, TimeSpan? latency, Exception? error = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint.Host);
        if (endpoint.Port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(endpoint));
        if (latency < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(latency));
        if (error is null && (!isConnected || latency is null) || error is not null && latency is not null)
            throw new ArgumentException("Success requires a connected observation and latency; failure requires an exception and no latency.");
        Endpoint = endpoint;
        IsConnected = isConnected;
        Latency = latency;
        Error = error;
    }

    /// <summary>The data endpoint captured for this probe.</summary>
    public RespireEndpoint Endpoint { get; }
    /// <summary>Whether the captured connection accepted commands before admission to the probe limit.</summary>
    public bool IsConnected { get; }
    /// <summary>Successful PING round-trip time, excluding admission; null on failure.</summary>
    public TimeSpan? Latency { get; }
    /// <summary>The original node failure, or null on success. Treat exception details as sensitive diagnostics.</summary>
    public Exception? Error { get; }
}
