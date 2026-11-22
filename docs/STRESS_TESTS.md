---
title: Stress tests
description: Latest sustained Respire and StackExchange.Redis stress-test results.
---

import ComparisonBarChart from '@site/src/components/ComparisonBarChart';

# Stress tests

:::info Automated results
Generated 2026-11-22 10:13 UTC from commit `e50626e1b416`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/45007468916) for logs, JSON results, and downloadable artifacts.
:::

3 min measured (+10s warmup) per scenario/client pass, 50 concurrent workers, 1,024 B values, .NET 11.0.0, Ubuntu 26.04.1 LTS.

## Throughput

<ComparisonBarChart
  title="Sustained throughput"
  description="Operations per second. Longer bars are better."
  format="integer"
  data={[{"label":"ping","other":231337,"respire":350146},{"label":"get","other":164998,"respire":283948},{"label":"set","other":183784,"respire":250262},{"label":"incr","other":204155,"respire":317857},{"label":"hash","other":81440,"respire":133996},{"label":"list","other":82030,"respire":119142},{"label":"mixed","other":169388,"respire":251928}]}
/>
| Scenario | StackExchange.Redis ops/s | Respire ops/s | Respire / StackExchange |
|---|---:|---:|---:|
| ping | 231,337 | 350,146 | 1.51x |
| get | 164,998 | 283,948 | 1.72x |
| set | 183,784 | 250,262 | 1.36x |
| incr | 204,155 | 317,857 | 1.56x |
| hash | 81,440 | 133,996 | 1.65x |
| list | 82,030 | 119,142 | 1.45x |
| mixed | 169,388 | 251,928 | 1.49x |

A ratio above 1.00x means Respire sustained more operations per second.

## Details

| Scenario | Client | Ops/s | p50 ms | p95 ms | p99 ms | p99.9 ms | Max ms | Errors | Alloc/op | Gen0/1/2 | GC pause s | CPU µs/op | Drift % | Status |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| ping | StackExchange.Redis | 231,337 | 0.180 | 0.490 | 0.630 | 1.500 | 5.0 | 0 | 502 B | 1254/628/2 | 1.40 | 6.2 | +1.4 | OK |
| ping | Respire | 350,146 | 0.130 | 0.300 | 0.510 | 0.650 | 4.3 | 0 | 184 B | 698/18/1 | 0.32 | 4.6 | +7.6 | OK |
| get | StackExchange.Redis | 164,998 | 0.270 | 0.610 | 0.780 | 1.450 | 6.3 | 0 | 3.61 KB | 6768/1758/2 | 2.84 | 9.0 | -9.4 | OK |
| get | Respire | 283,948 | 0.160 | 0.350 | 0.540 | 0.730 | 6.2 | 0 | 2.20 KB | 6965/19/2 | 2.19 | 5.8 | -0.1 | OK |
| set | StackExchange.Redis | 183,784 | 0.240 | 0.560 | 0.680 | 1.370 | 6.0 | 0 | 510 B | 1014/336/2 | 0.98 | 8.2 | -4.0 | OK |
| set | Respire | 250,262 | 0.170 | 0.460 | 0.600 | 0.850 | 6.1 | 0 | 184 B | 500/18/2 | 0.24 | 5.9 | +2.7 | OK |
| incr | StackExchange.Redis | 204,155 | 0.210 | 0.520 | 0.630 | 1.530 | 6.3 | 0 | 551 B | 1216/407/2 | 1.05 | 7.4 | +0.2 | OK |
| incr | Respire | 317,857 | 0.140 | 0.330 | 0.520 | 0.670 | 4.8 | 0 | 184 B | 635/18/2 | 0.30 | 5.3 | -5.3 | OK |
| hash | StackExchange.Redis | 81,440 | 0.590 | 0.920 | 1.310 | 2.300 | 10.6 | 0 | 3.97 KB | 3711/947/2 | 2.22 | 17.8 | -1.1 | OK |
| hash | Respire | 133,996 | 0.330 | 0.700 | 0.810 | 1.170 | 5.8 | 0 | 2.28 KB | 3435/364/2 | 1.33 | 10.7 | -1.5 | OK |
| list | StackExchange.Redis | 82,030 | 0.600 | 0.900 | 1.270 | 2.140 | 8.6 | 0 | 3.93 KB | 3691/925/1 | 2.17 | 17.8 | -3.6 | OK |
| list | Respire | 119,142 | 0.380 | 0.720 | 0.860 | 1.310 | 6.6 | 0 | 2.31 KB | 3090/468/1 | 1.17 | 12.8 | +6.8 | OK |
| mixed | StackExchange.Redis | 169,388 | 0.260 | 0.590 | 0.750 | 1.280 | 8.1 | 0 | 2.70 KB | 5261/1557/2 | 2.57 | 9.3 | -7.3 | OK |
| mixed | Respire | 251,928 | 0.180 | 0.400 | 0.600 | 0.840 | 6.3 | 0 | 1.62 KB | 4544/426/2 | 2.02 | 6.3 | -2.7 | OK |

## Notes

- Latency is per operation as issued by the workload; composite scenarios (hash, list) time the pair as one operation.
- Alloc/op and CPU µs/op include a harness overhead that is identical for both clients.
- Drift compares the last-third average of per-second throughput samples against the first third; a sustained negative value indicates degradation over the run.
