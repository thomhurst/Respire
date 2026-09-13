---
title: Stress tests
description: Latest sustained Respire and StackExchange.Redis stress-test results.
---

import ComparisonBarChart from '@site/src/components/ComparisonBarChart';

# Stress tests

:::info Automated results
Generated 2026-09-13 03:02 UTC from commit `165ee2ece00b`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/34732662525) for logs, JSON results, and downloadable artifacts.
:::

3 min measured (+10s warmup) per scenario/client pass, 50 concurrent workers, 1,024 B values, .NET 10.0.12, Ubuntu 24.04.5 LTS.

## Throughput

<ComparisonBarChart
  title="Sustained throughput"
  description="Operations per second. Longer bars are better."
  format="integer"
  data={[{"label":"ping","other":194094,"respire":236913},{"label":"get","other":122634,"respire":185072},{"label":"set","other":140698,"respire":185228},{"label":"incr","other":160517,"respire":204901},{"label":"hash","other":59982,"respire":89501},{"label":"list","other":59221,"respire":85270},{"label":"mixed","other":118351,"respire":184264}]}
/>
| Scenario | StackExchange.Redis ops/s | Respire ops/s | Respire / StackExchange |
|---|---:|---:|---:|
| ping | 194,094 | 236,913 | 1.22x |
| get | 122,634 | 185,072 | 1.51x |
| set | 140,698 | 185,228 | 1.32x |
| incr | 160,517 | 204,901 | 1.28x |
| hash | 59,982 | 89,501 | 1.49x |
| list | 59,221 | 85,270 | 1.44x |
| mixed | 118,351 | 184,264 | 1.56x |

A ratio above 1.00x means Respire sustained more operations per second.

## Details

| Scenario | Client | Ops/s | p50 ms | p95 ms | p99 ms | p99.9 ms | Max ms | Errors | Alloc/op | Gen0/1/2 | GC pause s | CPU µs/op | Drift % | Status |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| ping | StackExchange.Redis | 194,094 | 0.250 | 0.400 | 0.670 | 1.130 | 9.6 | 0 | 356 B | 748/375/2 | 0.76 | 10.0 | +2.6 | OK |
| ping | Respire | 236,913 | 0.220 | 0.280 | 0.340 | 0.480 | 3.7 | 0 | 121 B | 311/18/1 | 0.11 | 7.9 | +1.0 | OK |
| get | StackExchange.Redis | 122,634 | 0.400 | 0.640 | 0.950 | 1.300 | 3.7 | 0 | 3.55 KB | 5069/2525/2 | 2.55 | 16.2 | -0.2 | OK |
| get | Respire | 185,072 | 0.270 | 0.380 | 0.460 | 0.680 | 3.2 | 0 | 2.15 KB | 4428/103/2 | 1.33 | 10.0 | +1.2 | OK |
| set | StackExchange.Redis | 140,698 | 0.340 | 0.560 | 0.810 | 1.280 | 4.1 | 0 | 383 B | 585/196/2 | 0.68 | 13.8 | +0.0 | OK |
| set | Respire | 185,228 | 0.270 | 0.380 | 0.460 | 0.630 | 2.8 | 0 | 129 B | 260/19/2 | 0.11 | 10.3 | +0.3 | OK |
| incr | StackExchange.Redis | 160,517 | 0.300 | 0.470 | 0.720 | 1.160 | 4.3 | 0 | 495 B | 861/289/2 | 0.88 | 12.3 | +0.3 | OK |
| incr | Respire | 204,901 | 0.250 | 0.320 | 0.380 | 0.520 | 2.7 | 0 | 129 B | 288/19/2 | 0.12 | 8.9 | -2.0 | OK |
| hash | StackExchange.Redis | 59,982 | 0.810 | 1.180 | 1.640 | 2.190 | 6.2 | 0 | 3.99 KB | 2781/868/2 | 1.90 | 33.0 | +0.0 | OK |
| hash | Respire | 89,501 | 0.550 | 0.730 | 0.870 | 1.140 | 3.7 | 0 | 2.30 KB | 2316/93/2 | 0.83 | 20.6 | +0.4 | OK |
| list | StackExchange.Redis | 59,221 | 0.820 | 1.190 | 1.640 | 2.160 | 4.0 | 0 | 3.94 KB | 2710/781/1 | 1.88 | 33.1 | +0.2 | OK |
| list | Respire | 85,270 | 0.580 | 0.770 | 0.900 | 1.160 | 3.3 | 0 | 2.30 KB | 2206/238/1 | 0.71 | 21.2 | +0.0 | OK |
| mixed | StackExchange.Redis | 118,351 | 0.410 | 0.660 | 0.990 | 1.440 | 6.6 | 0 | 2.68 KB | 3672/1119/2 | 2.16 | 16.8 | +0.6 | OK |
| mixed | Respire | 184,264 | 0.270 | 0.380 | 0.470 | 0.730 | 2.8 | 0 | 1.61 KB | 3309/105/2 | 1.17 | 10.2 | +1.2 | OK |

## Notes

- Latency is per operation as issued by the workload; composite scenarios (hash, list) time the pair as one operation.
- Alloc/op and CPU µs/op include a harness overhead that is identical for both clients.
- Drift compares the last-third average of per-second throughput samples against the first third; a sustained negative value indicates degradation over the run.
