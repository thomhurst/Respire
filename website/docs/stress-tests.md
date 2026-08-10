---
title: Stress tests
description: Latest sustained Respire and StackExchange.Redis stress-test results.
---

# Stress tests

:::info Automated results
Generated 2026-08-10 22:44 UTC from commit `83347ad6092d`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31436343216) for logs, JSON results, and downloadable artifacts.
:::

3 min measured (+10s warmup) per scenario/client pass, 50 concurrent workers, 1,024 B values, .NET 10.0.10, Ubuntu 24.04.4 LTS.

## Throughput

| Scenario | StackExchange.Redis ops/s | Respire ops/s | Respire / StackExchange |
|---|---:|---:|---:|
| ping | 164,490 | 230,960 | 1.40x |
| get | 116,543 | 164,385 | 1.41x |
| set | 132,793 | 181,243 | 1.36x |
| incr | 146,433 | 196,072 | 1.34x |
| hash | 56,600 | 88,607 | 1.57x |
| list | 55,117 | 84,236 | 1.53x |
| mixed | 109,975 | 175,831 | 1.60x |

A ratio above 1.00x means Respire sustained more operations per second.

## Details

| Scenario | Client | Ops/s | p50 ms | p95 ms | p99 ms | p99.9 ms | Max ms | Errors | Alloc/op | Gen0/1/2 | GC pause s | CPU µs/op | Drift % | Status |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| ping | StackExchange.Redis | 164,490 | 0.260 | 0.690 | 0.980 | 1.390 | 7.2 | 0 | 355 B | 633/317/2 | 0.66 | 10.2 | +7.4 | OK |
| ping | Respire | 230,960 | 0.220 | 0.290 | 0.350 | 0.540 | 5.8 | 0 | 121 B | 303/18/2 | 0.11 | 7.7 | -0.9 | OK |
| get | StackExchange.Redis | 116,543 | 0.410 | 0.720 | 1.110 | 1.450 | 5.7 | 0 | 3.55 KB | 4803/2348/2 | 2.74 | 16.4 | -2.8 | OK |
| get | Respire | 164,385 | 0.310 | 0.380 | 0.480 | 0.720 | 7.9 | 0 | 2.15 KB | 3927/590/2 | 1.21 | 9.6 | -+0.0 | OK |
| set | StackExchange.Redis | 132,793 | 0.350 | 0.670 | 1.050 | 1.470 | 5.9 | 0 | 383 B | 551/186/2 | 0.66 | 13.7 | +4.0 | OK |
| set | Respire | 181,243 | 0.270 | 0.390 | 0.470 | 0.650 | 3.9 | 0 | 129 B | 255/18/2 | 0.11 | 9.7 | -0.2 | OK |
| incr | StackExchange.Redis | 146,433 | 0.320 | 0.630 | 0.990 | 1.420 | 6.2 | 0 | 495 B | 784/344/2 | 0.96 | 12.5 | +5.8 | OK |
| incr | Respire | 196,072 | 0.260 | 0.330 | 0.390 | 0.570 | 4.6 | 0 | 129 B | 275/18/2 | 0.11 | 8.7 | +1.3 | OK |
| hash | StackExchange.Redis | 56,600 | 0.840 | 1.410 | 1.820 | 2.420 | 6.6 | 0 | 3.99 KB | 2614/836/2 | 1.87 | 32.9 | +4.5 | OK |
| hash | Respire | 88,607 | 0.560 | 0.740 | 0.890 | 1.180 | 4.9 | 0 | 2.30 KB | 2291/154/1 | 0.88 | 20.3 | -1.0 | OK |
| list | StackExchange.Redis | 55,117 | 0.850 | 1.450 | 1.850 | 2.510 | 8.2 | 0 | 3.94 KB | 2506/837/2 | 1.59 | 32.6 | -1.3 | OK |
| list | Respire | 84,236 | 0.590 | 0.770 | 0.920 | 1.210 | 3.8 | 0 | 2.30 KB | 2177/178/2 | 0.80 | 20.6 | +0.4 | OK |
| mixed | StackExchange.Redis | 109,975 | 0.430 | 0.820 | 1.210 | 1.580 | 6.9 | 0 | 2.68 KB | 3395/1100/2 | 2.04 | 16.7 | -3.5 | OK |
| mixed | Respire | 175,831 | 0.280 | 0.390 | 0.480 | 0.740 | 5.8 | 0 | 1.61 KB | 3147/321/2 | 1.11 | 9.0 | +0.4 | OK |

## Notes

- Latency is per operation as issued by the workload; composite scenarios (hash, list) time the pair as one operation.
- Alloc/op and CPU µs/op include a harness overhead that is identical for both clients.
- Drift compares the last-third average of per-second throughput samples against the first third; a sustained negative value indicates degradation over the run.
