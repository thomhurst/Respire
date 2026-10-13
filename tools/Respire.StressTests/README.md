# Respire.StressTests

A sustained stress comparison of Respire and StackExchange.Redis. Where the
BenchmarkDotNet comparison (`benchmarks/Respire.ComparisonBenchmarks`) measures
per-operation cost, this runner holds a concurrent workload for minutes and looks
for throughput drift, latency tail growth, allocation buildup, errors, and stalls.

Both clients run the same operation sequence through `IStressClient`. In each
scenario StackExchange.Redis runs first as the baseline, then Respire, each on a
fresh connection.

## Scenarios

| Name | Workload |
|---|---|
| `ping` | PING round-trips (pure protocol + pipeline overhead) |
| `get` | GET of a seeded key, value read as a string |
| `set` | SET of a per-worker key with the configured payload |
| `incr` | INCR of a per-worker counter |
| `hash` | HSET + HGET pair on a per-worker hash |
| `list` | LPUSH + LPOP pair on a per-worker list (constant list size) |
| `mixed` | Cache-style mix: 60% GET, 20% SET, 10% INCR, 10% HGET |

## Running locally

From `tools/Respire.StressTests`:

```bash
# Full run: all scenarios, both clients, 5 minutes measured per pass
dotnet run -c Release -f net10.0 -- --duration 5

# Quick smoke: every scenario for 5 seconds
dotnet run -c Release -f net10.0 -- --duration-seconds 5 --warmup 2

# One scenario, one client
dotnet run -c Release -f net10.0 -- --scenario get --client respire --duration 2
```

- Uses `REDIS_HOST` / `REDIS_PORT` (default port 6379) when set; otherwise starts a
  throwaway `redis:8.10` Testcontainer (needs Docker).
- Each pass has a 10-second warmup by default, so the full run takes about
  14 × 5 min 10 s.
- `dotnet run -- --help` lists all options; an unknown option prints usage.
- Results go to `./results`: one JSON file per pass plus `stress-report.md`.

## Hedged-read tail latency

A separate experiment against its own Redis 7.2 primary and replica (needs
Docker; ignores `REDIS_HOST`/`REDIS_PORT`). From the repository root:

```powershell
& ./scripts/Invoke-AgentDotNet.ps1 -SingleNode -DotNetArguments @('run', '--project', 'tools/Respire.StressTests', '-c', 'Release', '-f', 'net10.0', '--', '--hedged-read-latency', 'results')
```

- It runs baseline, hedged, and baseline passes, each with 20 warmup reads and
  1,000 measured sequential GETs.
- Every twentieth read follows a 50 ms `CLIENT PAUSE ALL` on the replica. The
  hedged pass uses `ReplicaPreferred`, a 5 ms delay, and a 5% extra-load budget.
- Each pause expires before the next read, so this isolates single stalls; it
  does not model sustained load or backlog.
- `hedged-read-latency.json` holds all samples, p50/p95/p99 (excluding warmup),
  environment, configuration, and hedge counters (including warmup).
- The run fails on wrong replies, timeouts, or budget violations, but has no
  latency threshold.

One Windows/.NET 10 run on 2026-10-03 measured p99 93.632 ms before and 93.800 ms after,
versus 16.572 ms with hedging. All 50 hedges won: 4.90% extra requests across 1,020 reads,
below the 51-request budget. This is a controlled pause experiment, not a
production prediction.

## Failure policy

The process exits non-zero when any pass records an operation error, stalls (no
completed operations for 60 seconds), or surfaces an unobserved task exception.
Throughput drift is reported but does not fail the run, because shared CI
runners are too noisy.

## CI

`.github/workflows/stress-tests.yml` runs weekly (all scenarios, both clients,
3 minutes per pass) against a Redis 8.10 service container on `ubuntu-latest`.
Manual dispatch accepts scenario, client, duration, concurrency, value-size, and
framework inputs. The report goes to the job summary and the results to an
artifact. Scheduled runs, and manual runs on `main` with `full_run`, also
regenerate [`website/docs/stress-tests.md`](../../website/docs/stress-tests.md).
