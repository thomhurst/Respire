---
title: Standalone failover groups
---

`RespireFailoverGroup` monitors independent standalone Redis deployments and selects a healthy
endpoint for new operations. Lower candidate priorities win. The group uses bounded `PING`
probes, opens a circuit after consecutive failures, and waits for a recovered higher-priority
endpoint to remain healthy before failback.

Health means the endpoint answers `PING` within `ProbeTimeout`. The group does not inspect
application commands or infer that a primary role is writable. Redis errors such as `-LOADING`,
`-READONLY`, or `OOM` do not affect endpoint health while `PING` succeeds. Detection can take
approximately `FailureThreshold × (ProbeInterval + ProbeTimeout)` after an endpoint becomes
unreachable. Choose these settings with that detection delay in mind.

Candidates must use an unlimited reconnect policy (`MaxAttempts = null`, the default). A finite
budget can permanently disable an owned client after a long outage, preventing later probes from
recovering it. `ProbeInterval` must also fit the runtime timer limit of about 49.7 days.

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

Subscribe to `EndpointSwitched` for application-level resubscription or diagnostics. A client
reference obtained before a switch stays attached to its original deployment. The group keeps
all candidate clients alive until disposal, so in-flight calls are not interrupted or replayed.
Pub/sub subscriptions also stay on their original deployment and must be recreated after a
switch.

Failover does not replicate data between deployments or fence writes. An operation can reach one
deployment while another application instance writes to a different deployment during failure
detection or failback. Design consistency, replication, and write ownership at the application
layer. Client-side caching is rejected because cache entries cannot be shared safely across
independent deployments.

Sentinel and Cluster candidates are not accepted yet. They are tracked as separate follow-up
work under [multi-endpoint failover](https://github.com/thomhurst/Respire/issues/426).
