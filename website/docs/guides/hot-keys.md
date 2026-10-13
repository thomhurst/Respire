# Hot-key diagnostics

Redis 8.6+ HOTKEYS finds the keys that cost the most execution time and network bytes. Use it to diagnose load hotspots. Each server has one shared tracking session: starting a second active session fails, and other administrators can inspect, stop, or replace it. Respire claims no exclusive ownership and offers no substitute on older servers.

## Keep the lifecycle on one server

Get a pinned tracker before starting. Every operation uses that original physical connection, even with multiple connections or changing Cluster routing.

```csharp
// Construct redis with AllowAdmin = true to enable state-changing operations.
var tracker = await redis.Server.GetHotKeysTrackerAsync();
await tracker.StartAsync(new RespireHotKeysOptions
{
    Metrics = RespireHotKeysMetrics.Cpu | RespireHotKeysMetrics.Network,
    Count = 10,
    DurationSeconds = 30,
    SampleRatio = 1,
});
// Application traffic on this server contributes to the shared session.
var snapshots = await tracker.GetAsync();
bool stopped = await tracker.StopAsync();
// Inspect the retained snapshots before releasing the server's diagnostic data.
await tracker.ResetAsync();
```

The tracker:

- `Endpoint` identifies the server. `IsConnected` is a point-in-time check.
- Needs no disposal. The socket stays shared with ordinary commands, and holding a tracker reserves no pool capacity, even with one connection.
- Never reconnects, follows redirects, retries writes, or stops tracking for you. After a disconnect, get a new tracker and check server state before starting another session.
- Throws `RespireConnectionException` once disconnected and `ObjectDisposedException` after the parent client is disposed.
- HOTKEYS changes diagnostic state only; cached values are unaffected.

Operations:

| Operation | Behavior |
| --- | --- |
| START | Needs `AllowAdmin`. Starting after STOP replaces the previous data. |
| GET | No `AllowAdmin` needed. Returns null before any session or after RESET; otherwise the server's array of snapshots (normally one). |
| STOP | Needs `AllowAdmin`. Returns false if no session is active. Keeps completed data. |
| RESET | Needs `AllowAdmin`. Fails while tracking is active. |

Without `AllowAdmin`, START, STOP, and RESET throw `NotSupportedException` before any I/O. Server ACLs apply to all operations. See [STOP](https://redis.io/docs/latest/commands/hotkeys-stop/) and [RESET](https://redis.io/docs/latest/commands/hotkeys-reset/).

## Options and measurements

| Option | Accepted values | When omitted |
| --- | --- | --- |
| `Metrics` | `Cpu`, `Network`, or both | Both |
| `Count` | 1–64 | Server default: 10 |
| `DurationSeconds` | 1–1,000,000 whole seconds | No automatic stop |
| `SampleRatio` | 1–`int.MaxValue`; one in this many commands | Server default: 1 |
| `Slots` | Distinct integers 0–16383; empty omits the filter | All local slots |

- Slot filters need Cluster and slots owned by the target node; the server checks this. Slot numbers are literal and never key-prefixed.
- Values are copied before async work. Do not change the slot memory while the call runs.
- Redis 8.6 rejects an explicit zero duration, although its help text calls zero unlimited. Omit `DurationSeconds` for no limit.

See [START](https://redis.io/docs/latest/commands/hotkeys-start/) and the Redis 8.6 [source](https://github.com/redis/redis/blob/3b450c3dcb075e59f50dbc0377151e25c8f375d4/src/hotkeys.c).

Snapshots:

- `ByCpuTime` gives estimated execution microseconds per binary key; `ByNetworkBytes` gives estimated request and response bytes.
- Collection timestamps, duration, and process user/system CPU are in milliseconds. Property names include the units.
- Optional measurements and rankings are null when the server omits them. Selected slots are inclusive ranges.
- Reported keys keep their full server bytes, including any application prefix.
- Snapshots own their data, including unknown `AdditionalFields`, and stay usable after client disposal. `AdditionalFields` values need no disposal; disposing one invalidates its views.
- Arrays are caller-owned and mutable. Record equality does not compare array contents.

Cost and accuracy:

- Tracking uses server CPU and memory. Fetching costs more as the ranking grows.
- Sampling reduces the commands observed. Results are estimates, not exact counts or latency distributions.
- Multi-key commands split their cost across keys.
- No global ranking across servers is implied. See [GET](https://redis.io/docs/latest/commands/hotkeys-get/).

## Explicit all-node operations

`StartHotKeysOnAllNodesAsync`, `GetHotKeysOnAllNodesAsync`, `StopHotKeysOnAllNodesAsync`, and `ResetHotKeysOnAllNodesAsync` target every discovered Cluster member, including replicas and slotless members (needs CLUSTER NODES permission). Standalone returns one result. Each result has its endpoint and a value or the original error. Starts and resets return true for OK; a stop can succeed with false and a get can succeed with null.

```csharp
var results = await redis.Server.GetHotKeysOnAllNodesAsync();
foreach (var result in results)
{
    if (result.IsSuccess) Console.WriteLine($"{result.Endpoint}: {result.Value?.Length ?? 0} snapshots");
    else Console.WriteLine($"{result.Endpoint}: {result.Error!.Message}");
}
```

- Each call rediscovers membership. Compare endpoints between calls: new members can appear, and removed members can keep an earlier session. Use a pinned tracker to tie a lifecycle to one server.
- One slot filter sent to every member can fail on nodes that do not own those slots.
- Calls are not atomic or replicated. Partial successes remain; nothing is rolled back.

For a bounded run:

1. Set `DurationSeconds` before starting.
2. Keep the per-node start results.
3. After a partial failure, inspect the original endpoints, including those whose replies were lost. If membership changed, connect to each endpoint directly and get a pinned tracker.

:::warning
HOTKEYS has no session-owner token. A node that accepted your START may now hold another administrator's session. Coordinate before stopping a session or resetting data.
:::

Cancellation:

- Pre-cancelled operations send nothing.
- Cancellation during discovery throws; later cancellation is reported per node.
- Cancelling a sent mutation cannot undo it. A lost reply leaves the outcome unknown; inspect before retrying.

`GetHotKeysTrackerAsync` and the four `...HotKeysOnAllNodesAsync` methods are `IServerCommands` members. External implementations and mocks must implement or forward them. There are no batch or transaction HOTKEYS commands.
