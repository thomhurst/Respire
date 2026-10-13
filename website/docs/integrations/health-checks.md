---
title: Health checks
description: Check Redis through an existing Respire client with ASP.NET Core health checks.
sidebar_position: 4
---

# Health checks

`Respire.HealthChecks` checks an existing Respire client through ASP.NET Core's
`IHealthCheck` infrastructure. It targets .NET 8 and .NET 10 and does not depend on
StackExchange.Redis. It never registers or creates a Redis client of its own.

```bash
dotnet add package Respire.HealthChecks
```

```csharp
using Microsoft.AspNetCore.Builder;
using Respire.DependencyInjection;
using Respire.HealthChecks;

var builder = WebApplication.CreateBuilder();
builder.Services.AddRespire(builder.Configuration.GetConnectionString("redis")!);
builder.Services.AddHealthChecks().AddRespire(new RespireHealthCheckOptions
{
    DegradedLatency = TimeSpan.FromMilliseconds(100),
    ProbeTimeout = TimeSpan.FromSeconds(2),
}, tags: ["ready"]);

var app = builder.Build();
app.MapHealthChecks("/health/ready");
```

`AddRespire` checks the registered `IRespireClient` under the name `respire`. It also accepts
`name`, `failureStatus`, `tags` and a `clientFactory`.

## What the check does

For `RespireClient`, the check sends `PING` on an existing usable command connection. It never
creates a probe client, initializes an unused connection slot, discovers topology, or rents a
dedicated connection. A lazy client that has not connected yet reports unhealthy until
application work opens a connection. To validate Redis at startup, register a client that is
already connected, created with `RespireClient.ConnectAsync`.

By default the check probes one primary. For Cluster, this is a primary with an existing
usable connection in the current routing snapshot; it does not prove that every slot owner is
available. Set `ProbeAllNodes = true` to probe every known primary and replica. For
Standalone and Sentinel, all-node mode includes the current primary and configured or
discovered read replicas. A known node without an open usable command connection counts as a
failure, even if a new connection might succeed. Sentinel monitor sockets and retired nodes
are excluded. Topology and connection observations are snapshots, not guarantees about later
operations.

## Options

| `RespireHealthCheckOptions` | Default | Meaning |
| --- | --- | --- |
| `ProbeAllNodes` | `false` | Probe every known primary and replica instead of one primary. |
| `MaxConcurrentProbes` | `8` | Maximum simultaneous node PINGs. Must be positive. |
| `ProbeTimeout` | 2 seconds | Bound for the whole probe round, including waits for a concurrency slot. Must be greater than zero. |
| `DegradedLatency` | `null` | Report `Degraded` when any successful PING takes at least this long. `null` disables the threshold. |
| `IncludeClientSideCache` | `true` | Add client-side cache statistics to the health data when caching is enabled. |
| `DegradeOnFailover` | `true` | For failover groups, report `Degraded` when a candidate is unhealthy. |

Invalid values throw `ArgumentOutOfRangeException` at registration. Probe failures use the
registration's `failureStatus`, which defaults to `Unhealthy`. Caller cancellation propagates.
A probe never disposes the shared client. Application command timeouts may impose a shorter
bound than `ProbeTimeout`.

## Health data

The result's `Data` contains:

- `connected`: the client's `IsConnected` value.
- `nodes`: an array of `RespireNodeHealth(Endpoint, IsConnected, Latency, ErrorType)`.
- `failedNodes`: the number of failed node probes.
- `clientSideCache`: cache statistics, when client-side caching is enabled and
  `IncludeClientSideCache` is `true`. Cache counters do not affect the health status.

`nodes` and `failedNodes` appear only after a probe round produces node results. If an earlier
step fails, such as capturing the routing snapshot, the data holds only what was collected
before the failure.

Health data contains deployment addresses and diagnostic details. If a custom response writer
serializes `Data`, protect that endpoint against unauthenticated access.

## Keyed clients

For a keyed registration, select its client with `clientFactory`:

```csharp
builder.Services.AddHealthChecks().AddRespire(
    name: "session-redis",
    clientFactory: services => services.GetRequiredKeyedService<IRespireClient>("sessions"));
```

## Failover groups

For a registered `RespireFailoverGroup`, call
`builder.Services.AddHealthChecks().AddRespireFailoverGroup()` (default name
`respire-failover`, with an optional `groupFactory`). Each run selects the group's current
`ActiveClient` and probes that client's existing connections. Health data also includes
`failoverConnected` and `failoverEndpoints`, with candidate health, consecutive failures,
circuit deadlines and last error types.

