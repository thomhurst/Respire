using System.Diagnostics.CodeAnalysis;

namespace Respire;

/// <summary>The promotion policy for CLUSTER FAILOVER.</summary>
public enum RespireClusterFailoverMode
{
    /// <summary>Coordinate with the primary before promotion.</summary>
    Normal,
    /// <summary>Skip primary coordination but still obtain a majority vote.</summary>
    Force,
    /// <summary>Promote without primary coordination or a majority vote.</summary>
    Takeover,
}

/// <summary>The extent of a CLUSTER RESET.</summary>
public enum RespireClusterResetMode
{
    /// <summary>Retain the node identity and epochs.</summary>
    Soft,
    /// <summary>Generate a new node identity and clear epochs.</summary>
    Hard,
}

/// <summary>The local state change requested by CLUSTER SETSLOT.</summary>
public enum RespireClusterSlotState
{
    /// <summary>Import from the specified node.</summary>
    Importing,
    /// <summary>Migrate to the specified node.</summary>
    Migrating,
    /// <summary>Assign ownership to the specified node.</summary>
    Node,
    /// <summary>Clear importing and migrating state.</summary>
    Stable,
}

/// <summary>The resulting configuration epoch and whether CLUSTER BUMPEPOCH advanced it.</summary>
public readonly record struct RespireClusterEpochResult(bool Bumped, ulong Epoch);

/// <summary>An owned CLUSTER SLOTS member. Null, empty, and question-mark endpoints retain their server meanings.</summary>
/// <remarks>NodeId is absent on older servers. Metadata values are recursively GC-owned; disposal is optional.
/// Port zero means unavailable. This describes advertised networking, not a resolved connection endpoint.</remarks>
public sealed record RespireClusterSlotNode(string? Endpoint, int Port, string? NodeId,
    IReadOnlyDictionary<string, RespireResult> Metadata,
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "Response DTO retains its owned array storage without copying on access.")]
    RespireResult[] AdditionalValues);

/// <summary>An owned slot range, its primary, and its active replicas, as seen by the queried node.</summary>
/// <remarks>The replica array is caller-owned and mutable; record equality compares its reference.</remarks>
public sealed record RespireClusterSlotMapping(RespireClusterSlotRange Slots, RespireClusterSlotNode Primary,
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "Response DTO retains its owned array storage without copying on access.")]
    RespireClusterSlotNode[] Replicas);
