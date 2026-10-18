---
title: Stress tests
description: Latest sustained Respire and StackExchange.Redis stress-test results.
---

import ComparisonBarChart from '@site/src/components/ComparisonBarChart';

# Stress tests

:::info Automated results
Generated 2026-10-18 03:02 UTC from commit `95afd0ebba5b`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/39089467423) for logs, JSON results, and downloadable artifacts.
:::

3 min measured (+10s warmup) per scenario/client pass, 50 concurrent workers, 1,024 B values, .NET 10.0.13, Ubuntu 24.04.5 LTS.

## Throughput

<ComparisonBarChart
  title="Sustained throughput"
  description="Operations per second. Longer bars are better."
  format="integer"
  data={[{"label":"ping","other":188452,"respire":228438},{"label":"get","other":121271,"respire":167539},{"label":"set","other":140342,"respire":178928},{"label":"incr","other":158675,"respire":206696},{"label":"hash","other":59852,"respire":84918},{"label":"list","other":59443,"respire":80276},{"label":"mixed","other":117926,"respire":180663}]}
/>
| Scenario | StackExchange.Redis ops/s | Respire ops/s | Respire / StackExchange |
|---|---:|---:|---:|
| ping | 188,452 | 228,438 | 1.21x |
| get | 121,271 | 167,539 | 1.38x |
| set | 140,342 | 178,928 | 1.27x |
| incr | 158,675 | 206,696 | 1.30x |
| hash | 59,852 | 84,918 | 1.42x |
| list | 59,443 | 80,276 | 1.35x |
| mixed | 117,926 | 180,663 | 1.53x |

A ratio above 1.00x means Respire sustained more operations per second.

## Details

| Scenario | Client | Ops/s | p50 ms | p95 ms | p99 ms | p99.9 ms | Max ms | Errors | Alloc/op | Gen0/1/2 | GC pause s | CPU µs/op | Drift % | Status |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| ping | StackExchange.Redis | 188,452 | 0.250 | 0.440 | 0.770 | 1.220 | 8.6 | 0 | 356 B | 726/363/2 | 0.94 | 10.1 | -0.9 | OK |
| ping | Respire | 228,438 | 0.230 | 0.300 | 0.350 | 0.490 | 5.5 | 0 | 120 B | 300/18/3 | 0.13 | 8.0 | +1.9 | OK |
| get | StackExchange.Redis | 121,271 | 0.400 | 0.640 | 1.010 | 1.430 | 5.5 | 0 | 3.55 KB | 5015/1366/2 | 3.13 | 16.3 | +0.9 | OK |
| get | Respire | 167,539 | 0.310 | 0.380 | 0.470 | 0.810 | 7.1 | 0 | 2.15 KB | 4005/121/2 | 1.64 | 10.1 | 0.0 | OK |
| set | StackExchange.Redis | 140,342 | 0.340 | 0.560 | 0.840 | 1.290 | 5.4 | 0 | 383 B | 583/196/2 | 0.89 | 13.7 | -0.8 | OK |
| set | Respire | 178,928 | 0.280 | 0.400 | 0.470 | 0.690 | 6.6 | 0 | 128 B | 250/18/2 | 0.13 | 10.5 | +0.6 | OK |
| incr | StackExchange.Redis | 158,675 | 0.310 | 0.490 | 0.780 | 1.190 | 4.7 | 0 | 495 B | 850/284/2 | 1.08 | 12.3 | +2.2 | OK |
| incr | Respire | 206,696 | 0.240 | 0.330 | 0.390 | 0.550 | 3.6 | 0 | 128 B | 286/19/1 | 0.15 | 8.9 | -0.5 | OK |
| hash | StackExchange.Redis | 59,852 | 0.810 | 1.200 | 1.650 | 2.280 | 4.9 | 0 | 3.99 KB | 2773/925/2 | 1.93 | 32.8 | +0.5 | OK |
| hash | Respire | 84,918 | 0.580 | 0.780 | 0.950 | 1.260 | 4.2 | 0 | 2.30 KB | 2194/128/2 | 0.98 | 21.7 | -0.1 | OK |
| list | StackExchange.Redis | 59,443 | 0.810 | 1.190 | 1.680 | 2.270 | 5.8 | 0 | 3.94 KB | 2719/765/2 | 2.15 | 32.8 | +0.6 | OK |
| list | Respire | 80,276 | 0.610 | 0.820 | 1.000 | 1.320 | 2.5 | 0 | 2.30 KB | 2076/18/2 | 0.93 | 23.1 | +0.1 | OK |
| mixed | StackExchange.Redis | 117,926 | 0.410 | 0.660 | 1.050 | 1.810 | 5.8 | 0 | 2.68 KB | 3658/636/2 | 3.20 | 16.7 | +0.6 | OK |
| mixed | Respire | 180,663 | 0.270 | 0.390 | 0.480 | 0.800 | 4.6 | 0 | 1.61 KB | 3243/191/2 | 1.37 | 10.0 | -0.4 | OK |

## Notes

- Latency is per operation as issued by the workload; composite scenarios (hash, list) time the pair as one operation.
- Alloc/op and CPU µs/op include a harness overhead that is identical for both clients.
- Drift compares the last-third average of per-second throughput samples against the first third; a sustained negative value indicates degradation over the run.
