---
title: Stress tests
description: Latest sustained Respire and StackExchange.Redis stress-test results.
---

import ComparisonBarChart from '@site/src/components/ComparisonBarChart';

# Stress tests

:::info Automated results
Generated 2026-10-05 13:16 UTC from commit `99929295e05f`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/37309537385) for logs, JSON results, and downloadable artifacts.
:::

3 min measured (+10s warmup) per scenario/client pass, 50 concurrent workers, 1,024 B values, .NET 10.0.12, Ubuntu 24.04.5 LTS.

## Throughput

<ComparisonBarChart
  title="Sustained throughput"
  description="Operations per second. Longer bars are better."
  format="integer"
  data={[{"label":"ping","other":192653,"respire":228882},{"label":"get","other":121158,"respire":178802},{"label":"set","other":140252,"respire":184697},{"label":"incr","other":159505,"respire":194689},{"label":"hash","other":59651,"respire":88761},{"label":"list","other":59314,"respire":84701},{"label":"mixed","other":117623,"respire":181013}]}
/>
| Scenario | StackExchange.Redis ops/s | Respire ops/s | Respire / StackExchange |
|---|---:|---:|---:|
| ping | 192,653 | 228,882 | 1.19x |
| get | 121,158 | 178,802 | 1.48x |
| set | 140,252 | 184,697 | 1.32x |
| incr | 159,505 | 194,689 | 1.22x |
| hash | 59,651 | 88,761 | 1.49x |
| list | 59,314 | 84,701 | 1.43x |
| mixed | 117,623 | 181,013 | 1.54x |

A ratio above 1.00x means Respire sustained more operations per second.

## Details

| Scenario | Client | Ops/s | p50 ms | p95 ms | p99 ms | p99.9 ms | Max ms | Errors | Alloc/op | Gen0/1/2 | GC pause s | CPU µs/op | Drift % | Status |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| ping | StackExchange.Redis | 192,653 | 0.250 | 0.410 | 0.690 | 1.180 | 6.6 | 0 | 356 B | 742/371/2 | 0.89 | 10.0 | +5.0 | OK |
| ping | Respire | 228,882 | 0.230 | 0.290 | 0.340 | 0.500 | 4.1 | 0 | 120 B | 299/19/2 | 0.12 | 7.9 | +1.0 | OK |
| get | StackExchange.Redis | 121,158 | 0.400 | 0.650 | 0.970 | 1.380 | 4.2 | 0 | 3.55 KB | 5010/1644/2 | 2.70 | 16.4 | -1.0 | OK |
| get | Respire | 178,802 | 0.280 | 0.390 | 0.480 | 0.780 | 2.5 | 0 | 2.15 KB | 4280/85/2 | 1.69 | 10.1 | +0.3 | OK |
| set | StackExchange.Redis | 140,252 | 0.340 | 0.560 | 0.820 | 1.290 | 5.9 | 0 | 383 B | 583/195/2 | 0.81 | 13.8 | +0.6 | OK |
| set | Respire | 184,697 | 0.270 | 0.380 | 0.460 | 0.670 | 2.6 | 0 | 128 B | 257/18/2 | 0.14 | 10.1 | -0.2 | OK |
| incr | StackExchange.Redis | 159,505 | 0.310 | 0.480 | 0.730 | 1.170 | 5.5 | 0 | 495 B | 854/285/2 | 1.00 | 12.4 | +0.2 | OK |
| incr | Respire | 194,689 | 0.270 | 0.330 | 0.380 | 0.550 | 2.7 | 0 | 128 B | 270/18/2 | 0.15 | 9.0 | +0.7 | OK |
| hash | StackExchange.Redis | 59,651 | 0.810 | 1.190 | 1.740 | 2.440 | 5.6 | 0 | 3.99 KB | 2766/688/2 | 2.59 | 33.1 | -0.9 | OK |
| hash | Respire | 88,761 | 0.550 | 0.740 | 0.910 | 1.240 | 3.1 | 0 | 2.30 KB | 2292/25/1 | 1.10 | 20.4 | +0.1 | OK |
| list | StackExchange.Redis | 59,314 | 0.820 | 1.190 | 1.630 | 2.180 | 4.8 | 0 | 3.94 KB | 2712/905/2 | 1.76 | 32.8 | -+0.0 | OK |
| list | Respire | 84,701 | 0.580 | 0.770 | 0.920 | 1.200 | 3.2 | 0 | 2.30 KB | 2189/152/2 | 0.89 | 21.0 | +0.6 | OK |
| mixed | StackExchange.Redis | 117,623 | 0.420 | 0.660 | 1.000 | 1.490 | 4.5 | 0 | 2.68 KB | 3648/1205/2 | 2.31 | 16.8 | -0.5 | OK |
| mixed | Respire | 181,013 | 0.270 | 0.390 | 0.480 | 0.820 | 2.7 | 0 | 1.61 KB | 3248/81/2 | 1.48 | 10.2 | -0.3 | OK |

## Notes

- Latency is per operation as issued by the workload; composite scenarios (hash, list) time the pair as one operation.
- Alloc/op and CPU µs/op include a harness overhead that is identical for both clients.
- Drift compares the last-third average of per-second throughput samples against the first third; a sustained negative value indicates degradation over the run.
