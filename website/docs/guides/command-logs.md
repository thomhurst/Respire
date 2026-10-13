# Valkey command logs

Valkey 8.1+ keeps three independent, per-node command logs: slow commands, large requests, and large replies. Read and reset them with `CommandLogAsync`, `CommandLogLengthAsync`, and `ResetCommandLogAsync` on the Server facet, or their `OnAllNodesAsync` counterparts. On Redis, use `SlowLogAsync`; Redis servers without COMMANDLOG return their error, with no fallback.

```csharp
var entries = await redis.Server.CommandLogAsync(RespireCommandLogType.LargeReply, count: 20);
foreach (var entry in entries)
    Console.WriteLine($"{entry.Id}: {entry.ReplyBytes} bytes at {entry.TimestampUnixSeconds}");
```

| `RespireCommandLogType` | Measurement | Threshold configuration | Retention configuration |
| --- | --- | --- | --- |
| `Slow` | Execution microseconds, excluding client I/O | `commandlog-execution-slower-than` | `commandlog-slow-execution-max-len` |
| `LargeRequest` | Request bytes | `commandlog-request-larger-than` | `commandlog-large-request-max-len` |
| `LargeReply` | Reply bytes | `commandlog-reply-larger-than` | `commandlog-large-reply-max-len` |

Threshold `-1` disables a log; `0` records every eligible command. Larger retention uses more server memory. These APIs never change thresholds or retention. See Valkey's [GET](https://valkey.io/commands/commandlog-get/) and [LEN](https://valkey.io/commands/commandlog-len/) references.

## Entries

- Each entry has its type, ID, Unix timestamp in seconds, raw metric, binary arguments, and binary client address and name.
- `DurationMicroseconds`, `RequestBytes`, and `ReplyBytes` are set only for the matching log type; the others are null. `MetricValue` holds the raw metric for generic tooling.
- Unknown trailing fields go to `AdditionalValues` as `RespireResult` values. They need no disposal; disposing one invalidates its views.
- Entries stay usable after the client is disposed.

Count:

| `count` | Result |
| --- | --- |
| omitted | 10 entries |
| `-1` | All retained entries (unbounded; can transfer large payloads) |
| `0` | None |
| below `-1` | Throws before I/O (as do undefined log types) |

- Results keep server order; Valkey 8.1.3 returns newest first (see its [implementation](https://github.com/valkey-io/valkey/blob/8.1.3/src/commandlog.c)).
- IDs are scoped to a node and log type. Resetting a log does not reset its IDs.

:::warning
The server limits recorded argument counts and lengths and may omit or redact sensitive commands. Respire returns the bytes as given; it cannot rebuild the original command or guarantee the log contains no secrets.
:::

## Scope and resets

Ordinary calls use one node. With `UseCluster`, all-node calls discover replicas and slotless members (needs CLUSTER NODES permission); otherwise they query only the connected endpoint. Each result has its endpoint and either a value or the original error. Views can overlap; nothing is deduplicated.

```csharp
var results = await redis.Server.CommandLogLengthOnAllNodesAsync(RespireCommandLogType.Slow);
foreach (var result in results)
{
    if (result.IsSuccess) Console.WriteLine($"{result.Endpoint}: {result.Value}");
    else Console.WriteLine($"{result.Endpoint}: {result.Error!.Message}");
}
```

- Reading does not need `AllowAdmin`; server ACLs still apply.
- RESET needs `AllowAdmin` (checked before I/O) and permanently clears the selected log. Its cost grows with retained entries; see [COMMANDLOG RESET](https://valkey.io/commands/commandlog-reset/).
- All-node resets return `true` per successful node and can partly succeed, with no rollback or replication.

Cancellation:

- Pre-cancelled calls send nothing.
- Cancellation during discovery throws; after discovery it is reported per node.
- Cancellation after RESET is sent cannot undo it. Respire never retries a RESET.

These six methods are `IServerCommands` members. External implementations, decorators, and mocks must implement or forward them. They have no batch or transaction versions.
