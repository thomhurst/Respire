---
title: Stress tests
description: Latest sustained Respire and StackExchange.Redis stress-test results.
---

# Stress tests

:::info Automated results
Generated 2026-08-16 03:15 UTC from commit `8e7acd92d73e`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31922029044) for logs, JSON results, and downloadable artifacts.
:::

3 min measured (+10s warmup) per scenario/client pass, 50 concurrent workers, 1,024 B values, .NET 10.0.11, Ubuntu 24.04.4 LTS.

## Throughput

| Scenario | StackExchange.Redis ops/s | Respire ops/s | Respire / StackExchange |
|---|---:|---:|---:|
| ping | 194,019 | 236,375 | 1.22x |
| get | 120,702 | 182,543 | 1.51x |
| set | 139,434 | 185,499 | 1.33x |
| incr | 158,885 | 204,431 | 1.29x |
| hash | 60,032 | 90,488 | 1.51x |
| list | 59,356 | 86,166 | 1.45x |
| mixed | 118,401 | 185,919 | 1.57x |

A ratio above 1.00x means Respire sustained more operations per second.

## Details

| Scenario | Client | Ops/s | p50 ms | p95 ms | p99 ms | p99.9 ms | Max ms | Errors | Alloc/op | Gen0/1/2 | GC pause s | CPU µs/op | Drift % | Status |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| ping | StackExchange.Redis | 194,019 | 0.250 | 0.400 | 0.690 | 1.170 | 6.1 | 0 | 356 B | 748/375/2 | 0.88 | 10.0 | +3.0 | OK |
| ping | Respire | 236,375 | 0.220 | 0.290 | 0.340 | 0.470 | 3.2 | 0 | 121 B | 309/18/1 | 0.11 | 7.8 | -0.1 | OK |
| get | StackExchange.Redis | 120,702 | 0.400 | 0.650 | 1.000 | 1.390 | 6.4 | 0 | 3.55 KB | 4992/2486/2 | 2.83 | 16.6 | +0.0 | OK |
| get | Respire | 182,543 | 0.270 | 0.380 | 0.470 | 0.710 | 2.6 | 0 | 2.15 KB | 4366/101/2 | 1.40 | 10.1 | -0.3 | OK |
| set | StackExchange.Redis | 139,434 | 0.350 | 0.560 | 0.820 | 1.280 | 7.2 | 0 | 383 B | 580/196/2 | 0.85 | 14.0 | +0.1 | OK |
| set | Respire | 185,499 | 0.270 | 0.380 | 0.460 | 0.630 | 2.5 | 0 | 129 B | 261/18/2 | 0.11 | 10.2 | -0.4 | OK |
| incr | StackExchange.Redis | 158,885 | 0.310 | 0.480 | 0.750 | 1.190 | 3.7 | 0 | 495 B | 852/286/2 | 0.84 | 12.4 | -2.0 | OK |
| incr | Respire | 204,431 | 0.250 | 0.320 | 0.370 | 0.500 | 3.0 | 0 | 129 B | 287/19/2 | 0.11 | 8.9 | -0.9 | OK |
| hash | StackExchange.Redis | 60,032 | 0.810 | 1.190 | 1.650 | 2.200 | 6.3 | 0 | 3.99 KB | 2784/820/2 | 1.95 | 32.9 | +0.6 | OK |
| hash | Respire | 90,488 | 0.550 | 0.720 | 0.860 | 1.130 | 3.5 | 0 | 2.30 KB | 2340/98/2 | 0.77 | 20.3 | +0.2 | OK |
| list | StackExchange.Redis | 59,356 | 0.820 | 1.190 | 1.640 | 2.150 | 4.4 | 0 | 3.94 KB | 2716/793/1 | 1.79 | 33.0 | -0.3 | OK |
| list | Respire | 86,166 | 0.570 | 0.760 | 0.880 | 1.130 | 3.4 | 0 | 2.30 KB | 2228/245/1 | 0.67 | 21.0 | -0.3 | OK |
| mixed | StackExchange.Redis | 118,401 | 0.410 | 0.660 | 0.990 | 1.400 | 3.4 | 0 | 2.68 KB | 3674/1150/2 | 1.97 | 16.7 | +0.5 | OK |
| mixed | Respire | 185,919 | 0.270 | 0.380 | 0.470 | 0.700 | 3.2 | 0 | 1.61 KB | 3331/107/2 | 1.06 | 10.0 | +0.5 | OK |

## Notes

- Latency is per operation as issued by the workload; composite scenarios (hash, list) time the pair as one operation.
- Alloc/op and CPU µs/op include a harness overhead that is identical for both clients.
- Drift compares the last-third average of per-second throughput samples against the first third; a sustained negative value indicates degradation over the run.
