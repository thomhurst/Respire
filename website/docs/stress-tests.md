---
title: Stress tests
description: Latest sustained Respire and StackExchange.Redis stress-test results.
---

# Stress tests

:::info Automated results
Generated 2026-08-10 02:31 UTC from commit `cb3abd764ead`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31347957686) for logs, JSON results, and downloadable artifacts.
:::

3 min measured (+10s warmup) per scenario/client pass, 50 concurrent workers, 1,024 B values, .NET 10.0.10, Ubuntu 24.04.4 LTS.

## Throughput

| Scenario | StackExchange.Redis ops/s | Respire ops/s | Respire / StackExchange |
|---|---:|---:|---:|
| ping | 310,120 | 446,294 | 1.44x |
| get | 189,453 | 322,999 | 1.70x |
| set | 225,611 | 351,460 | 1.56x |
| incr | 263,272 | 392,245 | 1.49x |
| hash | 94,955 | 166,885 | 1.76x |
| list | 100,476 | 158,771 | 1.58x |
| mixed | 185,400 | 322,635 | 1.74x |

A ratio above 1.00x means Respire sustained more operations per second.

## Details

| Scenario | Client | Ops/s | p50 ms | p95 ms | p99 ms | p99.9 ms | Max ms | Errors | Alloc/op | Gen0/1/2 | GC pause s | CPU µs/op | Drift % | Status |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| ping | StackExchange.Redis | 310,120 | 0.140 | 0.350 | 0.490 | 0.630 | 28.6 | 0 | 357 B | 240/121/2 | 3.14 | 5.9 | +12.4 | OK |
| ping | Respire | 446,294 | 0.110 | 0.160 | 0.190 | 0.260 | 15.3 | 0 | 121 B | 117/10/1 | 0.06 | 4.3 | -1.4 | OK |
| get | StackExchange.Redis | 189,453 | 0.250 | 0.490 | 0.630 | 2.220 | 7.5 | 0 | 3.55 KB | 1553/777/2 | 2.06 | 9.7 | +0.5 | OK |
| get | Respire | 322,999 | 0.150 | 0.230 | 0.270 | 0.510 | 4.7 | 0 | 2.15 KB | 1545/43/2 | 0.56 | 5.8 | -5.9 | OK |
| set | StackExchange.Redis | 225,611 | 0.210 | 0.430 | 0.590 | 0.800 | 31.1 | 0 | 384 B | 188/94/1 | 2.60 | 8.1 | -8.6 | OK |
| set | Respire | 351,460 | 0.140 | 0.200 | 0.240 | 0.320 | 2.8 | 0 | 129 B | 98/8/0 | 0.04 | 5.3 | +0.2 | OK |
| incr | StackExchange.Redis | 263,272 | 0.170 | 0.370 | 0.530 | 0.670 | 25.4 | 0 | 495 B | 282/141/1 | 3.26 | 7.1 | +2.2 | OK |
| incr | Respire | 392,245 | 0.130 | 0.190 | 0.210 | 0.270 | 14.9 | 0 | 129 B | 110/10/2 | 0.06 | 4.7 | -0.9 | OK |
| hash | StackExchange.Redis | 94,955 | 0.500 | 0.780 | 0.990 | 4.820 | 16.8 | 0 | 3.99 KB | 874/437/2 | 2.24 | 19.6 | +1.5 | OK |
| hash | Respire | 166,885 | 0.290 | 0.410 | 0.490 | 0.740 | 3.0 | 0 | 2.30 KB | 859/38/1 | 0.36 | 11.3 | +0.4 | OK |
| list | StackExchange.Redis | 100,476 | 0.480 | 0.720 | 0.910 | 4.730 | 16.3 | 0 | 3.94 KB | 916/459/2 | 2.30 | 19.2 | -0.9 | OK |
| list | Respire | 158,771 | 0.310 | 0.440 | 0.510 | 0.770 | 8.2 | 0 | 2.30 KB | 817/36/2 | 0.35 | 11.5 | +0.6 | OK |
| mixed | StackExchange.Redis | 185,400 | 0.260 | 0.500 | 0.640 | 1.110 | 12.9 | 0 | 2.68 KB | 1144/573/2 | 2.11 | 10.0 | -4.7 | OK |
| mixed | Respire | 322,635 | 0.150 | 0.230 | 0.270 | 0.520 | 7.5 | 0 | 1.61 KB | 1154/44/2 | 0.46 | 5.6 | +2.3 | OK |

## Notes

- Latency is per operation as issued by the workload; composite scenarios (hash, list) time the pair as one operation.
- Alloc/op and CPU µs/op include a harness overhead that is identical for both clients.
- Drift compares the last-third average of per-second throughput samples against the first third; a sustained negative value indicates degradation over the run.
