---
title: Stress tests
description: Latest sustained Respire and StackExchange.Redis stress-test results.
---

import ComparisonBarChart from '@site/src/components/ComparisonBarChart';

# Stress tests

:::info Automated results
Generated 2026-08-23 03:16 UTC from commit `a166cd60050b`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/32613078452) for logs, JSON results, and downloadable artifacts.
:::

3 min measured (+10s warmup) per scenario/client pass, 50 concurrent workers, 1,024 B values, .NET 10.0.11, Ubuntu 24.04.4 LTS.

## Throughput

<ComparisonBarChart
  title="Sustained throughput"
  description="Operations per second. Longer bars are better."
  format="integer"
  data={[{"label":"ping","other":220128,"respire":295300},{"label":"get","other":137903,"respire":220810},{"label":"set","other":159826,"respire":223342},{"label":"incr","other":181756,"respire":258509},{"label":"hash","other":68585,"respire":106913},{"label":"list","other":67494,"respire":102840},{"label":"mixed","other":132584,"respire":219661}]}
/>
| Scenario | StackExchange.Redis ops/s | Respire ops/s | Respire / StackExchange |
|---|---:|---:|---:|
| ping | 220,128 | 295,300 | 1.34x |
| get | 137,903 | 220,810 | 1.60x |
| set | 159,826 | 223,342 | 1.40x |
| incr | 181,756 | 258,509 | 1.42x |
| hash | 68,585 | 106,913 | 1.56x |
| list | 67,494 | 102,840 | 1.52x |
| mixed | 132,584 | 219,661 | 1.66x |

A ratio above 1.00x means Respire sustained more operations per second.

## Details

| Scenario | Client | Ops/s | p50 ms | p95 ms | p99 ms | p99.9 ms | Max ms | Errors | Alloc/op | Gen0/1/2 | GC pause s | CPU µs/op | Drift % | Status |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| ping | StackExchange.Redis | 220,128 | 0.220 | 0.370 | 0.740 | 1.090 | 8.5 | 0 | 356 B | 849/425/2 | 1.02 | 8.8 | +6.9 | OK |
| ping | Respire | 295,300 | 0.170 | 0.240 | 0.280 | 0.420 | 3.5 | 0 | 121 B | 387/19/2 | 0.16 | 6.6 | +0.1 | OK |
| get | StackExchange.Redis | 137,903 | 0.350 | 0.570 | 0.880 | 1.240 | 5.7 | 0 | 3.55 KB | 5714/2105/2 | 2.86 | 14.6 | -0.1 | OK |
| get | Respire | 220,810 | 0.220 | 0.320 | 0.410 | 0.670 | 2.4 | 0 | 2.15 KB | 5283/103/2 | 1.80 | 8.6 | +0.8 | OK |
| set | StackExchange.Redis | 159,826 | 0.300 | 0.490 | 0.780 | 1.160 | 6.5 | 0 | 384 B | 666/224/2 | 0.94 | 12.3 | -1.8 | OK |
| set | Respire | 223,342 | 0.220 | 0.320 | 0.380 | 0.560 | 3.1 | 0 | 129 B | 314/18/2 | 0.15 | 8.6 | -1.5 | OK |
| incr | StackExchange.Redis | 181,756 | 0.260 | 0.470 | 0.840 | 1.120 | 4.4 | 0 | 494 B | 971/325/2 | 1.04 | 10.6 | +0.3 | OK |
| incr | Respire | 258,509 | 0.190 | 0.270 | 0.320 | 0.460 | 3.2 | 0 | 129 B | 362/18/2 | 0.17 | 7.3 | -0.3 | OK |
| hash | StackExchange.Redis | 68,585 | 0.700 | 1.090 | 1.490 | 2.130 | 5.4 | 0 | 3.99 KB | 3184/1056/2 | 2.35 | 28.9 | -0.1 | OK |
| hash | Respire | 106,913 | 0.460 | 0.620 | 0.780 | 1.060 | 3.4 | 0 | 2.30 KB | 2765/22/1 | 1.13 | 17.7 | -0.4 | OK |
| list | StackExchange.Redis | 67,494 | 0.710 | 1.130 | 1.510 | 2.050 | 4.6 | 0 | 3.94 KB | 3088/1030/2 | 1.99 | 28.7 | -0.8 | OK |
| list | Respire | 102,840 | 0.480 | 0.650 | 0.800 | 1.070 | 3.0 | 0 | 2.30 KB | 2660/221/2 | 1.03 | 18.1 | +0.1 | OK |
| mixed | StackExchange.Redis | 132,584 | 0.360 | 0.620 | 1.000 | 1.430 | 3.8 | 0 | 2.68 KB | 4111/1255/2 | 2.59 | 14.7 | -3.1 | OK |
| mixed | Respire | 219,661 | 0.230 | 0.330 | 0.410 | 0.710 | 3.0 | 0 | 1.61 KB | 3934/109/2 | 1.55 | 8.6 | +1.1 | OK |

## Notes

- Latency is per operation as issued by the workload; composite scenarios (hash, list) time the pair as one operation.
- Alloc/op and CPU µs/op include a harness overhead that is identical for both clients.
- Drift compares the last-third average of per-second throughput samples against the first third; a sustained negative value indicates degradation over the run.
