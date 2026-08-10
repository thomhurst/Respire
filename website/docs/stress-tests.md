---
title: Stress tests
description: Latest sustained Respire and StackExchange.Redis stress-test results.
---

# Stress tests

:::info Automated results
Generated 2026-08-10 21:04 UTC from commit `9c86e4a041aa`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31428466315) for logs, JSON results, and downloadable artifacts.
:::

3 min measured (+10s warmup) per scenario/client pass, 50 concurrent workers, 1,024 B values, .NET 10.0.10, Ubuntu 24.04.4 LTS.

## Throughput

| Scenario | StackExchange.Redis ops/s | Respire ops/s | Respire / StackExchange |
|---|---:|---:|---:|
| ping | 223,862 | 274,110 | 1.22x |
| get | 137,705 | 214,742 | 1.56x |
| set | 155,975 | 211,513 | 1.36x |
| incr | 188,746 | 248,086 | 1.31x |
| hash | 68,590 | 102,451 | 1.49x |
| list | 65,806 | 95,856 | 1.46x |
| mixed | 126,003 | 203,885 | 1.62x |

A ratio above 1.00x means Respire sustained more operations per second.

## Details

| Scenario | Client | Ops/s | p50 ms | p95 ms | p99 ms | p99.9 ms | Max ms | Errors | Alloc/op | Gen0/1/2 | GC pause s | CPU µs/op | Drift % | Status |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| ping | StackExchange.Redis | 223,862 | 0.210 | 0.360 | 0.720 | 1.120 | 8.4 | 0 | 356 B | 863/432/2 | 1.24 | 8.7 | +9.2 | OK |
| ping | Respire | 274,110 | 0.180 | 0.260 | 0.310 | 0.630 | 2.8 | 0 | 409 B | 1209/78/1 | 0.54 | 7.2 | +2.1 | OK |
| get | StackExchange.Redis | 137,705 | 0.350 | 0.580 | 0.930 | 1.230 | 5.3 | 0 | 3.55 KB | 5699/2845/2 | 2.88 | 14.4 | +3.3 | OK |
| get | Respire | 214,742 | 0.230 | 0.330 | 0.430 | 0.670 | 2.6 | 0 | 2.43 KB | 5824/1065/2 | 1.86 | 8.8 | -3.0 | OK |
| set | StackExchange.Redis | 155,975 | 0.300 | 0.560 | 0.910 | 1.230 | 6.5 | 0 | 384 B | 648/218/2 | 1.02 | 12.1 | -5.7 | OK |
| set | Respire | 211,513 | 0.230 | 0.340 | 0.400 | 0.720 | 4.9 | 0 | 417 B | 955/60/2 | 0.45 | 9.1 | +1.7 | OK |
| incr | StackExchange.Redis | 188,746 | 0.250 | 0.430 | 0.770 | 1.140 | 4.4 | 0 | 494 B | 1010/469/2 | 1.33 | 10.3 | +3.6 | OK |
| incr | Respire | 248,086 | 0.200 | 0.290 | 0.340 | 0.640 | 2.4 | 0 | 417 B | 1118/84/2 | 0.50 | 7.7 | +0.8 | OK |
| hash | StackExchange.Redis | 68,590 | 0.700 | 1.160 | 1.550 | 2.110 | 4.8 | 0 | 3.99 KB | 3179/1347/2 | 2.42 | 28.3 | +1.3 | OK |
| hash | Respire | 102,451 | 0.480 | 0.660 | 0.840 | 1.090 | 3.2 | 0 | 3.42 KB | 3920/712/2 | 1.52 | 18.7 | -1.8 | OK |
| list | StackExchange.Redis | 65,806 | 0.710 | 1.240 | 1.610 | 2.120 | 5.0 | 0 | 3.94 KB | 3002/1203/1 | 2.20 | 28.0 | +8.2 | OK |
| list | Respire | 95,856 | 0.510 | 0.700 | 0.890 | 1.180 | 4.4 | 0 | 3.35 KB | 3595/631/1 | 1.46 | 18.4 | -0.6 | OK |
| mixed | StackExchange.Redis | 126,003 | 0.360 | 0.780 | 1.140 | 2.070 | 8.0 | 0 | 2.68 KB | 3894/1937/2 | 2.51 | 14.6 | -20.6 | OK |
| mixed | Respire | 203,885 | 0.250 | 0.340 | 0.450 | 0.770 | 5.3 | 0 | 1.99 KB | 4518/926/2 | 1.84 | 8.5 | +2.8 | OK |

## Notes

- Latency is per operation as issued by the workload; composite scenarios (hash, list) time the pair as one operation.
- Alloc/op and CPU µs/op include a harness overhead that is identical for both clients.
- Drift compares the last-third average of per-second throughput samples against the first third; a sustained negative value indicates degradation over the run.
