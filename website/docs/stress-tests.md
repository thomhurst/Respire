---
title: Stress tests
description: Latest sustained Respire and StackExchange.Redis stress-test results.
---

import ComparisonBarChart from '@site/src/components/ComparisonBarChart';

# Stress tests

:::info Automated results
Generated 2026-09-27 03:05 UTC from commit `647409c32cb3`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/36288239924) for logs, JSON results, and downloadable artifacts.
:::

3 min measured (+10s warmup) per scenario/client pass, 50 concurrent workers, 1,024 B values, .NET 10.0.12, Ubuntu 24.04.5 LTS.

## Throughput

<ComparisonBarChart
  title="Sustained throughput"
  description="Operations per second. Longer bars are better."
  format="integer"
  data={[{"label":"ping","other":195251,"respire":242373},{"label":"get","other":123068,"respire":186412},{"label":"set","other":142646,"respire":184899},{"label":"incr","other":161278,"respire":208974},{"label":"hash","other":60431,"respire":90753},{"label":"list","other":60033,"respire":86338},{"label":"mixed","other":120400,"respire":186203}]}
/>
| Scenario | StackExchange.Redis ops/s | Respire ops/s | Respire / StackExchange |
|---|---:|---:|---:|
| ping | 195,251 | 242,373 | 1.24x |
| get | 123,068 | 186,412 | 1.51x |
| set | 142,646 | 184,899 | 1.30x |
| incr | 161,278 | 208,974 | 1.30x |
| hash | 60,431 | 90,753 | 1.50x |
| list | 60,033 | 86,338 | 1.44x |
| mixed | 120,400 | 186,203 | 1.55x |

A ratio above 1.00x means Respire sustained more operations per second.

## Details

| Scenario | Client | Ops/s | p50 ms | p95 ms | p99 ms | p99.9 ms | Max ms | Errors | Alloc/op | Gen0/1/2 | GC pause s | CPU µs/op | Drift % | Status |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| ping | StackExchange.Redis | 195,251 | 0.240 | 0.400 | 0.680 | 1.180 | 6.0 | 0 | 356 B | 752/376/2 | 0.67 | 10.0 | +2.1 | OK |
| ping | Respire | 242,373 | 0.210 | 0.280 | 0.330 | 0.440 | 2.8 | 0 | 121 B | 317/18/1 | 0.10 | 7.7 | -1.1 | OK |
| get | StackExchange.Redis | 123,068 | 0.400 | 0.630 | 0.930 | 1.270 | 6.5 | 0 | 3.55 KB | 5087/2536/2 | 2.25 | 16.1 | +0.4 | OK |
| get | Respire | 186,412 | 0.270 | 0.370 | 0.450 | 0.640 | 3.7 | 0 | 2.15 KB | 4460/104/2 | 1.16 | 9.9 | -0.1 | OK |
| set | StackExchange.Redis | 142,646 | 0.340 | 0.550 | 0.800 | 1.260 | 5.5 | 0 | 383 B | 593/198/2 | 0.61 | 13.7 | -0.9 | OK |
| set | Respire | 184,899 | 0.270 | 0.380 | 0.460 | 0.650 | 2.6 | 0 | 129 B | 260/18/2 | 0.12 | 10.3 | +0.2 | OK |
| incr | StackExchange.Redis | 161,278 | 0.300 | 0.470 | 0.720 | 1.170 | 4.1 | 0 | 495 B | 864/289/2 | 0.97 | 12.3 | +0.4 | OK |
| incr | Respire | 208,974 | 0.240 | 0.320 | 0.370 | 0.500 | 3.2 | 0 | 129 B | 294/18/2 | 0.11 | 8.8 | -0.9 | OK |
| hash | StackExchange.Redis | 60,431 | 0.810 | 1.170 | 1.610 | 2.090 | 7.4 | 0 | 3.99 KB | 2800/813/2 | 1.76 | 32.7 | -0.3 | OK |
| hash | Respire | 90,753 | 0.540 | 0.720 | 0.840 | 1.080 | 8.6 | 0 | 2.30 KB | 2348/74/2 | 0.70 | 20.4 | +0.4 | OK |
| list | StackExchange.Redis | 60,033 | 0.810 | 1.180 | 1.610 | 2.070 | 6.8 | 0 | 3.94 KB | 2746/718/1 | 1.71 | 32.5 | -0.2 | OK |
| list | Respire | 86,338 | 0.570 | 0.750 | 0.870 | 1.120 | 4.2 | 0 | 2.30 KB | 2233/240/1 | 0.60 | 21.0 | -0.2 | OK |
| mixed | StackExchange.Redis | 120,400 | 0.410 | 0.650 | 0.960 | 1.360 | 5.6 | 0 | 2.68 KB | 3735/1085/2 | 1.95 | 16.5 | +1.8 | OK |
| mixed | Respire | 186,203 | 0.270 | 0.380 | 0.460 | 0.670 | 3.0 | 0 | 1.61 KB | 3342/100/2 | 1.01 | 10.1 | +0.3 | OK |

## Notes

- Latency is per operation as issued by the workload; composite scenarios (hash, list) time the pair as one operation.
- Alloc/op and CPU µs/op include a harness overhead that is identical for both clients.
- Drift compares the last-third average of per-second throughput samples against the first third; a sustained negative value indicates degradation over the run.
