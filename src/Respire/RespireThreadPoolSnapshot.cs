namespace Respire;

/// <summary>An immutable process-wide scheduling probe and accompanying thread-pool counters.</summary>
/// <remarks>This is a diagnostic observation, not proof of starvation. When IsPending is true,
/// SchedulingDelay is a lower bound because the queued work item has not run yet.</remarks>
public sealed record RespireThreadPoolSnapshot(
    DateTimeOffset CapturedAt,
    TimeSpan SchedulingDelay,
    bool IsPending,
    int BusyWorkerThreads,
    int MinWorkerThreads,
    long PendingWorkItems);
