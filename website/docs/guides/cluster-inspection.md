# Cluster inspection

The Server facet exposes typed, read-only Cluster inspection. An ordinary call reports
one execution node's view. It does not replace the client's slot map, deduplicate a
global topology, configure Cluster membership, or enable Cluster mode on a standalone
server. `AllowAdmin` is not required; the server still enforces ACL permissions.

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

Unsupported commands and standalone-server failures remain `RespireServerException`.
No fallback fabricates a topology or returns an empty success. Redis 7.0 and 8.4
Cluster tests cover RESP2 and RESP3, including explicit errors for newer commands
against Redis 7.0. Standalone tests cover preserved server errors.

```csharp
RespireClusterInfo info = await redis.Server.ClusterInfoAsync();
RespireClusterShard[] shards = await redis.Server.ClusterShardsAsync();
foreach (var shard in shards)
{
    foreach (var member in shard.Nodes)
        Console.WriteLine($"{member.Id}: {member.Role}, {member.Health}, {member.Endpoint}");
}
```

## Owned node views

`RespireClusterInfo` exposes common counters and retains all report fields in
`Attributes`. `RespireClusterNode` retains advertised address text, flags, primary ID,
timing/epoch counters, link state, inclusive slot ranges, and importing/migrating slot
annotations. Unknown trailing tokens remain in `AdditionalTokens`. An address such as
`:0@0` is retained for inspection even when it cannot be used for a connection.

Shard models contain slot ranges and member nodes. Endpoint, IP, hostname, plain port,
and TLS port remain distinct. Null, empty, and `?` endpoints are preserved; a zero port
means unavailable. The client does not turn these advertised values into reachable
endpoints or infer missing addresses. Role and health strings preserve future values.
See Redis's [SHARDS](https://redis.io/docs/latest/commands/cluster-shards/) and
[NODES](https://redis.io/docs/latest/commands/cluster-nodes/) references.

Link models describe Cluster bus connections, including direction, peer ID, Unix
millisecond creation time, event flags, and buffer sizes. They do not represent the
client's own connections. See [CLUSTER LINKS](https://redis.io/docs/latest/commands/cluster-links/).

Structured models retain unknown fields in `AdditionalFields`. Those `RespireResult`
values are recursively copied into GC-owned storage; disposal is optional and
invalidates their element views. Other models need no disposal. All results remain
valid after the reply and client are disposed.

## Keys, slots, and statistics

`ClusterKeySlotAsync` applies the client view's key prefix and preserves binary key
bytes. It asks the execution node to calculate that key's slot; it does not route to
the key owner. Numeric slot arguments are literal numbers, validated in 0–16383.
Ranges are inclusive and cannot be reversed.

`ClusterCountKeysInSlotAsync` counts keys on the execution node, even if another node
owns the slot. Use the all-node variant to compare node-local counts. Summing replica
and primary counts can count the same data more than once.

```csharp
int slot = await redis.Server.ClusterKeySlotAsync("orders:42");
RespireClusterSlotStats[] local = await redis.Server.ClusterSlotStatsAsync(slot, slot);
RespireClusterSlotStats[] busiest = await redis.Server.ClusterSlotStatsByMetricAsync(
    RespireClusterSlotMetric.KeyCount, limit: 10, descending: true);
```

SLOT-STATS returns slots belonging to the queried node's shard. Range results retain
server order. Metric results default to descending order, with optional limits from
1 through 16384. `KeyCount` is always present; other metrics are nullable when disabled
by server configuration. Ordering by an unavailable metric surfaces the server error.
These APIs do not enable statistics collection. See
[CLUSTER SLOT-STATS](https://redis.io/docs/latest/commands/cluster-slot-stats/) for
metric configuration and ordering semantics.

## Results from every node

Every method has an `OnAllNodesAsync` counterpart with the same arguments. Discovery
includes replicas and members without assigned slots. Each `RespireServerResult<T>`
contains its endpoint and either an owned value or the original failure. Standalone
clients query their one endpoint; Cluster-only commands still fail on that server.
Without `UseCluster`, discovery queries only that connected endpoint even if the
server belongs to a Cluster.
Discovery itself requires permission to execute CLUSTER NODES.

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

Inspect every result. Node views may overlap or disagree during failover; no merge or
deduplication is implied. Cancellation during discovery throws. After discovery it is
attributed to affected nodes while completed successes remain available.

These 20 methods extend `IServerCommands`; external implementations, decorators, and
mocks must implement or forward them. They are immediate inspection APIs and do not
add batch or transaction methods.
