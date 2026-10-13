---
title: Container test fixtures
description: Run Redis and Valkey integration tests with disposable standalone, Cluster, and Sentinel fixtures.
---

# Container test fixtures

Install `Respire.Testing.Containers` in a test project. It uses Testcontainers and requires
a working Docker engine with Linux containers. The fixture has no dependency on a test
framework; use it with TUnit, xUnit, NUnit, or your own executable.

The package brings Testcontainers as a transitive dependency and is not Native AOT
compatible. Keep it in test projects that run on the normal .NET runtime.

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

Sentinel fixtures also expose `StopDataNodeAsync(index)`, `StartDataNodeAsync(index)`, and
`WaitForDataNodeReplicaAsync(index)` to stop a data node, restart it, and wait until it
reports itself as a replica. Index `0` is the initial primary and `1` the initial replica.
On Standalone and Cluster fixtures they throw `InvalidOperationException`.

The Sentinel fixture uses a 5-second down detection interval and a 10-second failover timeout
so a failed election can retry within bounded test deadlines instead of waiting six minutes.
Failover tests can shorten both with `SentinelDownAfter` and `SentinelFailoverTimeout`. Sentinels
retry an aborted election after twice the failover timeout. Very short values trade speed for
false failovers on an overloaded host, so keep the defaults for tests that do not fail over.

```csharp
using Respire.Testing.Containers;

await using var fixture = await RespireContainerFixture.StartAsync(new()
{
    Topology = RespireContainerTopology.Sentinel,
    SentinelDownAfter = TimeSpan.FromSeconds(3),
    SentinelFailoverTimeout = TimeSpan.FromSeconds(3)
});
```

## Docker networking and limits

All fixtures require a **local Docker engine** and publish every data and Sentinel port only
on `127.0.0.1`. Remote engines are unsupported, including for standalone fixtures. Standalone
fixtures use Docker-assigned random host ports. Cluster and Sentinel discovery advertises
loopback addresses, and each client port is the same inside and outside the container. This
preserves the endpoint identity needed by
[Sentinel discovery behind NAT](https://redis.io/docs/latest/operate/oss_and_stack/management/sentinel/#sentinel-docker-nat-and-possible-issues).
Cluster bus ports remain inside the container and are not published. Ports are chosen from the
operating system's ephemeral range rather than fixed service ports. Other fixtures and existing
services are never stopped.

A small race exists between choosing a free port and Docker binding it. All topologies retry a
recognized Docker host-port collision at most twice (three container attempts in total). Each
failed container is fully removed before a fresh one starts. Cluster and Sentinel exclude
previously selected ports; standalone asks Docker for another random host port.

Only bind failures reported by the Docker API with a known "port already allocated" or
"address already in use" message for a selected loopback port are retried. The recognized
formats cover Moby on Linux and Docker Desktop on Windows and macOS
([Moby bind implementation](https://github.com/moby/moby/blob/v28.5.2/libnetwork/drivers/bridge/port_mapping_linux.go),
[Moby port allocator](https://github.com/moby/moby/blob/v28.5.2/libnetwork/portallocator/portallocator.go),
[Docker Desktop bind report](https://github.com/docker/for-win/issues/13686)). Unknown formats,
permission or reserved-port errors, image and authentication errors, and readiness failures
are returned without retry. Recovery is not guaranteed for every Docker version or network backend.

The fixture checks for a local engine using the host Testcontainers reports after startup.
An unsupported remote engine can therefore pull and start the container, within
`StartupTimeout`, before the fixture rejects it and removes the container.

All topology processes share one container. Use separate deployments for machine-level failure,
network-partition, durability, or realistic high-availability testing. The Cluster fixture has
no replicas. These are unauthenticated development servers; do not put sensitive data in them.

Startup waits for PING, complete Cluster membership/slot coverage, or replication plus Sentinel
quorum and discovery of the healthy replica by every Sentinel. Readiness polling backs off
from 100 ms to 200 ms. One `StartupTimeout` covers image pull, every collision retry,
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
startup exception exposes the result in `Data["RespireFixture.DaemonLogs"]`. After cleanup,
the fixture also writes the result to standard error for test reports, waiting at most two
seconds. Output is best effort: a failed or stalled writer cannot replace the startup
failure, and further reports are skipped while an earlier write remains blocked. The
exception data remains available for custom reports. Collection has its own two-second
deadline, independent of an expired startup deadline or caller cancellation. Combined
diagnostics are limited to 32,768 characters, divided between ports so an oversized result
does not hide other daemons' labels and tails. Missing logs, failed collection, and collection
timeouts are reported without replacing the startup failure or preventing cleanup.

The [in-memory fake](in-memory-testing.md) runs deterministic tests without Docker.
The [shared sample](testing-sample.md) runs the same public-client scenarios against
the fake, Redis, and Valkey on both supported frameworks.
