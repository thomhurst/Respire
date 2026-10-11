---
title: Stress tests
description: Latest sustained Respire and StackExchange.Redis stress-test results.
---

import ComparisonBarChart from '@site/src/components/ComparisonBarChart';

# Stress tests

:::info Automated results
Generated 2026-10-11 03:12 UTC from commit `5c324a586d18`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/38105179006) for logs, JSON results, and downloadable artifacts.
:::

3 min measured (+10s warmup) per scenario/client pass, 50 concurrent workers, 1,024 B values, .NET 10.0.12, Ubuntu 24.04.5 LTS.

## Throughput

<ComparisonBarChart
  title="Sustained throughput"
  description="Operations per second. Longer bars are better."
  format="integer"
  data={[{"label":"ping","other":365338,"respire":497436},{"label":"get","other":215402,"respire":326245},{"label":"set","other":270081,"respire":380898},{"label":"incr","other":310432,"respire":437865},{"label":"hash","other":115542,"respire":181409},{"label":"list","other":116618,"respire":174926},{"label":"mixed","other":225641,"respire":352754}]}
/>
| Scenario | StackExchange.Redis ops/s | Respire ops/s | Respire / StackExchange |
|---|---:|---:|---:|
| ping | 365,338 | 497,436 | 1.36x |
| get | 215,402 | 326,245 | 1.51x |
| set | 270,081 | 380,898 | 1.41x |
| incr | 310,432 | 437,865 | 1.41x |
| hash | 115,542 | 181,409 | 1.57x |
| list | 116,618 | 174,926 | 1.50x |
| mixed | 225,641 | 352,754 | 1.56x |

A ratio above 1.00x means Respire sustained more operations per second.

## Details

| Scenario | Client | Ops/s | p50 ms | p95 ms | p99 ms | p99.9 ms | Max ms | Errors | Alloc/op | Gen0/1/2 | GC pause s | CPU µs/op | Drift % | Status |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| ping | StackExchange.Redis | 365,338 | 0.120 | 0.280 | 0.420 | 0.560 | 27.0 | 0 | 358 B | 282/142/2 | 3.36 | 5.1 | +11.5 | OK |
| ping | Respire | 497,436 | 0.100 | 0.150 | 0.170 | 0.240 | 4.5 | 0 | 120 B | 130/11/1 | 0.05 | 3.7 | -2.1 | OK |
| get | StackExchange.Redis | 215,402 | 0.220 | 0.440 | 0.550 | 1.990 | 5.5 | 0 | 3.55 KB | 1764/882/2 | 2.20 | 8.5 | +1.1 | OK |
| get | Respire | 326,245 | 0.160 | 0.210 | 0.240 | 0.500 | 2.2 | 0 | 2.15 KB | 1561/256/2 | 0.52 | 5.3 | +4.1 | OK |
| set | StackExchange.Redis | 270,081 | 0.180 | 0.320 | 0.480 | 0.650 | 31.9 | 0 | 384 B | 225/113/2 | 2.93 | 7.1 | +2.7 | OK |
| set | Respire | 380,898 | 0.130 | 0.190 | 0.220 | 0.300 | 2.7 | 0 | 128 B | 106/9/1 | 0.04 | 5.0 | -0.6 | OK |
| incr | StackExchange.Redis | 310,432 | 0.150 | 0.300 | 0.440 | 0.580 | 23.8 | 0 | 495 B | 333/166/2 | 3.63 | 6.1 | +12.4 | OK |
| incr | Respire | 437,865 | 0.120 | 0.170 | 0.200 | 0.260 | 1.8 | 0 | 128 B | 122/10/1 | 0.05 | 4.5 | +1.4 | OK |
| hash | StackExchange.Redis | 115,542 | 0.420 | 0.630 | 0.780 | 4.300 | 13.9 | 0 | 3.99 KB | 1067/534/2 | 2.42 | 17.0 | -3.7 | OK |
| hash | Respire | 181,409 | 0.270 | 0.390 | 0.460 | 0.700 | 2.8 | 0 | 2.30 KB | 936/117/2 | 0.36 | 10.5 | +1.0 | OK |
| list | StackExchange.Redis | 116,618 | 0.410 | 0.620 | 0.790 | 3.720 | 12.0 | 0 | 3.94 KB | 1063/532/2 | 2.12 | 16.2 | -+0.0 | OK |
| list | Respire | 174,926 | 0.280 | 0.400 | 0.470 | 0.700 | 2.9 | 0 | 2.30 KB | 904/116/2 | 0.33 | 10.3 | +0.9 | OK |
| mixed | StackExchange.Redis | 225,641 | 0.210 | 0.390 | 0.530 | 0.960 | 9.1 | 0 | 2.68 KB | 1394/698/2 | 2.09 | 8.5 | +1.9 | OK |
| mixed | Respire | 352,754 | 0.140 | 0.220 | 0.250 | 0.530 | 2.6 | 0 | 1.61 KB | 1265/185/2 | 0.50 | 5.3 | -3.0 | OK |

## Notes

- Latency is per operation as issued by the workload; composite scenarios (hash, list) time the pair as one operation.
- Alloc/op and CPU µs/op include a harness overhead that is identical for both clients.
- Drift compares the last-third average of per-second throughput samples against the first third; a sustained negative value indicates degradation over the run.
