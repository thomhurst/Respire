---
title: Container test fixtures
description: Run Redis and Valkey integration tests with disposable standalone, Cluster, and Sentinel fixtures.
---

# Container test fixtures

`Respire.Testing.Containers` starts disposable Redis or Valkey servers in Docker for integration tests.
It works with any test framework (TUnit, xUnit, NUnit, or none).

Requirements:

- A local Docker engine with Linux containers. Remote engines are not supported.
- The normal .NET runtime. The package depends on Testcontainers and is not Native AOT compatible; keep it in test projects.

```csharp
using Respire.Testing.Containers;

await using var fixture = await RespireContainerFixture.StartAsync();
await using var client = await RespireClient.ConnectAsync(fixture.CreateOptions());
await client.SetAsync("example", "value");
var value = await client.GetStringAsync("example");
if (value != "value") throw new InvalidOperationException("Round trip failed.");
```

Dispose clients before their fixture. Disposal removes only the fixture's own container.
Each fixture has its own data directory and never reuses containers or volumes. Failed or
cancelled startup cleans up before returning the error.

The [RESP010 lifetime warning](../diagnostics/RESP010.md) is disabled in projects
referencing `Respire.Testing.Containers`. Each test can own its clients, including
multiple connections to one fixture, without a local suppression.

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
    fixture.CreateOptions() with { Protocol = RespireProtocol.Resp3 });
await client.SetAsync("{customer}:name", "Ada");
```

| Topology | Processes | Client configuration |
| --- | --- | --- |
| `Standalone` | One server | One mapped endpoint |
| `Cluster` | Three primaries covering all 16,384 slots, no replicas | All three seeds, `UseCluster = true` |
| `Sentinel` | One primary, one replica, three Sentinels with quorum two | Three Sentinel endpoints and service name `respire-test` |

| Option | Default | Meaning |
| --- | --- | --- |
| `Server` | Redis | `RespireContainerServer.Redis` or `Valkey`. |
| `Image` | `redis:7.2-alpine` / `valkey/valkey:8.1-alpine` | Pin a tag or digest for reproducible runs. The image needs `/bin/sh`, `mkdir`, `tail`, and the server and CLI binaries. Cluster needs Redis 7+ or Valkey (`CLUSTER ADDSLOTSRANGE`). |
| `Topology` | `Standalone` | See the table above. |
| `StartupTimeout` | 2 minutes | One deadline for image pull, retries, and readiness. |
| `SentinelDownAfter` | 5 seconds | Sentinel down-detection interval. |
| `SentinelFailoverTimeout` | 10 seconds | Sentinel failover timeout. Sentinels retry an aborted election after twice this value. |

`CreateOptions()` returns fresh options each call. Use a `with` expression to set protocol, timeouts,
client name, or admin access. `DataEndpoints`, `SentinelEndpoints`, and `ContainerId` help with
diagnostics and direct node connections.

### Sentinel failover tests

- The first data endpoint is the **initial** primary; it is not updated after failover. Sentinel discovery finds the current primary at connect time.
- The fixture does not add automatic failover handling to the client.
- `StopDataNodeAsync(index)`, `StartDataNodeAsync(index)`, and `WaitForDataNodeReplicaAsync(index)` stop a node, restart it, and wait until it reports as a replica. Index `0` is the initial primary, `1` the initial replica. On other topologies they throw `InvalidOperationException`.
- Shorter Sentinel timeouts speed up failover tests but can cause false failovers on a busy host. Keep the defaults for tests that do not fail over.

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

- Ports are published only on `127.0.0.1`. Standalone uses Docker-assigned random ports. Cluster and Sentinel use the same port inside and outside the container, so advertised addresses work ([Sentinel and Docker NAT](https://redis.io/docs/latest/operate/oss_and_stack/management/sentinel/#sentinel-docker-nat-and-possible-issues)). Cluster bus ports are not published.
- Ports come from the OS ephemeral range. Other fixtures and services are never stopped.
- If Docker reports a host-port collision ("port already allocated" or "address already in use"), the fixture removes the container and retries up to twice. Other errors (permissions, reserved ports, images, authentication, readiness) are not retried.
- The local-engine check runs after startup, so a remote engine can pull and start the container before the fixture rejects and removes it.
- All processes share one container. Use real deployments to test machine failure, network partitions, durability, or high availability.
- Servers are unauthenticated. Do not store sensitive data in them.

## Startup and diagnostics

Startup waits until the topology is ready: PING for standalone, full slot coverage for Cluster,
or replication plus Sentinel quorum and replica discovery for Sentinel. `StartupTimeout` covers
the whole startup and is not reset for retries. A timeout error names the stage and last readiness reply.

On failure:

- The final Docker exception is thrown. Its `Data` includes `RespireFixture.StartupAttempt`, `RespireFixture.SelectedPorts`, `RespireFixture.ContainerId`, and `RespireFixture.PreviousPortCollisions`.
- If cleanup also fails, all causes are thrown together in an `AggregateException`.
- Cancellation and timeout exceptions keep the original error as the inner exception.
- If the container started, the last 4 KiB of each daemon log is stored in `Data["RespireFixture.DaemonLogs"]` (at most 32,768 characters total) and written to standard error. Log collection is best effort and never hides the startup error.

The [in-memory fake](in-memory-testing.md) runs deterministic tests without Docker.
The [shared sample](testing-sample.md) runs the same scenarios against the fake, Redis, and Valkey.
