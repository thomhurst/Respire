using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;

namespace Respire;

public partial interface IServerCommands
{
    /// <summary>Returns one node's CLUSTER INFO view. Redis 3+.</summary>
    ValueTask<RespireClusterInfo> ClusterInfoAsync(CancellationToken cancellationToken = default);
    /// <summary>Returns owned CLUSTER NODES rows, including migration annotations. Redis 3+.</summary>
    ValueTask<RespireClusterNode[]> ClusterNodesAsync(CancellationToken cancellationToken = default);
    /// <summary>Returns one node's owned shard view without changing client routing. Redis 7+.</summary>
    ValueTask<RespireClusterShard[]> ClusterShardsAsync(CancellationToken cancellationToken = default);
    /// <summary>Returns owned Cluster bus links from one node. Redis 7+.</summary>
    ValueTask<RespireClusterLink[]> ClusterLinksAsync(CancellationToken cancellationToken = default);
    /// <summary>Returns the execution node's Cluster node ID. Redis 3+.</summary>
    ValueTask<string> ClusterMyIdAsync(CancellationToken cancellationToken = default);
    /// <summary>Returns the execution node's shard ID. Redis 7.2+.</summary>
    ValueTask<string> ClusterMyShardIdAsync(CancellationToken cancellationToken = default);
    /// <summary>Returns the hash slot of the prefixed key, without routing to its owner. Redis 3+.</summary>
    ValueTask<int> ClusterKeySlotAsync(RespireKey key, CancellationToken cancellationToken = default);
    /// <summary>Counts keys locally on the execution node; does not route to the slot owner. Redis 3+.</summary>
    ValueTask<long> ClusterCountKeysInSlotAsync(int slot, CancellationToken cancellationToken = default);
    /// <summary>Returns local slot statistics for an inclusive range from 0 through 16383. Redis 8.2+.</summary>
    ValueTask<RespireClusterSlotStats[]> ClusterSlotStatsAsync(int startSlot, int endSlot, CancellationToken cancellationToken = default);
    /// <summary>Returns local slot statistics ordered by metric. Limit, when supplied, is 1 through 16384. Redis 8.2+.</summary>
    ValueTask<RespireClusterSlotStats[]> ClusterSlotStatsByMetricAsync(RespireClusterSlotMetric metric, int? limit = null, bool descending = true, CancellationToken cancellationToken = default);

