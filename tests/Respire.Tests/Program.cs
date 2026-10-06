using System.Diagnostics.CodeAnalysis;
using Respire.Tests.Networking;

namespace Respire.Tests;

internal static class Program
{
    [SuppressMessage("Usage", "TUnit0034", Justification =
        "The probe must run after module initialization and before the runner. Normal tests use the generated application helper.")]
    public static Task<int> Main(string[] args)
    {
        // Enter probes after module initialization, before the runner changes pool capacity.
        var probeResult = AsyncFlushSignalTests.RunIsolatedAllocationProbe();
        return probeResult is { } exitCode
            ? Task.FromResult(exitCode)
            : MicrosoftTestingPlatformApplication.RunAsync(args);
    }
}
