# Redis Cluster sample

This console application connects through one seed, inspects the discovered
shards, and round-trips three expiring keys in different hash-slot ranges.
It uses public Respire APIs and builds on .NET 8 and .NET 10 in CI.

The Compose topology is for local development: three Redis 8.4 primaries in one
container, with no replicas, persistence, authentication or host redundancy.
All processes share a loopback network namespace. Published host ports match
the advertised client ports, so a .NET process on the host can follow discovered
endpoints and MOVED redirects without an address mapper. This is not a production
cluster layout. Redis Cluster bus ports remain inside the container.

## Start and run

Install the SDK selected by `global.json`, the desired .NET runtime, and Docker
Compose v2 with Linux containers. Ports 7000–7002 must be free. From the repo root:

```sh
docker compose -p respire-cluster-sample -f samples/Respire.Samples.Cluster/compose.yaml up --build -d --wait --wait-timeout 60
dotnet run --project samples/Respire.Samples.Cluster -c Release -f net10.0
```

Use `-f net8.0` for .NET 8. Expected output: three discovered shards and three
`PASS` rows with different slots, followed by `Cluster sample completed`.
The sample throws on a mismatched reply and bounds its entire run to 20 seconds.
Keys start with `respire:cluster-sample:` and expire after two minutes.

Inspect the three owners and their assigned ranges:

```sh
docker compose -p respire-cluster-sample -f samples/Respire.Samples.Cluster/compose.yaml exec -T cluster redis-cli -p 7000 CLUSTER SLOTS
docker compose -p respire-cluster-sample -f samples/Respire.Samples.Cluster/compose.yaml exec -T cluster redis-cli -p 7000 GET 'respire:cluster-sample:{a}'
docker compose -p respire-cluster-sample -f samples/Respire.Samples.Cluster/compose.yaml exec -T cluster redis-cli -c -p 7000 GET 'respire:cluster-sample:{a}'
```

The first GET returns a MOVED response because `{a}` belongs to another primary.
The `-c` GET follows the redirect and returns the value. Respire's sample reaches
all three ranges after connecting to only port 7000; normal commands use its
discovered routing table. These are routing checks, not failover checks. With
no replicas this cluster cannot replace a failed primary. See the sibling
[Sentinel sample](../Respire.Samples.Sentinel/README.md) for a promotion demo.

`RESPIRE_CONNECTION` can point to another cluster, for example
`cache-a:6379,cache-b:6379,cluster=true`. Set it in your shell or secret provider;
the sample never prints it. Every advertised data endpoint must be reachable by
the client. This local topology advertises `127.0.0.1`, so run the sample on the
Docker host, not inside another container or on a remote machine. Do not change
only Compose's published port: its advertised port must match. For an external
cluster, shard count and slot ownership can differ from this three-range demo.

## Stop or diagnose

If readiness fails, inspect the startup output and node logs:

```sh
docker compose -p respire-cluster-sample -f samples/Respire.Samples.Cluster/compose.yaml logs
docker compose -p respire-cluster-sample -f samples/Respire.Samples.Cluster/compose.yaml exec -T cluster cat /data/7000/server.log
docker compose -p respire-cluster-sample -f samples/Respire.Samples.Cluster/compose.yaml down --volumes
```

The project name scopes cleanup to this sample. `/data` is tmpfs, so stopping
the container discards the topology and keys; startup creates a fresh cluster.
There are no shared volumes to preserve. Host bindings are loopback-only; the
unauthenticated container network is still a local development boundary.
Ordinary CI builds the sample. The separate manual [topology smoke workflow](../TopologySmoke.md)
runs it against its owned Compose project on both frameworks and requires successful
round trips in all three slot ranges. The same controller runs locally.
