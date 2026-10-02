namespace Respire;

/// <summary>Preferred server role for commands whose command metadata confirms they are read-only.</summary>
public enum RespireReadFrom
{
    /// <summary>Send every command to the primary.</summary>
    Primary,
    /// <summary>
    /// Prefer the primary. Use a replica when the primary cannot accept a connection, or retry once
    /// on a replica when the primary replies <c>LOADING</c>, <c>MASTERDOWN</c>, or <c>CLUSTERDOWN</c>.
    /// </summary>
    PrimaryPreferred,
    /// <summary>Send eligible reads to a replica. Fail when no healthy replica is available.</summary>
    Replica,
    /// <summary>
    /// Prefer a replica. Use the primary when no healthy replica is available, or retry once on the
    /// primary when the replica replies <c>LOADING</c>, <c>MASTERDOWN</c>, or <c>CLUSTERDOWN</c>.
    /// </summary>
    ReplicaPreferred,
}
