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

The Sentinel fixture uses a 5-second down detection interval and a 10-second failover timeout
so a failed election can retry within bounded test deadlines instead of waiting six minutes.

## Docker networking and limits

All fixtures require a **local Docker engine** and publish every data and Sentinel port only
on `127.0.0.1`. Remote engines are unsupported, including for standalone fixtures. Standalone
fixtures use Docker-assigned random host ports. Cluster and Sentinel discovery advertises
loopback addresses, and each client port is the same inside and outside the container.
Cluster bus ports remain inside the container and are not published. Ports are chosen from the operating
system's ephemeral range rather than fixed service ports. A small race exists between releasing
the temporary port reservations and Docker binding them. Cluster and Sentinel fixtures retry
recognized Docker host-port collisions at most twice (three container attempts total). Each
failed owned container is fully removed before a fresh container uses new ports; ports from
earlier attempts are excluded. Other fixtures and existing services are never stopped.

Retry requires a Docker API HTTP 500 response with a known port-allocation or TCP
address-in-use bind message naming one of the selected `127.0.0.1` ports. The recognized
formats cover Moby's allocator/Linux bind errors and Docker Desktop's TCP bind errors on
Windows and macOS. Unknown formats, permission/reserved-port errors, image/authentication
errors, readiness failures, and standalone startup failures are returned without retry.
This conservative recognition cannot guarantee recovery for every Docker version or network
backend. The [Moby bind implementation](https://github.com/moby/moby/blob/v28.5.2/libnetwork/drivers/bridge/port_mapping_linux.go),
[Moby port allocator](https://github.com/moby/moby/blob/v28.5.2/libnetwork/portallocator/portallocator.go),
and [Docker Desktop bind report](https://github.com/docker/for-win/issues/13686)
document the error forms used by the regression tests.

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
from 100 ms to one second. One `StartupTimeout` covers image pull, every collision retry,
and readiness; it is never restarted for a replacement container. Caller cancellation is preserved;
an elapsed startup deadline reports the stage and latest readiness reply. Cleanup is awaited
even after the startup deadline expires.

When retries exhaust, the final Docker exception remains the thrown error. Its `Data`
includes `RespireFixture.StartupAttempt`, `RespireFixture.SelectedPorts`,
`RespireFixture.ContainerId`, and any `RespireFixture.PreviousPortCollisions`, in addition
to startup-stage diagnostics. Cleanup failure stops retries and reports all startup and
cleanup causes together in an `AggregateException`. Cancellation or deadline wrappers
retain the original startup error as their inner exception.

If the container started but initialization failed, the fixture collects the last 4 KiB
of each Redis, Valkey, or Sentinel daemon log before removing the container. The original
startup exception exposes the result in `Data["RespireFixture.DaemonLogs"]`; it is also
written to standard error for test runners to capture. Collection has its own two-second
deadline, independent of an expired startup deadline or caller cancellation. Combined
diagnostics are limited to 32,768 characters. Missing logs, failed collection, and collection
timeouts are reported without replacing the startup failure or preventing cleanup.

The [in-memory fake](in-memory-testing.md) runs deterministic tests without Docker.
The [shared sample](testing-sample.md) runs the same public-client scenarios against
the fake, Redis, and Valkey on both supported frameworks.
