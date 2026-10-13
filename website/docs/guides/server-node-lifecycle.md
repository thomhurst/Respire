# Node lifecycle and administration

`redis.Server.OnNode` targets one physical server for lifecycle and administration commands: failover, replication, modules, backups, shutdown, key migration, and Cluster slot migration.

```csharp
RespireServerNode node = redis.Server.OnNode(new RespireEndpoint("redis-primary", 6379));
string password = await node.AclGeneratePasswordAsync();
byte[][] users = await node.AclUsersAsync();
RespireCommandKeyFlags[] keys = await node.CommandGetKeysAndFlagsAsync("MGET", ["key1", "key2"]);
```

- `OnNode` does not connect or discover topology. Each call opens its own connection to the endpoint and closes it afterwards.
- Authentication, renewable credentials, TLS, timeouts, protocol, and selected database come from the client.
- No command is redirected, replayed, or broadcast. The handle stops working when the client is disposed.
- Methods are immediate, not queueable in batches or transactions.
- Keys in arguments and results are **physical server keys**; `WithKeyPrefix` does not apply. Binary inputs are copied before I/O; binary results own their storage.
- `OnNode` is not part of the `IServerCommands` interface contract. On a third-party implementation it throws `NotSupportedException`.

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

- `ACL GENPASS` defaults to 256 bits; values must be 1 to 1024. Redis rounds up to whole hex digits.
- `COMMAND GETKEYSANDFLAGS` keeps unknown flags.
- Latency graphs need a recorded event; unknown events return a Redis error.
- Allocator reports depend on the server's allocator.
- `BACKUP STATUS` returns state, error text, and nullable Unix timestamps (zero means no recorded time). Unknown states and fields are kept.
- `BACKUP LIST` returns file paths on the server, not contents.

:::warning
**Use `KeysAsync` only for debugging.** `KEYS` scans the whole database and blocks the server. Use `redis.Keys.ScanAsync` in production.
:::

## Mutations

All operations below need `RespireOptions.AllowAdmin = true`. Redis ACL checks still apply. Unsupported commands return the server error. Respire invalidates the local client-side cache before and after each mutation, even if it fails or is cancelled.

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

- `FAILOVER` needs Redis 6.2. Forced failover needs both a target and a positive timeout. Aborting failover can leave replication inconsistent.
- Module paths and configuration refer to the server's filesystem and installed modules. `MODULE LOADEX` needs Redis 7.0; each configuration pair gets its own `CONFIG` token.
- Backup operations need Redis 8.10.

## Cancelling BUSY scripts and functions

```csharp
await redis.Server.OnNode(new RespireEndpoint("busy-node", 6379)).ScriptKillAsync();
```

Kill methods open a fresh control connection, so they work even while the client's only connection waits on the running script. The control handshake uses RESP2, database zero, and authentication only, skipping naming, database selection, topology, tracking, and maintenance negotiation. TLS and credential providers still apply. `FUNCTION KILL` needs Redis 7.0.

Redis refuses to kill a script or function that has already written data. `NOTBUSY`, `UNKILLABLE`, authorization, and transport errors are thrown to the caller.

## Shutdown and ambiguous outcomes

```csharp
RespireServerNode node = redis.Server.OnNode(new RespireEndpoint("redis-primary", 6379));
await node.SendShutdownAsync(new RespireShutdownOptions
{
    SaveMode = RespireShutdownSaveMode.NoSave,
    Now = true,
});
```

- Redis sends **no success reply** to a normal shutdown. `SendShutdownAsync` completes once the command is written to the local socket; it cannot confirm acceptance or see server errors. Verify shutdown separately.
- `NOW` skips waiting for replicas. `FORCE` can lose data when persistence fails.
- `NOW`, `FORCE`, and `AbortShutdownAsync` need Redis 7.0. `AbortShutdownAsync` waits for `OK`.
- Shutdown uses the same control handshake as the kill methods. With a BUSY script, use `NOSAVE`.

### MIGRATE

`MigrateAsync` takes one or more physical keys, a TCP destination, a destination database, and a positive server timeout. `RespireMigrateOptions` sets `COPY`, `REPLACE`, and destination `AUTH` or `AUTH2`. The result tells acknowledged `OK` from `NOKEY`.

Timeouts:

