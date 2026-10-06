# Node lifecycle and administration

Select the physical node explicitly before issuing lifecycle commands:

```csharp
RespireServerNode node = redis.Server.OnNode(new RespireEndpoint("redis-primary", 6379));
string password = await node.AclGeneratePasswordAsync();
byte[][] users = await node.AclUsersAsync();
RespireCommandKeyFlags[] keys = await node.CommandGetKeysAndFlagsAsync("MGET", ["key1", "key2"]);
```

`OnNode` does not connect or discover topology. Each call opens an independent connection
to that endpoint and disposes it when the operation finishes. Authentication, renewable
credentials, TLS, timeouts, protocol, and the selected database come from the client.
No command is redirected, replayed, or sent to all nodes. The handle follows client disposal.
These methods are immediate; they are not queueable in a batch or transaction.

All key arguments and returned keys are **physical server keys**. `WithKeyPrefix` does not
modify them. Binary inputs are copied before asynchronous I/O; binary results own their storage.
`OnNode` extends Respire's implementation of `IServerCommands` without adding requirements
to third-party interface implementations. Calling it on a third-party implementation throws
`NotSupportedException`.

## Read commands

| Method | Redis command | Minimum Redis version |
| --- | --- | --- |
| `AclGeneratePasswordAsync` | `ACL GENPASS [bits]` | 6.0 |
| `AclUsersAsync` | `ACL USERS` | 6.0 |
| `MemoryMallocStatsAsync` | `MEMORY MALLOC-STATS` | 4.0 |
| `LatencyGraphAsync` | `LATENCY GRAPH event` | 2.8.13 |
| `CommandGetKeysAndFlagsAsync` | `COMMAND GETKEYSANDFLAGS` | 7.0 |
| `BackupStatusAsync`, `BackupListAsync` | `BACKUP STATUS`, `BACKUP LIST` | 8.10 |
| `KeysAsync` | `KEYS pattern` | 1.0 |

`ACL GENPASS` defaults to 256 bits; explicit values must be 1 through 1024. Redis rounds
up to whole hexadecimal digits. `COMMAND GETKEYSANDFLAGS` preserves unknown flags.
Latency graphs require a recorded event; Redis reports an error for an unknown event.
Allocator reports depend on the server's allocator.

`BACKUP STATUS` exposes state, error text, and nullable Unix timestamps. Zero timestamps
mean no recorded time. Unknown states and fields remain available for newer servers.
`BACKUP LIST` returns immutable file paths on the server, not file contents.

**Use `KeysAsync` only for debugging.** `KEYS` scans the entire database and blocks the
server. Use `redis.Keys.ScanAsync` for production iteration instead.

## Mutations

Set `RespireOptions.AllowAdmin = true` to enable every operation below. Redis ACL checks
still apply. An unsupported command or unavailable feature produces the original server error.
Local client-side caching is conservatively invalidated before and after a mutation,
including a failed or cancelled mutation.

| Methods | Redis command |
| --- | --- |
| `AclLoadAsync`, `AclSaveAsync` | `ACL LOAD`, `ACL SAVE` |
| `FailoverAsync`, `AbortFailoverAsync` | `FAILOVER [TO host port [FORCE]] [TIMEOUT ms]`, `FAILOVER ABORT` |
| `ReplicaOfAsync`, `PromoteToPrimaryAsync` | `REPLICAOF host port`, `REPLICAOF NO ONE` |
| `SwapDatabasesAsync` | `SWAPDB first second` |
| `ModuleLoadAsync`, `ModuleLoadExtendedAsync`, `ModuleUnloadAsync` | `MODULE LOAD`, `MODULE LOADEX`, `MODULE UNLOAD` |
| `BackupStartAsync`, `BackupSealAsync`, `BackupAbortAsync`, `BackupCleanupAsync` | `BACKUP START`, `SEAL`, `ABORT`, `CLEANUP` |
| `ScriptKillAsync`, `FunctionKillAsync` | `SCRIPT KILL`, `FUNCTION KILL` |
| `SendShutdownAsync`, `AbortShutdownAsync` | `SHUTDOWN`, `SHUTDOWN ABORT` |
| `MigrateAsync` | `MIGRATE` with `KEYS` |

`FAILOVER` requires Redis 6.2; forced failover requires both a target and positive timeout.
Aborting failover can leave inconsistent replication state. Module paths and configuration
refer to the server's filesystem and installed modules. `MODULE LOADEX` requires Redis 7.0;
configuration pairs each emit their own `CONFIG` token. Backup operations require Redis 8.10.

## Cancelling BUSY scripts and functions

```csharp
await redis.Server.OnNode(new RespireEndpoint("busy-node", 6379)).ScriptKillAsync();
```

Kill methods create a fresh control connection even when the client's sole multiplexed
connection is waiting for the running script. The control handshake uses RESP2, database
zero, and authentication only. It skips client naming, database selection, topology queries,
tracking, and maintenance negotiation. TLS and credential providers still apply.
`FUNCTION KILL` requires Redis 7.0.

