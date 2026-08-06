namespace Respire.StressTests.Scenarios;

/// <summary>
/// Per-worker state created once before the worker loop starts, so scenario bodies
/// issue no per-operation key allocations. Shared "seeded" keys are written once by
/// the runner before the pass; "worker" keys are private to one worker.
/// </summary>
internal sealed class WorkerContext
{
    public required int WorkerIndex { get; init; }
    public required string Payload { get; init; }
    public required string SeededStringKey { get; init; }
    public required string SeededHashKey { get; init; }
    public required string WorkerStringKey { get; init; }
    public required string WorkerCounterKey { get; init; }
    public required string WorkerHashKey { get; init; }
    public required string WorkerListKey { get; init; }

    /// <summary>Loop counter used by mixed workloads to pick the next operation.</summary>
    public long Iteration;
}
