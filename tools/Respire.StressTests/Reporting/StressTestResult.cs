using Respire.StressTests.Metrics;

namespace Respire.StressTests.Reporting;

internal sealed class StressTestResult
{
    /// <summary>
    /// Minimum per-second samples before drift metrics are computed; below this the
    /// thirds are too small to mean anything.
    /// </summary>
    private const int MinSamplesForDrift = 6;

    public required string Scenario { get; init; }
    public required string ScenarioDescription { get; init; }
    public required string Client { get; init; }
    public required int DurationSeconds { get; init; }
    public required int WarmupSeconds { get; init; }
    public required int Concurrency { get; init; }
    public required int ValueSizeBytes { get; init; }
    public required DateTime StartedAtUtc { get; init; }
    public required DateTime CompletedAtUtc { get; init; }
    public required double ElapsedSeconds { get; init; }
    public required long Operations { get; init; }
    public required long Errors { get; init; }
    public required IReadOnlyList<string> ErrorSamples { get; init; }
    public required IReadOnlyList<double> OperationsPerSecondSamples { get; init; }
    public required LatencySnapshot Latency { get; init; }
    public required GcSnapshot Gc { get; init; }
    public required double CpuTimeSeconds { get; init; }

    /// <summary>True when the watchdog saw no completed operations for its full window.</summary>
    public required bool Stalled { get; init; }

    public double OperationsPerSecond =>
        ElapsedSeconds > 0 ? Operations / ElapsedSeconds : 0;

    public double? CpuMicrosecondsPerOperation =>
        Operations > 0 ? CpuTimeSeconds * 1_000_000.0 / Operations : null;

    public double? AllocatedBytesPerOperation =>
        Operations > 0 ? (double)Gc.AllocatedBytes / Operations : null;

    /// <summary>
    /// Percentage change from the first-third to the last-third average of the sampled
    /// per-second throughput. Sustained negative drift indicates degradation over time
    /// (leaks, fragmentation, backlog growth) that a short benchmark cannot see.
    /// </summary>
    public double? ThroughputDriftPercent
    {
        get
        {
            var samples = OperationsPerSecondSamples;
            if (samples.Count < MinSamplesForDrift)
                return null;

            var third = samples.Count / 3;
            var first = samples.Take(third).Average();
            var last = samples.Skip(samples.Count - third).Average();
            return first > 0 ? (last - first) / first * 100.0 : null;
        }
    }

    public bool Failed => Errors > 0 || Stalled;

    public string Status => Stalled ? "STALLED" : Errors > 0 ? "ERRORS" : "OK";
}
