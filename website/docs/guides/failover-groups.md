---
title: Failover groups
---

`RespireFailoverGroup` monitors independent standalone, Sentinel or Redis Cluster deployments and
sends new operations to a healthy one. Use it to fail over between separate deployments, such as
two regions. Lower candidate priorities win.

```csharp
await using var group = await RespireFailoverGroup.ConnectAsync(
[
    new RespireFailoverCandidate(new RespireOptions
    {
        Endpoints = ["redis-primary:6379"],
        Protocol = RespireProtocol.Auto,
    }, Priority: 0),
    new RespireFailoverCandidate(new RespireOptions
    {
        Endpoints = ["redis-secondary:6379"],
        Protocol = RespireProtocol.Auto,
    }, Priority: 1),
],
new RespireFailoverGroupOptions
{
    ProbeInterval = TimeSpan.FromSeconds(1),
    ProbeTimeout = TimeSpan.FromSeconds(2),
    FailureThreshold = 2,
    CircuitOpenDuration = TimeSpan.FromSeconds(5),
    FailbackGracePeriod = TimeSpan.FromSeconds(10),
});

// Read ActiveClient for every new operation so each call uses the current selection.
await group.ActiveClient.Strings.SetAsync("service:health", "ready");
```

The example values are the defaults.

- `ActiveClient` throws `RespireConnectionException` when no candidate is healthy; check `IsConnected` first to avoid it.
- `GetEndpointStatuses()` returns each candidate's health, consecutive failures, circuit deadline and last error type.
- To retry on whichever member is active, set `RespireFailoverGroupOptions.RetryPolicy` and call `group.ExecuteAsync`. See [failover-group retries](command-retries.md#failover-groups).

Requirements:

- Candidates must use an unlimited reconnect policy (`MaxAttempts = null`, the default). A finite budget can permanently disable a client after a long outage, so later probes cannot recover it.
- `ProbeInterval` and `ProbeTimeout` must fit the runtime timer limit of about 49.7 days.
- Client-side caching is rejected.

## Health probes and failback

| Deployment | Probe | Healthy when |
| --- | --- | --- |
| Standalone | `PING` | `PING` succeeds |
| Cluster | `CLUSTER INFO` | `cluster_state:ok` within `ProbeTimeout` |
| Sentinel | `ROLE`, then `PING`, on the discovered primary | The node is primary and answers `PING` |

- Probes do not inspect application commands. Errors such as `-LOADING`, `-READONLY` or `OOM` do not affect health while `PING` succeeds.
- Probes use the candidate's own connection, so a heavily loaded endpoint can miss probes and be marked unhealthy.
- Detection takes about `FailureThreshold × (ProbeInterval + ProbeTimeout)` after an endpoint becomes unreachable.
- `ConnectAsync` probes each candidate once. A candidate that fails starts unhealthy and is retried in the next background round.

Each candidate has its own probe circuit:

1. After `FailureThreshold` consecutive failed probes (timeouts included), the circuit opens for `CircuitOpenDuration`.
2. The group then sends one recovery probe. If it fails or times out, the circuit reopens.

`RespireFailoverEndpointStatus.CircuitOpenUntil` is a UTC snapshot of the deadline, saturating at
`DateTimeOffset.MaxValue`. This probe circuit is separate from the
[endpoint command circuit breaker](circuit-breakers.md): `RespireCircuitBreakerOptions` does not
configure it, and it does not gate or replay application commands.

Failback waits for a higher-priority endpoint to stay healthy for `FailbackGracePeriod`. Any failed
probe restarts that endpoint's grace period, so a flapping endpoint never becomes active through
failback. The group fails back to the highest-priority endpoint that has completed its grace period.

## Switch events

Subscribe to `EndpointSwitched` for resubscription or diagnostics. `Reason` is a
`RespireFailoverSwitchReasons` constant.

- The initial selection happens inside `ConnectAsync`, so the event never reports `FirstHealthy` (only metrics and logs do). Read `ActiveClient` after connecting to see the initial endpoint.
- `RecoveredFromNoHealthyEndpoint` marks recovery after every endpoint was unhealthy.
- A client reference obtained before a switch stays on its original deployment. All candidate clients stay alive until disposal, so in-flight calls are not interrupted or replayed.
- Pub/sub subscriptions stay on their original deployment; recreate them after a switch.

:::warning
Handlers run synchronously on the health monitor. Keep them short: a slow handler delays the next
probe round. Never wait for `DisposeAsync` in a handler, synchronously or with `await`: disposal
waits for the monitor running the handler and deadlocks.
:::

## Cluster candidates

```csharp
new RespireFailoverCandidate(new RespireOptions
{
    UseCluster = true,
    Endpoints = ["cluster-a-seed-1:6379", "cluster-a-seed-2:6379"],
}, Priority: 0);

// The credentials below are placeholders. Load real values from configuration or a secret store.
new RespireFailoverCandidate(new RespireOptions
{
    Endpoints = ["sentinel-a:26379", "sentinel-b:26379"],
    SentinelPrimaryName = "orders-primary",
    Username = "app",
    Password = "<data-password>",
    SentinelUsername = "sentinel-app",
    SentinelPassword = "<sentinel-password>",
    UseTls = true,
}, Priority: 0);
```

Set `UseCluster = true` and give one or more seed endpoints.

- Each candidate owns its own `RespireClient`, so slot maps and `MOVED`/`ASK` recovery stay within that Cluster.
- Status, events, logs and metrics identify the candidate by its first configured seed, even when commands route through other nodes.
- Redis reports `cluster_state:fail` when a slot has no reachable primary, so a partial outage triggers failover. The probe runs on one node, so it cannot detect a primary that only this client cannot reach.
- The candidate's user needs permission to run `CLUSTER INFO`.
- Each candidate must be a separate Cluster. Only seed endpoints are compared, so two candidates with different seeds in the same Cluster pass validation but give no redundancy.

## Sentinel candidates

Set `SentinelPrimaryName` to the service name and give one or more Sentinel endpoints (see the
second candidate above). Configure data and Sentinel credentials and TLS on each candidate's
`RespireOptions`.

- Each probe sends `ROLE` because a demoted node still answers `PING`. A node that reports primary but rejects `PING` (for example through ACL rules) is unhealthy.
- If the node was demoted, the probe rediscovers the primary through Sentinel and stays healthy if the replacement answers `PING` within `ProbeTimeout`.
- Endpoint status reports the validated primary, or null while none is known. Probe telemetry then uses the first configured Sentinel endpoint.
- A switch event reports the previous candidate's primary as it was when that candidate was selected.

### Duplicate deployments

Candidates must be separate deployments. Configuration validation rejects a Sentinel seed that is
also a standalone or Cluster data endpoint, and overlapping seeds for the same Sentinel service.
After discovery, the group also rejects:

- two Sentinel candidates that discover the same primary;
- two candidates for the same service whose discovered Sentinel peers overlap;
- a standalone or Cluster data endpoint that is a Sentinel candidate's discovered primary;
- a standalone or Cluster data endpoint that is a learned Sentinel peer. Sentinels answer `PING` but cannot serve application commands.

`ConnectAsync` throws `RespireConfigurationException` when these checks fail. Every later probe
repeats them, because discovery can change. A duplicate found later is marked unhealthy with
`LastErrorType` set to `RespireConfigurationException`, and a warning is logged.

- A healthy candidate keeps serving while a recovering duplicate stays unhealthy.
- With equal health, the lower-priority (or later) candidate is marked unhealthy.
- A learned Sentinel peer used as a data endpoint always fails the data candidate.
- Endpoints that differ but reach the same nodes (for example DNS aliases) cannot be detected.

## Maintenance notifications

Each candidate can opt into [maintenance notifications](../fundamentals/connections.md#maintenance-notifications)
with `MaintenanceNotifications = RespireMaintenanceNotificationMode.Auto` or `Enabled`. `Enabled`
requires RESP3 and server support; `Auto` tolerates servers that lack the negotiation command.

- `MOVING` replaces connections inside the candidate's client. `ActiveClient` stays the same and `EndpointSwitched` is not raised.
- Command timeouts use `MaintenanceRelaxedTimeout` during maintenance. Caller cancellation still applies.
- Standalone status and switch telemetry keep the configured endpoint while `ActiveClient` uses the handoff target.

For a candidate that has passed a health probe, the group postpones probes during a maintenance
window (`FAILING_OVER`, `MIGRATING`, `MOVING`). A failed probe in a window does not increment
`ConsecutiveFailures`, open the circuit or restart the grace period. Maintenance cannot establish
initial health or recover an unhealthy candidate.

- Cluster slot migrations (`SMIGRATING`, `SMIGRATED`) relax timeouts but do not postpone or suppress probes.
- `FAILED_OVER` and `MIGRATED` end their windows; overlapping windows must all finish.
- Missing completions expire after `MaintenanceWindowTimeout`. A `MOVING` window uses the smaller of its announced grace period and that timeout.
- Window state is limited to 256 connections per candidate. Overflow can delay probe resumption until its expiry.

:::warning
An outage during an announced maintenance window takes longer to detect. Normal probes resume once
windows end, so a real outage still switches deployments.
:::

## Metrics

The group records these instruments on the `Respire` meter:

| Instrument | Tags | Meaning |
| --- | --- | --- |
| `respire.failover.probes` | `server.address`, `server.port`, `respire.failover.probe.result` | Health probes by result (`success` or `failure`). |
| `respire.failover.probe.duration` | Same as above | Probe duration in seconds. |
| `respire.failover.endpoint.switches` | `respire.failover.switch.reason`, `respire.failover.endpoint.previous`, `respire.failover.endpoint.current` | Selected endpoint changes. The reason is a `RespireFailoverSwitchReasons` value; a missing endpoint is reported as `none`. |
| `respire.failover.monitor.errors` | `respire.failover.error.source`, `error.type` | Unexpected monitor failures (`monitor`) and exceptions thrown by `EndpointSwitched` handlers (`handler`). The monitor continues after either. |
| `redis.client.geofailover.failovers` | `db.client.geofailover.reason` (`automatic`), `db.client.geofailover.fail_from`, `db.client.geofailover.fail_to` | Switches between two different known endpoints. Recorded only when `RespireMetricGroups.Resiliency` is selected (it is in the default groups). |

With `RespireMetricGroups.Resiliency`, failed probes also report `redis.client.errors` with
`redis.client.errors.internal = true`. These cover rejected `PING`s, bad `CLUSTER INFO` replies,
unhealthy cluster state and deployment conflicts. Cancellation on shutdown reports nothing.
Commands sent through `ActiveClient` report errors normally.

## Logging

The group logs through the first candidate with `RespireOptions.LoggerFactory` set, under the
`Respire.FailoverGroup` category: switches at `Information`, monitor failures and handler
exceptions at `Warning`.

## Consistency

:::warning
Failover does not replicate data or fence writes. During detection or failback, one application
instance can write to one deployment while another writes to a different one. Design consistency,
replication and write ownership in your application.
:::
