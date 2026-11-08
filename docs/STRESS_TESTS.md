---
title: Stress tests
description: Latest sustained Respire and StackExchange.Redis stress-test results.
---

import ComparisonBarChart from '@site/src/components/ComparisonBarChart';

# Stress tests

:::info Automated results
Generated 2026-11-08 09:24 UTC from commit `ffc57de41b34`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/42463823545) for logs, JSON results, and downloadable artifacts.
:::

3 min measured (+10s warmup) per scenario/client pass, 50 concurrent workers, 1,024 B values, .NET 10.0.14, Ubuntu 24.04.5 LTS.

## Throughput

<ComparisonBarChart
  title="Sustained throughput"
  description="Operations per second. Longer bars are better."
  format="integer"
  data={[{"label":"ping","other":403408,"respire":514665},{"label":"get","other":242211,"respire":379897},{"label":"set","other":296596,"respire":377182},{"label":"incr","other":316577,"respire":432144},{"label":"hash","other":120689,"respire":183641},{"label":"list","other":116019,"respire":163664},{"label":"mixed","other":232071,"respire":351432}]}
/>
| Scenario | StackExchange.Redis ops/s | Respire ops/s | Respire / StackExchange |
|---|---:|---:|---:|
| ping | 403,408 | 514,665 | 1.28x |
| get | 242,211 | 379,897 | 1.57x |
| set | 296,596 | 377,182 | 1.27x |
| incr | 316,577 | 432,144 | 1.37x |
| hash | 120,689 | 183,641 | 1.52x |
| list | 116,019 | 163,664 | 1.41x |
| mixed | 232,071 | 351,432 | 1.51x |

A ratio above 1.00x means Respire sustained more operations per second.

## Details

| Scenario | Client | Ops/s | p50 ms | p95 ms | p99 ms | p99.9 ms | Max ms | Errors | Alloc/op | Gen0/1/2 | GC pause s | CPU µs/op | Drift % | Status |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| ping | StackExchange.Redis | 403,408 | 0.110 | 0.270 | 0.540 | 0.750 | 6.4 | 0 | 356 B | 1553/767/2 | 1.55 | 4.9 | -2.2 | OK |
| ping | Respire | 514,665 | 0.100 | 0.140 | 0.190 | 0.510 | 4.1 | 0 | 120 B | 670/20/2 | 0.24 | 4.1 | +4.2 | OK |
| get | StackExchange.Redis | 242,211 | 0.190 | 0.430 | 0.640 | 0.830 | 3.8 | 0 | 3.55 KB | 10016/2483/2 | 3.17 | 7.8 | +1.5 | OK |
| get | Respire | 379,897 | 0.130 | 0.180 | 0.350 | 0.570 | 2.4 | 0 | 2.15 KB | 9080/31/2 | 2.06 | 4.8 | +1.6 | OK |
| set | StackExchange.Redis | 296,596 | 0.150 | 0.360 | 0.610 | 0.800 | 3.7 | 0 | 379 B | 1216/315/2 | 1.04 | 6.2 | +6.2 | OK |
| set | Respire | 377,182 | 0.130 | 0.190 | 0.230 | 0.470 | 3.7 | 0 | 128 B | 524/18/2 | 0.23 | 5.1 | +2.4 | OK |
| incr | StackExchange.Redis | 316,577 | 0.130 | 0.420 | 0.610 | 0.750 | 4.2 | 0 | 491 B | 1679/522/2 | 1.01 | 5.6 | +3.9 | OK |
| incr | Respire | 432,144 | 0.120 | 0.160 | 0.210 | 0.560 | 2.5 | 0 | 128 B | 599/19/2 | 0.28 | 4.5 | +2.6 | OK |
| hash | StackExchange.Redis | 120,689 | 0.350 | 0.780 | 1.070 | 1.640 | 5.8 | 0 | 3.98 KB | 5522/835/2 | 3.86 | 14.2 | +1.3 | OK |
| hash | Respire | 183,641 | 0.270 | 0.370 | 0.660 | 0.860 | 4.0 | 0 | 2.30 KB | 4757/206/1 | 1.82 | 9.9 | -5.7 | OK |
| list | StackExchange.Redis | 116,019 | 0.360 | 0.800 | 1.000 | 1.450 | 4.5 | 0 | 3.93 KB | 5247/1621/2 | 2.46 | 14.8 | -1.1 | OK |
| list | Respire | 163,664 | 0.300 | 0.410 | 0.730 | 1.040 | 4.1 | 0 | 2.30 KB | 4239/18/2 | 1.72 | 10.8 | -1.2 | OK |
| mixed | StackExchange.Redis | 232,071 | 0.190 | 0.520 | 0.710 | 1.300 | 4.4 | 0 | 2.67 KB | 7136/1107/2 | 4.74 | 7.6 | +2.0 | OK |
| mixed | Respire | 351,432 | 0.140 | 0.200 | 0.410 | 0.610 | 2.9 | 0 | 1.61 KB | 6317/375/2 | 2.09 | 5.1 | +2.6 | OK |

## Notes

- Latency is per operation as issued by the workload; composite scenarios (hash, list) time the pair as one operation.
- Alloc/op and CPU µs/op include a harness overhead that is identical for both clients.
- Drift compares the last-third average of per-second throughput samples against the first third; a sustained negative value indicates degradation over the run.
