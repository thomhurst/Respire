using Respire.Internal;

namespace Respire.Tests;

// Call only in an unkeyed NotInParallel test: configuration is process-wide.
internal sealed class MetricConfigurationScope : IDisposable
{
    private readonly RespireMetricsOptions _previous = RespireMetrics.Configuration;

    internal MetricConfigurationScope(RespireMetricsOptions? options = null)
        => RespireMetrics.Configure(options ?? new() { Groups = RespireMetricGroups.All });

    public void Dispose()
    {
        try
        {
            // Close events can outlive socket disposal. Do not let them reach the next
            // test's listener, especially when pool identities share the overflow series.
            // Blocking-listener tests release their callbacks before this outer scope ends.
            if (!SpinWait.SpinUntil(() => ConnectionTelemetry.PendingCloseMeasurements == 0,
                TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Close metric delivery did not drain before restoring metric configuration.");
        }
        finally { RespireMetrics.Configure(_previous); }
    }
}