    /// <summary>Runs ClusterInfo separately on each discovered node, including replicas, retaining endpoint provenance.</summary>
    /// <remarks>Standalone returns one result. Discovery cancellation throws; subsequent cancellation and failures
    /// are per-node results. Views are not deduplicated or reconciled and do not update client routing.</remarks>
    ValueTask<RespireServerResult<RespireClusterInfo>[]> ClusterInfoOnAllNodesAsync(CancellationToken cancellationToken = default);
    /// <summary>Runs ClusterNodes separately on each discovered node, including replicas, retaining endpoint provenance.</summary>
    /// <remarks>Standalone returns one result. Discovery cancellation throws; subsequent cancellation and failures
    /// are per-node results. Views are not deduplicated or reconciled and do not update client routing.</remarks>
    ValueTask<RespireServerResult<RespireClusterNode[]>[]> ClusterNodesOnAllNodesAsync(CancellationToken cancellationToken = default);
    /// <summary>Runs ClusterShards separately on each discovered node, including replicas, retaining endpoint provenance.</summary>
    /// <remarks>Standalone returns one result. Discovery cancellation throws; subsequent cancellation and failures
    /// are per-node results. Views are not deduplicated or reconciled and do not update client routing.</remarks>
    ValueTask<RespireServerResult<RespireClusterShard[]>[]> ClusterShardsOnAllNodesAsync(CancellationToken cancellationToken = default);
    /// <summary>Runs ClusterLinks separately on each discovered node, including replicas, retaining endpoint provenance.</summary>
    /// <remarks>Standalone returns one result. Discovery cancellation throws; subsequent cancellation and failures
    /// are per-node results. Views are not deduplicated or reconciled and do not update client routing.</remarks>
    ValueTask<RespireServerResult<RespireClusterLink[]>[]> ClusterLinksOnAllNodesAsync(CancellationToken cancellationToken = default);
    /// <summary>Runs ClusterMyId separately on each discovered node, including replicas, retaining endpoint provenance.</summary>
    /// <remarks>Standalone returns one result. Discovery cancellation throws; subsequent cancellation and failures
    /// are per-node results. Views are not deduplicated or reconciled and do not update client routing.</remarks>
    ValueTask<RespireServerResult<string>[]> ClusterMyIdOnAllNodesAsync(CancellationToken cancellationToken = default);
    /// <summary>Runs ClusterMyShardId separately on each discovered node, including replicas, retaining endpoint provenance.</summary>
    /// <remarks>Standalone returns one result. Discovery cancellation throws; subsequent cancellation and failures
    /// are per-node results. Views are not deduplicated or reconciled and do not update client routing.</remarks>
    ValueTask<RespireServerResult<string>[]> ClusterMyShardIdOnAllNodesAsync(CancellationToken cancellationToken = default);
    /// <summary>Runs ClusterKeySlot separately on each discovered node, including replicas, retaining endpoint provenance.</summary>
    /// <remarks>Standalone returns one result. Discovery cancellation throws; subsequent cancellation and failures
    /// are per-node results. Views are not deduplicated or reconciled and do not update client routing.</remarks>
    ValueTask<RespireServerResult<int>[]> ClusterKeySlotOnAllNodesAsync(RespireKey key, CancellationToken cancellationToken = default);
    /// <summary>Runs ClusterCountKeysInSlot separately on each discovered node, including replicas, retaining endpoint provenance.</summary>
    /// <remarks>Standalone returns one result. Discovery cancellation throws; subsequent cancellation and failures
    /// are per-node results. Views are not deduplicated or reconciled and do not update client routing.</remarks>
    ValueTask<RespireServerResult<long>[]> ClusterCountKeysInSlotOnAllNodesAsync(int slot, CancellationToken cancellationToken = default);
    /// <summary>Runs ClusterSlotStats separately on each discovered node, including replicas, retaining endpoint provenance.</summary>
    /// <remarks>Standalone returns one result. Discovery cancellation throws; subsequent cancellation and failures
    /// are per-node results. Views are not deduplicated or reconciled and do not update client routing.</remarks>
    ValueTask<RespireServerResult<RespireClusterSlotStats[]>[]> ClusterSlotStatsOnAllNodesAsync(int startSlot, int endSlot, CancellationToken cancellationToken = default);
    /// <summary>Runs ClusterSlotStatsByMetric separately on each discovered node, including replicas, retaining endpoint provenance.</summary>
    /// <remarks>Standalone returns one result. Discovery cancellation throws; subsequent cancellation and failures
    /// are per-node results. Views are not deduplicated or reconciled and do not update client routing.</remarks>
    ValueTask<RespireServerResult<RespireClusterSlotStats[]>[]> ClusterSlotStatsByMetricOnAllNodesAsync(RespireClusterSlotMetric metric, int? limit = null, bool descending = true, CancellationToken cancellationToken = default);
}

internal sealed partial class ServerCommands
{
    private readonly record struct ClusterInspectionCall<T>(string Operation, CmdN Command, ResponseConverter<ServerCommands, T> Convert);
    private static readonly Verb InspectKeySlot = new(-1, "CLUSTER", "KEYSLOT");
    private static readonly Verb InspectCountKeys = new(-1, "CLUSTER", "COUNTKEYSINSLOT");
    private static readonly Verb InspectSlotStats = new(-1, "CLUSTER", "SLOT-STATS");
    private static readonly ClusterInspectionCall<RespireClusterInfo> InfoInspectionCall = new("CLUSTER INFO", new(new Verb(-1, "CLUSTER", "INFO"), []),
        static (ServerCommands _, in RespValue value) => ClusterInspectionParser.Info(in value));
    // Do not capture the ClusterNodes field from another partial: its initialization order is unspecified.
    private static readonly ClusterInspectionCall<RespireClusterNode[]> NodesInspectionCall = new("CLUSTER NODES", new(new Verb(-1, "CLUSTER", "NODES"), []),
        static (ServerCommands _, in RespValue value) => ClusterInspectionParser.Nodes(in value));
    private static readonly ClusterInspectionCall<RespireClusterShard[]> ShardsInspectionCall = new("CLUSTER SHARDS", new(new Verb(-1, "CLUSTER", "SHARDS"), []),
        static (ServerCommands _, in RespValue value) => ClusterInspectionParser.Shards(in value));
    private static readonly ClusterInspectionCall<RespireClusterLink[]> LinksInspectionCall = new("CLUSTER LINKS", new(new Verb(-1, "CLUSTER", "LINKS"), []),
        static (ServerCommands _, in RespValue value) => ClusterInspectionParser.Links(in value));
    private static readonly ClusterInspectionCall<string> MyIdInspectionCall = new("CLUSTER MYID", new(new Verb(-1, "CLUSTER", "MYID"), []),
        static (ServerCommands _, in RespValue value) => ClusterInspectionParser.Text(in value));
    private static readonly ClusterInspectionCall<string> MyShardIdInspectionCall = new("CLUSTER MYSHARDID", new(new Verb(-1, "CLUSTER", "MYSHARDID"), []),
        static (ServerCommands _, in RespValue value) => ClusterInspectionParser.Text(in value));

