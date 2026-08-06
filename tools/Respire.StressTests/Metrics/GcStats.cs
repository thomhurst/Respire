namespace Respire.StressTests.Metrics;

/// <summary>
/// Tracks process-wide managed allocations, GC collection counts, and GC pause time
/// for one stress pass. Create at the start of the measured window, then call
/// <see cref="Capture"/> after it to compute deltas. Only the client under test is
/// active during the window, so process-wide deltas attribute to that client plus
/// a harness overhead that is identical across clients.
/// </summary>
internal sealed class GcStats
{
    private readonly int _gen0Before = GC.CollectionCount(0);
    private readonly int _gen1Before = GC.CollectionCount(1);
    private readonly int _gen2Before = GC.CollectionCount(2);
    private readonly long _allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
    private readonly double _pauseMsBefore = GC.GetTotalPauseDuration().TotalMilliseconds;

    public GcSnapshot Capture() => new()
    {
        Gen0Collections = GC.CollectionCount(0) - _gen0Before,
        Gen1Collections = GC.CollectionCount(1) - _gen1Before,
        Gen2Collections = GC.CollectionCount(2) - _gen2Before,
        AllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - _allocatedBefore,
        PauseMs = GC.GetTotalPauseDuration().TotalMilliseconds - _pauseMsBefore
    };
}

internal sealed class GcSnapshot
{
    public required int Gen0Collections { get; init; }
    public required int Gen1Collections { get; init; }
    public required int Gen2Collections { get; init; }
    public required long AllocatedBytes { get; init; }
    public required double PauseMs { get; init; }
}
