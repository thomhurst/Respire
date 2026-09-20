---
title: Stress tests
description: Latest sustained Respire and StackExchange.Redis stress-test results.
---

import ComparisonBarChart from '@site/src/components/ComparisonBarChart';

# Stress tests

:::info Automated results
Generated 2026-09-20 03:02 UTC from commit `c894dd29f13b`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/35483585082) for logs, JSON results, and downloadable artifacts.
:::

3 min measured (+10s warmup) per scenario/client pass, 50 concurrent workers, 1,024 B values, .NET 10.0.12, Ubuntu 24.04.5 LTS.

## Throughput

<ComparisonBarChart
  title="Sustained throughput"
  description="Operations per second. Longer bars are better."
  format="integer"
  data={[{"label":"ping","other":391459,"respire":567957},{"label":"get","other":236099,"respire":379282},{"label":"set","other":313404,"respire":458860},{"label":"incr","other":339124,"respire":478176},{"label":"hash","other":125048,"respire":204861},{"label":"list","other":123367,"respire":194971},{"label":"mixed","other":232888,"respire":388244}]}
/>
| Scenario | StackExchange.Redis ops/s | Respire ops/s | Respire / StackExchange |
|---|---:|---:|---:|
| ping | 391,459 | 567,957 | 1.45x |
| get | 236,099 | 379,282 | 1.61x |
| set | 313,404 | 458,860 | 1.46x |
| incr | 339,124 | 478,176 | 1.41x |
| hash | 125,048 | 204,861 | 1.64x |
| list | 123,367 | 194,971 | 1.58x |
| mixed | 232,888 | 388,244 | 1.67x |

A ratio above 1.00x means Respire sustained more operations per second.

## Details

| Scenario | Client | Ops/s | p50 ms | p95 ms | p99 ms | p99.9 ms | Max ms | Errors | Alloc/op | Gen0/1/2 | GC pause s | CPU µs/op | Drift % | Status |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| ping | StackExchange.Redis | 391,459 | 0.110 | 0.270 | 0.360 | 0.500 | 39.4 | 0 | 358 B | 303/152/2 | 4.45 | 4.7 | +4.1 | OK |
| ping | Respire | 567,957 | 0.090 | 0.130 | 0.160 | 0.220 | 16.3 | 0 | 121 B | 150/14/2 | 0.07 | 3.3 | -4.7 | OK |
| get | StackExchange.Redis | 236,099 | 0.200 | 0.390 | 0.480 | 1.760 | 8.8 | 0 | 3.55 KB | 1929/965/2 | 2.51 | 7.5 | +14.8 | OK |
| get | Respire | 379,282 | 0.130 | 0.200 | 0.230 | 0.490 | 5.0 | 0 | 2.15 KB | 1812/47/2 | 0.66 | 4.8 | -6.6 | OK |
| set | StackExchange.Redis | 313,404 | 0.150 | 0.270 | 0.400 | 0.560 | 33.3 | 0 | 384 B | 262/131/2 | 3.82 | 6.1 | -0.9 | OK |
| set | Respire | 458,860 | 0.110 | 0.160 | 0.190 | 0.260 | 2.1 | 0 | 129 B | 129/12/1 | 0.05 | 4.1 | +1.6 | OK |
| incr | StackExchange.Redis | 339,124 | 0.130 | 0.300 | 0.390 | 0.530 | 35.8 | 0 | 494 B | 362/182/2 | 4.52 | 5.3 | -4.2 | OK |
| incr | Respire | 478,176 | 0.100 | 0.160 | 0.180 | 0.240 | 12.4 | 0 | 129 B | 134/13/2 | 0.06 | 3.6 | -5.8 | OK |
| hash | StackExchange.Redis | 125,048 | 0.380 | 0.580 | 0.750 | 4.830 | 10.4 | 0 | 3.99 KB | 1154/578/2 | 3.05 | 15.2 | +7.9 | OK |
| hash | Respire | 204,861 | 0.240 | 0.350 | 0.400 | 0.640 | 9.0 | 0 | 2.30 KB | 1058/139/2 | 0.39 | 8.7 | -1.2 | OK |
| list | StackExchange.Redis | 123,367 | 0.390 | 0.590 | 0.770 | 3.550 | 6.7 | 0 | 3.94 KB | 1117/559/1 | 2.24 | 14.1 | -1.5 | OK |
| list | Respire | 194,971 | 0.250 | 0.360 | 0.410 | 0.620 | 7.6 | 0 | 2.30 KB | 1006/125/1 | 0.33 | 8.6 | -0.1 | OK |
| mixed | StackExchange.Redis | 232,888 | 0.200 | 0.400 | 0.500 | 0.970 | 15.2 | 0 | 2.68 KB | 1428/715/2 | 2.28 | 7.5 | -10.1 | OK |
| mixed | Respire | 388,244 | 0.130 | 0.190 | 0.230 | 0.460 | 7.4 | 0 | 1.61 KB | 1389/71/2 | 0.53 | 4.5 | +0.8 | OK |

## Notes

- Latency is per operation as issued by the workload; composite scenarios (hash, list) time the pair as one operation.
- Alloc/op and CPU µs/op include a harness overhead that is identical for both clients.
- Drift compares the last-third average of per-second throughput samples against the first third; a sustained negative value indicates degradation over the run.
