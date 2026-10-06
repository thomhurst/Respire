# Explicit-node Cluster administration

Use `redis.Server.OnNode(endpoint)` for slot and topology administration. Each call
uses a short-lived connection to that exact physical endpoint, inheriting the client's
authentication, TLS, protocol, database and timeouts. Cluster deployments require
database zero unless the server explicitly supports another database. The handle never
follows MOVED/ASK, substitutes another node, or replays a failed administrative request.

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

All mutations require `AllowAdmin` and fence the shared client cache before and after
the attempt, including failed attempts. Reads do not require `AllowAdmin`. Server ACLs
still apply. Invalid slots, reversed ranges, negative counts/epochs, invalid enum values,
and invalid TCP endpoints are rejected before connecting. Slot arguments retain their
order and duplicates; the server decides whether assignments are legal. SETSLOT STABLE
rejects a node ID; the other states require one. GETKEYSINSLOT accepts a zero count and
returns owned binary physical keys, regardless of a client view's key prefix.

The handle exposes SLOTS, ADDSLOTS/ADDSLOTSRANGE, DELSLOTS/DELSLOTSRANGE, BUMPEPOCH,
COUNT-FAILURE-REPORTS, FAILOVER, FORGET, GETKEYSINSLOT, MEET, REPLICAS, REPLICATE,
RESET, SAVECONFIG, SET-CONFIG-EPOCH, SETSLOT and FLUSHSLOTS. Existing server-facet
Cluster inspection methods keep their existing selection and fanout behavior.

Successful mutations acknowledge the local command; they do not establish global
topology convergence, complete a failover, migrate key data, or update Respire's routing
table directly. Coordinate other nodes and verify convergence separately. Ordinary
Cluster routing can subsequently discover changes through its existing mechanisms.
Cancellation or a lost reply cannot prove a mutation did not happen. Inspect node state
before deciding whether a later request is appropriate.

FAILOVER Normal coordinates with the primary and requires a majority vote. Force skips
primary coordination but still requires a vote. Takeover skips both and can lose data or
create conflicting ownership. RESET Hard replaces node identity and clears epochs;
Soft retains them. FLUSHSLOTS requires an empty database. FORGET's propagation and
temporary ban behavior depend on server version; do not assume one call removes a node
everywhere. These APIs do not orchestrate a safe resharding or failover workflow.

SLOTS returns owned ranges, primary and replica networking, optional node IDs, an
owned metadata dictionary, and any future positional node values. Null, empty, `?`, and advertised endpoints are preserved;
they are not resolved against the queried endpoint. Port zero remains zero. REPLICAS
returns the same owned node-row model as CLUSTER NODES, preserving unknown flags and
annotations. Arrays are caller-owned and mutable. Unknown metadata recursively owns
its bytes and remains valid after the reply or client is disposed.

`RespireClusterNode.ConfigurationEpoch` uses `ulong` to preserve Redis's full unsigned
64-bit epoch range, including epochs returned after BUMPEPOCH crosses `long.MaxValue`.
Code storing this property in a signed `long` must use `ulong` instead. Node IDs may use
future formats, but embedded whitespace is rejected before connecting.

Classic commands require Redis 3.0 or later unless noted: REPLICAS requires 5.0,
MEET's optional bus port requires 4.0, and ADDSLOTSRANGE/DELSLOTSRANGE require 7.0.
SLOTS node IDs appeared in 4.0 and networking metadata in 7.0; older two-field node
replies are accepted. Redis deprecates SLOTS in favor of SHARDS starting in 7.0;
this explicit-node method remains available for administration interoperability.
BUMPEPOCH returns both BUMPED/STILL and the unsigned epoch, matching server wire output.
Owned integration tests exercise Redis 8.4 and Valkey 9 using RESP2 and RESP3.
Redis CLUSTER MIGRATION and Valkey atomic-migration commands are outside this surface.

References: [Redis SLOTS](https://redis.io/docs/latest/commands/cluster-slots/),
[SETSLOT](https://redis.io/docs/latest/commands/cluster-setslot/),
[FAILOVER](https://redis.io/docs/latest/commands/cluster-failover/), and
[Redis 8.4 command implementation](https://github.com/redis/redis/blob/8.4/src/cluster_legacy.c).
