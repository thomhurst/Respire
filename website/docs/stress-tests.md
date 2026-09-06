---
title: Stress tests
description: Latest sustained Respire and StackExchange.Redis stress-test results.
---

import ComparisonBarChart from '@site/src/components/ComparisonBarChart';

# Stress tests

:::info Automated results
Generated 2026-09-06 03:02 UTC from commit `cd6517e4c782`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/34006082609) for logs, JSON results, and downloadable artifacts.
:::

3 min measured (+10s warmup) per scenario/client pass, 50 concurrent workers, 1,024 B values, .NET 10.0.11, Ubuntu 24.04.4 LTS.

## Throughput

<ComparisonBarChart
  title="Sustained throughput"
  description="Operations per second. Longer bars are better."
  format="integer"
  data={[{"label":"ping","other":306842,"respire":405921},{"label":"get","other":175483,"respire":286766},{"label":"set","other":205352,"respire":309042},{"label":"incr","other":239633,"respire":352388},{"label":"hash","other":88813,"respire":147442},{"label":"list","other":88501,"respire":139807},{"label":"mixed","other":172354,"respire":290758}]}
/>
| Scenario | StackExchange.Redis ops/s | Respire ops/s | Respire / StackExchange |
|---|---:|---:|---:|
| ping | 306,842 | 405,921 | 1.32x |
| get | 175,483 | 286,766 | 1.63x |
| set | 205,352 | 309,042 | 1.50x |
| incr | 239,633 | 352,388 | 1.47x |
| hash | 88,813 | 147,442 | 1.66x |
| list | 88,501 | 139,807 | 1.58x |
| mixed | 172,354 | 290,758 | 1.69x |

A ratio above 1.00x means Respire sustained more operations per second.

## Details

| Scenario | Client | Ops/s | p50 ms | p95 ms | p99 ms | p99.9 ms | Max ms | Errors | Alloc/op | Gen0/1/2 | GC pause s | CPU µs/op | Drift % | Status |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| ping | StackExchange.Redis | 306,842 | 0.160 | 0.260 | 0.420 | 0.680 | 8.6 | 0 | 357 B | 791/396/2 | 1.50 | 6.4 | +2.0 | OK |
| ping | Respire | 405,921 | 0.120 | 0.180 | 0.210 | 0.290 | 5.1 | 0 | 121 B | 356/18/2 | 0.13 | 4.8 | +0.8 | OK |
| get | StackExchange.Redis | 175,483 | 0.280 | 0.460 | 0.710 | 1.520 | 5.4 | 0 | 3.55 KB | 4838/2419/2 | 3.55 | 11.5 | -+0.0 | OK |
| get | Respire | 286,766 | 0.170 | 0.260 | 0.310 | 0.540 | 2.2 | 0 | 2.15 KB | 4576/114/2 | 1.40 | 6.7 | -0.2 | OK |
| set | StackExchange.Redis | 205,352 | 0.240 | 0.380 | 0.530 | 0.820 | 8.2 | 0 | 386 B | 572/270/2 | 1.67 | 9.7 | -1.2 | OK |
| set | Respire | 309,042 | 0.160 | 0.230 | 0.280 | 0.390 | 4.9 | 0 | 129 B | 290/19/2 | 0.10 | 6.3 | +0.9 | OK |
| incr | StackExchange.Redis | 239,633 | 0.200 | 0.330 | 0.480 | 0.760 | 5.3 | 0 | 495 B | 856/429/2 | 1.76 | 8.4 | -0.3 | OK |
| incr | Respire | 352,388 | 0.140 | 0.210 | 0.240 | 0.320 | 2.9 | 0 | 129 B | 330/18/2 | 0.11 | 5.5 | -0.6 | OK |
| hash | StackExchange.Redis | 88,813 | 0.550 | 0.790 | 1.070 | 2.690 | 5.4 | 0 | 3.99 KB | 2752/1190/2 | 2.96 | 22.8 | -0.5 | OK |
| hash | Respire | 147,442 | 0.330 | 0.460 | 0.570 | 0.790 | 3.3 | 0 | 2.30 KB | 2549/276/1 | 0.84 | 13.0 | -0.6 | OK |
| list | StackExchange.Redis | 88,501 | 0.550 | 0.790 | 1.080 | 2.610 | 4.7 | 0 | 3.95 KB | 2707/1354/2 | 3.06 | 22.5 | -0.1 | OK |
| list | Respire | 139,807 | 0.350 | 0.490 | 0.600 | 0.820 | 2.9 | 0 | 2.30 KB | 2418/265/2 | 0.80 | 13.5 | +0.0 | OK |
| mixed | StackExchange.Redis | 172,354 | 0.290 | 0.460 | 0.690 | 1.870 | 6.6 | 0 | 2.68 KB | 3574/1721/2 | 3.31 | 11.7 | -0.5 | OK |
| mixed | Respire | 290,758 | 0.170 | 0.260 | 0.310 | 0.580 | 3.4 | 0 | 1.61 KB | 3482/160/2 | 1.26 | 6.6 | -1.0 | OK |

## Notes

- Latency is per operation as issued by the workload; composite scenarios (hash, list) time the pair as one operation.
- Alloc/op and CPU µs/op include a harness overhead that is identical for both clients.
- Drift compares the last-third average of per-second throughput samples against the first third; a sustained negative value indicates degradation over the run.
