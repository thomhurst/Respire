namespace Respire;

/// <summary>Settings for an opt-in endpoint circuit breaker.</summary>
/// <remarks>Set <see cref="RespireOptions.CircuitBreaker"/> to enable standalone or Cluster data endpoint admission.</remarks>
public sealed record RespireCircuitBreakerOptions
{
    /// <summary>Failure fraction required to open the circuit, from greater than zero through one. Defaults to 0.5.</summary>
    public double FailureRateThreshold { get; init; } = 0.5;

    /// <summary>Minimum failures required to open the circuit. Defaults to five.</summary>
    public int MinimumFailureCount { get; init; } = 5;

    /// <summary>Maximum age of completed outcomes in the rolling history. Defaults to 30 seconds.</summary>
    public TimeSpan SamplingWindow { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum number of recent completed outcomes retained, from one through 65536. Defaults to 1024.</summary>
    /// <remarks>The failure rate uses the most recent outcomes within both this count limit and SamplingWindow.</remarks>
    public int MaximumSampleCount { get; init; } = 1024;

    /// <summary>Delay before an open circuit admits recovery probes. Defaults to five seconds.</summary>
    public TimeSpan OpenDuration { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Successful recovery probes required to close the circuit, and maximum concurrent probes. Defaults to one.</summary>
    public int HalfOpenProbeCount { get; init; } = 1;

    internal void Validate()
    {
        if (!double.IsFinite(FailureRateThreshold) || FailureRateThreshold <= 0 || FailureRateThreshold > 1)
            throw new ArgumentOutOfRangeException(nameof(FailureRateThreshold));
        if (MaximumSampleCount is < 1 or > 65536)
            throw new ArgumentOutOfRangeException(nameof(MaximumSampleCount));
        if (MinimumFailureCount < 1 || MinimumFailureCount > MaximumSampleCount)
            throw new ArgumentOutOfRangeException(nameof(MinimumFailureCount));
        if (SamplingWindow <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(SamplingWindow));
        if (OpenDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(OpenDuration));
        if (HalfOpenProbeCount is < 1 or > 65536) throw new ArgumentOutOfRangeException(nameof(HalfOpenProbeCount));
    }
}

/// <summary>An endpoint circuit rejected an operation before dispatch.</summary>
public sealed class RespireCircuitOpenException : RespireException
{
    /// <summary>Creates a rejection for an endpoint and its remaining recovery delay.</summary>
    /// <param name="endpoint">The rejected data endpoint.</param>
    /// <param name="retryAfter">Remaining monotonic delay, or null when recovery probes already occupy all admission slots.</param>
    public RespireCircuitOpenException(RespireEndpoint endpoint, TimeSpan? retryAfter)
        : base($"The circuit for endpoint {endpoint} is open.")
    {
        if (retryAfter < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retryAfter));
        Endpoint = endpoint;
        RetryAfter = retryAfter;
        IsCommandNotSubmitted = true;
    }

    /// <summary>The rejected data endpoint.</summary>
    public RespireEndpoint Endpoint { get; }

    /// <summary>Remaining recovery delay, or null when probe completion determines the next admission.</summary>
    /// <remarks>This is a snapshot, not permission to resend a command. A retry must reacquire endpoint admission.</remarks>
    public TimeSpan? RetryAfter { get; }
}
