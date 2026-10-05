namespace Respire.Tests;

// Call only in an unkeyed NotInParallel test: configuration is process-wide.
internal sealed class MetricConfigurationScope : IDisposable
{
    private readonly RespireMetricsOptions _previous = RespireMetrics.Configuration;

    internal MetricConfigurationScope(RespireMetricsOptions? options = null)
        => RespireMetrics.Configure(options ?? new() { Groups = RespireMetricGroups.All });

    public void Dispose() => RespireMetrics.Configure(_previous);
}
