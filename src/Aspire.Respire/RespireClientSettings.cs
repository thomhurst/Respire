namespace Aspire.Respire;

/// <summary>Host integration settings under <c>Aspire:Respire</c>, optionally overridden by connection name.</summary>
public sealed class RespireClientSettings
{
    /// <summary>Connection string. A named <c>ConnectionStrings</c> entry takes precedence over configuration settings.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Disables automatic registration of the Respire connection health check.</summary>
    public bool DisableHealthChecks { get; set; }

    /// <summary>Disables adding the Respire activity source to the host's OpenTelemetry tracing provider.</summary>
    public bool DisableTracing { get; set; }

    /// <summary>Disables adding the Respire meter to the host's OpenTelemetry metrics provider.</summary>
    public bool DisableMetrics { get; set; }

    /// <summary>Uses a null logger factory, including when the host registers its own logger factory.</summary>
    public bool DisableLogging { get; set; }

    /// <summary>Copies registration values without retaining a caller-owned mutable settings object.</summary>
    internal RespireClientSettings Copy() => new()
    {
        ConnectionString = ConnectionString,
        DisableHealthChecks = DisableHealthChecks,
        DisableTracing = DisableTracing,
        DisableMetrics = DisableMetrics,
        DisableLogging = DisableLogging,
    };
}
