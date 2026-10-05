# Typed Sentinel administration

`RespireSentinelClient` connects directly to one Sentinel endpoint. It is independent of
the data client: commands do not go through primary discovery or Cluster routing.
The client owns its connection and must be disposed separately.

```csharp
var options = new RespireOptions
{
    SentinelUsername = "operator",
    SentinelPassword = sentinelPassword,
    SentinelUseTls = true,
    AllowAdmin = true,
};
await using var sentinel = await RespireSentinelClient.ConnectAsync(
    new RespireEndpoint("sentinel.example", 26379), options);

var primary = await sentinel.PrimaryAsync("orders");
var replicas = await sentinel.ReplicasAsync("orders");
var peers = await sentinel.SentinelsAsync("orders");
var quorum = await sentinel.CheckQuorumAsync("orders");
await sentinel.SetAsync("orders", new Dictionary<string, string>
{
    ["parallel-syncs"] = "1",
});
```

The existing Sentinel credential rules apply: separate Sentinel credentials take
precedence over data credentials; an empty `SentinelPassword` explicitly disables
authentication. `SentinelUseTls` and `SentinelTlsOptions` override the data transport
settings. No database selection, client-side cache, key prefix, replica routing, or
data-primary discovery is applied to this client.

`INFO SERVER` permission is required during connection. Valkey 8+ uses `PRIMARY`,
`PRIMARIES`, and `IS-PRIMARY-DOWN-BY-ADDR`; Redis uses the legacy master names.
Redis before 5 uses `SLAVES`, while Redis 5+ and Valkey use `REPLICAS`.
These names are selected from server family/version rather than retrying arbitrary
server errors. Unsupported commands retain their original server errors.

## Commands and results

| API | Sentinel command | Result |
| --- | --- | --- |
| `PrimariesAsync`, `PrimaryAsync` | `MASTERS`/`PRIMARIES`, `MASTER`/`PRIMARY` | Owned primary records, including configuration epoch and quorum |
| `ReplicasAsync` | `REPLICAS`/`SLAVES` | Owned replica records with primary address, priority, offset, and flags |
| `SentinelsAsync` | `SENTINELS` | Owned peer records with run IDs |
| `CheckQuorumAsync` | `CKQUORUM` | Success description; quorum failure throws |
| `InfoCacheAsync` | `INFO-CACHE` | Cached INFO text and age in milliseconds, grouped by primary name; Redis 3.2+ |
| `MyIdAsync` | `MYID` | Node ID; Redis 6.2+ |
| `PendingScriptsAsync` | `PENDING-SCRIPTS` | Script arguments, scheduling state, process ID, runtime/delay, and retries |
| `ConfigGetAsync`, `ConfigSetAsync` | `CONFIG GET`, `CONFIG SET` | Global configuration; Redis 6.2+ |
| `IsPrimaryDownByAddressAsync` | `IS-MASTER-DOWN-BY-ADDR`/`IS-PRIMARY-DOWN-BY-ADDR` | Down state, leader ID, and leader epoch |
| `FailoverAsync`, `ResetAsync` | `FAILOVER`, `RESET` | Failover request, or reset count |
| `SetAsync`, `FlushConfigAsync` | `SET`, `FLUSHCONFIG` | Monitoring configuration, or configuration-file rewrite |
| `MonitorAsync`, `RemoveAsync` | `MONITOR`, `REMOVE` | Start/stop monitoring one primary |
| `SimulateFailureAsync` | `SIMULATE-FAILURE` | Crash simulation; `None` clears flags; Redis 3.2+ |

Snapshots retain down/disconnected rows and all reported scalar attributes. A replica's
`Primary` is null when Sentinel has not yet learned its upstream address from INFO. They do
not filter the server's observations into a healthy routing table. Malformed replies
throw `RespireProtocolException`; an unknown primary throws the server error.
Returned strings and records remain valid after subsequent commands or client disposal.

Every mutation requires `AllowAdmin=true`. `IsPrimaryDownByAddressAsync` defaults to
`runId="*"`, which only observes state. A different run ID requests a leader vote and
also requires `AllowAdmin`. Operations affect only the selected Sentinel. In particular,
`FAILOVER` forces failover without agreement from other Sentinels, and crash simulation
can terminate the server. Coordinate changes across your deployment explicitly.
Accepted commands are not replayed after a disconnect. Cancellation and command
timeouts use the existing Respire connection lifecycle.

Wire-name and reply-shape references:
[Redis Sentinel API](https://redis.io/docs/latest/operate/oss_and_stack/management/sentinel/#sentinel-api),
[Redis 8.10 source](https://github.com/redis/redis/blob/8.10/src/sentinel.c),
[Valkey 8.0 source](https://github.com/valkey-io/valkey/blob/8.0/src/sentinel.c).
