namespace Respire;

public sealed partial class RespireServerNode
{
    /// <summary>Reads this node's slot topology without updating client routing.</summary>
    public ValueTask<RespireClusterSlotMapping[]> ClusterSlotsAsync(CancellationToken cancellationToken = default)
        => ExecuteAsync("CLUSTER SLOTS", [], ClusterAdministrationParser.Slots, cancellationToken);

    /// <summary>Assigns unassigned slots to this node. Requires AllowAdmin.</summary>
    public ValueTask ClusterAddSlotsAsync(ReadOnlySpan<int> slots, CancellationToken cancellationToken = default)
        => MutationAsync("CLUSTER ADDSLOTS", SlotArguments(slots), cancellationToken);

    /// <summary>Assigns inclusive slot ranges to this node. Requires AllowAdmin and Redis 7.0 or later.</summary>
    public ValueTask ClusterAddSlotsRangeAsync(ReadOnlySpan<RespireClusterSlotRange> ranges, CancellationToken cancellationToken = default)
        => MutationAsync("CLUSTER ADDSLOTSRANGE", RangeArguments(ranges), cancellationToken);

    /// <summary>Removes this node's slot assignments. Requires AllowAdmin.</summary>
    public ValueTask ClusterDeleteSlotsAsync(ReadOnlySpan<int> slots, CancellationToken cancellationToken = default)
        => MutationAsync("CLUSTER DELSLOTS", SlotArguments(slots), cancellationToken);

    /// <summary>Removes inclusive slot ranges. Requires AllowAdmin and Redis 7.0 or later.</summary>
    public ValueTask ClusterDeleteSlotsRangeAsync(ReadOnlySpan<RespireClusterSlotRange> ranges, CancellationToken cancellationToken = default)
        => MutationAsync("CLUSTER DELSLOTSRANGE", RangeArguments(ranges), cancellationToken);

    /// <summary>Attempts to advance this node's configuration epoch without consensus. Requires AllowAdmin.</summary>
    public ValueTask<RespireClusterEpochResult> ClusterBumpEpochAsync(CancellationToken cancellationToken = default)
        => ExecuteAsync("CLUSTER BUMPEPOCH", [], ClusterAdministrationParser.Epoch, cancellationToken, NodeCallKind.Mutation);

    /// <summary>Counts this node's failure reports about the specified node.</summary>
    public ValueTask<long> ClusterCountFailureReportsAsync(string nodeId, CancellationToken cancellationToken = default)
        => ExecuteAsync("CLUSTER COUNT-FAILURE-REPORTS", [ClusterNodeId(nodeId)], AclParser.NonnegativeInteger, cancellationToken);

    /// <summary>Requests replica promotion. Completion acknowledges the request, not convergence. Requires AllowAdmin.</summary>
    public ValueTask ClusterFailoverAsync(RespireClusterFailoverMode mode = RespireClusterFailoverMode.Normal,
        CancellationToken cancellationToken = default)
        => MutationAsync("CLUSTER FAILOVER", mode switch
        {
            RespireClusterFailoverMode.Normal => [],
            RespireClusterFailoverMode.Force => ["FORCE"],
            RespireClusterFailoverMode.Takeover => ["TAKEOVER"],
            _ => throw InvalidClusterOption(nameof(mode)),
        }, cancellationToken);

    /// <summary>Forgets a node in this node's topology. Requires AllowAdmin.</summary>
    public ValueTask ClusterForgetAsync(string nodeId, CancellationToken cancellationToken = default)
        => MutationAsync("CLUSTER FORGET", [ClusterNodeId(nodeId)], cancellationToken);

    /// <summary>Returns up to count owned binary physical keys held locally in a slot. View prefixes are not removed.</summary>
    public ValueTask<byte[][]> ClusterGetKeysInSlotAsync(int slot, int count, CancellationToken cancellationToken = default)
    {
        ValidateClusterSlot(slot);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return ExecuteAsync("CLUSTER GETKEYSINSLOT", [slot, count], AclParser.ByteStrings, cancellationToken);
    }

    /// <summary>Introduces a TCP node to this node. Requires AllowAdmin. The optional bus port requires Redis 4.0 or later.</summary>
    public ValueTask ClusterMeetAsync(RespireEndpoint endpoint, int? busPort = null, CancellationToken cancellationToken = default)
    {
        ValidateEndpoint(endpoint, allowUnixSocket: false);
        if (busPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(busPort));
        return MutationAsync("CLUSTER MEET", busPort.HasValue
            ? [endpoint.Host, endpoint.Port, busPort.Value] : [endpoint.Host, endpoint.Port], cancellationToken);
    }

