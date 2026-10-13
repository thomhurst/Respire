---
title: Stress tests
description: Latest sustained Respire and StackExchange.Redis stress-test results.
---

import ComparisonBarChart from '@site/src/components/ComparisonBarChart';

# Stress tests

:::info Automated results
Generated 2026-10-12 18:51 UTC from commit `6d666d82d677`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/38288561836) for logs, JSON results, and downloadable artifacts.
:::

3 min measured (+10s warmup) per scenario/client pass, 50 concurrent workers, 1,024 B values, .NET 10.0.12, Ubuntu 24.04.5 LTS.

## Throughput

<ComparisonBarChart
  title="Sustained throughput"
  description="Operations per second. Longer bars are better."
  format="integer"
  data={[{"label":"ping","other":357273,"respire":503215},{"label":"get","other":220142,"respire":370011},{"label":"set","other":230589,"respire":362021},{"label":"incr","other":249222,"respire":412607},{"label":"hash","other":96520,"respire":173204},{"label":"list","other":98340,"respire":169401},{"label":"mixed","other":199768,"respire":358889}]}
/>
| Scenario | StackExchange.Redis ops/s | Respire ops/s | Respire / StackExchange |
|---|---:|---:|---:|
| ping | 357,273 | 503,215 | 1.41x |
| get | 220,142 | 370,011 | 1.68x |
| set | 230,589 | 362,021 | 1.57x |
| incr | 249,222 | 412,607 | 1.66x |
| hash | 96,520 | 173,204 | 1.79x |
| list | 98,340 | 169,401 | 1.72x |
| mixed | 199,768 | 358,889 | 1.80x |

A ratio above 1.00x means Respire sustained more operations per second.

## Details

| Scenario | Client | Ops/s | p50 ms | p95 ms | p99 ms | p99.9 ms | Max ms | Errors | Alloc/op | Gen0/1/2 | GC pause s | CPU µs/op | Drift % | Status |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| ping | StackExchange.Redis | 357,273 | 0.120 | 0.350 | 0.560 | 0.750 | 4.9 | 0 | 356 B | 1376/688/2 | 1.10 | 5.2 | +11.4 | OK |
| ping | Respire | 503,215 | 0.100 | 0.140 | 0.170 | 0.310 | 3.1 | 0 | 120 B | 655/20/2 | 0.24 | 4.0 | +1.9 | OK |
| get | StackExchange.Redis | 220,142 | 0.200 | 0.500 | 0.670 | 0.900 | 5.4 | 0 | 3.55 KB | 9046/2539/2 | 3.20 | 8.2 | -0.1 | OK |
| get | Respire | 370,011 | 0.140 | 0.180 | 0.260 | 0.450 | 2.8 | 0 | 2.15 KB | 8848/19/2 | 2.01 | 4.6 | +3.3 | OK |
| set | StackExchange.Redis | 230,589 | 0.180 | 0.540 | 0.670 | 0.880 | 5.4 | 0 | 382 B | 954/303/2 | 1.05 | 7.0 | +0.3 | OK |
| set | Respire | 362,021 | 0.140 | 0.200 | 0.230 | 0.370 | 3.2 | 0 | 128 B | 502/18/2 | 0.23 | 5.1 | -0.2 | OK |
| incr | StackExchange.Redis | 249,222 | 0.160 | 0.530 | 0.640 | 0.900 | 4.6 | 0 | 493 B | 1330/444/2 | 1.16 | 6.4 | +1.8 | OK |
| incr | Respire | 412,607 | 0.120 | 0.160 | 0.200 | 0.350 | 2.3 | 0 | 128 B | 572/18/2 | 0.26 | 4.3 | 0.0 | OK |
| hash | StackExchange.Redis | 96,520 | 0.440 | 0.840 | 1.280 | 2.040 | 5.3 | 0 | 3.99 KB | 4409/729/2 | 4.04 | 16.4 | -1.6 | OK |
| hash | Respire | 173,204 | 0.290 | 0.380 | 0.590 | 0.840 | 8.0 | 0 | 2.30 KB | 4456/189/1 | 1.76 | 10.0 | +1.9 | OK |
| list | StackExchange.Redis | 98,340 | 0.430 | 0.830 | 1.100 | 1.570 | 5.8 | 0 | 3.94 KB | 4429/1444/2 | 2.23 | 15.8 | +2.2 | OK |
| list | Respire | 169,401 | 0.290 | 0.390 | 0.520 | 0.800 | 3.3 | 0 | 2.30 KB | 4364/227/2 | 1.68 | 10.5 | +0.7 | OK |
| mixed | StackExchange.Redis | 199,768 | 0.210 | 0.580 | 0.720 | 1.100 | 5.3 | 0 | 2.67 KB | 6124/1712/2 | 3.30 | 8.3 | -1.0 | OK |
| mixed | Respire | 358,889 | 0.140 | 0.190 | 0.250 | 0.570 | 3.1 | 0 | 1.61 KB | 6428/416/2 | 2.23 | 5.0 | +2.7 | OK |

## Notes

- Latency is per operation as issued by the workload; composite scenarios (hash, list) time the pair as one operation.
- Alloc/op and CPU µs/op include a harness overhead that is identical for both clients.
- Drift compares the last-third average of per-second throughput samples against the first third; a sustained negative value indicates degradation over the run.