Redis refuses to kill a script or function that has already written data. `NOTBUSY`,
`UNKILLABLE`, authorization errors, and transport errors propagate to the caller.

## Shutdown and ambiguous outcomes

```csharp
RespireServerNode node = redis.Server.OnNode(new RespireEndpoint("redis-primary", 6379));
await node.SendShutdownAsync(new RespireShutdownOptions
{
    SaveMode = RespireShutdownSaveMode.NoSave,
    Now = true,
});
```

Redis sends **no success reply** for a normal shutdown. `SendShutdownAsync` completes
after the command is written to the local socket. It does not confirm server acceptance
or observe server-side errors. Verify shutdown separately. `NOW` skips waiting for replicas;
`FORCE` can lose data when persistence fails. Both modifiers and `AbortShutdownAsync`
require Redis 7.0. The abort method waits for `OK`. Shutdown uses the same independent
control handshake as the kill methods; a BUSY script requires `NOSAVE`.

`MigrateAsync` accepts one or more physical keys, a TCP destination, destination database,
and positive server timeout. `RespireMigrateOptions` selects `COPY`, `REPLACE`, and
destination `AUTH` or `AUTH2`. The result distinguishes acknowledged `OK` from `NOKEY`.
The [server timeout](https://redis.io/docs/latest/commands/migrate/) limits idle time during
communication with the destination, not the total transfer duration. Respire's
`CommandTimeout` and caller cancellation still apply independently. Set
`RespireMigrateOptions.CommandTimeout` to override the client response budget for this
call and its dedicated connection setup; null inherits the shared client's setting.
The override must be at least one millisecond and does not change other operations,
`ConnectionIdleReadTimeout`, or caller cancellation. Respire never extends these budgets
automatically. Configure the client timeout for the expected total transfer time,
including time spent transferring large values and waiting for replies. A client timeout
can expire while Redis is still transferring keys, even when it exceeds the server timeout.
For example, choose a five-second server idle timeout and a thirty-second client command
budget only when thirty seconds covers the expected whole transfer.

Respire's command telemetry records the operation name without MIGRATE arguments.
Destination authentication credentials are not included. Client-generated diagnostics do not
format those arguments. Original server errors are preserved; treat their text and
server-side command logs according to the server's handling of sensitive data.

Timeouts, errors, cancellation, and disconnects can leave a transfer or other lifecycle
mutation partially applied. Inspect the affected servers before retrying. Respire never
replays these operations automatically.

## Atomic Redis Cluster slot migration

Redis 8.4 and later support [CLUSTER MIGRATION](https://redis.io/docs/latest/commands/cluster-migration/).
Use the destination primary's explicit endpoint to start an import. Set `AllowAdmin = true`
for imports and cancellation; status queries do not require the client-side admin flag.
Redis ACL permissions still apply to all three operations. This protocol is distinct from
Valkey slot migration and classic key-by-key `MIGRATE`.

```csharp
var destination = redis.Server.OnNode(new RespireEndpoint("destination-primary", 6379));
string taskId = await destination.ClusterMigrationImportAsync(
    [new RespireClusterSlotRange(0, 100), new RespireClusterSlotRange(200, 300)], cancellationToken);
RespireClusterMigrationTask[] tasks = await destination.ClusterMigrationStatusAsync(taskId, cancellationToken);
long cancelled = await destination.ClusterMigrationCancelAsync(taskId, cancellationToken);
```

Ranges are inclusive, non-overlapping, and within 0–16383; Redis accepts fewer than 16384 ranges. The source is determined
by the server's slot ownership. The returned task ID confirms task creation, not migration
completion. Poll with your own cancellation/deadline until the task reports `completed` or
a terminal failure, and inspect `LastError` and `Retries`. Respire does not orchestrate the
migration, wait for cluster convergence, or silently refresh its routing table.

`ClusterMigrationStatusAsync()` sends `STATUS ALL` and includes active and archived tasks
on this node. The string overload sends `STATUS ID task-id`; an absent task returns an empty
array. `RespireClusterMigrationStatusScope.Default` explicitly sends bare `STATUS`, matching
the published optional-selector syntax. Redis 8.4 and 8.10 currently reject that form; use
`All` for those versions. Respire preserves the error without substituting another request.
The discrepancy is visible in the [Redis 8.4 implementation](https://github.com/redis/redis/blob/8.4/src/cluster_asm.c)
and [Redis 8.10 implementation](https://github.com/redis/redis/blob/8.10/src/cluster_asm.c).

Task results own their data. `Slots` retains the server's range text; `Operation`, `State`,
and `AdditionalFields` preserve future values. Timestamps are decoded from Unix **milliseconds**;
unset start/end values (`-1`) become null. `WritePauseMilliseconds` is the server's pause duration.

`ClusterMigrationCancelAllAsync()` sends `CANCEL ALL`. Both cancellation methods return the
number of tasks cancelled on the selected node. Cancelling on the source does **not** stop
the destination retrying: cancel at the destination too. Cancellation does not undo an
already completed migration. Check status and slot ownership across the affected nodes.

Every request uses a short-lived connection to exactly the selected endpoint. Imports and
cancellation conservatively fence the local client cache, including error paths. These
requests never redirect or replay. A client timeout, cancellation, or disconnect after
submission may leave an import running; inspect task status before deciding what to do next.

## Valkey slot migration and deletion

Valkey 9.0 and later support a separate atomic slot migration protocol. These methods
target exactly the endpoint selected by `Server.OnNode`; they do not use Redis's
`CLUSTER MIGRATION` command family and are not supported by Redis.

```csharp
RespireServerNode source = redis.Server.OnNode(new RespireEndpoint("valkey-source", 6379));
// Replace these placeholders with primary IDs from CLUSTER NODES.
string firstTargetId = "<first-target-node-id>";
string secondTargetId = "<second-target-node-id>";
await source.ClusterMigrateSlotsAsync([
    new RespireValkeySlotMigrationGroup(firstTargetId, [new(0, 99), new(200, 299)]),
    new RespireValkeySlotMigrationGroup(secondTargetId, [new(100, 199)]),
]);
RespireValkeySlotMigration[] jobs = await source.ClusterGetSlotMigrationsAsync();
```

`ClusterMigrateSlotsAsync` sends one `SLOTSRANGE ... NODE ...` group per destination.
Ranges are inclusive, must lie within 0–16383, and cannot overlap within or across
groups. All groups must be admitted together, but an `OK` reply only starts the
asynchronous work. Inspect status for completion before relying on changed ownership.
Client cancellation stops waiting; it does not cancel server jobs. Ambiguous transport
failures are not retried or redirected.

`ClusterCancelSlotMigrationsAsync` cancels active exports started on the selected
**source** node. Calling it on a target does not cancel that target's imports.
Valkey returns `ERR No migrations ongoing` when that node has no active exports;
the method preserves this server error rather than treating cancellation as idempotent.
Start, cancellation, and flush require `AllowAdmin`; status is read-only under the
client policy. Server ACL permissions apply independently to every operation.

Status snapshots own their strings and nested `AdditionalFields`, so they remain
usable after connections and the client are disposed. `success`, `failed`, and
`cancelled` are terminal states; other states remain active. Operation and state strings
preserve future values. Source and target IDs are absent on tracking replicas.
Timestamps are Unix **seconds**, represented as `DateTimeOffset`. Valkey 9.1 adds
`RemainingReplicationBytes`; it is null when the server omits `remaining_repl_size`.
The server retains only a bounded history of inactive jobs, in memory; snapshots are
not a durable audit log. See [Valkey's status documentation](https://valkey.io/commands/cluster-getslotmigrations/).

`ClusterFlushSlotAsync(slot, ServerFlushMode.Default/Sync/Async)` implements the
supported Valkey 9+ **FLUSHSLOT** command. It destructively deletes the selected slot's
keys across databases on that node. It does not remove slot ownership and is distinct
from **FLUSHSLOTS**, which clears ownership metadata. Default follows the server's
`lazyfree-lazy-user-flush` setting. Use only on a deliberately selected endpoint.
The [Valkey 9.0 command schema](https://github.com/valkey-io/valkey/blob/a100149d56208209c03f8af9840afe2efa7fb8d1/src/commands/cluster-flushslot.json)
and [implementation](https://github.com/valkey-io/valkey/blob/a100149d56208209c03f8af9840afe2efa7fb8d1/src/cluster.c)
establish this supported user-facing boundary.

There is deliberately no typed `SYNCSLOTS` request API. Valkey documents it as an
[internal state-machine command](https://valkey.io/commands/cluster-syncslots/).
Its [implementation](https://github.com/valkey-io/valkey/blob/a100149d56208209c03f8af9840afe2efa7fb8d1/src/cluster_migrateslots.c)
uses migration-owned connections, disabled ordinary replies, and pushed protocol
messages. A normal one-request/one-reply wrapper cannot implement that protocol.
The raw-command API remains available for server-specific ordinary commands, but
does not turn `SYNCSLOTS` into a supported RPC.

Current online MIGRATESLOTS documentation also shows per-group `AUTH`. The released
[9.0 implementation](https://github.com/valkey-io/valkey/blob/a100149d56208209c03f8af9840afe2efa7fb8d1/src/cluster_migrateslots.c)
and [9.1 grammar](https://github.com/valkey-io/valkey/blob/7f1dffedff6de73058b2c2a389422b6ecd56c8fb/src/commands/cluster-migrateslots.json)
do not accept that option. This typed API targets their shared grammar; configure
server-to-server migration authentication on the servers. Client authentication still
comes from the client's normal options.