If no deployment is active, the check reports the failure status. If the active deployment
responds but another candidate is unhealthy, the check reports `Degraded`. Set
`DegradeOnFailover = false` to use only the active deployment's probe results. The group's
background monitor remains responsible for selection and recovery.

The public `RespireHealthCheck` constructors accept an existing client or group, so
integrations such as Aspire can reuse the same check with registered clients.

## Custom clients

The check supports any `IRespireClient`:

- Clients that implement `IRespireHealthProbe` support both primary-only and all-node probes.
  `RespireClient` and its prefixed and read views implement it.
- Other implementations get a single-node fallback. The check requires `IsConnected`, then
  calls `PingAsync` with the probe cancellation token. The implementation controls connection
  creation. The check bounds its own wait even if the implementation ignores cancellation,
  but it cannot stop that work or interrupt an implementation that blocks synchronously
  before returning its task.
- `ProbeAllNodes = true` requires `IRespireHealthProbe`. Without it, the `RespireHealthCheck`
  constructor throws `NotSupportedException`. With DI registration, this happens when the check
  is resolved.

### Implementing the probe contract

Implement `IRespireHealthProbe` (namespace `Respire`) alongside your custom `IRespireClient`.
The contract has no ASP.NET Core dependency and does not expose connection objects. You can
also call it directly:

```csharp
async Task<RespireHealthProbeResult[]> ProbeExistingAsync(
    IRespireClient client, CancellationToken cancellationToken)
{
    if (client is not IRespireHealthProbe probe)
        throw new NotSupportedException("This client does not expose existing-connection probes.");

    return await probe.ProbeHealthAsync(new RespireHealthProbeOptions
    {
        ProbeAllNodes = true,
        MaxConcurrentProbes = 4,
        Timeout = TimeSpan.FromSeconds(2),
    }, cancellationToken);
}
```

`RespireHealthProbeOptions` defaults to one primary, eight concurrent probes and a two-second
timeout. An implementation must follow these rules:

- Call `RespireHealthProbeOptions.Validate()` before probing. Concurrency must be positive;
  the timeout must be greater than zero and no greater than 4,294,967,294 milliseconds.
- Capture data-node membership and existing connection identities before awaiting probes.
  Primary-only mode selects one primary, preferring an existing usable connection. All-node
  mode includes every known primary and replica, including unavailable nodes. Return results
  in capture order. Never probe Sentinel monitor sockets.
- Reuse only captured command connections. Do not create a client, initialize a lazy slot,
  discover topology, reconnect, rent a connection, or substitute a replacement after a
  redirect, retirement, or routing change. Report an unavailable captured connection as a
  failed node. Never dispose the caller's client or connections.
- Return promptly without synchronous blocking. Apply `MaxConcurrentProbes` to simultaneous
  PINGs, and include admission waits in `Timeout`. Honor caller cancellation by throwing
  `OperationCanceledException`. A round timeout may throw or return failed node results.
  Snapshot acquisition failures throw; individual node failures keep their exception.
- Return a new, caller-owned array of immutable `RespireHealthProbeResult` values. Do not
  mutate the array after returning it or retain pooled data. A successful result has
  `IsConnected = true`, a non-negative latency, and no error. A failed result has an exception
  and no latency. `IsConnected` records availability before admission, so a connection that
  retires while queued can report `true` with an error. Latency excludes admission time.

For `RespireClient`, Cluster membership comes from one routing publication. Standalone and
Sentinel primary selection and replica membership are separate observations, each captured
once. A round has fixed membership and connection identities; it does not promise an atomic
global topology snapshot, stable roles, or reachability after capture. The next call captures
current state again. Key prefixes and read preferences do not change health selection, and
primary-only mode never routes PING to replicas.

The health check passes its timeout and concurrency settings to the provider and also bounds
its own wait. That outer wait allows up to 250 milliseconds of extra completion time beyond
`ProbeTimeout`, so cooperative providers can return per-node timeout diagnostics. Caller
cancellation remains immediate. A null result array or null node result produces an
`InvalidOperationException` in the health result. A custom provider remains responsible for
cancelling its underlying work and releasing temporary resources. Node exceptions are
aggregated into the health result's `AggregateException`.

## Package compatibility

Use matching releases of `Respire` and `Respire.HealthChecks`, and update both package
references together. The health package uses only the public `IRespireHealthProbe` contract,
so it needs a core version that defines that interface.
