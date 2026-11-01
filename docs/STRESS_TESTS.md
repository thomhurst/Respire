---
title: Stress tests
description: Latest sustained Respire and StackExchange.Redis stress-test results.
---

import ComparisonBarChart from '@site/src/components/ComparisonBarChart';

# Stress tests

:::info Automated results
Generated 2026-11-01 02:57 UTC from commit `2fbcf4af5b45`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/41264160536) for logs, JSON results, and downloadable artifacts.
:::

3 min measured (+10s warmup) per scenario/client pass, 50 concurrent workers, 1,024 B values, .NET 10.0.14, Ubuntu 24.04.5 LTS.

## Throughput

<ComparisonBarChart
  title="Sustained throughput"
  description="Operations per second. Longer bars are better."
  format="integer"
  data={[{"label":"ping","other":417933,"respire":497303},{"label":"get","other":242625,"respire":398206},{"label":"set","other":295852,"respire":385974},{"label":"incr","other":326633,"respire":452490},{"label":"hash","other":122272,"respire":182836},{"label":"list","other":122694,"respire":171532},{"label":"mixed","other":248327,"respire":367844}]}
/>
| Scenario | StackExchange.Redis ops/s | Respire ops/s | Respire / StackExchange |
|---|---:|---:|---:|
| ping | 417,933 | 497,303 | 1.19x |
| get | 242,625 | 398,206 | 1.64x |
| set | 295,852 | 385,974 | 1.30x |
| incr | 326,633 | 452,490 | 1.39x |
| hash | 122,272 | 182,836 | 1.50x |
| list | 122,694 | 171,532 | 1.40x |
| mixed | 248,327 | 367,844 | 1.48x |

A ratio above 1.00x means Respire sustained more operations per second.

## Details

| Scenario | Client | Ops/s | p50 ms | p95 ms | p99 ms | p99.9 ms | Max ms | Errors | Alloc/op | Gen0/1/2 | GC pause s | CPU µs/op | Drift % | Status |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| ping | StackExchange.Redis | 417,933 | 0.100 | 0.270 | 0.520 | 0.680 | 31.0 | 0 | 356 B | 1608/790/2 | 1.26 | 4.7 | -0.3 | OK |
| ping | Respire | 497,303 | 0.100 | 0.140 | 0.190 | 0.520 | 4.6 | 0 | 120 B | 648/19/2 | 0.26 | 4.3 | -3.9 | OK |
| get | StackExchange.Redis | 242,625 | 0.190 | 0.430 | 0.660 | 0.930 | 5.2 | 0 | 3.55 KB | 10035/2474/2 | 3.93 | 7.7 | +4.8 | OK |
| get | Respire | 398,206 | 0.130 | 0.170 | 0.340 | 0.550 | 3.2 | 0 | 2.15 KB | 9522/19/2 | 2.24 | 4.6 | -1.4 | OK |
| set | StackExchange.Redis | 295,852 | 0.150 | 0.380 | 0.610 | 0.790 | 3.9 | 0 | 379 B | 1212/299/2 | 1.05 | 6.1 | -3.8 | OK |
| set | Respire | 385,974 | 0.130 | 0.180 | 0.230 | 0.500 | 2.0 | 0 | 128 B | 535/18/2 | 0.23 | 5.0 | -0.2 | OK |
| incr | StackExchange.Redis | 326,633 | 0.130 | 0.410 | 0.590 | 0.730 | 5.2 | 0 | 491 B | 1732/538/2 | 0.94 | 5.5 | +3.1 | OK |
| incr | Respire | 452,490 | 0.110 | 0.150 | 0.220 | 0.530 | 2.1 | 0 | 128 B | 627/18/2 | 0.24 | 4.2 | +1.1 | OK |
| hash | StackExchange.Redis | 122,272 | 0.350 | 0.790 | 1.050 | 1.630 | 5.4 | 0 | 3.98 KB | 5602/854/2 | 3.89 | 14.3 | -6.0 | OK |
| hash | Respire | 182,836 | 0.270 | 0.370 | 0.670 | 0.890 | 4.6 | 0 | 2.30 KB | 4732/218/1 | 1.72 | 9.8 | +0.6 | OK |
| list | StackExchange.Redis | 122,694 | 0.350 | 0.780 | 0.930 | 1.330 | 5.6 | 0 | 3.93 KB | 5555/1730/2 | 2.23 | 14.3 | -2.3 | OK |
| list | Respire | 171,532 | 0.280 | 0.400 | 0.660 | 0.860 | 3.6 | 0 | 2.30 KB | 4452/18/2 | 1.29 | 10.7 | +4.0 | OK |
| mixed | StackExchange.Redis | 248,327 | 0.180 | 0.470 | 0.670 | 1.090 | 4.2 | 0 | 2.67 KB | 7646/1194/2 | 3.83 | 7.2 | 0.0 | OK |
| mixed | Respire | 367,844 | 0.140 | 0.190 | 0.350 | 0.580 | 2.8 | 0 | 1.61 KB | 6613/395/2 | 1.52 | 4.9 | +3.7 | OK |

## Notes

- Latency is per operation as issued by the workload; composite scenarios (hash, list) time the pair as one operation.
- Alloc/op and CPU µs/op include a harness overhead that is identical for both clients.
- Drift compares the last-third average of per-second throughput samples against the first third; a sustained negative value indicates degradation over the run.
