---
title: Health checks
description: Check Redis through an existing Respire client with ASP.NET Core health checks.
sidebar_position: 4
---

# Health checks

`Respire.HealthChecks` adds an ASP.NET Core health check that pings Redis through your existing
Respire client. It never creates a client of its own. It targets .NET 8 and .NET 10 and does
not depend on StackExchange.Redis.

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
`name`, `failureStatus`, `tags`, and a `clientFactory`.

## What the check does

For `RespireClient`, the check sends `PING` on an existing usable command connection. It never
creates a client, opens a connection, discovers topology, or rents a dedicated connection.

- A lazy client that has not connected yet reports unhealthy until application work opens a
  connection. To validate Redis at startup, register a client created with
  `RespireClient.ConnectAsync`.
- By default it probes one primary. For Cluster, that is one primary with a usable connection;
  it does not prove every slot owner is available.
- `ProbeAllNodes = true` probes every known primary and replica (for Standalone and Sentinel:
  the current primary and configured or discovered replicas). A known node without an open
  usable connection counts as failed.
- Sentinel monitor sockets and retired nodes are excluded.
- Results are snapshots, not guarantees about later operations.

## Options

| `RespireHealthCheckOptions` | Default | Meaning |
| --- | --- | --- |
| `ProbeAllNodes` | `false` | Probe every known primary and replica instead of one primary. |
| `MaxConcurrentProbes` | `8` | Maximum simultaneous node PINGs. Must be positive. |
| `ProbeTimeout` | 2 seconds | Bound for the whole round, including waits for a concurrency slot. Must be greater than zero. |
| `DegradedLatency` | `null` | Report `Degraded` when any successful PING takes at least this long. `null` disables it. |
| `IncludeClientSideCache` | `true` | Add client-side cache statistics to the health data when caching is enabled. |
| `DegradeOnFailover` | `true` | For failover groups, report `Degraded` when a candidate is unhealthy. |

- Invalid values throw `ArgumentOutOfRangeException` at registration.
- Probe failures use the registration's `failureStatus` (default `Unhealthy`).
- Caller cancellation propagates.
- The probe never disposes the shared client.
- Application command timeouts can impose a shorter bound than `ProbeTimeout`.

## Health data

| `Data` key | Value |
| --- | --- |
| `connected` | The client's `IsConnected` |
| `nodes` | Array of `RespireNodeHealth(Endpoint, IsConnected, Latency, ErrorType)` |
| `failedNodes` | Number of failed node probes |
| `clientSideCache` | Cache statistics, when caching is enabled and `IncludeClientSideCache` is `true`; does not affect status |

`nodes` and `failedNodes` appear only after a probe round produces node results. If an earlier
step fails, `Data` holds only what was collected before the failure.

:::warning
Health data contains deployment addresses and diagnostic details. If a custom response writer
serializes `Data`, protect that endpoint against unauthenticated access.
:::

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
`respire-failover`, optional `groupFactory`). Each run probes the existing connections of the
group's current `ActiveClient`.

- Health data adds `failoverConnected` and `failoverEndpoints` (candidate health, consecutive
  failures, circuit deadlines, last error types).
- No active deployment: the check reports the failure status.
- Active deployment healthy but another candidate unhealthy: `Degraded`. Set
  `DegradeOnFailover = false` to use only the active deployment's result.
- The group's background monitor still handles selection and recovery.

The public `RespireHealthCheck` constructors accept an existing client or group, so
integrations such as Aspire reuse the same check.

## Custom clients

The check supports any `IRespireClient`:

- Clients that implement `IRespireHealthProbe` support primary-only and all-node probes.
  `RespireClient` and its prefixed and read views implement it.
- Other clients get a single-node fallback: the check requires `IsConnected`, then calls
  `PingAsync` with the probe token. The check bounds its own wait, but cannot stop work that
  ignores cancellation or blocks synchronously before returning its task.
- `ProbeAllNodes = true` requires `IRespireHealthProbe`. Otherwise the `RespireHealthCheck`
  constructor throws `NotSupportedException` (with DI, when the check is resolved).

### Implementing the probe contract

Implement `IRespireHealthProbe` (namespace `Respire`) on your custom `IRespireClient`. You can
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

`RespireHealthProbeOptions` defaults to one primary, eight concurrent probes, and a two-second
timeout. The contract has no ASP.NET Core dependency and exposes no connection objects. An
implementation must:

1. Call `RespireHealthProbeOptions.Validate()` first. Concurrency must be positive; the
   timeout must be greater than zero and at most 4,294,967,294 milliseconds.
2. Capture node membership and existing connection identities before awaiting probes.
   Primary-only mode selects one primary, preferring one with a usable connection. All-node
   mode includes every known primary and replica, even unavailable ones. Never probe Sentinel
   monitor sockets.
3. Reuse only captured command connections. Do not create a client, initialize a lazy slot,
   discover topology, reconnect, rent a connection, or substitute a replacement after a
   redirect, retirement, or routing change. Report an unavailable connection as a failed
   node. Never dispose the caller's client or connections.
4. Return promptly without blocking. Apply `MaxConcurrentProbes` to simultaneous PINGs and
   include admission waits in `Timeout`. Throw `OperationCanceledException` on caller
   cancellation. A round timeout may throw or return failed results. Snapshot failures throw;
   node failures keep their exception.
5. Return a new, caller-owned array of immutable `RespireHealthProbeResult` values, in capture
   order. Do not mutate it afterwards or retain pooled data.
   - Success: `IsConnected = true`, non-negative latency, no error.
   - Failure: an exception and no latency.
   - `IsConnected` reflects availability before admission, so a connection that retires while
     queued can report `true` with an error. Latency excludes admission time.

For `RespireClient`, each round has fixed membership and connection identities, but no atomic
global topology snapshot or stable roles. Key prefixes and read preferences do not affect
selection, and primary-only mode never sends PING to replicas.

A configured [command hook](../guides/command-hooks.md#imports-bulk-writes-and-health-probes)
admits each explicit `RespireClient.ProbeHealthAsync` round as `PROBE HEALTH` before
capturing connections or sending PING. Returning the result array completes the hook with
`Succeeded`, including when nodes failed; inspect each node's `Error` for health status.
Caller cancellation completes with `Canceled`. Internal sampled health checks and the
round's per-node PINGs do not create additional command-hook scopes.

How the health check uses a provider:

- It passes its timeout and concurrency settings, and waits up to 250 milliseconds beyond
  `ProbeTimeout` so cooperative providers can return per-node timeout diagnostics. Caller
  cancellation is immediate.
- A null result array or null node result becomes an `InvalidOperationException` in the
  health result. Node exceptions are aggregated into an `AggregateException`.
- The provider must cancel its own work and release temporary resources.

## Package compatibility

Update `Respire` and `Respire.HealthChecks` together, to matching releases. The health package
needs a core version that defines `IRespireHealthProbe`.
