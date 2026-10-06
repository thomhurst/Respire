# Redis Sentinel sample

This console application discovers the primary from three Sentinel seeds and
round-trips an expiring value. An optional iteration count keeps the same client
alive while you trigger a controlled promotion. It builds on .NET 8 and .NET 10
in CI and uses only public Respire APIs.

The local Compose topology runs one primary, one replica and three Sentinels
inside one container. This lets both the server processes and a host-side .NET
client reach advertised loopback addresses at matching ports. It demonstrates
discovery and promotion, not independent failure domains or production high
availability. Persistence and authentication are disabled. Data is held in tmpfs;
the short Sentinel timeouts are demonstration settings, not production guidance.

## Start and run

Use the SDK selected by `global.json`, the desired .NET runtime, and Docker
Compose v2 with Linux containers. Ports 7100–7101 and 27100–27102 must be free.
From the repository root:

```sh
docker compose -p respire-sentinel-sample -f samples/Respire.Samples.Sentinel/compose.yaml up --build -d --wait --wait-timeout 60
dotnet run --project samples/Respire.Samples.Sentinel -c Release -f net10.0
```

Use `-f net8.0` for .NET 8. Expected output includes `PASS 1: primary 127.0.0.1:7100`
and one successful round trip. A later run after promotion may report port 7101.
The key `respire:sentinel-sample:value` expires after two minutes. The default
single-iteration run fails on any command error or mismatched value.

## Observe a promotion with the same client

Start 60 iterations in one terminal (one-second pauses between operations):

```sh
dotnet run --project samples/Respire.Samples.Sentinel -c Release -f net10.0 -- 60
```

After its first successful row, use a second terminal:

```sh
docker compose -p respire-sentinel-sample -f samples/Respire.Samples.Sentinel/compose.yaml exec -T sentinel redis-cli -p 27100 SENTINEL FAILOVER sample-primary
docker compose -p respire-sentinel-sample -f samples/Respire.Samples.Sentinel/compose.yaml exec -T sentinel redis-cli -p 27100 SENTINEL GET-MASTER-ADDR-BY-NAME sample-primary
```

Poll the address command until its port changes, allowing up to 30 seconds.
Successful sample rows should then report that new port without recreating the
client. Sentinel reconfigures the old primary as a replica. A requested promotion
is asynchronous; `OK` does not mean it has completed. If a prior promotion is
still running or no eligible replica exists, Redis returns an error instead.

The multi-iteration demo prints failures and continues with a **new** write/read
iteration. It never replays the failed write. The final success count alone does
not prove failover: verify successful rows from both old and new endpoints.
Transient connection, timeout, READONLY or value-mismatch errors are possible.
Replication is asynchronous, so an acknowledged write can be lost during
promotion; this example does not promise lossless delivery or exactly-once retry.
Ctrl+C cancels the loop and disposes the client. Accepted iteration counts are
1–120. Run only one copy per key namespace when comparing write/read values.

Set `RESPIRE_CONNECTION` through your shell or secret provider for an external
deployment, using Sentinel endpoints plus `serviceName=...`. The sample does not
print the connection string. Data and Sentinel authentication have separate
settings; see [connection documentation](../../website/docs/fundamentals/connections.md#redis-sentinel).
Every discovered endpoint must be reachable. This Compose topology advertises
`127.0.0.1`, so run the .NET sample on the Docker host, not inside another
container or on a remote machine. Do not remap only the host ports.

## Stop or diagnose

```sh
docker compose -p respire-sentinel-sample -f samples/Respire.Samples.Sentinel/compose.yaml logs
docker compose -p respire-sentinel-sample -f samples/Respire.Samples.Sentinel/compose.yaml exec -T sentinel cat /data/27100/server.log
docker compose -p respire-sentinel-sample -f samples/Respire.Samples.Sentinel/compose.yaml down --volumes
```

Cleanup targets only this Compose project. Stopping the container discards its
tmpfs data and rewritten Sentinel configuration; startup begins again with port
7100 as primary. No shared Redis service or volume is touched. Host bindings are
loopback-only; the container's unauthenticated network is for local development.
Ordinary CI builds both framework targets. The separate [topology smoke workflow](../TopologySmoke.md), triggered on demand and on PRs touching the sample,
runs the sample on both frameworks, requests promotion, and requires successful rows
from both primary endpoints in the same process. The same controller runs locally.
