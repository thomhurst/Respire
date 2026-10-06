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
