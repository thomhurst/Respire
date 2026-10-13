# Redis Cluster sample

A console app that connects through one seed, lists the discovered shards, and
round-trips three expiring keys in different hash-slot ranges.

The Compose topology is for local development only: three Redis 8.10 primaries
in one container, with no replicas, persistence, or authentication. Host ports
match the advertised ports, so a .NET process on the host can follow discovered
endpoints and MOVED redirects.

## Start and run

You need the SDK from `global.json`, a .NET 8 or .NET 10 runtime, and Docker
Compose v2 with Linux containers. Ports 7000–7002 must be free. From the repo root:

```sh
docker compose -p respire-cluster-sample -f samples/Respire.Samples.Cluster/compose.yaml up --build -d --wait --wait-timeout 60
dotnet run --project samples/Respire.Samples.Cluster -c Release -f net10.0
```

Use `-f net8.0` for .NET 8. Expected output:

```text
Discovered 3 shards from one seed.
PASS: respire:cluster-sample:{a}, slot 15495
PASS: respire:cluster-sample:{b}, slot 3300
PASS: respire:cluster-sample:{c}, slot 7365
Cluster sample completed; all three values round-tripped.
```

The sample throws on a mismatched reply and stops after 20 seconds. Keys start
with `respire:cluster-sample:` and expire after two minutes.

Inspect the slot owners:

```sh
docker compose -p respire-cluster-sample -f samples/Respire.Samples.Cluster/compose.yaml exec -T cluster redis-cli -p 7000 CLUSTER SLOTS
docker compose -p respire-cluster-sample -f samples/Respire.Samples.Cluster/compose.yaml exec -T cluster redis-cli -p 7000 GET 'respire:cluster-sample:{a}'
docker compose -p respire-cluster-sample -f samples/Respire.Samples.Cluster/compose.yaml exec -T cluster redis-cli -c -p 7000 GET 'respire:cluster-sample:{a}'
```

The first `GET` returns MOVED because `{a}` belongs to another primary; the `-c`
`GET` follows the redirect. Respire reaches all three ranges from port 7000
alone. This demo checks routing, not failover: with no replicas, a failed primary
is not replaced. See the [Sentinel sample](../Respire.Samples.Sentinel/README.md)
for a promotion demo.

To use another cluster, set `RESPIRE_CONNECTION` (for example
`cache-a:6379,cache-b:6379,cluster=true`) in your shell or secret provider; the
sample never prints it. The client must reach every advertised endpoint. This
topology advertises `127.0.0.1`, so run the sample on the Docker host, and do not
change only the published port.

## Stop or diagnose

If readiness fails, check the logs, then tear down:

```sh
docker compose -p respire-cluster-sample -f samples/Respire.Samples.Cluster/compose.yaml logs
docker compose -p respire-cluster-sample -f samples/Respire.Samples.Cluster/compose.yaml exec -T cluster cat /data/7000/server.log
docker compose -p respire-cluster-sample -f samples/Respire.Samples.Cluster/compose.yaml down --volumes
```

- Cleanup affects only this Compose project. `/data` is tmpfs, so stopping
  discards the topology and keys.
- Ports bind to loopback only; the container network is unauthenticated.
- If any Redis process exits, the container stops. Restart the topology before
  continuing.
- The [topology smoke workflow](../TopologySmoke.md) runs this sample on both
  frameworks, in CI or locally.
