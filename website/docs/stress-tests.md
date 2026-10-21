---
title: Stress tests
description: Latest sustained Respire and StackExchange.Redis stress-test results.
---

import ComparisonBarChart from '@site/src/components/ComparisonBarChart';

# Stress tests

:::info Automated results
Generated 2026-10-21 20:16 UTC from commit `5e0a6f4cdd0c`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/39662150657) for logs, JSON results, and downloadable artifacts.
:::

3 min measured (+10s warmup) per scenario/client pass, 50 concurrent workers, 1,024 B values, .NET 10.0.13, Ubuntu 24.04.5 LTS.

## Throughput

<ComparisonBarChart
  title="Sustained throughput"
  description="Operations per second. Longer bars are better."
  format="integer"
  data={[{"label":"ping","other":235341,"respire":260886},{"label":"get","other":141612,"respire":187815},{"label":"set","other":166038,"respire":192333},{"label":"incr","other":196805,"respire":227435},{"label":"hash","other":70896,"respire":91268},{"label":"list","other":70093,"respire":86446},{"label":"mixed","other":140035,"respire":197938}]}
/>
| Scenario | StackExchange.Redis ops/s | Respire ops/s | Respire / StackExchange |
|---|---:|---:|---:|
| ping | 235,341 | 260,886 | 1.11x |
| get | 141,612 | 187,815 | 1.33x |
| set | 166,038 | 192,333 | 1.16x |
| incr | 196,805 | 227,435 | 1.16x |
| hash | 70,896 | 91,268 | 1.29x |
| list | 70,093 | 86,446 | 1.23x |
| mixed | 140,035 | 197,938 | 1.41x |

A ratio above 1.00x means Respire sustained more operations per second.

## Details

| Scenario | Client | Ops/s | p50 ms | p95 ms | p99 ms | p99.9 ms | Max ms | Errors | Alloc/op | Gen0/1/2 | GC pause s | CPU µs/op | Drift % | Status |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| ping | StackExchange.Redis | 235,341 | 0.200 | 0.320 | 0.600 | 1.080 | 5.7 | 0 | 354 B | 901/441/2 | 0.62 | 8.3 | +5.0 | OK |
| ping | Respire | 260,886 | 0.190 | 0.270 | 0.320 | 0.470 | 4.0 | 0 | 120 B | 340/18/1 | 0.13 | 7.4 | 0.0 | OK |
| get | StackExchange.Redis | 141,612 | 0.340 | 0.540 | 0.870 | 1.270 | 5.4 | 0 | 3.55 KB | 5868/1358/2 | 2.74 | 14.0 | +1.3 | OK |
| get | Respire | 187,815 | 0.270 | 0.360 | 0.450 | 0.770 | 2.5 | 0 | 2.15 KB | 4497/19/2 | 1.67 | 9.7 | +2.4 | OK |
| set | StackExchange.Redis | 166,038 | 0.290 | 0.450 | 0.670 | 1.190 | 4.7 | 0 | 380 B | 684/210/2 | 0.73 | 11.8 | +0.8 | OK |
| set | Respire | 192,333 | 0.260 | 0.370 | 0.440 | 0.650 | 2.9 | 0 | 128 B | 267/19/2 | 0.13 | 10.0 | -0.5 | OK |
| incr | StackExchange.Redis | 196,805 | 0.250 | 0.370 | 0.590 | 1.070 | 3.1 | 0 | 491 B | 1044/331/2 | 0.70 | 10.1 | +0.1 | OK |
| incr | Respire | 227,435 | 0.220 | 0.310 | 0.360 | 0.500 | 2.3 | 0 | 128 B | 316/19/2 | 0.16 | 8.5 | -1.4 | OK |
| hash | StackExchange.Redis | 70,896 | 0.680 | 1.010 | 1.490 | 2.030 | 6.6 | 0 | 3.98 KB | 3266/655/2 | 2.22 | 27.7 | 0.0 | OK |
| hash | Respire | 91,268 | 0.540 | 0.720 | 0.880 | 1.210 | 3.4 | 0 | 2.30 KB | 2359/92/2 | 0.96 | 20.9 | +1.3 | OK |
| list | StackExchange.Redis | 70,093 | 0.680 | 1.030 | 1.470 | 1.970 | 5.5 | 0 | 3.93 KB | 3193/758/1 | 1.98 | 27.8 | +0.1 | OK |
| list | Respire | 86,446 | 0.570 | 0.760 | 0.900 | 1.190 | 3.0 | 0 | 2.30 KB | 2235/199/1 | 0.75 | 22.0 | -0.5 | OK |
| mixed | StackExchange.Redis | 140,035 | 0.350 | 0.530 | 0.860 | 1.270 | 3.2 | 0 | 2.67 KB | 4333/1239/2 | 2.05 | 14.1 | +0.9 | OK |
| mixed | Respire | 197,938 | 0.250 | 0.360 | 0.440 | 0.670 | 5.2 | 0 | 1.61 KB | 3554/191/2 | 1.11 | 9.6 | -0.4 | OK |

## Notes

- Latency is per operation as issued by the workload; composite scenarios (hash, list) time the pair as one operation.
- Alloc/op and CPU µs/op include a harness overhead that is identical for both clients.
- Drift compares the last-third average of per-second throughput samples against the first third; a sustained negative value indicates degradation over the run.
