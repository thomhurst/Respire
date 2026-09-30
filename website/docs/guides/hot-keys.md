# Hot-key diagnostics

Redis 8.6+ HOTKEYS tracks expensive keys by command execution time and network bytes.
One server has one shared tracking session. Starting another active session fails;
other administrators can inspect, stop, or replace the same session. Respire does not
claim exclusive ownership or silently substitute another diagnostic on older servers.

## Keep the lifecycle on one server

Acquire a pinned handle before starting. Every operation uses that original physical
connection, even when the client has multiple connections or Cluster routing changes.
`Endpoint` identifies its server; `IsConnected` is a point-in-time observation. The
handle uses a client-owned socket and requires no disposal. The socket remains shared
with ordinary commands; holding a tracker does not reserve pool capacity, even with
one configured connection. It does not reconnect,
follow redirects, retry writes, or automatically stop tracking. After disconnection,
reacquire explicitly and inspect the server's state before starting another session.
Operations on a disconnected handle fail with `RespireConnectionException`; operations
after parent-client disposal fail with `ObjectDisposedException`. HOTKEYS controls
change diagnostic state only and preserve cached keyspace values.

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

START, STOP, and RESET require `AllowAdmin` before command I/O. GET does not require
that local opt-in; server ACLs apply to every operation. STOP returns false when no
active session exists, and retains completed data. GET returns null before any session
or after RESET; otherwise it returns the server's array of snapshot maps (normally one).
RESET fails while tracking is active. Starting after STOP replaces the previous data.
See [STOP](https://redis.io/docs/latest/commands/hotkeys-stop/) and
[RESET](https://redis.io/docs/latest/commands/hotkeys-reset/).

## Options and measurements

| Option | Accepted values | When omitted |
| --- | --- | --- |
| `Metrics` | `Cpu`, `Network`, or both | Both |
| `Count` | 1–64 | Server default: 10 |
| `DurationSeconds` | 1–1,000,000 whole seconds | No automatic stop |
| `SampleRatio` | 1–`int.MaxValue`; one in this many commands | Server default: 1 |
| `Slots` | Distinct integers 0–16383; empty omits the filter | All local slots |

Slot filters require Cluster and ownership by the target node; these remain server
checks. Slot numbers are literal and never key-prefixed. Values are copied before
asynchronous work; do not mutate supplied slot memory concurrently with the call.
Explicit zero duration is rejected by the pinned Redis 8.6 implementation, despite
the help text describing zero as unlimited; omit `DurationSeconds` for that behavior.
See [START](https://redis.io/docs/latest/commands/hotkeys-start/) and the audited
[source](https://github.com/redis/redis/blob/3b450c3dcb075e59f50dbc0377151e25c8f375d4/src/hotkeys.c).

`ByCpuTime` reports estimated execution microseconds per binary key; `ByNetworkBytes`
reports estimated request/response bytes. Collection timestamps and duration use
milliseconds, as do total process user/system CPU measurements. Property names retain
these units. Optional measurements and ranking arrays are null when omitted by the
server. Selected slots preserve inclusive ranges, including individual slots.

Each snapshot owns its key bytes and unknown `AdditionalFields`, including nested
future values. It remains usable after client disposal. Additional field results use
GC-owned storage and need no disposal; explicit disposal invalidates their views.
Arrays are caller-owned and mutable, and record equality does not compare array contents.
Reported keys retain their full server bytes, including any application prefix.

Tracking consumes server CPU and memory. Sampling reduces the observed commands;
top-key estimates are not an exact access count or latency distribution. Multi-key
commands apportion their cost across keys. Fetching results has a cost proportional
to the retained ranking size. No client-side performance guarantee or global ranking
is implied. See [GET](https://redis.io/docs/latest/commands/hotkeys-get/).

## Explicit all-node operations

`StartHotKeysOnAllNodesAsync`, `GetHotKeysOnAllNodesAsync`, `StopHotKeysOnAllNodesAsync`,
and `ResetHotKeysOnAllNodesAsync` target every currently discovered Cluster member,
including replicas and slotless members. Standalone returns one endpoint result.
Discovery needs CLUSTER NODES permission. Each result carries its endpoint and value
or original error. Starts and resets return true for OK; stops can successfully return
false, and inspection can successfully return null.

```csharp
var results = await redis.Server.GetHotKeysOnAllNodesAsync();
foreach (var result in results)
{
    if (result.IsSuccess) Console.WriteLine($"{result.Endpoint}: {result.Value?.Length ?? 0} snapshots");
    else Console.WriteLine($"{result.Endpoint}: {result.Error!.Message}");
}
```

Each all-node call discovers membership afresh. Compare endpoints between calls;
new members can appear and removed members can retain an earlier session. Use a pinned
handle for a lifecycle tied to one server. A single slot filter sent to all members
can fail on nodes that do not own those slots. Operations are neither atomic nor
automatically replicated; partial successes remain and no rollback is attempted.

Pre-cancelled operations send no command. Discovery cancellation throws; later fan-out
cancellation is reported per affected node. Cancelling a dispatched mutation cannot
undo it. A lost reply can leave the outcome uncertain; inspect before retrying.

This feature adds five members to `IServerCommands` and the `RespireHotKeysTracker`
handle. External implementations and mocks must implement or forward the new members.
It does not add batch or transaction commands.
