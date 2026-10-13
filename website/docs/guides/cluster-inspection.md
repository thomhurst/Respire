# Cluster inspection

The Server facet offers typed, read-only Cluster inspection. Use it to see a node's view of the topology, slots, links, and slot statistics. An ordinary call reports one node's view. It does not update the client's slot map, merge a global topology, change membership, or enable Cluster mode. `AllowAdmin` is not required; the server still enforces ACLs.

| API | Redis command | Minimum Redis version |
| --- | --- | --- |
| `ClusterInfoAsync` | CLUSTER INFO | 3.0 |
| `ClusterNodesAsync` | CLUSTER NODES | 3.0 |
| `ClusterShardsAsync` | CLUSTER SHARDS | 7.0 |
| `ClusterLinksAsync` | CLUSTER LINKS | 7.0 |
| `ClusterMyIdAsync` | CLUSTER MYID | 3.0 |
| `ClusterMyShardIdAsync` | CLUSTER MYSHARDID | 7.2 |
| `ClusterKeySlotAsync` | CLUSTER KEYSLOT | 3.0 |
| `ClusterCountKeysInSlotAsync` | CLUSTER COUNTKEYSINSLOT | 3.0 |
| `ClusterSlotStatsAsync` | CLUSTER SLOT-STATS SLOTSRANGE | 8.2 |
| `ClusterSlotStatsByMetricAsync` | CLUSTER SLOT-STATS ORDERBY | 8.2 |

Unsupported commands and standalone servers throw `RespireServerException`; no fake topology or empty success is returned. RESP2 and RESP3 are both supported.

```csharp
RespireClusterInfo info = await redis.Server.ClusterInfoAsync();
ulong? currentEpoch = info.CurrentEpoch;
ulong? localEpoch = info.MyEpoch;
Console.WriteLine($"Current epoch: {currentEpoch}; local epoch: {localEpoch}");
RespireClusterShard[] shards = await redis.Server.ClusterShardsAsync();
foreach (var shard in shards)
{
    foreach (var member in shard.Nodes)
        Console.WriteLine($"{member.Id}: {member.Role}, {member.Health}, {member.Endpoint}");
}
```

## Owned node views

`RespireClusterInfo`:

- Exposes common counters and keeps every field's original text in `Attributes`.
- `CurrentEpoch` and `MyEpoch` are `ulong?`, covering the full unsigned 64-bit range (including values above `long.MaxValue`); missing fields are null. Store them in `ulong?`, not `long?`. See the [epoch specification](https://redis.io/docs/latest/operate/oss_and_stack/reference/cluster-spec/#cluster-current-epoch).
- Other counters are nonnegative `long?` values.

`RespireClusterNode` ([NODES](https://redis.io/docs/latest/commands/cluster-nodes/)) keeps the advertised address text, flags, primary ID, timing and epoch counters, link state, inclusive slot ranges, and importing/migrating annotations. Unknown trailing tokens go to `AdditionalTokens`. Unusable addresses such as `:0@0` are kept for inspection.

Shard models ([SHARDS](https://redis.io/docs/latest/commands/cluster-shards/)) hold slot ranges and member nodes:

- Endpoint, IP, hostname, plain port, and TLS port are separate. Null, empty, and `?` endpoints are kept; port zero means unavailable.
- The client does not turn advertised values into reachable endpoints or guess missing addresses.
- Role and health strings keep future values.

Link models ([CLUSTER LINKS](https://redis.io/docs/latest/commands/cluster-links/)) describe Cluster bus connections, not the client's: direction, peer ID, creation time (Unix milliseconds), event flags, and buffer sizes.

All models:

- Unknown fields go to `AdditionalFields` as `RespireResult` values. Disposing them is optional and invalidates their element views. Other models need no disposal.
- Results stay valid after the reply and client are disposed.
- Each call returns its own snapshot. Arrays are caller-owned and mutable. Record equality compares array references, so compare elements yourself when comparing snapshots.

## Keys, slots, and statistics

- `ClusterKeySlotAsync` applies the view's key prefix and keeps binary key bytes. The node calculates the slot; the call is not routed to the key's owner.
- Numeric slot arguments must be 0–16383. Ranges are inclusive and cannot be reversed.
- `ClusterCountKeysInSlotAsync` counts keys on the queried node, even if another node owns the slot. Use the all-node variant to compare nodes. Summing primary and replica counts double-counts data.

```csharp
int slot = await redis.Server.ClusterKeySlotAsync("orders:42");
RespireClusterSlotStats[] local = await redis.Server.ClusterSlotStatsAsync(slot, slot);
RespireClusterSlotStats[] busiest = await redis.Server.ClusterSlotStatsByMetricAsync(
    RespireClusterSlotMetric.KeyCount, limit: 10, descending: true);
```

[CLUSTER SLOT-STATS](https://redis.io/docs/latest/commands/cluster-slot-stats/):

- Returns slots in the queried node's shard. Range results keep server order.
- Metric results default to descending; `limit` is 1 to 16384.
- `KeyCount` is always present. Other metrics are null when disabled in server configuration. Ordering by a disabled metric returns the server error.
- These APIs do not enable statistics collection.

## Results from every node

Every method has an `OnAllNodesAsync` counterpart with the same arguments. It returns a `RespireServerResult<T>` per endpoint with an owned value or the original failure.

- Discovery includes replicas and slotless members, and needs permission to run CLUSTER NODES.
- Without `UseCluster`, only the connected endpoint is queried, even if it belongs to a Cluster. Cluster-only commands still fail on a standalone server.

```csharp
var results = await redis.Server.ClusterShardsOnAllNodesAsync();
foreach (var result in results)
{
    if (result.IsSuccess)
        Console.WriteLine($"{result.Endpoint}: {result.Value.Length} reported shards");
    else
        Console.WriteLine($"{result.Endpoint}: {result.Error!.Message}");
}
```

- Check every result. Views can overlap or disagree during failover; nothing is merged.
- Cancellation during discovery throws. After discovery it is recorded per node, and completed results stay available.

These 20 methods are `IServerCommands` members; external implementations, decorators, and mocks must implement or forward them. They are immediate, with no batch or transaction versions.
