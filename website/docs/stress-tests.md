---
title: Stress tests
description: Latest sustained Respire and StackExchange.Redis stress-test results.
---

import ComparisonBarChart from '@site/src/components/ComparisonBarChart';

# Stress tests

:::info Automated results
Generated 2026-10-03 18:04 UTC from commit `eb71c9fb38c1`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/37140036336) for logs, JSON results, and downloadable artifacts.
:::

3 min measured (+10s warmup) per scenario/client pass, 50 concurrent workers, 1,024 B values, .NET 10.0.12, Ubuntu 24.04.5 LTS.

## Throughput

<ComparisonBarChart
  title="Sustained throughput"
  description="Operations per second. Longer bars are better."
  format="integer"
  data={[{"label":"ping","other":194801,"respire":232207},{"label":"get","other":122092,"respire":181493},{"label":"set","other":140548,"respire":185360},{"label":"incr","other":160675,"respire":198746},{"label":"hash","other":59711,"respire":89001},{"label":"list","other":59385,"respire":84950},{"label":"mixed","other":118489,"respire":182891}]}
/>
| Scenario | StackExchange.Redis ops/s | Respire ops/s | Respire / StackExchange |
|---|---:|---:|---:|
| ping | 194,801 | 232,207 | 1.19x |
| get | 122,092 | 181,493 | 1.49x |
| set | 140,548 | 185,360 | 1.32x |
| incr | 160,675 | 198,746 | 1.24x |
| hash | 59,711 | 89,001 | 1.49x |
| list | 59,385 | 84,950 | 1.43x |
| mixed | 118,489 | 182,891 | 1.54x |

A ratio above 1.00x means Respire sustained more operations per second.

## Details

| Scenario | Client | Ops/s | p50 ms | p95 ms | p99 ms | p99.9 ms | Max ms | Errors | Alloc/op | Gen0/1/2 | GC pause s | CPU µs/op | Drift % | Status |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| ping | StackExchange.Redis | 194,801 | 0.240 | 0.400 | 0.680 | 1.140 | 4.6 | 0 | 356 B | 751/376/2 | 0.80 | 10.0 | +4.4 | OK |
| ping | Respire | 232,207 | 0.220 | 0.290 | 0.340 | 0.500 | 3.3 | 0 | 121 B | 306/19/2 | 0.12 | 8.0 | -0.2 | OK |
| get | StackExchange.Redis | 122,092 | 0.400 | 0.640 | 0.960 | 1.350 | 6.5 | 0 | 3.55 KB | 5048/1658/2 | 2.62 | 16.3 | -0.1 | OK |
| get | Respire | 181,493 | 0.270 | 0.380 | 0.480 | 0.760 | 3.0 | 0 | 2.15 KB | 4343/86/2 | 1.64 | 10.2 | +0.6 | OK |
| set | StackExchange.Redis | 140,548 | 0.340 | 0.560 | 0.810 | 1.270 | 4.3 | 0 | 383 B | 584/196/2 | 0.74 | 13.9 | -0.4 | OK |
| set | Respire | 185,360 | 0.270 | 0.380 | 0.460 | 0.650 | 2.4 | 0 | 129 B | 261/18/2 | 0.13 | 10.2 | -0.2 | OK |
| incr | StackExchange.Redis | 160,675 | 0.300 | 0.470 | 0.720 | 1.160 | 5.7 | 0 | 495 B | 860/288/2 | 0.92 | 12.3 | -0.4 | OK |
| incr | Respire | 198,746 | 0.260 | 0.330 | 0.390 | 0.650 | 2.9 | 0 | 129 B | 280/18/2 | 0.14 | 9.0 | +0.2 | OK |
| hash | StackExchange.Redis | 59,711 | 0.810 | 1.190 | 1.710 | 2.320 | 5.2 | 0 | 3.99 KB | 2768/695/2 | 2.39 | 33.1 | -+0.0 | OK |
| hash | Respire | 89,001 | 0.550 | 0.740 | 0.910 | 1.210 | 2.9 | 0 | 2.30 KB | 2302/102/1 | 1.04 | 20.7 | -0.3 | OK |
| list | StackExchange.Redis | 59,385 | 0.820 | 1.190 | 1.620 | 2.150 | 4.6 | 0 | 3.94 KB | 2716/907/2 | 1.74 | 32.9 | +0.1 | OK |
| list | Respire | 84,950 | 0.580 | 0.770 | 0.920 | 1.200 | 3.6 | 0 | 2.30 KB | 2199/122/2 | 0.86 | 21.2 | -0.3 | OK |
| mixed | StackExchange.Redis | 118,489 | 0.410 | 0.660 | 0.980 | 1.460 | 5.4 | 0 | 2.68 KB | 3674/1207/2 | 2.20 | 16.7 | +0.3 | OK |
| mixed | Respire | 182,891 | 0.270 | 0.390 | 0.480 | 0.790 | 4.8 | 0 | 1.61 KB | 3279/84/2 | 1.41 | 10.2 | +0.0 | OK |

## Notes

- Latency is per operation as issued by the workload; composite scenarios (hash, list) time the pair as one operation.
- Alloc/op and CPU µs/op include a harness overhead that is identical for both clients.
- Drift compares the last-third average of per-second throughput samples against the first third; a sustained negative value indicates degradation over the run.
