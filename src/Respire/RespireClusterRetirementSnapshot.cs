namespace Respire;

/// <summary>An owned, immutable observation of retained Cluster generations.</summary>
/// <remarks>
/// Membership is captured under the router gate; transport and pool counters can change
/// concurrently. Categories can overlap. Zero pending fences does not prove completion
/// while a transport is still draining or publishing its identities. This snapshot holds
/// no connections, pools, exceptions, or router references and remains usable after disposal.
/// </remarks>
public sealed class RespireClusterRetirementSnapshot
{
    internal RespireClusterRetirementSnapshot() { }

    /// <summary>UTC time at which capture began.</summary>
    public DateTimeOffset CapturedAt { get; internal init; }
    /// <summary>Detached generations still owned for drain, fencing, or failed cleanup.</summary>
    public int RetiringGenerationCount { get; internal init; }
    /// <summary>Monotonic elapsed age of the oldest retained generation; zero when none remain.</summary>
    public TimeSpan OldestRetirementAge { get; internal init; }
    /// <summary>Generations whose transport drain and identity-publication boundary has not completed.</summary>
    /// <remarks>Includes a generation whose cleanup failed before that boundary.</remarks>
    public int UndrainedGenerationCount { get; internal init; }
    /// <summary>Transport-drained generations with at least one unacknowledged correction fence.</summary>
    /// <remarks>Dedicated borrowed operations may still be active for these generations.</remarks>
    public int AwaitingFenceGenerationCount { get; internal init; }
    /// <summary>Distinct captured physical-peer/client-ID obligations currently owed by retained generations.</summary>
    public long PendingCorrectionFenceCount { get; internal init; }
    /// <summary>Generations with an observed unexpected transport, pool, or retirement cleanup failure.</summary>
    /// <remarks>Recoverable fence failures alone do not increment this count.</remarks>
    public int CleanupFailedGenerationCount { get; internal init; }
    /// <summary>Borrowed connections in dedicated operation pools attached to retained generations.</summary>
    /// <remarks>Excludes correction/control pools and pools belonging to active generations.</remarks>
    public long BorrowedDedicatedConnectionCount { get; internal init; }
    /// <summary>Unfinished connection acquisitions in those dedicated operation pools.</summary>
    public long ConnectingDedicatedConnectionCount { get; internal init; }
}
