---
title: Stress tests
description: Latest sustained Respire and StackExchange.Redis stress-test results.
---

# Stress tests

:::info Automated results
Generated 2026-08-15 05:07 UTC from commit `bce58e5acf8c`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31864238600) for logs, JSON results, and downloadable artifacts.
:::

3 min measured (+10s warmup) per scenario/client pass, 50 concurrent workers, 1,024 B values, .NET 10.0.11, Ubuntu 24.04.4 LTS.

## Throughput

| Scenario | StackExchange.Redis ops/s | Respire ops/s | Respire / StackExchange |
|---|---:|---:|---:|
| ping | 192,263 | 234,956 | 1.22x |
| get | 120,128 | 180,836 | 1.51x |
| set | 140,403 | 183,896 | 1.31x |
| incr | 160,801 | 205,112 | 1.28x |
| hash | 59,763 | 89,398 | 1.50x |
| list | 58,815 | 84,758 | 1.44x |
| mixed | 117,126 | 175,780 | 1.50x |

A ratio above 1.00x means Respire sustained more operations per second.

## Details

| Scenario | Client | Ops/s | p50 ms | p95 ms | p99 ms | p99.9 ms | Max ms | Errors | Alloc/op | Gen0/1/2 | GC pause s | CPU µs/op | Drift % | Status |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| ping | StackExchange.Redis | 192,263 | 0.250 | 0.400 | 0.680 | 1.150 | 10.7 | 0 | 356 B | 741/371/2 | 0.84 | 10.1 | +3.9 | OK |
| ping | Respire | 234,956 | 0.220 | 0.290 | 0.350 | 0.510 | 3.1 | 0 | 121 B | 308/18/2 | 0.13 | 7.9 | -1.1 | OK |
| get | StackExchange.Redis | 120,128 | 0.400 | 0.650 | 1.020 | 1.450 | 5.5 | 0 | 3.55 KB | 4966/2428/2 | 3.09 | 16.6 | -0.5 | OK |
| get | Respire | 180,836 | 0.270 | 0.390 | 0.490 | 0.780 | 4.6 | 0 | 2.15 KB | 4328/101/2 | 1.70 | 10.2 | +0.6 | OK |
| set | StackExchange.Redis | 140,403 | 0.340 | 0.560 | 0.810 | 1.260 | 4.2 | 0 | 383 B | 584/196/2 | 0.72 | 13.9 | +0.3 | OK |
| set | Respire | 183,896 | 0.270 | 0.390 | 0.460 | 0.650 | 5.6 | 0 | 129 B | 258/18/2 | 0.11 | 10.3 | +0.5 | OK |
| incr | StackExchange.Redis | 160,801 | 0.300 | 0.470 | 0.720 | 1.160 | 7.1 | 0 | 495 B | 862/289/2 | 0.73 | 12.3 | +0.4 | OK |
| incr | Respire | 205,112 | 0.250 | 0.330 | 0.390 | 0.540 | 2.2 | 0 | 129 B | 288/18/2 | 0.13 | 8.9 | -1.9 | OK |
| hash | StackExchange.Redis | 59,763 | 0.810 | 1.190 | 1.640 | 2.290 | 7.4 | 0 | 3.99 KB | 2771/922/2 | 2.06 | 33.1 | +0.2 | OK |
| hash | Respire | 89,398 | 0.550 | 0.730 | 0.900 | 1.180 | 4.0 | 0 | 2.30 KB | 2313/19/1 | 0.96 | 20.6 | -0.4 | OK |
| list | StackExchange.Redis | 58,815 | 0.830 | 1.200 | 1.640 | 2.210 | 7.5 | 0 | 3.94 KB | 2691/899/2 | 1.82 | 33.3 | -0.2 | OK |
| list | Respire | 84,758 | 0.580 | 0.770 | 0.930 | 1.210 | 3.9 | 0 | 2.30 KB | 2193/182/2 | 0.87 | 21.3 | +0.4 | OK |
| mixed | StackExchange.Redis | 117,126 | 0.420 | 0.670 | 1.020 | 1.600 | 3.3 | 0 | 2.68 KB | 3634/1086/2 | 2.58 | 16.9 | +0.2 | OK |
| mixed | Respire | 175,780 | 0.280 | 0.400 | 0.510 | 0.910 | 8.1 | 0 | 1.61 KB | 3149/86/2 | 1.38 | 9.6 | -6.0 | OK |

## Notes

- Latency is per operation as issued by the workload; composite scenarios (hash, list) time the pair as one operation.
- Alloc/op and CPU µs/op include a harness overhead that is identical for both clients.
- Drift compares the last-third average of per-second throughput samples against the first third; a sustained negative value indicates degradation over the run.
