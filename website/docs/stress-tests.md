---
title: Stress tests
description: Latest sustained Respire and StackExchange.Redis stress-test results.
---

# Stress tests

:::info Automated results
Generated 2026-08-13 18:10 UTC from commit `995db6f36b5e`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31725639904) for logs, JSON results, and downloadable artifacts.
:::

3 min measured (+10s warmup) per scenario/client pass, 50 concurrent workers, 1,024 B values, .NET 10.0.11, Ubuntu 24.04.4 LTS.

## Throughput

| Scenario | StackExchange.Redis ops/s | Respire ops/s | Respire / StackExchange |
|---|---:|---:|---:|
| ping | 358,169 | 516,049 | 1.44x |
| get | 221,077 | 399,661 | 1.81x |
| set | 237,870 | 393,617 | 1.65x |
| incr | 261,924 | 455,705 | 1.74x |
| hash | 109,212 | 194,720 | 1.78x |
| list | 106,459 | 187,335 | 1.76x |
| mixed | 215,801 | 386,866 | 1.79x |

A ratio above 1.00x means Respire sustained more operations per second.

## Details

| Scenario | Client | Ops/s | p50 ms | p95 ms | p99 ms | p99.9 ms | Max ms | Errors | Alloc/op | Gen0/1/2 | GC pause s | CPU µs/op | Drift % | Status |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| ping | StackExchange.Redis | 358,169 | 0.120 | 0.360 | 0.550 | 0.760 | 7.8 | 0 | 356 B | 1379/690/2 | 1.11 | 5.1 | -0.6 | OK |
| ping | Respire | 516,049 | 0.100 | 0.130 | 0.160 | 0.270 | 3.6 | 0 | 121 B | 676/19/2 | 0.24 | 3.9 | +1.0 | OK |
| get | StackExchange.Redis | 221,077 | 0.200 | 0.510 | 0.650 | 0.890 | 5.7 | 0 | 3.55 KB | 9067/3847/3 | 3.10 | 7.9 | +5.8 | OK |
| get | Respire | 399,661 | 0.130 | 0.170 | 0.230 | 0.390 | 2.9 | 0 | 2.15 KB | 9554/1432/2 | 1.79 | 4.4 | -0.1 | OK |
| set | StackExchange.Redis | 237,870 | 0.180 | 0.520 | 0.650 | 0.840 | 3.7 | 0 | 382 B | 984/329/2 | 0.95 | 6.9 | +0.1 | OK |
| set | Respire | 393,617 | 0.130 | 0.180 | 0.220 | 0.350 | 2.9 | 0 | 129 B | 552/19/2 | 0.22 | 4.7 | -1.2 | OK |
| incr | StackExchange.Redis | 261,924 | 0.160 | 0.500 | 0.620 | 0.820 | 4.2 | 0 | 493 B | 1396/467/1 | 1.19 | 6.2 | +9.4 | OK |
| incr | Respire | 455,705 | 0.110 | 0.150 | 0.180 | 0.270 | 2.5 | 0 | 129 B | 636/18/1 | 0.23 | 4.2 | +1.4 | OK |
| hash | StackExchange.Redis | 109,212 | 0.400 | 0.790 | 1.020 | 1.480 | 4.4 | 0 | 3.99 KB | 5013/1562/2 | 2.64 | 15.8 | +4.4 | OK |
| hash | Respire | 194,720 | 0.260 | 0.340 | 0.460 | 0.680 | 3.3 | 0 | 2.30 KB | 5022/468/2 | 1.56 | 9.2 | +1.1 | OK |
| list | StackExchange.Redis | 106,459 | 0.400 | 0.790 | 1.050 | 1.510 | 5.8 | 0 | 3.94 KB | 4813/1349/2 | 2.70 | 15.4 | -8.4 | OK |
| list | Respire | 187,335 | 0.270 | 0.350 | 0.480 | 0.710 | 3.8 | 0 | 2.30 KB | 4830/374/2 | 1.52 | 9.2 | +1.0 | OK |
| mixed | StackExchange.Redis | 215,801 | 0.200 | 0.530 | 0.680 | 1.020 | 5.3 | 0 | 2.67 KB | 6630/1860/2 | 3.24 | 8.0 | +6.4 | OK |
| mixed | Respire | 386,866 | 0.130 | 0.180 | 0.230 | 0.480 | 2.5 | 0 | 1.61 KB | 6937/600/2 | 1.83 | 4.6 | +0.4 | OK |

## Notes

- Latency is per operation as issued by the workload; composite scenarios (hash, list) time the pair as one operation.
- Alloc/op and CPU µs/op include a harness overhead that is identical for both clients.
- Drift compares the last-third average of per-second throughput samples against the first third; a sustained negative value indicates degradation over the run.
