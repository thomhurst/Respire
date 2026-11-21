namespace Respire.Aspire;

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

    /// <summary>Reads the connection string and <c>Options</c> sections when the client is first resolved instead of at registration.</summary>
    /// <remarks>Lets configuration added after registration, such as a test host's endpoint override, reach the client.
    /// A connection string set by the settings callback still takes precedence. The other settings are read at registration.</remarks>
    public bool DeferConfiguration { get; set; }

    /// <summary>Copies registration values without retaining a caller-owned mutable settings object.</summary>
    internal RespireClientSettings Copy() => new()
    {
        ConnectionString = ConnectionString,
        DisableHealthChecks = DisableHealthChecks,
        DisableTracing = DisableTracing,
        DisableMetrics = DisableMetrics,
        DisableLogging = DisableLogging,
        DeferConfiguration = DeferConfiguration,
    };
}
