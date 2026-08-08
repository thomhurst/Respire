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
