---
title: Stress tests
description: Latest sustained Respire and StackExchange.Redis stress-test results.
---

import ComparisonBarChart from '@site/src/components/ComparisonBarChart';

# Stress tests

:::info Automated results
Generated 2026-10-25 03:03 UTC from commit `ffae194bb093`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/40153908538) for logs, JSON results, and downloadable artifacts.
:::

3 min measured (+10s warmup) per scenario/client pass, 50 concurrent workers, 1,024 B values, .NET 10.0.13, Ubuntu 24.04.5 LTS.

## Throughput

<ComparisonBarChart
  title="Sustained throughput"
  description="Operations per second. Longer bars are better."
  format="integer"
  data={[{"label":"ping","other":342312,"respire":487907},{"label":"get","other":216741,"respire":374276},{"label":"set","other":229209,"respire":364457},{"label":"incr","other":289526,"respire":427656},{"label":"hash","other":94346,"respire":170482},{"label":"list","other":97668,"respire":165511},{"label":"mixed","other":207646,"respire":387631}]}
/>
| Scenario | StackExchange.Redis ops/s | Respire ops/s | Respire / StackExchange |
|---|---:|---:|---:|
| ping | 342,312 | 487,907 | 1.43x |
| get | 216,741 | 374,276 | 1.73x |
| set | 229,209 | 364,457 | 1.59x |
| incr | 289,526 | 427,656 | 1.48x |
| hash | 94,346 | 170,482 | 1.81x |
| list | 97,668 | 165,511 | 1.69x |
| mixed | 207,646 | 387,631 | 1.87x |

A ratio above 1.00x means Respire sustained more operations per second.

## Details

| Scenario | Client | Ops/s | p50 ms | p95 ms | p99 ms | p99.9 ms | Max ms | Errors | Alloc/op | Gen0/1/2 | GC pause s | CPU µs/op | Drift % | Status |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| ping | StackExchange.Redis | 342,312 | 0.130 | 0.370 | 0.580 | 0.780 | 8.3 | 0 | 355 B | 1317/659/2 | 1.24 | 5.3 | -0.6 | OK |
| ping | Respire | 487,907 | 0.100 | 0.140 | 0.170 | 0.250 | 3.2 | 0 | 120 B | 634/19/2 | 0.19 | 4.2 | +0.2 | OK |
| get | StackExchange.Redis | 216,741 | 0.210 | 0.510 | 0.680 | 0.890 | 5.6 | 0 | 3.55 KB | 8908/2496/2 | 3.06 | 8.3 | -4.1 | OK |
| get | Respire | 374,276 | 0.140 | 0.180 | 0.270 | 0.540 | 3.1 | 0 | 2.15 KB | 8945/19/2 | 2.36 | 4.5 | -1.8 | OK |
| set | StackExchange.Redis | 229,209 | 0.180 | 0.540 | 0.680 | 0.900 | 4.5 | 0 | 382 B | 949/314/2 | 1.03 | 7.1 | -5.1 | OK |
| set | Respire | 364,457 | 0.140 | 0.200 | 0.250 | 0.420 | 2.7 | 0 | 128 B | 505/18/2 | 0.24 | 4.9 | +0.2 | OK |
| incr | StackExchange.Redis | 289,526 | 0.150 | 0.430 | 0.630 | 0.840 | 6.3 | 0 | 494 B | 1546/517/2 | 1.18 | 6.2 | -2.7 | OK |
| incr | Respire | 427,656 | 0.120 | 0.160 | 0.190 | 0.340 | 3.6 | 0 | 128 B | 593/18/2 | 0.26 | 4.2 | 0.0 | OK |
| hash | StackExchange.Redis | 94,346 | 0.450 | 0.870 | 1.220 | 1.860 | 5.5 | 0 | 3.99 KB | 4309/925/2 | 3.25 | 16.8 | +2.0 | OK |
| hash | Respire | 170,482 | 0.290 | 0.390 | 0.550 | 0.820 | 3.4 | 0 | 2.30 KB | 4387/217/1 | 1.68 | 10.2 | +2.6 | OK |
| list | StackExchange.Redis | 97,668 | 0.440 | 0.840 | 1.110 | 1.560 | 5.9 | 0 | 3.94 KB | 4408/1450/2 | 2.18 | 16.3 | +5.1 | OK |
| list | Respire | 165,511 | 0.300 | 0.400 | 0.500 | 0.800 | 2.9 | 0 | 2.30 KB | 4265/18/2 | 1.58 | 10.9 | -2.1 | OK |
| mixed | StackExchange.Redis | 207,646 | 0.210 | 0.540 | 0.730 | 1.380 | 5.7 | 0 | 2.67 KB | 6396/1229/2 | 4.59 | 8.5 | +0.7 | OK |
| mixed | Respire | 387,631 | 0.130 | 0.180 | 0.230 | 0.490 | 2.4 | 0 | 1.61 KB | 6943/500/2 | 1.95 | 4.7 | +5.2 | OK |

## Notes

- Latency is per operation as issued by the workload; composite scenarios (hash, list) time the pair as one operation.
- Alloc/op and CPU µs/op include a harness overhead that is identical for both clients.
- Drift compares the last-third average of per-second throughput samples against the first third; a sustained negative value indicates degradation over the run.
