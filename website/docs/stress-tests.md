---
title: Stress tests
description: Latest sustained Respire and StackExchange.Redis stress-test results.
---

import ComparisonBarChart from '@site/src/components/ComparisonBarChart';

# Stress tests

:::info Automated results
Generated 2026-10-04 03:37 UTC from commit `c7a794a395cb`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/37172366551) for logs, JSON results, and downloadable artifacts.
:::

3 min measured (+10s warmup) per scenario/client pass, 50 concurrent workers, 1,024 B values, .NET 10.0.12, Ubuntu 24.04.5 LTS.

## Throughput

<ComparisonBarChart
  title="Sustained throughput"
  description="Operations per second. Longer bars are better."
  format="integer"
  data={[{"label":"ping","other":169388,"respire":225245},{"label":"get","other":119006,"respire":170513},{"label":"set","other":132553,"respire":179179},{"label":"incr","other":152406,"respire":192645},{"label":"hash","other":57221,"respire":86192},{"label":"list","other":57320,"respire":82062},{"label":"mixed","other":114834,"respire":171335}]}
/>
| Scenario | StackExchange.Redis ops/s | Respire ops/s | Respire / StackExchange |
|---|---:|---:|---:|
| ping | 169,388 | 225,245 | 1.33x |
| get | 119,006 | 170,513 | 1.43x |
| set | 132,553 | 179,179 | 1.35x |
| incr | 152,406 | 192,645 | 1.26x |
| hash | 57,221 | 86,192 | 1.51x |
| list | 57,320 | 82,062 | 1.43x |
| mixed | 114,834 | 171,335 | 1.49x |

A ratio above 1.00x means Respire sustained more operations per second.

## Details

| Scenario | Client | Ops/s | p50 ms | p95 ms | p99 ms | p99.9 ms | Max ms | Errors | Alloc/op | Gen0/1/2 | GC pause s | CPU µs/op | Drift % | Status |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| ping | StackExchange.Redis | 169,388 | 0.260 | 0.630 | 0.960 | 1.370 | 5.8 | 0 | 355 B | 652/326/2 | 0.75 | 10.3 | +8.2 | OK |
| ping | Respire | 225,245 | 0.230 | 0.300 | 0.350 | 0.560 | 4.5 | 0 | 121 B | 296/18/1 | 0.12 | 7.9 | -+0.0 | OK |
| get | StackExchange.Redis | 119,006 | 0.410 | 0.670 | 1.010 | 1.370 | 6.9 | 0 | 3.55 KB | 4914/1640/2 | 2.63 | 16.5 | +1.5 | OK |
| get | Respire | 170,513 | 0.300 | 0.390 | 0.490 | 0.800 | 5.3 | 0 | 2.15 KB | 4076/80/2 | 1.60 | 10.1 | -4.9 | OK |
| set | StackExchange.Redis | 132,553 | 0.350 | 0.650 | 1.020 | 1.430 | 8.1 | 0 | 383 B | 551/185/2 | 0.81 | 13.9 | +0.2 | OK |
| set | Respire | 179,179 | 0.280 | 0.400 | 0.480 | 0.680 | 2.8 | 0 | 129 B | 253/18/2 | 0.13 | 10.2 | +0.2 | OK |
| incr | StackExchange.Redis | 152,406 | 0.310 | 0.530 | 0.880 | 1.270 | 4.5 | 0 | 494 B | 816/273/2 | 0.95 | 12.5 | -4.2 | OK |
| incr | Respire | 192,645 | 0.270 | 0.330 | 0.390 | 0.570 | 2.9 | 0 | 129 B | 271/18/2 | 0.14 | 8.7 | -0.3 | OK |
| hash | StackExchange.Redis | 57,221 | 0.840 | 1.340 | 1.840 | 2.520 | 5.0 | 0 | 3.99 KB | 2646/672/2 | 2.42 | 33.4 | -3.3 | OK |
| hash | Respire | 86,192 | 0.570 | 0.760 | 0.930 | 1.240 | 6.9 | 0 | 2.30 KB | 2229/85/2 | 1.03 | 20.6 | +0.3 | OK |
| list | StackExchange.Redis | 57,320 | 0.840 | 1.310 | 1.800 | 2.410 | 10.2 | 0 | 3.94 KB | 2617/655/1 | 2.26 | 33.2 | -1.7 | OK |
| list | Respire | 82,062 | 0.600 | 0.790 | 0.960 | 1.290 | 3.8 | 0 | 2.30 KB | 2122/45/1 | 0.99 | 21.1 | +0.2 | OK |
| mixed | StackExchange.Redis | 114,834 | 0.420 | 0.690 | 1.090 | 1.600 | 5.0 | 0 | 2.68 KB | 3558/1013/2 | 2.56 | 17.0 | -1.8 | OK |
| mixed | Respire | 171,335 | 0.290 | 0.400 | 0.500 | 0.850 | 5.2 | 0 | 1.61 KB | 3067/84/2 | 1.39 | 9.9 | +1.2 | OK |

## Notes

- Latency is per operation as issued by the workload; composite scenarios (hash, list) time the pair as one operation.
- Alloc/op and CPU µs/op include a harness overhead that is identical for both clients.
- Drift compares the last-third average of per-second throughput samples against the first third; a sustained negative value indicates degradation over the run.
