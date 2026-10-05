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

Other `IRespireClient` implementations are supported in default single-node mode. The check
first requires `IsConnected`, then calls their `PingAsync` with the probe cancellation token.
The custom implementation controls connection creation and cancellation handling, so the
existing-connection and timeout guarantees depend on that implementation.

The default probes one primary. For Cluster this is a primary with an existing usable
connection in the current routing
snapshot; it does not prove that every slot owner is available. Set `ProbeAllNodes = true`
to probe every known primary and replica. Standalone and Sentinel checks include the
current primary and configured or discovered read replicas. A known node without an open
usable command connection reports a failure, even if that server might be reachable by
opening a new connection. Sentinel monitor sockets and retired nodes are excluded.
All-node mode requires a concrete `RespireClient`, including its prefixed/read views.
Constructing a check with a custom client and `ProbeAllNodes = true` throws
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
