---
title: Stress tests
description: Latest sustained Respire and StackExchange.Redis stress-test results.
---

import ComparisonBarChart from '@site/src/components/ComparisonBarChart';

# Stress tests

:::info Automated results
Generated 2026-08-30 03:02 UTC from commit `8266202694e7`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/33287644201) for logs, JSON results, and downloadable artifacts.
:::

3 min measured (+10s warmup) per scenario/client pass, 50 concurrent workers, 1,024 B values, .NET 10.0.11, Ubuntu 24.04.4 LTS.

## Throughput

<ComparisonBarChart
  title="Sustained throughput"
  description="Operations per second. Longer bars are better."
  format="integer"
  data={[{"label":"ping","other":213126,"respire":299260},{"label":"get","other":138185,"respire":228009},{"label":"set","other":154902,"respire":225844},{"label":"incr","other":173721,"respire":263048},{"label":"hash","other":67201,"respire":108308},{"label":"list","other":66142,"respire":103600},{"label":"mixed","other":127506,"respire":219857}]}
/>
| Scenario | StackExchange.Redis ops/s | Respire ops/s | Respire / StackExchange |
|---|---:|---:|---:|
| ping | 213,126 | 299,260 | 1.40x |
| get | 138,185 | 228,009 | 1.65x |
| set | 154,902 | 225,844 | 1.46x |
| incr | 173,721 | 263,048 | 1.51x |
| hash | 67,201 | 108,308 | 1.61x |
| list | 66,142 | 103,600 | 1.57x |
| mixed | 127,506 | 219,857 | 1.72x |

A ratio above 1.00x means Respire sustained more operations per second.

## Details

| Scenario | Client | Ops/s | p50 ms | p95 ms | p99 ms | p99.9 ms | Max ms | Errors | Alloc/op | Gen0/1/2 | GC pause s | CPU µs/op | Drift % | Status |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| ping | StackExchange.Redis | 213,126 | 0.210 | 0.490 | 0.850 | 1.150 | 6.8 | 0 | 356 B | 822/411/2 | 0.99 | 8.7 | +4.9 | OK |
| ping | Respire | 299,260 | 0.170 | 0.230 | 0.280 | 0.420 | 6.5 | 0 | 121 B | 393/18/2 | 0.16 | 6.6 | +0.9 | OK |
| get | StackExchange.Redis | 138,185 | 0.350 | 0.570 | 0.910 | 1.210 | 5.6 | 0 | 3.55 KB | 5720/2102/2 | 2.67 | 14.4 | +2.8 | OK |
| get | Respire | 228,009 | 0.220 | 0.310 | 0.390 | 0.610 | 3.3 | 0 | 2.15 KB | 5455/106/2 | 1.64 | 8.4 | -0.5 | OK |
| set | StackExchange.Redis | 154,902 | 0.310 | 0.560 | 0.920 | 1.250 | 5.0 | 0 | 384 B | 644/216/2 | 0.86 | 12.2 | -1.4 | OK |
| set | Respire | 225,844 | 0.220 | 0.320 | 0.380 | 0.560 | 3.0 | 0 | 129 B | 317/18/2 | 0.14 | 8.5 | -1.6 | OK |
| incr | StackExchange.Redis | 173,721 | 0.260 | 0.610 | 0.940 | 1.200 | 4.7 | 0 | 494 B | 928/311/2 | 0.96 | 10.4 | -0.5 | OK |
| incr | Respire | 263,048 | 0.190 | 0.260 | 0.310 | 0.450 | 3.1 | 0 | 129 B | 369/18/2 | 0.16 | 7.1 | +0.5 | OK |
| hash | StackExchange.Redis | 67,201 | 0.700 | 1.240 | 1.580 | 2.190 | 4.3 | 0 | 3.99 KB | 3109/929/2 | 2.39 | 28.3 | +0.3 | OK |
| hash | Respire | 108,308 | 0.460 | 0.610 | 0.760 | 1.050 | 4.1 | 0 | 2.30 KB | 2797/142/1 | 1.05 | 16.5 | +0.3 | OK |
| list | StackExchange.Redis | 66,142 | 0.710 | 1.240 | 1.570 | 2.110 | 6.6 | 0 | 3.94 KB | 3017/1007/2 | 1.90 | 28.1 | -4.5 | OK |
| list | Respire | 103,600 | 0.480 | 0.640 | 0.790 | 1.070 | 3.6 | 0 | 2.30 KB | 2676/222/2 | 0.97 | 16.7 | -0.1 | OK |
| mixed | StackExchange.Redis | 127,506 | 0.360 | 0.770 | 1.090 | 1.420 | 4.2 | 0 | 2.68 KB | 3936/1222/2 | 2.32 | 14.3 | +3.8 | OK |
| mixed | Respire | 219,857 | 0.230 | 0.320 | 0.410 | 0.680 | 3.4 | 0 | 1.61 KB | 3947/124/2 | 1.42 | 8.3 | -2.4 | OK |

## Notes

- Latency is per operation as issued by the workload; composite scenarios (hash, list) time the pair as one operation.
- Alloc/op and CPU µs/op include a harness overhead that is identical for both clients.
- Drift compares the last-third average of per-second throughput samples against the first third; a sustained negative value indicates degradation over the run.