- The [server timeout](https://redis.io/docs/latest/commands/migrate/) limits idle time talking to the destination, not total transfer time.
- Respire's `CommandTimeout` and caller cancellation apply separately. A client timeout can expire while Redis is still transferring, even if it exceeds the server timeout.
- `RespireMigrateOptions.CommandTimeout` overrides the client response budget for this call and its connection setup (null inherits; minimum one millisecond). It does not affect other operations, `ConnectionIdleReadTimeout`, or caller cancellation.
- Respire never extends these budgets. Set the client timeout to cover the whole expected transfer, including large values. For example, a five-second server timeout with a thirty-second client budget works only if thirty seconds covers the transfer.

Command telemetry records the operation name only, without MIGRATE arguments or destination credentials, and client diagnostics never format them. Server errors are kept intact; treat their text and server-side command logs according to the server's handling of sensitive data.

:::warning
Timeouts, errors, cancellation, and disconnects can leave a migration or other lifecycle mutation partly applied. Inspect the affected servers before retrying. Respire never replays these operations.
:::

## Atomic Redis Cluster slot migration

Redis 8.4+ supports [CLUSTER MIGRATION](https://redis.io/docs/latest/commands/cluster-migration/). This is separate from Valkey slot migration and from key-by-key `MIGRATE`. Start an import on the destination primary's endpoint. Imports and cancellation need `AllowAdmin = true`; status does not. Redis ACLs apply to all three.

```csharp
var destination = redis.Server.OnNode(new RespireEndpoint("destination-primary", 6379));
string taskId = await destination.ClusterMigrationImportAsync(
    [new RespireClusterSlotRange(0, 100), new RespireClusterSlotRange(200, 300)], cancellationToken);
RespireClusterMigrationTask[] tasks = await destination.ClusterMigrationStatusAsync(taskId, cancellationToken);
long cancelled = await destination.ClusterMigrationCancelAsync(taskId, cancellationToken);
```

Import:

- Ranges are inclusive, non-overlapping, and within 0–16383; Redis accepts fewer than 16384 ranges. The server's slot ownership determines the source.
- The task ID confirms the task was created, not completed. Poll with your own cancellation or deadline until it reports `completed` or a terminal failure; check `LastError` and `Retries`.
- Respire does not orchestrate the migration, wait for convergence, or refresh its routing table.

Status:

- `ClusterMigrationStatusAsync()` sends `STATUS ALL`: active and archived tasks on this node.
- The string overload sends `STATUS ID task-id`; an unknown task returns an empty array.
- `RespireClusterMigrationStatusScope.Default` sends bare `STATUS`, as documented. Redis 8.4 and 8.10 reject it (see the [8.4](https://github.com/redis/redis/blob/8.4/src/cluster_asm.c) and [8.10](https://github.com/redis/redis/blob/8.10/src/cluster_asm.c) implementations); use `All`. Respire returns the error unchanged.
- Results own their data. `Slots` keeps the server's range text. `Operation`, `State`, and `AdditionalFields` keep future values.
- Timestamps come from Unix **milliseconds**; unset start/end (`-1`) becomes null. `WritePauseMilliseconds` is the server's pause duration.

Cancel:

- `ClusterMigrationCancelAllAsync()` sends `CANCEL ALL`. Both cancel methods return the number cancelled on the node.
- Cancelling on the source does **not** stop the destination retrying; cancel there too.
- Cancellation does not undo a completed migration. Check status and slot ownership on the affected nodes.

Each request uses a short-lived connection to exactly the selected endpoint and is never redirected or replayed. Imports and cancellation invalidate the local client cache, including on errors. A timeout, cancellation, or disconnect after submission can leave an import running; check task status before acting.

## Valkey slot migration and deletion

Valkey 9.0+ has its own atomic slot migration protocol. These methods target exactly the `Server.OnNode` endpoint, do not use Redis's `CLUSTER MIGRATION`, and do not work on Redis.

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

Start:

- `ClusterMigrateSlotsAsync` sends one `SLOTSRANGE ... NODE ...` group per destination.
- Ranges are inclusive, within 0–16383, and cannot overlap within or across groups.
- All groups are admitted together, but `OK` only starts the work. Check status before relying on new ownership.
- Cancelling the client call stops waiting; it does not cancel server jobs. Ambiguous transport failures are not retried or redirected.

Cancel:

- `ClusterCancelSlotMigrationsAsync` cancels active exports started on the selected **source**. Calling it on a target does not cancel its imports.
- With no active exports, Valkey returns `ERR No migrations ongoing`, and the method throws it rather than treating cancel as idempotent.

Start, cancel, and flush need `AllowAdmin`; status does not. Server ACLs apply to every operation.

[Status](https://valkey.io/commands/cluster-getslotmigrations/):

- Snapshots own their strings and `AdditionalFields`, and stay usable after the client is disposed.
- `success`, `failed`, and `cancelled` are terminal; other states are active. Operation and state strings keep future values.
- Source and target IDs are absent on tracking replicas.
- Timestamps are Unix **seconds**, as `DateTimeOffset`.
- `RemainingReplicationBytes` (Valkey 9.1) is null when the server omits `remaining_repl_size`.
- The server keeps a bounded, in-memory history of inactive jobs. It is not a durable audit log.

### FLUSHSLOT

:::warning
`ClusterFlushSlotAsync(slot, ServerFlushMode.Default/Sync/Async)` sends Valkey 9+ **FLUSHSLOT**, which deletes all keys in the slot across databases on that node. Use it only on a deliberately chosen endpoint.
:::

- It keeps slot ownership; **FLUSHSLOTS** (which clears ownership metadata) is a different command.
- `Default` follows the server's `lazyfree-lazy-user-flush` setting.
- See the [Valkey 9.0 command schema](https://github.com/valkey-io/valkey/blob/a100149d56208209c03f8af9840afe2efa7fb8d1/src/commands/cluster-flushslot.json) and [implementation](https://github.com/valkey-io/valkey/blob/a100149d56208209c03f8af9840afe2efa7fb8d1/src/cluster.c).

### Not supported

- **SYNCSLOTS** has no typed API. Valkey documents it as an [internal state-machine command](https://valkey.io/commands/cluster-syncslots/); its [implementation](https://github.com/valkey-io/valkey/blob/a100149d56208209c03f8af9840afe2efa7fb8d1/src/cluster_migrateslots.c) uses pushed protocol messages that a request/reply call cannot handle. The raw-command API does not make it work either.
- **Per-group `AUTH`** in MIGRATESLOTS appears in the online docs, but the released [9.0 implementation](https://github.com/valkey-io/valkey/blob/a100149d56208209c03f8af9840afe2efa7fb8d1/src/cluster_migrateslots.c) and [9.1 grammar](https://github.com/valkey-io/valkey/blob/7f1dffedff6de73058b2c2a389422b6ecd56c8fb/src/commands/cluster-migrateslots.json) reject it. Configure server-to-server migration authentication on the servers. Client authentication comes from the client's normal options.
