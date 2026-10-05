---
sidebar_position: 4
---

# Health checks

Install `Respire.HealthChecks` to check an existing Respire client through ASP.NET Core's
`IHealthCheck` infrastructure. The package supports .NET 8 and .NET 10 and does not depend
on StackExchange.Redis.

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

For `RespireClient`, the check sends `PING` on an existing usable command connection. It never creates a probe
client, initializes an unused connection slot, discovers topology, or rents a dedicated
connection. A lazy client that has not connected reports unhealthy until application work
establishes a connection. For eager startup validation, register an already connected
`IRespireClient` created with `RespireClient.ConnectAsync`.

Clients implementing the optional core `IRespireHealthProbe` contract support both modes,
including third-party `IRespireClient` implementations. `RespireClient` and its prefixed/read
views implement this contract. The health package consumes public node observations and
does not access transport or routing implementation types.

Other `IRespireClient` implementations retain the default single-node fallback. The check
first requires `IsConnected`, then calls their `PingAsync` with the probe cancellation token.
Those implementations control connection creation. The check bounds its asynchronous wait,
even if a custom implementation ignores cancellation; it cannot forcibly stop that work or
interrupt an implementation that blocks synchronously before returning its task.

The default probes one primary. For Cluster this is a primary with an existing usable
connection in the current routing
snapshot; it does not prove that every slot owner is available. Set `ProbeAllNodes = true`
to probe every known primary and replica. Standalone and Sentinel checks include the
current primary and configured or discovered read replicas. A known node without an open
usable command connection reports a failure, even if that server might be reachable by
opening a new connection. Sentinel monitor sockets and retired nodes are excluded.
All-node mode requires `IRespireHealthProbe`.
Constructing a check with a client without that contract and `ProbeAllNodes = true` throws
`NotSupportedException`. With DI factories, this validation runs when the check is resolved.
Topology and connection observations are snapshots, not guarantees of subsequent operations.

Probe failures use the registration's `failureStatus`, defaulting to `Unhealthy`. A
successful PING at or above `DegradedLatency` reports `Degraded`; the default has no
latency threshold. `ProbeTimeout` bounds the whole probe round, including admission to
the `MaxConcurrentProbes` limit, which defaults to eight. Caller cancellation propagates. A probe never
disposes the shared client. Application command timeouts may impose a shorter bound.

Health data includes `connected`, a `nodes` array of `RespireNodeHealth` with endpoints,
connection state, latency and failure type, a `failedNodes` count, and `clientSideCache`
statistics when enabled. `nodes` and `failedNodes` are available after a probe round
produces node results. If an earlier step fails, such as capturing the routing snapshot,
the result retains only data collected before the failure; these fields can be absent.
Set `IncludeClientSideCache = false` to omit cache statistics. Cache counters do not
independently determine health.
Health data contains deployment addresses and diagnostic details. Protect health endpoints
whose custom response writer serializes `Data` against unauthenticated access.

For a keyed registration, select its shared client:

```csharp
builder.Services.AddHealthChecks().AddRespire(
    name: "session-redis",
    clientFactory: services => services.GetRequiredKeyedService<IRespireClient>("sessions"));
```

For an already registered `RespireFailoverGroup`, use
`services.AddHealthChecks().AddRespireFailoverGroup()`. Each invocation selects the current
`ActiveClient` and probes that client's existing connections. Health data additionally
includes `failoverConnected` and `failoverEndpoints`, with candidate health, consecutive
failures, circuit deadlines and last error types. No active deployment reports unhealthy;
an unhealthy candidate with a responsive active deployment reports degraded. Set
`DegradeOnFailover = false` to use only active-deployment probe results. The group's
background monitor remains responsible for selection and recovery.

The public `RespireHealthCheck` constructors also accept an existing client or group,
so integrations such as Aspire can reuse the same check and registered clients.

## Implementing the probe contract

Implement `IRespireHealthProbe` alongside your custom `IRespireClient`. Do not add members
to `IRespireClient` or expose connection objects. The core contract has no ASP.NET Core
dependency and can also be called directly:

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

An implementation must follow these requirements:

- Call `RespireHealthProbeOptions.Validate()` before probing. Concurrency must be positive;
  timeout must be greater than zero and no greater than 4,294,967,294 milliseconds.
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
  Snapshot acquisition failures throw; individual node failures preserve their exception.
- Return a new, caller-owned array of immutable `RespireHealthProbeResult` observations.
  Do not mutate the array after returning it or retain pooled data. A successful result has
  `IsConnected = true`, a non-negative latency, and no error. A failed result has an exception
  and no latency. `IsConnected` records availability before admission, so a connection that
  retires while queued can report `true` with an error. Latency excludes admission time.

For `RespireClient`, Cluster membership comes from one routing publication. Standalone and
Sentinel primary selection and replica membership are independently published observations,
each captured once. The resulting round has fixed membership and connection identities;
it does not promise an atomic global topology snapshot, stable roles, or reachability after
capture. A subsequent call captures current state again. Prefixes and read preferences do
not change health selection or cause PING to route to replicas in primary-only mode.

The health package passes its timeout and concurrency settings to the provider and bounds
its own asynchronous wait as well. The outer wait allows up to 250 milliseconds of completion
grace beyond `ProbeTimeout`, capped at the timer limit, so cooperative providers can return
per-node timeout diagnostics. Caller cancellation remains immediate. A null result array or
null node produces a descriptive `InvalidOperationException` in the health result.
A custom provider remains responsible for cancelling its
underlying work and releasing any temporary resources. Node exceptions are aggregated into
the health result; `RespireNodeHealth` remains the package's existing public diagnostic shape.

## Package compatibility

Use matching releases of `Respire` and `Respire.HealthChecks`, and honor the health package's
declared minimum core dependency. A new health package requires a core version that defines
`IRespireHealthProbe`; it cannot run against an older core binary. Existing custom clients
need not implement the new interface: single-node PING fallback and explicit unsupported
all-node behavior remain available.

The core retains the original internal health entry point and friend-assembly permission
for older `Respire.HealthChecks` binaries. New integrations must use the public contract.
Removal at the next major version is tracked in [#950](https://github.com/thomhurst/Respire/issues/950).
Future optional probe capabilities require a separate opt-in interface rather than adding
abstract members to `IRespireHealthProbe`. The contract does not transfer client ownership
or promise access to transport implementation types.
