---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

import ComparisonBarChart from '@site/src/components/ComparisonBarChart';

# Benchmarks

:::info Automated results
Generated 2026-11-01 03:38 UTC from commit `baeeaeff5c23`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/41267582849) for logs and downloadable artifacts.
:::

StackExchange.Redis is the baseline. A ratio below `1.00` means Respire completed the operation faster.

## Visual comparison

StackExchange.Redis has no built-in server-assisted client cache, so its values are ordinary server reads. Respire server reads are included for a like-for-like uncached comparison.

<ComparisonBarChart
  title="Client-cache hit time — net10.0"
  description="StackExchange.Redis and Respire server reads vs Respire client-cache hit. Shorter bars are faster."
  format="duration-ns"
  respireLabel="Respire cache hit"
  scale="group"
  showRatio
  data={[{"label":"EXISTS hot","other":79455.1,"respire":294.9,"respireServer":80610.4},{"label":"GET hot","other":79665.6,"respire":111.5,"respireServer":80794.5},{"label":"GET missing hot","other":78270.6,"respire":111.8,"respireServer":81164.0},{"label":"HGET hot","other":79761.8,"respire":332.7,"respireServer":82523.2}]}
/>

<ComparisonBarChart
  title="Selected operation time — net10.0"
  description="Mean time. Shorter bars are faster."
  format="duration-ns"
  scale="group"
  showRatio
  data={[{"label":"GET","other":78423.0,"respire":80381.0},{"label":"GET x200 pipelined","other":1526.0,"respire":1401.0},{"label":"GET x50 concurrent","other":3080.0,"respire":2971.0},{"label":"HGET","other":78061.0,"respire":82730.0},{"label":"HSET","other":83574.0,"respire":81558.0},{"label":"LPUSH+LPOP","other":164493.0,"respire":164187.0},{"label":"SET 1KB","other":85812.0,"respire":82529.0}]}
/>

## net10.0

### ClientSideCachingBenchmarks

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
AMD EPYC 9V74 3.69GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.403
  [Host]     : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v4
  Job-IDGKZI : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v4

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                              | Categories      | Mean        | Error       | StdDev      | Median      | Op/s        | Ratio | MannWhitney(5%) | RatioSD | Allocated | Alloc Ratio |
|------------------------------------ |---------------- |------------:|------------:|------------:|------------:|------------:|------:|---------------- |--------:|----------:|------------:|
| StackExchange_Exists_ServerRead     | EXISTS hot      | 79,455.1 ns | 1,816.39 ns | 2,662.44 ns | 79,849.3 ns |    12,585.7 | 1.001 | Baseline        |    0.05 |     292 B |        1.00 |
| Respire_Exists_ServerRead           | EXISTS hot      | 80,610.4 ns | 1,497.98 ns | 2,242.10 ns | 80,867.7 ns |    12,405.4 | 1.016 | Same            |    0.04 |         - |        0.00 |
| Respire_Exists_ClientCacheHit       | EXISTS hot      |    294.9 ns |     1.72 ns |     2.35 ns |    294.9 ns | 3,390,697.2 | 0.004 | Faster          |    0.00 |         - |        0.00 |
|                                     |                 |             |             |             |             |             |       |                 |         |           |             |
| StackExchange_Get_ServerRead        | GET hot         | 79,665.6 ns | 3,589.83 ns | 5,373.09 ns | 80,029.1 ns |    12,552.5 | 1.005 | Baseline        |    0.10 |     506 B |        1.00 |
| Respire_Get_ServerRead              | GET hot         | 80,794.5 ns | 1,829.30 ns | 2,623.52 ns | 81,467.7 ns |    12,377.1 | 1.019 | Same            |    0.08 |      64 B |        0.13 |
| Respire_Get_ClientCacheHit          | GET hot         |    111.5 ns |     0.36 ns |     0.51 ns |    111.6 ns | 8,969,471.0 | 0.001 | Faster          |    0.00 |         - |        0.00 |
|                                     |                 |             |             |             |             |             |       |                 |         |           |             |
| StackExchange_GetMissing_ServerRead | GET missing hot | 78,270.6 ns | 3,932.35 ns | 5,885.75 ns | 79,223.0 ns |    12,776.2 | 1.006 | Baseline        |    0.11 |     396 B |        1.00 |
| Respire_GetMissing_ServerRead       | GET missing hot | 81,164.0 ns | 1,352.28 ns | 1,982.15 ns | 81,663.1 ns |    12,320.7 | 1.043 | Same            |    0.08 |         - |        0.00 |
| Respire_GetMissing_ClientCacheHit   | GET missing hot |    111.8 ns |     0.78 ns |     1.07 ns |    111.2 ns | 8,941,150.6 | 0.001 | Faster          |    0.00 |         - |        0.00 |
|                                     |                 |             |             |             |             |             |       |                 |         |           |             |
| StackExchange_HGet_ServerRead       | HGET hot        | 79,761.8 ns | 3,317.46 ns | 4,965.41 ns | 79,925.3 ns |    12,537.3 | 1.004 | Baseline        |    0.09 |     540 B |        1.00 |
| Respire_HGet_ServerRead             | HGET hot        | 82,523.2 ns | 1,402.16 ns | 2,055.27 ns | 82,388.6 ns |    12,117.8 | 1.039 | Same            |    0.07 |      64 B |        0.12 |
| Respire_HGet_ClientCacheHit         | HGET hot        |    332.7 ns |     1.63 ns |     2.33 ns |    332.6 ns | 3,005,704.7 | 0.004 | Faster          |    0.00 |         - |        0.00 |

