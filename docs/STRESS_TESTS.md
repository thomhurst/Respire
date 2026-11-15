---
title: Stress tests
description: Latest sustained Respire and StackExchange.Redis stress-test results.
---

import ComparisonBarChart from '@site/src/components/ComparisonBarChart';

# Stress tests

:::info Automated results
Generated 2026-11-15 09:38 UTC from commit `d0f2b81dba44`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/43696504363) for logs, JSON results, and downloadable artifacts.
:::

3 min measured (+10s warmup) per scenario/client pass, 50 concurrent workers, 1,024 B values, .NET 11.0.0, Ubuntu 26.04.1 LTS.

## Throughput

<ComparisonBarChart
  title="Sustained throughput"
  description="Operations per second. Longer bars are better."
  format="integer"
  data={[{"label":"ping","other":216495,"respire":317302},{"label":"get","other":158725,"respire":270874},{"label":"set","other":166532,"respire":249907},{"label":"incr","other":201588,"respire":311720},{"label":"hash","other":82406,"respire":128633},{"label":"list","other":82423,"respire":115724},{"label":"mixed","other":163330,"respire":257157}]}
/>
| Scenario | StackExchange.Redis ops/s | Respire ops/s | Respire / StackExchange |
|---|---:|---:|---:|
| ping | 216,495 | 317,302 | 1.47x |
| get | 158,725 | 270,874 | 1.71x |
| set | 166,532 | 249,907 | 1.50x |
| incr | 201,588 | 311,720 | 1.55x |
| hash | 82,406 | 128,633 | 1.56x |
| list | 82,423 | 115,724 | 1.40x |
| mixed | 163,330 | 257,157 | 1.57x |

A ratio above 1.00x means Respire sustained more operations per second.

## Details

| Scenario | Client | Ops/s | p50 ms | p95 ms | p99 ms | p99.9 ms | Max ms | Errors | Alloc/op | Gen0/1/2 | GC pause s | CPU µs/op | Drift % | Status |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| ping | StackExchange.Redis | 216,495 | 0.190 | 0.530 | 0.670 | 2.090 | 7.4 | 0 | 502 B | 1174/586/2 | 1.68 | 6.6 | +3.2 | OK |
| ping | Respire | 317,302 | 0.140 | 0.330 | 0.560 | 0.800 | 4.5 | 0 | 184 B | 634/19/2 | 0.29 | 4.7 | -2.4 | OK |
| get | StackExchange.Redis | 158,725 | 0.280 | 0.640 | 0.860 | 1.410 | 5.6 | 0 | 3.61 KB | 6538/1823/2 | 3.76 | 9.4 | -2.0 | OK |
| get | Respire | 270,874 | 0.170 | 0.300 | 0.610 | 0.790 | 4.3 | 0 | 2.20 KB | 6658/19/2 | 3.30 | 5.4 | +0.8 | OK |
| set | StackExchange.Redis | 166,532 | 0.260 | 0.630 | 0.780 | 1.660 | 6.0 | 0 | 510 B | 918/295/2 | 1.36 | 8.0 | -0.9 | OK |
| set | Respire | 249,907 | 0.170 | 0.440 | 0.640 | 0.840 | 5.1 | 0 | 184 B | 500/18/2 | 0.29 | 5.4 | -1.7 | OK |
| incr | StackExchange.Redis | 201,588 | 0.210 | 0.540 | 0.700 | 1.360 | 11.5 | 0 | 551 B | 1202/402/2 | 1.78 | 7.1 | -0.3 | OK |
| incr | Respire | 311,720 | 0.150 | 0.320 | 0.550 | 0.710 | 5.3 | 0 | 184 B | 623/18/2 | 0.35 | 4.6 | +2.3 | OK |
| hash | StackExchange.Redis | 82,406 | 0.560 | 0.940 | 1.430 | 2.290 | 6.3 | 0 | 3.97 KB | 3779/946/2 | 3.50 | 17.4 | -2.4 | OK |
| hash | Respire | 128,633 | 0.350 | 0.720 | 0.880 | 1.250 | 4.7 | 0 | 2.28 KB | 3302/324/1 | 1.73 | 10.8 | -0.2 | OK |
| list | StackExchange.Redis | 82,423 | 0.570 | 0.930 | 1.310 | 2.090 | 6.0 | 0 | 3.93 KB | 3731/1245/2 | 2.51 | 17.1 | -0.5 | OK |
| list | Respire | 115,724 | 0.400 | 0.770 | 0.930 | 1.320 | 5.1 | 0 | 2.31 KB | 3006/18/2 | 1.51 | 12.3 | +0.3 | OK |
| mixed | StackExchange.Redis | 163,330 | 0.270 | 0.600 | 0.820 | 1.920 | 8.0 | 0 | 2.70 KB | 5100/934/2 | 4.86 | 9.3 | +2.3 | OK |
| mixed | Respire | 257,157 | 0.180 | 0.310 | 0.600 | 0.820 | 5.2 | 0 | 1.62 KB | 4651/449/2 | 2.44 | 5.6 | -0.5 | OK |

## Notes

- Latency is per operation as issued by the workload; composite scenarios (hash, list) time the pair as one operation.
- Alloc/op and CPU µs/op include a harness overhead that is identical for both clients.
- Drift compares the last-third average of per-second throughput samples against the first third; a sustained negative value indicates degradation over the run.
