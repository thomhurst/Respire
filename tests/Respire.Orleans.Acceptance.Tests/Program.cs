using System.Diagnostics.CodeAnalysis;

namespace Respire.Orleans.Acceptance.Tests;

internal static class Program
{
    [SuppressMessage("Usage", "TUnit0034", Justification = "A child silo must start without entering the test runner.")]
    public static async Task<int> Main(string[] args)
    {
        if (args is ["--child-silo", var endpoint, var serviceId, var clusterId])
        {
            await SiloScenarios.RunChildSilo(endpoint, serviceId, clusterId);
            return 0;
        }
        return await MicrosoftTestingPlatformApplication.RunAsync(args);
    }
}
