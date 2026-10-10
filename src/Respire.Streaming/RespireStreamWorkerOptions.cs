namespace Respire.Streaming;

/// <summary>Immutable settings captured when registering a hosted stream consumer.</summary>
public sealed record RespireStreamWorkerOptions
{
    /// <summary>Stable consumer-name base, suffixed with each reader's index. Null creates unique names.</summary>
    /// <remarks>Reuse the same name and consumer count to replay owned pending entries once at startup.
    /// Active registrations and hosts sharing a group must use different names.</remarks>
    public string? ConsumerName { get; init; }

    /// <summary>Maximum simultaneous handlers and blocking readers. Defaults to one.</summary>
    public int ConsumerCount { get; init; } = 1;

    /// <summary>Maximum entries fetched per reader. Each reader processes its batch sequentially.</summary>
    public int BatchSize { get; init; } = 1;

    /// <summary>Positive blocking wait for new entries. Defaults to five seconds.</summary>
    public TimeSpan ReadWait { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Positive visibility timeout before pending entries can be recovered. Defaults to one minute.</summary>
    /// <remarks>On Redis 8.8 or later, unsuccessful processing uses fenced XNACK release only after this delivery's timeout has elapsed.
    /// Younger failures remain pending for ordinary recovery without holding a reader.</remarks>
    public TimeSpan MinimumIdleTime { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Positive interval between bounded recovery scans per reader. Defaults to five seconds.</summary>
    public TimeSpan RecoveryPollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Delete acknowledged entries when every group has read and acknowledged them. Defaults to false.</summary>
    /// <remarks>Uses fenced XACKDEL ACKED on Redis 8.2 or later. Older servers acknowledge with XACK and retain the body.
    /// Dead-letter completion always retains the source body for other groups.</remarks>
    public bool DeleteAcknowledgedEntries { get; init; }

    /// <summary>Logical stream key for atomic dead-letter completion. Must share the source's resolved Cluster slot.</summary>
    /// <remarks>Enabling dead-letter completion limits each source entry to 1024 field/value pairs.
    /// A larger delivery faults the worker before invoking its handler and remains pending without a dead-letter write.</remarks>
    public string? DeadLetterStream { get; init; }

    /// <summary>Maximum delivery attempts before unsuccessful processing is dead-lettered. Null retries without a limit.</summary>
    /// <remarks>Counts initial deliveries, startup replay and recovery claims. Requires DeadLetterStream.</remarks>
    public int? DeliveryLimit { get; init; }

    /// <summary>Whether startup creates the stream and group. Existing groups are retained.</summary>
    public bool CreateGroup { get; init; } = true;

    /// <summary>Initial position for a newly created group. Defaults to the beginning.</summary>
    public RespireStreamId GroupStart { get; init; } = RespireStreamId.Beginning;

    /// <summary>Low-cardinality registration label matching [A-Za-z0-9._-]{1,128} for worker metrics and traces.</summary>
    /// <remarks>Names are case-sensitive and must be unique within the service collection, including the default.
    /// Use a fixed application role, never a message, tenant or consumer identifier.</remarks>
    public string TelemetryName { get; init; } = "default";

    /// <summary>Interval of at least one millisecond between group metric polls. No queries run without a gauge listener.</summary>
    public TimeSpan MetricsPollInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Deadline of at least one millisecond for each group metric query. Failures do not stop message processing.</summary>
    public TimeSpan MetricsPollTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Stream field containing a W3C version-00 traceparent. Null disables parent extraction.</summary>
    public string? TraceParentField { get; init; } = "traceparent";

    /// <summary>Optional stream field containing W3C tracestate. Invalid state is ignored.</summary>
    public string? TraceStateField { get; init; } = "tracestate";

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(TelemetryName);
        if (TelemetryName.Length > 128) throw new ArgumentOutOfRangeException(nameof(TelemetryName));
        foreach (var character in TelemetryName)
        {
            if (character is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-'))
                throw new ArgumentException("Telemetry names must match [A-Za-z0-9._-]{1,128}.", nameof(TelemetryName));
        }
        if (TraceParentField is not null) ArgumentException.ThrowIfNullOrWhiteSpace(TraceParentField);
        if (TraceStateField is not null) ArgumentException.ThrowIfNullOrWhiteSpace(TraceStateField);
        if (TraceParentField is not null && TraceParentField == TraceStateField)
            throw new ArgumentException("Trace context fields must have distinct names.");
        ValidatePollTime(MetricsPollInterval, nameof(MetricsPollInterval));
        ValidatePollTime(MetricsPollTimeout, nameof(MetricsPollTimeout));
        if (ConsumerName is not null) ArgumentException.ThrowIfNullOrWhiteSpace(ConsumerName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ConsumerCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(BatchSize);
        if (DeadLetterStream is not null) ArgumentException.ThrowIfNullOrWhiteSpace(DeadLetterStream);
        if (DeliveryLimit is { } limit)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
            if (DeadLetterStream is null)
                throw new ArgumentException("A delivery limit requires a dead-letter stream.", nameof(DeadLetterStream));
        }
        if (ReadWait <= TimeSpan.Zero || ReadWait > TimeSpan.FromMilliseconds(int.MaxValue))
            throw new ArgumentOutOfRangeException(nameof(ReadWait), "Read wait must be positive and at most Int32.MaxValue milliseconds.");
        if (MinimumIdleTime <= TimeSpan.Zero || MinimumIdleTime > TimeSpan.FromMilliseconds(int.MaxValue))
            throw new ArgumentOutOfRangeException(nameof(MinimumIdleTime), "Minimum idle time must be positive and at most Int32.MaxValue milliseconds.");
        if (RecoveryPollInterval <= TimeSpan.Zero || RecoveryPollInterval > TimeSpan.FromMilliseconds(int.MaxValue))
            throw new ArgumentOutOfRangeException(nameof(RecoveryPollInterval), "Recovery polling must be positive and at most Int32.MaxValue milliseconds.");
    }

    private static void ValidatePollTime(TimeSpan value, string name)
    {
        if (value < TimeSpan.FromMilliseconds(1) || value > TimeSpan.FromMilliseconds(int.MaxValue))
            throw new ArgumentOutOfRangeException(name, "Metric polling times must be at least one and at most Int32.MaxValue milliseconds.");
    }
}
