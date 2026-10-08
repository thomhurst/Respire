using TUnit.Core;

namespace Respire.Tests;

public static class TestConfiguration
{
    [Before(HookType.TestDiscovery)]
    public static void ConfigureReporting(BeforeTestDiscoveryContext context)
    {
        // Core tests own their telemetry listeners. The HTML reporter's process-wide
        // listener enables command tracing even when a test disables command metrics.
        // Keep plain local runs consistent with CI without an environment variable.
        context.Settings.Reporting.HtmlReportEnabled = false;
    }
}
