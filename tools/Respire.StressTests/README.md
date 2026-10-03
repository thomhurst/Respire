# Respire.StressTests

Sustained stress comparison of Respire against StackExchange.Redis across common Redis
operations. Where the BenchmarkDotNet comparison (`benchmarks/Respire.ComparisonBenchmarks`)
measures steady-state per-operation cost, this runner holds a concurrent workload for
minutes at a time and watches for what short benchmarks miss: throughput drift,
latency tail growth, allocation buildup, errors, and outright stalls.

Both clients are driven through the same `IStressClient` surface, so each scenario issues
an identical operation sequence per client. Within every scenario StackExchange.Redis
runs first as the baseline, then Respire, each on a fresh connection.

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

```bash
# Full run: all scenarios, both clients, 5 minutes measured per pass
dotnet run -c Release -f net10.0 -- --duration 5

# Quick smoke: every scenario for 5 seconds
dotnet run -c Release -f net10.0 -- --duration-seconds 5 --warmup 2

# One scenario, one client
dotnet run -c Release -f net10.0 -- --scenario get --client respire --duration 2
```

Uses `REDIS_HOST` / `REDIS_PORT` when set; otherwise starts a throwaway Redis
Testcontainer (requires Docker). Run `dotnet run -- --help` for all options — any
unknown option prints usage.

Results land in `./results`: one JSON file per scenario/client pass plus
`stress-report.md`, a markdown comparison report.

## Hedged-read tail latency

Run a separate, bounded experiment against an owned Redis 7.2 primary/replica deployment:

```powershell
& ./scripts/Invoke-AgentDotNet.ps1 -SingleNode -DotNetArguments @('run', '--project', 'tools/Respire.StressTests', '-c', 'Release', '-f', 'net10.0', '--', '--hedged-read-latency', 'results')
```

Run from the repository root. Docker is required. This mode does not use `REDIS_HOST` or
`REDIS_PORT`; it pauses only its owned replica. It runs baseline, hedged, and baseline passes
with 20 warmup reads and 1,000 measured sequential GETs per pass. Every twentieth measured
read follows an acknowledged 50 ms `CLIENT PAUSE ALL` on the replica. The hedged pass uses
`ReplicaPreferred`, a 5 ms delay, and a 5% extra-load budget.

The probe waits for each pause to expire outside the timed GET before starting the next read.
This isolates each induced stall; it does not model sustained arrival rates or backlog under
overload. Redis scheduling can make a 50 ms pause last longer, and host timer resolution can
make a 5 ms hedge start later. Fresh clients share one fixture across the three passes.

`hedged-read-latency.json` includes all measured samples, nearest-rank p50/p95/p99, runtime,
OS, configuration, and hedge counters. Counters and the budget include warmup; latency
percentiles exclude warmup. The process fails on incorrect replies, timeouts, or budget
violations. It reports latency improvement without imposing a noisy CI performance threshold.

One Windows/.NET 10 run on 2026-10-03 measured p99 93.632 ms before and 93.800 ms after,
versus 16.572 ms with hedging. All 50 hedges won: 4.90% extra requests across 1,020 reads,
below the 51-request budget. These are results for this controlled pause experiment,
not a throughput claim or a prediction for production workloads.

## Failure policy

The process exits non-zero when any pass records an operation error, stalls (no
completed operations for 60 consecutive seconds), or an unobserved task exception
surfaces — so CI runs fail loudly rather than publishing numbers from a broken run.
Throughput drift is reported but does not fail the run; shared CI runners are too
noisy for that to be a reliable signal.

## CI

`.github/workflows/stress-tests.yml` runs weekly (all scenarios, both clients) and on
manual dispatch with scenario/client/duration/concurrency/value-size/framework inputs,
against a Redis 8.0 service container on `ubuntu-latest`. The report is published to
the job summary and the full results are uploaded as an artifact.