    public ValueTask<RespireClusterInfo> ClusterInfoAsync(CancellationToken cancellationToken = default)
        => ExecuteClusterInspectionAsync(InfoInspectionCall, cancellationToken);
    public ValueTask<RespireClusterNode[]> ClusterNodesAsync(CancellationToken cancellationToken = default)
        => ExecuteClusterInspectionAsync(NodesInspectionCall, cancellationToken);
    public ValueTask<RespireClusterShard[]> ClusterShardsAsync(CancellationToken cancellationToken = default)
        => ExecuteClusterInspectionAsync(ShardsInspectionCall, cancellationToken);
    public ValueTask<RespireClusterLink[]> ClusterLinksAsync(CancellationToken cancellationToken = default)
        => ExecuteClusterInspectionAsync(LinksInspectionCall, cancellationToken);
    public ValueTask<string> ClusterMyIdAsync(CancellationToken cancellationToken = default)
        => ExecuteClusterInspectionAsync(MyIdInspectionCall, cancellationToken);
    public ValueTask<string> ClusterMyShardIdAsync(CancellationToken cancellationToken = default)
        => ExecuteClusterInspectionAsync(MyShardIdInspectionCall, cancellationToken);
    public ValueTask<int> ClusterKeySlotAsync(RespireKey key, CancellationToken cancellationToken = default)
        => ExecuteClusterInspectionAsync(KeySlotInspectionCall(key), cancellationToken);
    public ValueTask<long> ClusterCountKeysInSlotAsync(int slot, CancellationToken cancellationToken = default)
        => ExecuteClusterInspectionAsync(CountKeysInspectionCall(slot), cancellationToken);
    public ValueTask<RespireClusterSlotStats[]> ClusterSlotStatsAsync(int startSlot, int endSlot, CancellationToken cancellationToken = default)
        => ExecuteClusterInspectionAsync(SlotRangeInspectionCall(startSlot, endSlot), cancellationToken);
    public ValueTask<RespireClusterSlotStats[]> ClusterSlotStatsByMetricAsync(RespireClusterSlotMetric metric, int? limit = null, bool descending = true, CancellationToken cancellationToken = default)
        => ExecuteClusterInspectionAsync(SlotMetricInspectionCall(metric, limit, descending), cancellationToken);

    public ValueTask<RespireServerResult<RespireClusterInfo>[]> ClusterInfoOnAllNodesAsync(CancellationToken cancellationToken = default)
        => ExecuteClusterInspectionOnAllNodesAsync(InfoInspectionCall, cancellationToken);
    public ValueTask<RespireServerResult<RespireClusterNode[]>[]> ClusterNodesOnAllNodesAsync(CancellationToken cancellationToken = default)
        => ExecuteClusterInspectionOnAllNodesAsync(NodesInspectionCall, cancellationToken);
    public ValueTask<RespireServerResult<RespireClusterShard[]>[]> ClusterShardsOnAllNodesAsync(CancellationToken cancellationToken = default)
        => ExecuteClusterInspectionOnAllNodesAsync(ShardsInspectionCall, cancellationToken);
    public ValueTask<RespireServerResult<RespireClusterLink[]>[]> ClusterLinksOnAllNodesAsync(CancellationToken cancellationToken = default)
        => ExecuteClusterInspectionOnAllNodesAsync(LinksInspectionCall, cancellationToken);
    public ValueTask<RespireServerResult<string>[]> ClusterMyIdOnAllNodesAsync(CancellationToken cancellationToken = default)
        => ExecuteClusterInspectionOnAllNodesAsync(MyIdInspectionCall, cancellationToken);
    public ValueTask<RespireServerResult<string>[]> ClusterMyShardIdOnAllNodesAsync(CancellationToken cancellationToken = default)
        => ExecuteClusterInspectionOnAllNodesAsync(MyShardIdInspectionCall, cancellationToken);
    public ValueTask<RespireServerResult<int>[]> ClusterKeySlotOnAllNodesAsync(RespireKey key, CancellationToken cancellationToken = default)
        => ExecuteClusterInspectionOnAllNodesAsync(KeySlotInspectionCall(key), cancellationToken);
    public ValueTask<RespireServerResult<long>[]> ClusterCountKeysInSlotOnAllNodesAsync(int slot, CancellationToken cancellationToken = default)
        => ExecuteClusterInspectionOnAllNodesAsync(CountKeysInspectionCall(slot), cancellationToken);
    public ValueTask<RespireServerResult<RespireClusterSlotStats[]>[]> ClusterSlotStatsOnAllNodesAsync(int startSlot, int endSlot, CancellationToken cancellationToken = default)
        => ExecuteClusterInspectionOnAllNodesAsync(SlotRangeInspectionCall(startSlot, endSlot), cancellationToken);
    public ValueTask<RespireServerResult<RespireClusterSlotStats[]>[]> ClusterSlotStatsByMetricOnAllNodesAsync(RespireClusterSlotMetric metric, int? limit = null, bool descending = true, CancellationToken cancellationToken = default)
        => ExecuteClusterInspectionOnAllNodesAsync(SlotMetricInspectionCall(metric, limit, descending), cancellationToken);

