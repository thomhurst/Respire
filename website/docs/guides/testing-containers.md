---
title: Container test fixtures
description: Run Redis and Valkey integration tests with disposable standalone, Cluster, and Sentinel fixtures.
---

# Container test fixtures

Install `Respire.Testing.Containers` in a test project. It uses Testcontainers and requires
a working Docker engine with Linux containers. The fixture has no dependency on a test
framework; use it with TUnit, xUnit, NUnit, or your own executable.

The package brings Testcontainers 4.15.0 as a transitive dependency and does not claim
Native AOT compatibility. Keep it in test projects that run on the normal .NET runtime.

```csharp
using Respire.Testing.Containers;

await using var fixture = await RespireContainerFixture.StartAsync();
await using var client = await RespireClient.ConnectAsync(fixture.CreateOptions());
await client.SetAsync("example", "value");
var value = await client.GetStringAsync("example");
if (value != "value") throw new InvalidOperationException("Round trip failed.");
```

Dispose clients before their fixture. The fixture removes only its own container and server
processes. Concurrent disposal calls join the same cleanup. Failed or cancelled startup also
awaits cleanup before returning the error. Each fixture gets an independent data directory;
it does not reuse containers or persistent volumes.

## Topologies and versions

```csharp
using Respire.Testing.Containers;

await using var fixture = await RespireContainerFixture.StartAsync(new()
{
    Server = RespireContainerServer.Valkey,
    Topology = RespireContainerTopology.Cluster,
    Image = "valkey/valkey:8.1-alpine",
    StartupTimeout = TimeSpan.FromMinutes(2)
});
await using var client = await RespireClient.ConnectAsync(
    fixture.CreateOptions() with { Protocol = RespProtocol.Resp3 });
await client.SetAsync("{customer}:name", "Ada");
```

| Topology | Processes | Client configuration |
| --- | --- | --- |
| `Standalone` | One server | One mapped endpoint |
| `Cluster` | Three primaries covering all 16,384 slots | All three seeds, `UseCluster = true` |
| `Sentinel` | One primary, one replica, three Sentinels with quorum two | Three Sentinel endpoints and service name `respire-test` |

The default family is Redis. Default images are `redis:7.2-alpine` and
`valkey/valkey:8.1-alpine`. Override `Image` with a compatible tag or digest for reproducibility;
the image must contain `/bin/sh`, `mkdir`, `tail`, and the selected family's server and CLI binaries.
Cluster uses `CLUSTER ADDSLOTSRANGE`, requiring Redis 7+ or Valkey. Images are not built or
installed by the fixture; Testcontainers pulls them when needed.

`CreateOptions()` returns a fresh options object and endpoint collection each time. Use a
`with` expression to select RESP2/RESP3, timeouts, a client name, or administrative access.
`DataEndpoints`, `SentinelEndpoints`, and `ContainerId` support diagnostics and direct node
connections. In Sentinel mode, the first data endpoint identifies the **initial** primary;
it is not updated after failover. Native Sentinel discovery selects the current primary at
connection time. This fixture does not add automatic Sentinel failover to the client.

## Docker networking and limits

Standalone fixtures use Docker-assigned random host ports and Testcontainers' reported host,
so remote engines work when their published ports are reachable. Cluster and Sentinel
fixtures require a **local Docker engine**: discovery advertises loopback addresses, and each
client port is the same inside and outside the container. Ports are chosen from the operating
system's ephemeral range rather than fixed service ports. A small race exists between releasing
the temporary port reservations and Docker binding them; a collision fails startup and cleans
up instead of connecting to another fixture. Other fixtures and existing services are never stopped.

Local-host validation uses the host reported by Testcontainers after startup, so an
unsupported remote engine can pull and start the container before rejection and owned
cleanup. This can take as long as image pull/startup within `StartupTimeout`; checking
`DOCKER_HOST` alone would not cover all Testcontainers endpoint configuration sources.

This preserves the endpoint identity needed by [Sentinel discovery behind NAT](https://redis.io/docs/latest/operate/oss_and_stack/management/sentinel/#sentinel-docker-nat-and-possible-issues).

All topology processes share one container. Use separate deployments for machine-level failure,
network-partition, durability, or realistic high-availability testing. The Cluster fixture has
no replicas. These are unauthenticated development servers; do not put sensitive data in them.

Startup waits for PING, complete Cluster membership/slot coverage, or replication plus Sentinel
quorum and discovery of the healthy replica by every Sentinel. Readiness polling backs off
from 100 ms to one second. `StartupTimeout` includes image pull and readiness. Caller cancellation is preserved;
an elapsed startup deadline reports the stage and latest readiness reply. Cleanup is awaited
even after the startup deadline expires.

An in-memory fake and the shared fake/container sample remain tracked by
[#531](https://github.com/thomhurst/Respire/issues/531) and
[#532](https://github.com/thomhurst/Respire/issues/532).
