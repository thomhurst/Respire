using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public partial class SentinelIdentityTests
{
    private const string MatchingProbeMode = "RESPIRE_SENTINEL_MATCHING_PROBE";
    private const string MatchingProbeRuntime = "RESPIRE_SENTINEL_MATCHING_RUNTIME";
    private const string MatchingProbeHandshake = "RESPIRE_SENTINEL_MATCHING_HANDSHAKE";

    [Test]
    [NotInParallel]
    [Arguments("allocating", "Expected zero Sentinel matching allocations")]
    [Arguments("lost-region", "no-GC region was not retained")]
    public async Task IsolatedMatchingPreservesStrictFailures(string mode, string expected)
    {
        var failure = await Assert.That(() => RunMatchingProbeAsync(mode)).Throws<InvalidOperationException>();
        await Assert.That(failure!.Message).Contains(expected);
    }

    [Test]
    [NotInParallel]
    public async Task RunnerCollectionCannotInterruptIsolatedMatchingRegion()
    {
        var handshake = Path.Combine(Path.GetTempPath(), $"respire-sentinel-{Guid.NewGuid():N}");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var collected = false;
        async Task CollectWhenChildRegionIsOpen()
        {
            while (!File.Exists(handshake + ".ready")) await Task.Delay(10, deadline.Token);
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            collected = true;
            await File.WriteAllTextAsync(handshake + ".continue", "", deadline.Token);
        }

        var activity = CollectWhenChildRegionIsOpen();
        try
        {
            await RunMatchingProbeAsync("normal", handshake);
            await activity;
            await Assert.That(collected).IsTrue();
        }
        finally
        {
            deadline.Cancel();
            try { await activity; }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
            File.Delete(handshake + ".ready");
            File.Delete(handshake + ".continue");
        }
    }

    private static async Task RunMatchingProbeAsync(string mode, string? handshake = null)
    {
        var start = AsyncFlushSignalTests.CreateProbeStartInfo(Environment.ProcessPath,
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH"), typeof(SentinelIdentityTests).Assembly.Location);
        start.Environment[MatchingProbeMode] = mode;
        start.Environment[MatchingProbeRuntime] = Environment.Version.ToString();
        if (handshake is not null) start.Environment[MatchingProbeHandshake] = handshake;
        await AsyncFlushSignalTests.RunProbeAsync(start, TimeSpan.FromSeconds(30));
    }

    internal static int? RunIsolatedAllocationProbe()
    {
        var mode = Environment.GetEnvironmentVariable(MatchingProbeMode);
        if (mode is null) return null;
        try
        {
            if (Environment.GetEnvironmentVariable(MatchingProbeRuntime) != Environment.Version.ToString())
                throw new InvalidOperationException("Sentinel matching probe runtime differs from parent.");
            MeasureNormalizedEvidence(mode, Environment.GetEnvironmentVariable(MatchingProbeHandshake));
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
