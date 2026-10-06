namespace Respire;

/// <summary>Selects the wire form of CLUSTER MIGRATION STATUS.</summary>
public enum RespireClusterMigrationStatusScope
{
    /// <summary>Omits the selector. Redis 8.4 and 8.10 reject this documented form; use All on those versions.</summary>
    Default,
    /// <summary>Requests all active and archived tasks on the selected node.</summary>
    All,
}

/// <summary>An owned atomic slot migration task from Redis 8.4 or later.</summary>
/// <remarks>Operation and State preserve future server values. Slots retains the server's range text.
/// Time values originate as Unix milliseconds; an unset start or end time is null.</remarks>
public sealed record RespireClusterMigrationTask(
    string Id,
    string Slots,
    string SourceNodeId,
    string DestinationNodeId,
    string Operation,
    string State,
    string LastError,
    long Retries,
    DateTimeOffset CreateTime,
    DateTimeOffset? StartTime,
    DateTimeOffset? EndTime,
    long WritePauseMilliseconds,
    IReadOnlyDictionary<string, RespireResult> AdditionalFields);

public sealed partial class RespireServerNode
{
    /// <summary>Starts atomic import of inclusive slot ranges into this destination primary. Requires AllowAdmin and Redis 8.4+.</summary>
    /// <remarks>Returns an owned task ID, not completion. Ranges must be non-overlapping and within 0–16383.
    /// Ranges are copied and sent in ascending slot order without changing the caller's input.
    /// Uses only this endpoint and never replays. Cancellation or connection loss can leave a running task.
    /// Poll status and reconcile topology independently; this call does not refresh the client's routing table.</remarks>
    public ValueTask<string> ClusterMigrationImportAsync(ReadOnlySpan<RespireClusterSlotRange> ranges,
        CancellationToken cancellationToken = default)
    {
        if (ranges.IsEmpty) throw new ArgumentException("At least one slot range is required.", nameof(ranges));
        // Redis 8.4/8.10 slotRangeArrayNormalizeAndValidate rejects 16384 ranges before merging them.
        if (ranges.Length >= 16384) throw new ArgumentOutOfRangeException(nameof(ranges), "Redis accepts fewer than 16384 ranges.");
        var ordered = ranges.ToArray();
        Array.Sort(ordered, static (left, right) => left.Start.CompareTo(right.Start));
        var arguments = new RespireValue[checked(ranges.Length * 2)];
        var previousEnd = -1;
        for (var index = 0; index < ranges.Length; index++)
        {
            var range = ordered[index];
            if (range.Start < 0 || range.End > 16383 || range.End < range.Start)
                throw new ArgumentOutOfRangeException(nameof(ranges), "Slot ranges must be within 0 through 16383 with start <= end.");
            if (range.Start <= previousEnd)
                throw new ArgumentException("Slot ranges must be non-overlapping.", nameof(ranges));
            previousEnd = range.End;
            arguments[index * 2] = range.Start;
            arguments[index * 2 + 1] = range.End;
        }
        return ExecuteAsync("CLUSTER MIGRATION IMPORT", arguments, ClusterMigrationParser.TaskId,
            cancellationToken, NodeCallKind.Mutation);
    }

    /// <summary>Cancels one task on this node and returns the number cancelled. Requires AllowAdmin and Redis 8.4+.</summary>
    /// <remarks>Cancelling at the source does not stop the destination retrying. Cancel at the destination too.
    /// This operation never redirects or replays, and does not roll back an already completed migration.</remarks>
    public ValueTask<long> ClusterMigrationCancelAsync(string taskId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        return ExecuteAsync("CLUSTER MIGRATION CANCEL", ["ID", taskId], ServerDiagnosticsParser.NonnegativeInteger,
            cancellationToken, NodeCallKind.Mutation);
    }

    /// <summary>Cancels all tasks on this node. Requires AllowAdmin and Redis 8.4+. Source cancellation alone does not stop destination retries.</summary>
    public ValueTask<long> ClusterMigrationCancelAllAsync(CancellationToken cancellationToken = default)
        => ExecuteAsync("CLUSTER MIGRATION CANCEL", ["ALL"], ServerDiagnosticsParser.NonnegativeInteger,
            cancellationToken, NodeCallKind.Mutation);

    /// <summary>Reads owned active and archived task status at this node. Requires Redis 8.4+.</summary>
    /// <remarks>All is the interoperable default. The explicit Default scope omits the selector as documented by Redis,
    /// but Redis 8.4 and 8.10 reject that wire form. Server errors propagate without fallback or replay.</remarks>
    public ValueTask<RespireClusterMigrationTask[]> ClusterMigrationStatusAsync(
        RespireClusterMigrationStatusScope scope = RespireClusterMigrationStatusScope.All,
        CancellationToken cancellationToken = default)
        => ExecuteAsync("CLUSTER MIGRATION STATUS", scope switch
        {
            RespireClusterMigrationStatusScope.Default => [],
            RespireClusterMigrationStatusScope.All => ["ALL"],
            _ => throw new ArgumentOutOfRangeException(nameof(scope)),
        }, ClusterMigrationParser.Tasks, cancellationToken);

    /// <summary>Reads one owned migration task at this node, or an empty array when absent. Requires Redis 8.4+.</summary>
    public ValueTask<RespireClusterMigrationTask[]> ClusterMigrationStatusAsync(string taskId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        return ExecuteAsync("CLUSTER MIGRATION STATUS", ["ID", taskId], ClusterMigrationParser.Tasks, cancellationToken);
    }
}
