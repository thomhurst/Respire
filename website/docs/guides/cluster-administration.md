---
title: Cluster administration
description: Run Redis Cluster slot and topology administration against one explicitly selected node.
---

# Cluster administration

`redis.Server.OnNode(endpoint)` returns a `RespireServerNode` handle for one physical node. Use it for slot assignment, slot migration state, failover, and other topology commands that must reach a specific node. For read-only inspection, see [Cluster inspection](cluster-inspection.md). For lifecycle commands, shutdown, and atomic slot migration on the same handle, see [Node lifecycle and administration](server-node-lifecycle.md).

```csharp
var node = redis.Server.OnNode(new RespireEndpoint("127.0.0.1", 7000));
var topology = await node.ClusterSlotsAsync(cancellationToken);
var primaryId = topology[0].Primary.NodeId
    ?? throw new InvalidOperationException("The server did not report a primary node ID.");
var replicas = await node.ClusterReplicasAsync(primaryId, cancellationToken);

// Replace this example with the destination's actual ID from CLUSTER NODES.
var destinationId = "0123456789abcdef0123456789abcdef01234567";

// The client must have AllowAdmin = true. These operations change this node only.
await node.ClusterAddSlotsRangeAsync([new(0, 5460)], cancellationToken);
await node.ClusterSetSlotAsync(10, RespireClusterSlotState.Migrating, destinationId, cancellationToken);
await node.ClusterSetSlotAsync(10, RespireClusterSlotState.Stable, cancellationToken: cancellationToken);
```

## How the handle connects

- `OnNode` does not connect or discover topology. Each operation opens a short-lived connection to that exact endpoint.
- It inherits the client's authentication, TLS, protocol, database, and timeouts. Cluster nodes need database zero unless the server supports others.
- It never follows `MOVED` or `ASK`, substitutes another node, or replays a failed request.
- Keys in arguments and results are physical server keys; the view's key prefix is not applied.

## Commands

| Method | Redis command | Notes |
| --- | --- | --- |
| `ClusterSlotsAsync` | CLUSTER SLOTS | Read. Does not update client routing. |
| `ClusterReplicasAsync` | CLUSTER REPLICAS | Read. Redis 5.0 or later. |
| `ClusterCountFailureReportsAsync` | CLUSTER COUNT-FAILURE-REPORTS | Read. |
| `ClusterGetKeysInSlotAsync` | CLUSTER GETKEYSINSLOT | Read. |
| `ClusterAddSlotsAsync` | CLUSTER ADDSLOTS | Mutation. |
| `ClusterAddSlotsRangeAsync` | CLUSTER ADDSLOTSRANGE | Mutation. Redis 7.0 or later. |
| `ClusterDeleteSlotsAsync` | CLUSTER DELSLOTS | Mutation. |
| `ClusterDeleteSlotsRangeAsync` | CLUSTER DELSLOTSRANGE | Mutation. Redis 7.0 or later. |
| `ClusterSetSlotAsync` | CLUSTER SETSLOT | Mutation. |
| `ClusterFlushSlotsAsync` | CLUSTER FLUSHSLOTS | Mutation. The server requires an empty database. |
| `ClusterBumpEpochAsync` | CLUSTER BUMPEPOCH | Mutation. |
| `ClusterSetConfigEpochAsync` | CLUSTER SET-CONFIG-EPOCH | Mutation. |
| `ClusterFailoverAsync` | CLUSTER FAILOVER | Mutation. |
| `ClusterForgetAsync` | CLUSTER FORGET | Mutation. |
| `ClusterMeetAsync` | CLUSTER MEET | Mutation. The optional bus port requires Redis 4.0 or later. |
| `ClusterReplicateAsync` | CLUSTER REPLICATE | Mutation. |
| `ClusterResetAsync` | CLUSTER RESET | Mutation. |
| `ClusterSaveConfigAsync` | CLUSTER SAVECONFIG | Mutation. |

Other commands require Redis 3.0 or later. The same handle also exposes Redis 8.4 `CLUSTER MIGRATION` and Valkey 9 slot migration and `FLUSHSLOT`; see [Node lifecycle and administration](server-node-lifecycle.md).

## Permissions and validation

- Mutations require `AllowAdmin`. Reads do not. Server ACLs still apply.
- Mutations invalidate the client-side cache before and after the attempt, even if it fails.
- Respire rejects invalid slots, reversed ranges, negative counts or epochs, undefined enum values, and invalid TCP endpoints before connecting.
- Node IDs must be nonblank with no whitespace; any other format is accepted.
- Slot arguments keep their order and duplicates. The server decides whether an assignment is legal.
- `ClusterSetSlotAsync` with `RespireClusterSlotState.Stable` rejects a node ID. `Importing`, `Migrating`, and `Node` require one.
- `ClusterGetKeysInSlotAsync` accepts a zero count. It returns owned binary physical keys regardless of the client's key prefix.

## What a mutation does and does not do

Success means this node accepted the command. It does not mean the topology converged, a failover completed, data migrated, or Respire's routing changed (routing picks changes up on its usual refresh). Coordinate other nodes and verify convergence yourself. These methods do not orchestrate safe resharding or failover.

Cancellation or a lost reply does not prove a mutation did not happen. Inspect the node before sending another request.

- `RespireClusterFailoverMode.Normal` coordinates with the primary and needs a majority vote. `Force` skips primary coordination but still needs a vote. `Takeover` skips both and can lose data or create conflicting ownership.
- `RespireClusterResetMode.Hard` replaces the node identity and clears epochs. `Soft`, the default, keeps them.
- `FORGET` propagation and its temporary ban depend on the server version. Do not assume one call removes a node everywhere.

## Results

`ClusterSlotsAsync` returns `RespireClusterSlotMapping` values: the slot range, the primary, and its replicas.

- Each `RespireClusterSlotNode` has the endpoint, port, optional node ID, an owned metadata dictionary, and any extra positional values from newer servers.
- Null, empty, `?`, and advertised endpoints are kept as reported, not resolved against the queried endpoint. Port zero stays zero.
- Node IDs appeared in Redis 4.0 and networking metadata in 7.0. Older two-field node replies are accepted.
- Redis 7.0 deprecated `CLUSTER SLOTS` in favor of `CLUSTER SHARDS`; this method stays for interoperability.

`ClusterReplicasAsync` returns `RespireClusterNode` rows, the same model as `CLUSTER NODES`, with unknown flags and annotations kept.

Arrays are owned by the caller and mutable. Unknown metadata owns its bytes and stays valid after the reply or client is disposed.

`RespireClusterNode.ConfigurationEpoch` is a `ulong`, covering the full unsigned 64-bit range, including after `BUMPEPOCH` passes `long.MaxValue`. Store it in a `ulong`, not a `long`. `ClusterBumpEpochAsync` returns `RespireClusterEpochResult` with `Bumped` (from `BUMPED` or `STILL`) and the unsigned `Epoch`.

Tested on Redis 8.4 and Valkey 9 over RESP2 and RESP3.

## Redis references

[CLUSTER SLOTS](https://redis.io/docs/latest/commands/cluster-slots/), [CLUSTER SETSLOT](https://redis.io/docs/latest/commands/cluster-setslot/), [CLUSTER FAILOVER](https://redis.io/docs/latest/commands/cluster-failover/).
