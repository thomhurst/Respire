namespace Respire;

/// <summary>Preferred server role for commands whose command metadata confirms they are read-only.</summary>
/// <remarks>
/// Replication to replicas is asynchronous. Every policy that can select a replica may return stale
/// data, including a read immediately after a successful write. These policies do not guarantee
/// read-your-writes consistency when a replica serves the read.
/// </remarks>
public enum RespireReadFrom
{
    /// <summary>Send every command to the primary.</summary>
    Primary,
    /// <summary>
    /// Prefer the primary. Use a replica when the primary cannot accept a connection. In Cluster mode, retry once
    /// on a replica when the primary replies <c>LOADING</c>, <c>MASTERDOWN</c>, or <c>CLUSTERDOWN</c>.
    /// Reads served by that fallback replica may be stale.
    /// </summary>
    PrimaryPreferred,
    /// <summary>
    /// Send eligible reads to a replica. Fail when no healthy replica is available.
    /// In Cluster mode, fail on ASK redirects to an importing primary, or when replica topology
    /// cannot be refreshed after a redirect.
    /// Reads may be stale, including immediately after a successful write.
    /// </summary>
    Replica,
    /// <summary>
    /// Prefer a replica. Use the primary when no healthy replica is available. In Cluster mode, retry once on the
    /// primary when the replica replies <c>LOADING</c>, <c>MASTERDOWN</c>, or <c>CLUSTERDOWN</c>.
    /// Reads served by the preferred replica may be stale; primary fallback does not make the policy consistent.
    /// </summary>
    ReplicaPreferred,
    /// <summary>
    /// Select the healthy eligible primary or replica with the lowest sampled PING latency.
    /// Ties and absent measurements rotate. Sampling is bounded and advisory; it includes local
    /// connection queueing and does not measure replication lag. Replica reads may be stale.
    /// </summary>
    Nearest,
    /// <summary>Prefer replicas in the client's availability zone, then other replicas, then the primary.</summary>
    AzAffinity,
    /// <summary>
    /// Prefer linked replicas in the client's availability zone, then a primary in that zone,
    /// then other linked replicas, then unlinked replicas (same-zone first), then the primary in any zone.
    /// </summary>
    AzAffinityReplicasAndPrimary,
}