    private ValueTask<T> ExecuteClusterInspectionAsync<T>(ClusterInspectionCall<T> call, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ConvertAsync(call.Operation, call.Command, cancellationToken, call.Convert);
    }

    private ValueTask<RespireServerResult<T>[]> ExecuteClusterInspectionOnAllNodesAsync<T>(ClusterInspectionCall<T> call, CancellationToken cancellationToken)
        => FanOutAsync(call.Operation, call.Command, cancellationToken, call.Convert);

    private ClusterInspectionCall<int> KeySlotInspectionCall(RespireKey key)
        => new("CLUSTER KEYSLOT", new(InspectKeySlot, [client.Key(in key).Snapshot()]),
            static (ServerCommands _, in RespValue value) => ClusterInspectionParser.Slot(in value));

    private static ClusterInspectionCall<long> CountKeysInspectionCall(int slot)
    {
        ValidateInspectionSlot(slot, nameof(slot));
        return new("CLUSTER COUNTKEYSINSLOT", new(InspectCountKeys, [slot]),
            static (ServerCommands _, in RespValue value) => ClusterInspectionParser.NonnegativeInteger(in value));
    }

    private static ClusterInspectionCall<RespireClusterSlotStats[]> SlotRangeInspectionCall(int startSlot, int endSlot)
    {
        ValidateInspectionSlot(startSlot, nameof(startSlot));
        ValidateInspectionSlot(endSlot, nameof(endSlot));
        if (endSlot < startSlot) throw new ArgumentException("End slot must not precede start slot.", nameof(endSlot));
        return new("CLUSTER SLOT-STATS", new(InspectSlotStats, ["SLOTSRANGE", startSlot, endSlot]),
            static (ServerCommands _, in RespValue value) => ClusterInspectionParser.SlotStats(in value));
    }

    private static ClusterInspectionCall<RespireClusterSlotStats[]> SlotMetricInspectionCall(RespireClusterSlotMetric metric, int? limit, bool descending)
    {
        if (limit is < 1 or > 16384) throw new ArgumentOutOfRangeException(nameof(limit));
        var name = metric switch
        {
            RespireClusterSlotMetric.KeyCount => "KEY-COUNT",
            RespireClusterSlotMetric.MemoryBytes => "MEMORY-BYTES",
            RespireClusterSlotMetric.CpuMicroseconds => "CPU-USEC",
            RespireClusterSlotMetric.NetworkBytesIn => "NETWORK-BYTES-IN",
            RespireClusterSlotMetric.NetworkBytesOut => "NETWORK-BYTES-OUT",
            _ => throw new ArgumentOutOfRangeException(nameof(metric)),
        };
        RespireValue order = descending ? "DESC" : "ASC";
        RespireValue[] arguments = limit is { } count ? ["ORDERBY", name, "LIMIT", count, order] : ["ORDERBY", name, order];
        return new("CLUSTER SLOT-STATS", new(InspectSlotStats, arguments),
            static (ServerCommands _, in RespValue value) => ClusterInspectionParser.SlotStats(in value));
    }

    private static void ValidateInspectionSlot(int slot, string parameterName)
    {
        if ((uint)slot >= 16384) throw new ArgumentOutOfRangeException(parameterName);
    }
}
