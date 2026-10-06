using System.Diagnostics.CodeAnalysis;

namespace Respire;

/// <summary>An inclusive Cluster hash-slot range.</summary>
public readonly record struct RespireClusterSlotRange(int Start, int End);

/// <summary>A migrating or importing slot annotation in CLUSTER NODES.</summary>
public sealed record RespireClusterSlotTransition(int Slot, string Direction, string PeerNodeId);

/// <summary>One owned CLUSTER NODES row, as seen by the queried node.</summary>
/// <remarks>Address preserves the full advertised address, bus port, hostname and future suffixes.
/// Unknown flag values, link states and trailing tokens are preserved.
/// Arrays are caller-owned and mutable; record equality compares their references, not their contents.</remarks>
public sealed record RespireClusterNode(
    string Id, string Address,
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "Response DTO retains its existing array storage without copying on access.")]
    string[] Flags, string? PrimaryId,
    long PingSentMilliseconds, long PongReceivedMilliseconds, long ConfigurationEpoch,
    string LinkState,
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "Response DTO retains its existing array storage without copying on access.")]
    RespireClusterSlotRange[] Slots,
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "Response DTO retains its existing array storage without copying on access.")]
    RespireClusterSlotTransition[] Transitions,
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "Response DTO retains its existing array storage without copying on access.")]
    string[] AdditionalTokens);

/// <summary>One node's owned CLUSTER INFO report. Attributes retain every reported field.</summary>
public sealed record RespireClusterInfo(
    string State, long? SlotsAssigned, long? SlotsOk, long? SlotsPossiblyFailing, long? SlotsFailing,
    long? KnownNodes, long? ClusterSize, long? CurrentEpoch, long? MyEpoch,
    IReadOnlyDictionary<string, string> Attributes);

/// <summary>An owned CLUSTER SHARDS member. Endpoint values, including null, empty and ?, are not rewritten.</summary>
/// <remarks>Plain and TLS ports are independently optional. Zero means unavailable.
/// Unknown fields contain recursively copied GC-owned results; disposal is optional.</remarks>
public sealed record RespireClusterShardNode(
    string Id, string Role, string Health, long ReplicationOffset, string? Endpoint,
    string? Ip, string? Hostname, int? Port, int? TlsPort,
    IReadOnlyDictionary<string, RespireResult> AdditionalFields);

/// <summary>An owned shard description from one node's view, not a globally reconciled topology.</summary>
/// <remarks>Unknown fields contain GC-owned results; disposal is optional.
/// Arrays are caller-owned and mutable; record equality compares their references, not their contents.</remarks>
public sealed record RespireClusterShard(
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "Response DTO retains its existing array storage without copying on access.")]
    RespireClusterSlotRange[] Slots,
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "Response DTO retains its existing array storage without copying on access.")]
    RespireClusterShardNode[] Nodes,
    IReadOnlyDictionary<string, RespireResult> AdditionalFields);

/// <summary>An owned Cluster bus link. Creation time is Unix milliseconds.</summary>
/// <remarks>Direction and Events retain server text. Unknown fields contain GC-owned results; disposal is optional.</remarks>
public sealed record RespireClusterLink(
    string Direction, string NodeId, long CreatedUnixMilliseconds, string Events,
    long SendBufferAllocated, long SendBufferUsed, IReadOnlyDictionary<string, RespireResult> AdditionalFields);

/// <summary>Owned usage statistics for one slot on one node. Disabled metrics are null.</summary>
/// <remarks>Unknown fields contain GC-owned results; disposal is optional.</remarks>
public sealed record RespireClusterSlotStats(
    int Slot, long KeyCount, long? MemoryBytes, long? CpuMicroseconds, long? NetworkBytesIn,
    long? NetworkBytesOut, IReadOnlyDictionary<string, RespireResult> AdditionalFields);

/// <summary>The metric used to order CLUSTER SLOT-STATS results.</summary>
public enum RespireClusterSlotMetric
{
    /// <summary>Number of keys in the slot.</summary>
    KeyCount,
    /// <summary>Memory allocated for the slot, in bytes.</summary>
    MemoryBytes,
    /// <summary>CPU time spent handling the slot, in microseconds.</summary>
    CpuMicroseconds,
    /// <summary>Incoming network bytes attributed to the slot.</summary>
    NetworkBytesIn,
    /// <summary>Outgoing network bytes attributed to the slot.</summary>
    NetworkBytesOut,
}
