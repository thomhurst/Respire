---
title: Failover groups
---

:::note Endpoint circuit breaker foundation

[Circuit breakers](circuit-breakers.md) support standalone and Cluster data-node
command admission through `RespireOptions.CircuitBreaker`. Sentinel admission and
remaining resilience work stay tracked by [#863](https://github.com/thomhurst/Respire/issues/863).
Failover-group configuration below remains the health-checked switching API across deployments.

The endpoint foundation retains the most recent `MaximumSampleCount` completed
outcomes whose age is less than `SamplingWindow`: 1024 outcomes and 30 seconds by
default. Both the failure-rate threshold and minimum failure count must be reached
before opening. Recovery waits for the monotonic `OpenDuration` and admits at most
`HalfOpenProbeCount` concurrent probes. All required probes must succeed; any
failed probe reopens the circuit. Canceled or ignored probes release capacity
without counting as successes or failures.

Every admitted operation must complete its internal permit, including cancellation,
exceptions, and operations that never dispatch. Permits have no lease or automatic
expiry: abandoning a half-open permit retains its slot indefinitely and can prevent
recovery. The dispatch integration in [#1255](https://github.com/thomhurst/Respire/issues/1255)
must guard every admission with `finally` or a `using`-style completion guard, and
complete canceled or undispatched operations with the ignored outcome. Operation
timeouts and cancellation belong to that integration; `OpenDuration` only controls
the delay before recovery starts, not the lifetime of an admitted probe.

The rejection contract identifies the endpoint and remaining `RetryAfter` delay.
`RetryAfter` is null when recovery probes fill the slots and their completion
determines the next admission. This delay is informational; a retry must reacquire
endpoint admission. Remaining topology, telemetry, and safe retry composition work
stays tracked by the native children of #863.

:::

`RespireFailoverGroup` monitors independent standalone Redis, Sentinel, or Redis Cluster deployments and selects a healthy
deployment for new operations. Lower candidate priorities win. The group uses bounded health
probes, opens a circuit after consecutive failures, and waits for a recovered higher-priority
endpoint to remain healthy before failback.

Failover health probes use the shared endpoint circuit core for the open delay and
a single recovery admission per candidate. `RespireFailoverGroupOptions` still
controls consecutive failures, probe timeout, open duration, and failback grace;
`RespireCircuitBreakerOptions` does not configure failover probes. Consecutive
failure thresholds have no rolling-history count limit. Application commands are
not gated or replayed by this probe circuit.

Each admitted probe completes its permit with a `finally` fallback. Failed probes
complete before metrics or logging callbacks, so observer delays do not extend the
circuit's open period. Parent cancellation is
ignored for health and releases recovery capacity, including cancellation before
dispatch. A failed or timed-out recovery probe reopens the circuit for the
configured duration. Ordinary probe timeouts count toward `FailureThreshold`.
Open delays use monotonic time; the public UTC deadline is a
status snapshot and saturates at `DateTimeOffset.MaxValue` for very long durations.

Standalone health probes use `PING`. Cluster probes use `CLUSTER INFO`. Sentinel probes check
the discovered primary with `ROLE` and then send `PING`. The group does not
inspect application commands or infer that a primary role is writable. Redis errors such as `-LOADING`,
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
validation and provide no deployment redundancy.

For a Sentinel deployment, set `SentinelPrimaryName` to that deployment's service name and
provide one or more Sentinel endpoints. Configure data and Sentinel credentials and TLS settings
on each candidate's `RespireOptions`. Sentinel candidates also need the unlimited reconnect policy
described above. The client validates the discovered primary with `ROLE`
before routing `PING` or application commands. Each probe also sends `ROLE` to the current primary,
because a demoted node still answers `PING`. The probe then sends `PING` as well, so a node that
reports the primary role but rejects `PING` (for example through ACL rules) is unhealthy.
When that node has been demoted, the probe rediscovers the primary through Sentinel and stays
healthy if a validated replacement answers `PING` within `ProbeTimeout`; it fails only when
rediscovery or the replacement fails. Endpoint status reports the validated current primary, or
null while discovery has no primary. A switch event reports the previous candidate's primary as it
was when that candidate was selected. When discovery has not produced a primary, probe telemetry
uses the first configured Sentinel endpoint.

Candidates must be separate deployments. Configuration validation rejects a Sentinel seed that is
also a standalone or Cluster data endpoint, and overlapping seeds for the same Sentinel service.
After discovery, the group also rejects:

- two Sentinel candidates that discover the same primary;
- two candidates for the same service whose discovered Sentinel peers overlap;
- a standalone or Cluster data endpoint that is a Sentinel candidate's discovered primary;
- a standalone or Cluster data endpoint that is a learned Sentinel peer. Sentinels answer `PING`
  but cannot serve application commands.

`ConnectAsync` throws `RespireConfigurationException` when these checks fail after the initial
probes. Discovery can change later, for example when a candidate that failed its first probe
recovers, or when Sentinel learns a new peer or fails over. Every probe repeats the checks, and a
duplicate candidate is marked unhealthy at once with `LastErrorType` set to
`RespireConfigurationException`. A warning is logged as well. A healthy candidate keeps serving
while a recovering duplicate stays unhealthy. When both have the same health, the candidate with
the lower priority, or the later one in input order, is marked unhealthy. A learned Sentinel peer
used as a data endpoint always fails the data candidate. As with Cluster seeds, two Sentinel
deployments that reach the same data nodes through endpoints that differ (for example DNS aliases)
cannot be detected.

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

## Metrics

The group records these instruments on the `Respire` meter:

| Instrument | Tags | Meaning |
| --- | --- | --- |
| `respire.failover.probes` | `server.address`, `server.port`, `respire.failover.probe.result` | Health probes by result (`success` or `failure`). |
| `respire.failover.probe.duration` | Same as above | Probe duration in seconds. |
| `respire.failover.endpoint.switches` | `respire.failover.switch.reason`, `respire.failover.endpoint.previous`, `respire.failover.endpoint.current` | Selected endpoint changes. The reason is a `RespireFailoverSwitchReasons` value; a missing endpoint is reported as `none`. |
| `respire.failover.monitor.errors` | `respire.failover.error.source`, `error.type` | Unexpected monitor failures (`monitor`) and exceptions thrown by `EndpointSwitched` handlers (`handler`). The monitor continues after either. |

When Resiliency metrics are enabled, failed health probes also report
`redis.client.errors` with `redis.client.errors.internal = true`. This includes rejected
PINGs, failed or malformed CLUSTER INFO replies, an unhealthy cluster state and deployment
conflicts. Probe failures update endpoint health; they are not final application command
failures. Expected cancellation when startup or monitoring stops does not report a probe
error. Commands sent through `ActiveClient` retain their normal final error boundary.

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
