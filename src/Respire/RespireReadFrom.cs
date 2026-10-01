namespace Respire;

/// <summary>Preferred server role for commands whose catalog metadata confirms they are read-only.</summary>
public enum RespireReadFrom
{
    /// <summary>Send every command to the primary.</summary>
    Primary,
    /// <summary>Prefer the primary. Use a replica only when the primary cannot accept a connection.</summary>
    PrimaryPreferred,
    /// <summary>Send eligible reads to a replica. Fail when no healthy replica is available.</summary>
    Replica,
    /// <summary>Prefer a replica. Use the primary when no healthy replica is available.</summary>
    ReplicaPreferred,
}
