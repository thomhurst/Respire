---
title: Stress tests
description: Latest sustained Respire and StackExchange.Redis stress-test results.
---

# Stress tests

:::info Automated results
Generated 2026-08-10 17:19 UTC from commit `b0934cc5d5fa`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31409661435) for logs, JSON results, and downloadable artifacts.
:::

3 min measured (+10s warmup) per scenario/client pass, 50 concurrent workers, 1,024 B values, .NET 10.0.10, Ubuntu 24.04.4 LTS.

## Throughput

| Scenario | StackExchange.Redis ops/s | Respire ops/s | Respire / StackExchange |
|---|---:|---:|---:|
| ping | 214,362 | 297,227 | 1.39x |
| get | 135,985 | 203,411 | 1.50x |
| set | 157,658 | 223,134 | 1.42x |
| incr | 177,742 | 259,412 | 1.46x |
| hash | 69,763 | 108,152 | 1.55x |
| list | 65,099 | 102,189 | 1.57x |
| mixed | 132,773 | 217,859 | 1.64x |

A ratio above 1.00x means Respire sustained more operations per second.

## Details

| Scenario | Client | Ops/s | p50 ms | p95 ms | p99 ms | p99.9 ms | Max ms | Errors | Alloc/op | Gen0/1/2 | GC pause s | CPU µs/op | Drift % | Status |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| ping | StackExchange.Redis | 214,362 | 0.210 | 0.450 | 0.820 | 1.120 | 8.4 | 0 | 356 B | 826/413/2 | 1.08 | 8.8 | +0.6 | OK |
| ping | Respire | 297,227 | 0.170 | 0.230 | 0.280 | 0.440 | 5.4 | 0 | 121 B | 390/19/2 | 0.16 | 6.4 | +0.2 | OK |
| get | StackExchange.Redis | 135,985 | 0.350 | 0.610 | 0.990 | 1.320 | 5.3 | 0 | 3.55 KB | 5624/2755/2 | 3.22 | 14.4 | +2.0 | OK |
| get | Respire | 203,411 | 0.250 | 0.320 | 0.410 | 0.680 | 3.0 | 0 | 2.15 KB | 4866/707/2 | 1.63 | 8.9 | +0.1 | OK |
| set | StackExchange.Redis | 157,658 | 0.300 | 0.520 | 0.850 | 1.210 | 6.8 | 0 | 384 B | 656/221/2 | 0.98 | 12.2 | +4.0 | OK |
| set | Respire | 223,134 | 0.220 | 0.320 | 0.380 | 0.560 | 3.0 | 0 | 129 B | 314/18/2 | 0.14 | 8.4 | -0.4 | OK |
| incr | StackExchange.Redis | 177,742 | 0.260 | 0.530 | 0.890 | 1.210 | 4.7 | 0 | 494 B | 950/409/2 | 1.18 | 10.5 | -0.8 | OK |
| incr | Respire | 259,412 | 0.190 | 0.270 | 0.320 | 0.450 | 2.3 | 0 | 129 B | 364/19/2 | 0.16 | 7.3 | -0.7 | OK |
| hash | StackExchange.Redis | 69,763 | 0.690 | 1.030 | 1.450 | 2.210 | 5.6 | 0 | 3.99 KB | 3241/1082/2 | 2.47 | 28.8 | -0.5 | OK |
| hash | Respire | 108,152 | 0.460 | 0.610 | 0.770 | 1.070 | 3.6 | 0 | 2.30 KB | 2797/162/1 | 1.22 | 17.3 | +1.7 | OK |
| list | StackExchange.Redis | 65,099 | 0.720 | 1.250 | 1.590 | 2.180 | 6.2 | 0 | 3.94 KB | 2969/991/2 | 2.11 | 28.5 | +0.2 | OK |
| list | Respire | 102,189 | 0.480 | 0.650 | 0.800 | 1.080 | 3.0 | 0 | 2.30 KB | 2641/228/2 | 1.09 | 17.2 | -+0.0 | OK |
| mixed | StackExchange.Redis | 132,773 | 0.360 | 0.640 | 1.000 | 1.480 | 6.5 | 0 | 2.68 KB | 4116/1358/2 | 2.67 | 14.6 | +2.0 | OK |
| mixed | Respire | 217,859 | 0.230 | 0.320 | 0.400 | 0.720 | 2.4 | 0 | 1.61 KB | 3904/403/2 | 1.55 | 8.1 | -0.5 | OK |

## Notes

- Latency is per operation as issued by the workload; composite scenarios (hash, list) time the pair as one operation.
- Alloc/op and CPU µs/op include a harness overhead that is identical for both clients.
- Drift compares the last-third average of per-second throughput samples against the first third; a sustained negative value indicates degradation over the run.