    /// <summary>Returns owned CLUSTER NODES rows for a primary's replicas, as seen by this node. Requires Redis 5.0 or later.</summary>
    public ValueTask<RespireClusterNode[]> ClusterReplicasAsync(string nodeId, CancellationToken cancellationToken = default)
        => ExecuteAsync("CLUSTER REPLICAS", [ClusterNodeId(nodeId)], ClusterAdministrationParser.Replicas, cancellationToken);

    /// <summary>Configures this node to replicate the specified primary. Requires AllowAdmin.</summary>
    public ValueTask ClusterReplicateAsync(string nodeId, CancellationToken cancellationToken = default)
        => MutationAsync("CLUSTER REPLICATE", [ClusterNodeId(nodeId)], cancellationToken);

    /// <summary>Resets this node's Cluster state. Hard reset also replaces its identity and epochs. Requires AllowAdmin.</summary>
    public ValueTask ClusterResetAsync(RespireClusterResetMode mode = RespireClusterResetMode.Soft,
        CancellationToken cancellationToken = default)
        => MutationAsync("CLUSTER RESET", mode switch
        {
            RespireClusterResetMode.Soft => ["SOFT"],
            RespireClusterResetMode.Hard => ["HARD"],
            _ => throw InvalidClusterOption(nameof(mode)),
        }, cancellationToken);

    /// <summary>Persists this node's Cluster configuration. Requires AllowAdmin.</summary>
    public ValueTask ClusterSaveConfigAsync(CancellationToken cancellationToken = default)
        => MutationAsync("CLUSTER SAVECONFIG", [], cancellationToken);

    /// <summary>Sets a fresh node's configuration epoch. Requires AllowAdmin.</summary>
    public ValueTask ClusterSetConfigEpochAsync(long epoch, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(epoch);
        return MutationAsync("CLUSTER SET-CONFIG-EPOCH", [epoch], cancellationToken);
    }

    /// <summary>Changes local slot ownership or migration state. Stable requires no node ID; other states require one. Requires AllowAdmin.</summary>
    public ValueTask ClusterSetSlotAsync(int slot, RespireClusterSlotState state, string? nodeId = null,
        CancellationToken cancellationToken = default)
    {
        ValidateClusterSlot(slot);
        var token = state switch
        {
            RespireClusterSlotState.Importing => "IMPORTING",
            RespireClusterSlotState.Migrating => "MIGRATING",
            RespireClusterSlotState.Node => "NODE",
            RespireClusterSlotState.Stable => "STABLE",
            _ => throw InvalidClusterOption(nameof(state)),
        };
        if (state != RespireClusterSlotState.Stable)
            return MutationAsync("CLUSTER SETSLOT", [slot, token, ClusterNodeId(nodeId)], cancellationToken);
        if (nodeId is not null) throw new ArgumentException("Stable does not accept a node ID.", nameof(nodeId));
        return MutationAsync("CLUSTER SETSLOT", [slot, token], cancellationToken);
    }

    /// <summary>Removes all local slot assignments; the server requires an empty database. Requires AllowAdmin.</summary>
    public ValueTask ClusterFlushSlotsAsync(CancellationToken cancellationToken = default)
        => MutationAsync("CLUSTER FLUSHSLOTS", [], cancellationToken);

    private static string ClusterNodeId(string? nodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        foreach (var character in nodeId)
            if (char.IsWhiteSpace(character))
                throw new ArgumentException("Node ID must not contain whitespace.", nameof(nodeId));
        return nodeId;
    }

    private static ArgumentOutOfRangeException InvalidClusterOption(string parameterName)
        => new(parameterName);

    private static void ValidateClusterSlot(int slot)
    {
        if (slot is < 0 or >= 16384) throw new ArgumentOutOfRangeException(nameof(slot), "Slot must be between 0 and 16383.");
    }

    private static RespireValue[] SlotArguments(ReadOnlySpan<int> slots)
    {
        if (slots.IsEmpty) throw new ArgumentException("At least one slot is required.", nameof(slots));
        var arguments = new RespireValue[slots.Length];
        for (var index = 0; index < slots.Length; index++)
        {
            ValidateClusterSlot(slots[index]);
            arguments[index] = slots[index];
        }
        return arguments;
    }

    private static RespireValue[] RangeArguments(ReadOnlySpan<RespireClusterSlotRange> ranges)
    {
        if (ranges.IsEmpty) throw new ArgumentException("At least one range is required.", nameof(ranges));
        var arguments = new RespireValue[checked(ranges.Length * 2)];
        for (var index = 0; index < ranges.Length; index++)
        {
            var range = ranges[index];
            ValidateClusterSlot(range.Start);
            ValidateClusterSlot(range.End);
            if (range.Start > range.End) throw new ArgumentException("Slot range must not be reversed.", nameof(ranges));
            arguments[index * 2] = range.Start;
            arguments[index * 2 + 1] = range.End;
        }
        return arguments;
    }
}
