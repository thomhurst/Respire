---
title: Typed Sentinel administration
description: Query and administer one Sentinel directly with typed results, separate credentials, and TLS.
---

# Typed Sentinel administration

`RespireSentinelClient` connects directly to one Sentinel endpoint to query and administer it. It is separate from the data client: no primary discovery or Cluster routing. It owns its connection; dispose it separately.

```csharp
var options = new RespireOptions
{
    SentinelUsername = "operator",
    SentinelPassword = Environment.GetEnvironmentVariable("SENTINEL_PASSWORD")
        ?? throw new InvalidOperationException("Set SENTINEL_PASSWORD before connecting."),
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

Connection settings:

- `SentinelUsername`, `SentinelPassword`, and `SentinelCredentialProvider` override the data credentials; null falls back to them. An empty `SentinelPassword` disables authentication.
- `SentinelUseTls` and `SentinelTlsOptions` override the data transport settings.
- No database selection, client-side cache, key prefix, replica routing, or primary discovery applies.

Command names are chosen from the server family and version reported by `INFO SERVER` at connect, not by retrying errors:

- Valkey 8+ uses `PRIMARY`, `PRIMARIES`, and `IS-PRIMARY-DOWN-BY-ADDR`; Redis uses the legacy master names.
- Redis before 5 uses `SLAVES`; Redis 5+ and Valkey use `REPLICAS`.
- If `INFO SERVER` returns ACL `NOPERM`, the client uses legacy master/slave names and single-option global configuration. Grant INFO permission to enable version-specific features. Authentication and other errors still fail the connection.
- Unsupported commands return the server error.

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
| `ConfigGetAsync`, `ConfigSetAsync` | `CONFIG GET`, `CONFIG SET` | Global configuration; Redis 6.2+. Setting multiple option pairs requires Redis 7.2+ or Valkey 8+ |
| `IsPrimaryDownByAddressAsync` | `IS-MASTER-DOWN-BY-ADDR`/`IS-PRIMARY-DOWN-BY-ADDR` | Down state, leader ID, and leader epoch |
| `FailoverAsync`, `ResetAsync` | `FAILOVER`, `RESET` | Failover request, or reset count |
| `SetAsync`, `FlushConfigAsync` | `SET`, `FLUSHCONFIG` | Monitoring configuration, or configuration-file rewrite |
| `MonitorAsync`, `RemoveAsync` | `MONITOR`, `REMOVE` | Start/stop monitoring one primary |
| `SimulateFailureAsync` | `SIMULATE-FAILURE` | Crash simulation; `None` clears flags; Redis 3.2+ |

- Snapshots include down and disconnected rows and every reported attribute; they are not a filtered routing table.
- A replica's `Primary` is null until Sentinel learns its upstream address from INFO.
- Malformed replies throw `RespireProtocolException`. An unknown primary throws the server error.
- Results stay valid after later commands and client disposal.
- `SetAsync` keeps your enumeration order, including duplicates. Use a list or array when order matters, such as `auth-user` before `auth-pass`.
- `SimulateFailureAsync(None)` sends `SIMULATE-FAILURE HELP`, which clears flags and returns the supported flag names; the client validates the reply.

## Mutations

Every mutation needs `AllowAdmin=true`.

- `IsPrimaryDownByAddressAsync` defaults to `runId="*"`, which only observes. Any other run ID requests a leader vote and also needs `AllowAdmin`.
- Operations affect only the connected Sentinel. `FAILOVER` forces failover without agreement from other Sentinels, and crash simulation can terminate the server. Coordinate changes across your deployment yourself.
- `ConfigSetAsync` sends one command. On older or unidentified servers, multiple option pairs throw `NotSupportedException` before writing. Send separate single-option updates if partial application is acceptable.
- Serialize dependent steps.

:::warning
Accepted commands are not replayed after a disconnect. An interrupted write may have applied even if you see cancellation, timeout, or disposal. Check Sentinel state before repeating failover, reset, monitoring changes, or crash simulation.
:::

Cancellation and command timeouts work as on a data client.

Wire-name and reply-shape references:
[Redis Sentinel API](https://redis.io/docs/latest/operate/oss_and_stack/management/sentinel/#sentinel-api),
[Redis 8.10 source](https://github.com/redis/redis/blob/8.10/src/sentinel.c),
[Valkey 8.0 source](https://github.com/valkey-io/valkey/blob/8.0/src/sentinel.c).
