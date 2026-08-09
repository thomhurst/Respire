---
title: Stress tests
description: Latest sustained Respire and StackExchange.Redis stress-test results.
---

# Stress tests

:::info Automated results
Generated 2026-08-09 03:29 UTC from commit `e6396bc3273a`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31290969587) for logs, JSON results, and downloadable artifacts.
:::

3 min measured (+10s warmup) per scenario/client pass, 50 concurrent workers, 1,024 B values, .NET 10.0.10, Ubuntu 24.04.4 LTS.

## Throughput

| Scenario | StackExchange.Redis ops/s | Respire ops/s | Respire / StackExchange |
|---|---:|---:|---:|
| ping | 386,156 | 462,639 | 1.20x |
| get | 233,969 | 336,043 | 1.44x |
| set | 274,472 | 341,762 | 1.25x |
| incr | 319,391 | 414,137 | 1.30x |
| hash | 120,655 | 167,152 | 1.39x |
| list | 116,379 | 160,664 | 1.38x |
| mixed | 224,932 | 328,549 | 1.46x |

A ratio above 1.00x means Respire sustained more operations per second.

## Details

| Scenario | Client | Ops/s | p50 ms | p95 ms | p99 ms | p99.9 ms | Max ms | Errors | Alloc/op | Gen0/1/2 | GC pause s | CPU µs/op | Drift % | Status |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| ping | StackExchange.Redis | 386,156 | 0.120 | 0.240 | 0.390 | 0.520 | 26.2 | 0 | 358 B | 300/150/2 | 3.57 | 5.0 | +0.9 | OK |
| ping | Respire | 462,639 | 0.100 | 0.190 | 0.340 | 0.450 | 9.9 | 0 | 120 B | 121/10/2 | 0.06 | 4.1 | +2.6 | OK |
| get | StackExchange.Redis | 233,969 | 0.210 | 0.360 | 0.510 | 2.170 | 10.2 | 0 | 3.55 KB | 1929/965/2 | 2.41 | 8.6 | +0.7 | OK |
| get | Respire | 336,043 | 0.140 | 0.270 | 0.410 | 0.550 | 3.0 | 0 | 2.15 KB | 1666/41/1 | 0.55 | 5.5 | +5.8 | OK |
| set | StackExchange.Redis | 274,472 | 0.170 | 0.310 | 0.470 | 0.630 | 30.8 | 0 | 385 B | 229/115/2 | 3.06 | 7.1 | +5.3 | OK |
| set | Respire | 341,762 | 0.130 | 0.310 | 0.430 | 0.570 | 3.3 | 0 | 128 B | 96/8/1 | 0.04 | 5.1 | -0.8 | OK |
| incr | StackExchange.Redis | 319,391 | 0.150 | 0.290 | 0.430 | 0.560 | 28.1 | 0 | 495 B | 341/171/2 | 3.84 | 6.1 | +4.9 | OK |
| incr | Respire | 414,137 | 0.110 | 0.250 | 0.370 | 0.480 | 12.0 | 0 | 128 B | 115/11/2 | 0.06 | 4.4 | -0.5 | OK |
| hash | StackExchange.Redis | 120,655 | 0.400 | 0.600 | 0.740 | 4.660 | 6.7 | 0 | 3.99 KB | 1119/559/1 | 2.75 | 17.1 | -2.1 | OK |
| hash | Respire | 167,152 | 0.280 | 0.500 | 0.630 | 0.890 | 4.1 | 0 | 2.30 KB | 877/34/1 | 0.33 | 10.9 | +1.9 | OK |
| list | StackExchange.Redis | 116,379 | 0.410 | 0.630 | 0.800 | 4.020 | 7.2 | 0 | 3.94 KB | 1061/531/2 | 2.27 | 16.2 | -0.5 | OK |
| list | Respire | 160,664 | 0.290 | 0.510 | 0.660 | 0.940 | 7.8 | 0 | 2.30 KB | 844/35/2 | 0.33 | 11.2 | +2.2 | OK |
| mixed | StackExchange.Redis | 224,932 | 0.210 | 0.400 | 0.540 | 0.940 | 11.8 | 0 | 2.68 KB | 1391/697/2 | 2.37 | 8.6 | -1.1 | OK |
| mixed | Respire | 328,549 | 0.150 | 0.270 | 0.420 | 0.570 | 7.6 | 0 | 1.61 KB | 1204/46/2 | 0.45 | 5.6 | +5.8 | OK |

## Notes

- Latency is per operation as issued by the workload; composite scenarios (hash, list) time the pair as one operation.
- Alloc/op and CPU µs/op include a harness overhead that is identical for both clients.
- Drift compares the last-third average of per-second throughput samples against the first third; a sustained negative value indicates degradation over the run.
