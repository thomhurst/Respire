namespace Respire;

/// <summary>One Valkey slot export group. Ranges are inclusive and target a primary node ID.</summary>
public sealed record RespireValkeySlotMigrationGroup(string TargetNodeId, IReadOnlyList<RespireClusterSlotRange> Slots);

/// <summary>An owned Valkey slot migration snapshot. Times use Unix seconds on the wire.</summary>
/// <remarks>SourceNodeId and TargetNodeId are absent on tracking replicas. RemainingReplicationBytes is
/// absent before Valkey 9.1. State and Operation retain unknown future values.</remarks>
public sealed record RespireValkeySlotMigration(
    string Name, string Operation, string SlotRanges, string? SourceNodeId, string? TargetNodeId,
    DateTimeOffset CreateTime, DateTimeOffset LastUpdateTime, DateTimeOffset LastAcknowledgementTime,
    string State, string Message, long CopyOnWriteBytes, long? RemainingReplicationBytes,
    IReadOnlyDictionary<string, RespireResult> AdditionalFields)
{
    /// <summary>Whether the server reports a known terminal state. Other states remain active.</summary>
    public bool IsTerminal => State is "success" or "failed" or "cancelled";
}

public sealed partial class RespireServerNode
{
    /// <summary>Starts Valkey 9+ slot exports from this source node. Requires AllowAdmin.</summary>
    /// <remarks>Completion acknowledges admission, not migration completion. Each group may contain multiple
    /// nonoverlapping ranges. Cancellation stops waiting; it does not cancel admitted server jobs.
    /// Redirects and ambiguous failures are never replayed. Redis does not support this command.</remarks>
    public ValueTask ClusterMigrateSlotsAsync(ReadOnlySpan<RespireValkeySlotMigrationGroup> groups,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (groups.IsEmpty) throw new ArgumentException("At least one migration group is required.", nameof(groups));
            var arguments = new List<RespireValue>();
            var ranges = new List<RespireClusterSlotRange>();
            foreach (var group in groups)
            {
                ArgumentNullException.ThrowIfNull(group, nameof(groups));
                ArgumentException.ThrowIfNullOrWhiteSpace(group.TargetNodeId, nameof(groups));
                if (group.TargetNodeId.Any(char.IsWhiteSpace))
                    throw new ArgumentException("A target node ID cannot contain whitespace.", nameof(groups));
                if (group.Slots is null || group.Slots.Count == 0)
                    throw new ArgumentException("Each migration group requires slot ranges.", nameof(groups));
                arguments.Add("SLOTSRANGE");
                foreach (var range in group.Slots)
                {
                    if (range.Start < 0 || range.End >= 16384 || range.End < range.Start)
                        throw new ArgumentOutOfRangeException(nameof(groups), "Slot ranges must be inclusive and within 0 through 16383.");
                    ranges.Add(range);
                    arguments.Add(range.Start);
                    arguments.Add(range.End);
                }
                arguments.Add("NODE");
                arguments.Add(group.TargetNodeId);
            }
            ranges.Sort(static (left, right) => left.Start.CompareTo(right.Start));
            for (var index = 1; index < ranges.Count; index++)
                if (ranges[index].Start <= ranges[index - 1].End)
                    throw new ArgumentException("Migration slot ranges cannot overlap, including across groups.", nameof(groups));
            return MutationAsync("CLUSTER MIGRATESLOTS", arguments.ToArray(), cancellationToken);
        }
        catch (Exception error) { RecordPreflightFailure(error); throw; }
    }

    /// <summary>Gets owned active and recent Valkey 9+ migration snapshots from this node.</summary>
    /// <remarks>Does not require the client AllowAdmin option. Server ACL checks still apply.</remarks>
    public ValueTask<RespireValkeySlotMigration[]> ClusterGetSlotMigrationsAsync(CancellationToken cancellationToken = default)
        => ExecuteAsync("CLUSTER GETSLOTMIGRATIONS", [], ValkeySlotMigrationParser.Parse, cancellationToken);

    /// <summary>Cancels all active exports initiated on this Valkey 9+ source node. Requires AllowAdmin.</summary>
    /// <remarks>Does not cancel imports on a target node. Observe status separately for terminal results.</remarks>
    public ValueTask ClusterCancelSlotMigrationsAsync(CancellationToken cancellationToken = default)
        => MutationAsync("CLUSTER CANCELSLOTMIGRATIONS", [], cancellationToken);

    /// <summary>Deletes one slot's keys across databases on the selected Valkey 9+ node. Requires AllowAdmin.</summary>
    /// <remarks>This destructive FLUSHSLOT operation is distinct from FLUSHSLOTS, which removes slot ownership.
    /// Default uses the server's lazyfree-lazy-user-flush setting. Redis does not support FLUSHSLOT.</remarks>
    public ValueTask ClusterFlushSlotAsync(int slot, ServerFlushMode mode = ServerFlushMode.Default,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if ((uint)slot >= 16384) throw new ArgumentOutOfRangeException(nameof(slot));
            RespireValue[] arguments = mode switch
            {
                ServerFlushMode.Default => [slot],
                ServerFlushMode.Sync => [slot, "SYNC"],
                ServerFlushMode.Async => [slot, "ASYNC"],
                _ => throw new ArgumentOutOfRangeException(nameof(mode)),
            };
            return MutationAsync("CLUSTER FLUSHSLOT", arguments, cancellationToken);
        }
        catch (Exception error) { RecordPreflightFailure(error); throw; }
    }
}
