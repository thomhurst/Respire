---
title: Failover groups
---

<<`RespireFailoverGroup` monitors independent standalone Redis, Sentinel, or Redis Cluster deployments and selects a healthy`r`ndeployment for new operations. Lower candidate priorities win. The group uses bounded health`r`nprobes, opens a circuit after consecutive failures, and waits for a recovered higher-priority
endpoint to remain healthy before failback.

Health means a standalone endpoint answers `PING` within `ProbeTimeout`. Cluster candidates use the
`CLUSTER INFO` probe described below. The group does not inspect
application commands or infer that a primary role is writable. Redis errors such as `-LOADING`,
`-READONLY`, or `OOM` do not affect endpoint health while `PING` succeeds. Detection can take
approximately `FailureThreshold × (ProbeInterval + ProbeTimeout)` after an endpoint becomes
unreachable. Choose these settings with that detection delay in mind. Probes use the candidate
client's own connection, so a heavily loaded endpoint can miss probes and be treated as unhealthy.

`ConnectAsync` probes each candidate once. A candidate that fails that probe starts unhealthy and
is reconsidered on the next background probe round. Any failed probe restarts the failback grace
period for that endpoint, even when the failure does not reach `FailureThreshold`, so an endpoint
that alternates between failures and successes does not become active through failback. When
several higher-priority endpoints outrank the active one, the group fails back to the
highest-priority endpoint that has completed its grace period, so an unstable top-priority
endpoint does not block failback to a stable intermediate one.

Candidates must use an unlimited reconnect policy (`MaxAttempts = null`, the default). A finite
budget can permanently disable an owned client after a long outage, preventing later probes from
recovering it. `ProbeInterval` and `ProbeTimeout` must also fit the runtime timer limit of about
49.7 days.

```csharp
await using var group = await RespireFailoverGroup.ConnectAsync(
[
    new RespireFailoverCandidate(new RespireOptions
    {
        Endpoints = ["redis-primary:6379"],
        Protocol = RespProtocol.Auto,
    }, Priority: 0),
    new RespireFailoverCandidate(new RespireOptions
    {
        Endpoints = ["redis-secondary:6379"],
        Protocol = RespProtocol.Auto,
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

Subscribe to `EndpointSwitched` for application-level resubscription or diagnostics. The
`Reason` value is one of the `RespireFailoverSwitchReasons` constants. The initial selection
happens inside `ConnectAsync`, before a handler can be attached, so the event never reports
`FirstHealthy`; that reason appears only in the switch metric and logs. Read `ActiveClient` after
connecting to see the initial endpoint. `RecoveredFromNoHealthyEndpoint` marks a later recovery
after every endpoint was unhealthy. Handlers run synchronously on the health monitor, so keep them short. A slow
handler delays the next probe round, and a handler must never wait for `DisposeAsync`, either
synchronously or with `await`, because disposal waits for the monitor that runs the handler. A client
reference obtained before a switch stays attached to its original deployment. The group keeps
all candidate clients alive until disposal, so in-flight calls are not interrupted or replayed.
Pub/sub subscriptions also stay on their original deployment and must be recreated after a
switch.

For a Cluster deployment, set `UseCluster = true` and provide one or more seed endpoints. Each
candidate owns a separate `RespireClient`, so slot maps and `MOVED`/`ASK` recovery stay within the
selected Cluster. Status, switch events, logs, and metrics identify a Cluster candidate by its first
configured seed endpoint. That value stays the same when the Cluster topology changes, even when the
client routes commands through another node.

A Cluster candidate is healthy when `CLUSTER INFO` reports `cluster_state:ok` within
`ProbeTimeout`, instead of answering `PING`. Redis reports `cluster_state:fail` when a hash slot has
no reachable primary, so a partial Cluster outage triggers failover even while some nodes still
respond. The probe runs on one node, so it does not detect a primary that only this client cannot
reach. The candidate's user needs permission to run `CLUSTER INFO`.

Each Cluster candidate must be a separate Cluster. Duplicate detection compares only the configured
seed endpoints. Two candidates whose seeds differ but whose nodes belong to the same Cluster pass
validation and provide no deployment redundancy.`r`n`r`nFor a Sentinel deployment, set `SentinelPrimaryName` to that deployment's service name and
provide one or more Sentinel endpoints. Configure data and Sentinel credentials and TLS settings
on each candidate's `RespireOptions`. The client validates the discovered primary with `ROLE`
before routing `PING` or application commands. Endpoint status and switch events report the
validated current primary; when discovery has not produced a primary, failed-probe telemetry uses
the first configured Sentinel endpoint.

```csharp
new RespireFailoverCandidate(new RespireOptions
{
    UseCluster = true,
    Endpoints = ["cluster-a-seed-1:6379", "cluster-a-seed-2:6379"],
}, Priority: 0);
    Endpoints = ["sentinel-a:26379", "sentinel-b:26379"],
    SentinelPrimaryName = "orders-primary",
    Username = "app",
    Password = "<data-password>",
    SentinelUsername = "sentinel-app",
    SentinelPassword = "<sentinel-password>",
    UseTls = true,
}, Priority: 0);
```

## Metrics

The group records these instruments on the `Respire` meter:

| Instrument | Tags | Meaning |
| --- | --- | --- |
| `respire.failover.probes` | `server.address`, `server.port`, `respire.failover.probe.result` | Health probes by result (`success` or `failure`). |
| `respire.failover.probe.duration` | Same as above | Probe duration in seconds. |
| `respire.failover.endpoint.switches` | `respire.failover.switch.reason`, `respire.failover.endpoint.previous`, `respire.failover.endpoint.current` | Selected endpoint changes. The reason is a `RespireFailoverSwitchReasons` value; a missing endpoint is reported as `none`. |
| `respire.failover.monitor.errors` | `respire.failover.error.source`, `error.type` | Unexpected monitor failures (`monitor`) and exceptions thrown by `EndpointSwitched` handlers (`handler`). The monitor continues after either. |

## Logging

The group logs through the first candidate whose `RespireOptions.LoggerFactory` is set, under the
`Respire.FailoverGroup` category. Endpoint switches are logged at `Information`; monitor-round
failures and `EndpointSwitched` handler exceptions are logged at `Warning` with the exception.

## Consistency

Failover does not replicate data between deployments or fence writes. An operation can reach one
deployment while another application instance writes to a different deployment during failure
detection or failback. Design consistency, replication, and write ownership at the application
layer. Client-side caching is rejected because cache entries cannot be shared safely across
independent deployments.