### CommonOperationsBenchmarks

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
AMD EPYC 9V74 3.69GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.403
  [Host]     : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v4
  Job-IDGKZI : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v4

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                         | Categories           | Mean       | Error     | StdDev    | Ratio | MannWhitney(5%) | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|----------:|----------:|------:|---------------- |--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               |  79.513 μs | 1.9823 μs | 2.9670 μs |  1.00 | Baseline        |    0.05 |      - |     291 B |        1.00 |
| Respire_Exists                 | EXISTS               |  80.167 μs | 1.4880 μs | 2.2271 μs |  1.01 | Same            |    0.05 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get              | GET                  |  78.423 μs | 2.9646 μs | 4.3454 μs |  1.00 | Baseline        |    0.08 |      - |     479 B |        1.00 |
| Respire_Get                    | GET                  |  80.381 μs | 1.1292 μs | 1.6901 μs |  1.03 | Same            |    0.06 |      - |      48 B |        0.10 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_GetLong          | GET long             |  79.814 μs | 2.5019 μs | 3.7447 μs |  1.00 | Baseline        |    0.07 |      - |     400 B |        1.00 |
| Respire_GetLong                | GET long             |  81.286 μs | 1.4189 μs | 2.0797 μs |  1.02 | Same            |    0.06 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  |  71.988 μs | 1.0016 μs | 1.4992 μs |  1.00 | Baseline        |    0.03 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  |  72.287 μs | 0.5613 μs | 0.8228 μs |  1.00 | Same            |    0.02 |      - |      50 B |        0.15 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Pipelined    | GET x200 pipelined   |   1.526 μs | 0.0529 μs | 0.0793 μs |  1.00 | Baseline        |    0.08 | 0.0146 |     289 B |        1.00 |
| Respire_Get_Pipelined          | GET x200 pipelined   |   1.401 μs | 0.0105 μs | 0.0148 μs |  0.92 | Same            |    0.05 |      - |      49 B |        0.17 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   3.080 μs | 0.0437 μs | 0.0641 μs |  1.00 | Baseline        |    0.03 | 0.0098 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   2.971 μs | 0.0351 μs | 0.0525 μs |  0.97 | Same            |    0.03 |      - |      52 B |        0.18 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HGet             | HGET                 |  78.061 μs | 4.3910 μs | 6.5722 μs |  1.01 | Baseline        |    0.12 |      - |     493 B |        1.00 |
| Respire_HGet                   | HGET                 |  82.730 μs | 1.6152 μs | 2.4176 μs |  1.07 | Same            |    0.10 |      - |      48 B |        0.10 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HSet             | HSET                 |  83.574 μs | 2.3763 μs | 3.5568 μs |  1.00 | Baseline        |    0.06 |      - |     328 B |        1.00 |
| Respire_HSet                   | HSET                 |  81.558 μs | 2.2771 μs | 3.3377 μs |  0.98 | Same            |    0.06 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Incr             | INCR                 |  83.379 μs | 1.4159 μs | 2.1193 μs |  1.00 | Baseline        |    0.04 |      - |     296 B |        1.00 |
| Respire_Incr                   | INCR                 |  80.914 μs | 1.8850 μs | 2.8214 μs |  0.97 | Same            |    0.04 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 164.493 μs | 3.4683 μs | 5.1911 μs |  1.00 | Baseline        |    0.04 |      - |     748 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 164.187 μs | 1.0449 μs | 1.4986 μs |  1.00 | Same            |    0.03 |      - |     255 B |        0.34 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping             | PING                 |  79.395 μs | 1.4846 μs | 2.1762 μs |  1.00 | Baseline        |    0.04 |      - |     303 B |        1.00 |
| Respire_Ping                   | PING                 |  77.382 μs | 1.5097 μs | 2.2596 μs |  0.98 | Same            |    0.04 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential |  71.215 μs | 1.2619 μs | 1.8887 μs |  1.00 | Baseline        |    0.04 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential |  70.883 μs | 0.9086 μs | 1.3031 μs |  1.00 | Same            |    0.03 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SAdd             | SADD                 |  81.330 μs | 2.5215 μs | 3.7740 μs |  1.00 | Baseline        |    0.07 |      - |     304 B |        1.00 |
| Respire_SAdd                   | SADD                 |  79.740 μs | 1.7245 μs | 2.5278 μs |  0.98 | Same            |    0.06 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 105.732 μs | 3.2407 μs | 4.8505 μs |  1.00 | Baseline        |    0.07 |      - |     311 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             |  93.284 μs | 1.7540 μs | 2.6253 μs |  0.88 | Faster          |    0.05 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              |  83.619 μs | 1.7234 μs | 2.5795 μs |  1.00 | Baseline        |    0.04 |      - |     307 B |        1.00 |
| Respire_Set_Small              | SET 13B              |  80.572 μs | 1.7462 μs | 2.6136 μs |  0.96 | Same            |    0.04 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              |  85.812 μs | 1.5659 μs | 2.3438 μs |  1.00 | Baseline        |    0.04 |      - |     311 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              |  82.529 μs | 2.4401 μs | 3.6523 μs |  0.96 | Same            |    0.05 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  |  72.293 μs | 1.4580 μs | 2.1371 μs |  1.00 | Baseline        |    0.04 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  |  73.145 μs | 0.4563 μs | 0.6397 μs |  1.01 | Same            |    0.03 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 157.570 μs | 3.5369 μs | 5.1843 μs |  1.00 | Baseline        |    0.05 |      - |     646 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 157.906 μs | 2.3588 μs | 3.4576 μs |  1.00 | Same            |    0.04 |      - |     200 B |        0.31 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            |  82.112 μs | 1.5493 μs | 2.3189 μs |  1.00 | Baseline        |    0.04 |      - |     311 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            |  79.764 μs | 1.4495 μs | 2.1696 μs |  0.97 | Same            |    0.04 |      - |         - |        0.00 |

### PubSubComparisonBenchmarks

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
AMD EPYC 9V74 3.69GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.403
  [Host]     : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v4
  Job-IDGKZI : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v4

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                          | Mean     | Error   | StdDev  | Ratio | MannWhitney(5%) | RatioSD | Allocated | Alloc Ratio |
|-------------------------------- |---------:|--------:|--------:|------:|---------------- |--------:|----------:|------------:|
| StackExchange_PublishAndConsume | 101.2 μs | 5.40 μs | 8.08 μs |  1.01 | Baseline        |    0.11 |     666 B |        1.00 |
| Respire_PublishAndConsume       | 108.4 μs | 3.28 μs | 4.91 μs |  1.08 | Same            |    0.10 |     397 B |        0.60 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
