# Redis Sentinel sample

A console app that finds the primary through three Sentinel seeds and
round-trips an expiring value. Pass an iteration count to keep the same client
running while you trigger a promotion.

The Compose topology is for local development only: one primary, one replica,
and three Sentinels in one container, with tmpfs data and no persistence or
authentication. The short Sentinel timeouts are for the demo, not production.

## Start and run

You need the SDK from `global.json`, a .NET 8 or .NET 10 runtime, and Docker
Compose v2 with Linux containers. Ports 7100–7101 and 27100–27102 must be free.
From the repository root:

```sh
docker compose -p respire-sentinel-sample -f samples/Respire.Samples.Sentinel/compose.yaml up --build -d --wait --wait-timeout 60
dotnet run --project samples/Respire.Samples.Sentinel -c Release -f net10.0
```

Use `-f net8.0` for .NET 8. Expected output:

```text
PASS 1: primary 127.0.0.1:7100
Sentinel sample completed: 1 successful round trips.
```

After a promotion, the port may be 7101. The key `respire:sentinel-sample:value`
expires after two minutes. A single run fails on any error or mismatch.

## Observe a promotion with the same client

1. Start 60 iterations, one second apart:

   ```sh
   dotnet run --project samples/Respire.Samples.Sentinel -c Release -f net10.0 -- 60
   ```

2. After the first successful row, request a promotion in a second terminal:

   ```sh
   docker compose -p respire-sentinel-sample -f samples/Respire.Samples.Sentinel/compose.yaml exec -T sentinel redis-cli -p 27100 SENTINEL FAILOVER sample-primary
   docker compose -p respire-sentinel-sample -f samples/Respire.Samples.Sentinel/compose.yaml exec -T sentinel redis-cli -p 27100 SENTINEL GET-MASTER-ADDR-BY-NAME sample-primary
   ```

3. Repeat the address command until the port changes (up to 30 seconds). Later
   rows report the new port without a new client.

Notes:

- `OK` from `SENTINEL FAILOVER` means requested, not finished. Redis returns an
  error if a promotion is running or no replica is eligible.
- A failed iteration is printed and the loop moves on to a new write and read;
  it never replays the failed write. Check for successful rows from both ports,
  not just the final count.
- Connection, timeout, `READONLY`, or mismatch errors can occur. Replication is
  asynchronous, so an acknowledged write can be lost during promotion.
- Iteration counts are 1–120. Run one copy per key namespace. Ctrl+C stops the
  loop and disposes the client.

For an external deployment, set `RESPIRE_CONNECTION` (Sentinel endpoints plus
`serviceName=...`) in your shell or secret provider; the sample never prints it.
Data and Sentinel authentication are separate settings; see the
[connection documentation](../../website/docs/fundamentals/connections.md#redis-sentinel).
The client must reach every discovered endpoint. This topology advertises
`127.0.0.1`, so run the sample on the Docker host, and do not remap only the
host ports.

## Stop or diagnose

```sh
docker compose -p respire-sentinel-sample -f samples/Respire.Samples.Sentinel/compose.yaml logs
docker compose -p respire-sentinel-sample -f samples/Respire.Samples.Sentinel/compose.yaml exec -T sentinel cat /data/27100/server.log
docker compose -p respire-sentinel-sample -f samples/Respire.Samples.Sentinel/compose.yaml down --volumes
```

- Cleanup affects only this Compose project. Stopping discards data and Sentinel
  configuration; the next start has port 7100 as primary again.
- Ports bind to loopback only; the container network is unauthenticated.
- Readiness requires Sentinel quorum, the primary's role, and an eligible
  replica. Health can briefly fail during a promotion.
- If any process exits, the container stops. Restart the topology before
  continuing.
- The [topology smoke workflow](../TopologySmoke.md) runs this sample, including
  a promotion, on both frameworks, in CI or locally.
