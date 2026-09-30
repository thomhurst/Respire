# Valkey command logs

Valkey 8.1+ exposes three independent node-local command logs. Respire's Server facet
provides `CommandLogAsync`, `CommandLogLengthAsync`, and `ResetCommandLogAsync`, plus
explicit `OnAllNodesAsync` counterparts. Existing Redis `SlowLogAsync` behavior is unchanged.
Redis servers without COMMANDLOG return their command errors; there is no fallback.

| `RespireCommandLogType` | Measurement | Threshold configuration | Retention configuration |
| --- | --- | --- | --- |
| `Slow` | Execution microseconds, excluding client I/O | `commandlog-execution-slower-than` | `commandlog-slow-execution-max-len` |
| `LargeRequest` | Request bytes | `commandlog-request-larger-than` | `commandlog-large-request-max-len` |
| `LargeReply` | Reply bytes | `commandlog-reply-larger-than` | `commandlog-large-reply-max-len` |

Threshold `-1` disables the selected log; `0` records every eligible command. Larger
retention limits consume more server memory. These APIs do not change thresholds or
retention automatically. See Valkey's [GET](https://valkey.io/commands/commandlog-get/)
and [LEN](https://valkey.io/commands/commandlog-len/) references.

```csharp
var entries = await redis.Server.CommandLogAsync(RespireCommandLogType.LargeReply, count: 20);
foreach (var entry in entries)
    Console.WriteLine($"{entry.Id}: {entry.ReplyBytes} bytes at {entry.TimestampUnixSeconds}");
```

Each owned entry includes its type, ID, Unix timestamp in seconds, raw metric, binary
arguments, and binary client address/name. `DurationMicroseconds`, `RequestBytes`, and
`ReplyBytes` expose only the applicable measurement; the others are null. Select
log-specific measurements by `Type`; the nullable views make units
explicit at the cost of a null check, while `MetricValue` remains available for generic tooling.
Unknown trailing fields remain in recursively copied `AdditionalValues`. Those GC-owned
`RespireResult` values need no disposal; explicitly disposing one invalidates its views.
Replies remain usable after the client is disposed.

Count defaults to 10; `-1` requests all retained entries and `0` returns none. Values
below `-1` and undefined log types fail before I/O. Results preserve server order.
Pinned Valkey 8.1.3 returns newest first, verified against its
[implementation](https://github.com/valkey-io/valkey/blob/8.1.3/src/commandlog.c).
IDs are scoped to a node and log type; resetting a log does not reset its ID sequence.

The server bounds recorded argument counts and lengths and can omit or redact sensitive
commands. Respire preserves the returned bytes; it cannot reconstruct the original command
or guarantee that logs contain no secrets. Retrieval can transfer large payloads;
`count: -1` is unbounded by the client. Prefer a count appropriate to the workload.

## Scope and resets

Ordinary calls use one execution node. With `UseCluster`, all-node discovery includes
replicas and slotless members; otherwise it queries only the connected endpoint.
Each result carries its endpoint and either a value or the original error. Discovery
requires CLUSTER NODES permission. Views can overlap; results are not deduplicated.

```csharp
var results = await redis.Server.CommandLogLengthOnAllNodesAsync(RespireCommandLogType.Slow);
foreach (var result in results)
{
    if (result.IsSuccess) Console.WriteLine($"{result.Endpoint}: {result.Value}");
    else Console.WriteLine($"{result.Endpoint}: {result.Error!.Message}");
}
```

Inspection does not require `AllowAdmin` locally; server ACLs still apply.
RESET requires `AllowAdmin` before I/O and permanently clears the selected log.
Explicit all-node resets can partially succeed, return `true` for each successful node,
and offer no rollback or automatic replication. Reset costs grow with retained entries;
see [COMMANDLOG RESET](https://valkey.io/commands/commandlog-reset/).

Pre-cancelled calls send nothing. Discovery cancellation throws; cancellation after
discovery is reported per affected node. Cancellation after RESET dispatch cannot undo
the operation. No write retry is added by this API.

These six methods extend `IServerCommands`. External implementations, decorators, and
mocks must implement or forward them. No batch/transaction methods are added.
