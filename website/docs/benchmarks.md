---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

import ComparisonBarChart from '@site/src/components/ComparisonBarChart';

# Benchmarks

:::info Automated results
Generated 2026-10-05 13:12 UTC from commit `99929295e05f`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/37309533824) for logs and downloadable artifacts.
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
  data={[{"label":"EXISTS hot","other":106346.3,"respire":356.5,"respireServer":104745.4},{"label":"GET hot","other":106540.6,"respire":184.4,"respireServer":104990.1},{"label":"GET missing hot","other":104930.1,"respire":153.8,"respireServer":104638.5},{"label":"HGET hot","other":104537.5,"respire":422.2,"respireServer":102618.4}]}
/>

<ComparisonBarChart
  title="Selected operation time — net10.0"
  description="Mean time. Shorter bars are faster."
  format="duration-ns"
  scale="group"
  showRatio
  data={[{"label":"GET","other":105596.0,"respire":105073.0},{"label":"GET x200 pipelined","other":1650.0,"respire":1600.0},{"label":"GET x50 concurrent","other":3438.0,"respire":3067.0},{"label":"HGET","other":107397.0,"respire":107982.0},{"label":"HSET","other":109111.0,"respire":108222.0},{"label":"LPUSH+LPOP","other":207837.0,"respire":202397.0},{"label":"SET 1KB","other":106251.0,"respire":106012.0}]}
/>

## net10.0

### ClientSideCachingBenchmarks

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
INTEL XEON PLATINUM 8573C 2.30GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  Job-IDGKZI : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                              | Categories      | Mean         | Error       | StdDev      | Median       | Op/s        | Ratio | MannWhitney(5%) | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------------ |---------------- |-------------:|------------:|------------:|-------------:|------------:|------:|---------------- |--------:|-------:|----------:|------------:|
| StackExchange_Exists_ServerRead     | EXISTS hot      | 106,346.3 ns |   736.25 ns | 1,101.98 ns | 106,367.6 ns |     9,403.2 | 1.000 | Baseline        |    0.01 |      - |     296 B |        1.00 |
| Respire_Exists_ServerRead           | EXISTS hot      | 104,745.4 ns | 1,040.21 ns | 1,556.93 ns | 105,055.1 ns |     9,547.0 | 0.985 | Same            |    0.02 |      - |         - |        0.00 |
| Respire_Exists_ClientCacheHit       | EXISTS hot      |     356.5 ns |     3.65 ns |     5.35 ns |     356.8 ns | 2,805,095.1 | 0.003 | Faster          |    0.00 |      - |         - |        0.00 |
|                                     |                 |              |             |             |              |             |       |                 |         |        |           |             |
| StackExchange_Get_ServerRead        | GET hot         | 106,540.6 ns |   777.08 ns | 1,163.10 ns | 106,508.8 ns |     9,386.1 | 1.000 | Baseline        |    0.02 |      - |     528 B |        1.00 |
| Respire_Get_ServerRead              | GET hot         | 104,990.1 ns | 1,478.66 ns | 2,213.19 ns | 105,562.9 ns |     9,524.7 | 0.986 | Same            |    0.02 |      - |      64 B |        0.12 |
| Respire_Get_ClientCacheHit          | GET hot         |     184.4 ns |     2.35 ns |     3.44 ns |     184.8 ns | 5,423,051.7 | 0.002 | Faster          |    0.00 | 0.0007 |      64 B |        0.12 |
|                                     |                 |              |             |             |              |             |       |                 |         |        |           |             |
| StackExchange_GetMissing_ServerRead | GET missing hot | 104,930.1 ns |   533.02 ns |   797.79 ns | 104,977.2 ns |     9,530.2 | 1.000 | Baseline        |    0.01 |      - |     416 B |        1.00 |
| Respire_GetMissing_ServerRead       | GET missing hot | 104,638.5 ns | 1,260.85 ns | 1,887.18 ns | 105,097.3 ns |     9,556.7 | 0.997 | Same            |    0.02 |      - |         - |        0.00 |
| Respire_GetMissing_ClientCacheHit   | GET missing hot |     153.8 ns |     5.47 ns |     8.02 ns |     160.3 ns | 6,500,394.6 | 0.001 | Faster          |    0.00 |      - |         - |        0.00 |
|                                     |                 |              |             |             |              |             |       |                 |         |        |           |             |
| StackExchange_HGet_ServerRead       | HGET hot        | 104,537.5 ns |   888.42 ns | 1,329.74 ns | 104,746.1 ns |     9,565.9 | 1.000 | Baseline        |    0.02 |      - |     544 B |        1.00 |
| Respire_HGet_ServerRead             | HGET hot        | 102,618.4 ns |   665.72 ns |   975.80 ns | 102,750.0 ns |     9,744.8 | 0.982 | Same            |    0.02 |      - |      64 B |        0.12 |
| Respire_HGet_ClientCacheHit         | HGET hot        |     422.2 ns |     4.95 ns |     7.25 ns |     425.8 ns | 2,368,306.2 | 0.004 | Faster          |    0.00 | 0.0005 |      64 B |        0.12 |

### CommonOperationsBenchmarks

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
INTEL XEON PLATINUM 8573C 2.30GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  Job-IDGKZI : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                         | Categories           | Mean       | Error     | StdDev    | Ratio | MannWhitney(5%) | RatioSD | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|----------:|----------:|------:|---------------- |--------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 103.544 μs | 0.6095 μs | 0.9123 μs |  1.00 | Baseline        |    0.01 |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 102.890 μs | 0.6398 μs | 0.9378 μs |  0.99 | Same            |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Get              | GET                  | 105.596 μs | 0.5087 μs | 0.7614 μs |  1.00 | Baseline        |    0.01 |     504 B |        1.00 |
| Respire_Get                    | GET                  | 105.073 μs | 0.7470 μs | 1.1181 μs |  1.00 | Same            |    0.01 |      48 B |        0.10 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  |  92.671 μs | 1.2645 μs | 1.8535 μs |  1.00 | Baseline        |    0.03 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  |  89.002 μs | 1.0507 μs | 1.5727 μs |  0.96 | Same            |    0.03 |      50 B |        0.15 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Get_Pipelined    | GET x200 pipelined   |   1.650 μs | 0.0804 μs | 0.1203 μs |  1.01 | Baseline        |    0.10 |     289 B |        1.00 |
| Respire_Get_Pipelined          | GET x200 pipelined   |   1.600 μs | 0.0154 μs | 0.0220 μs |  0.97 | Same            |    0.07 |      60 B |        0.21 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   3.438 μs | 0.0482 μs | 0.0676 μs |  1.00 | Baseline        |    0.03 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   3.067 μs | 0.0341 μs | 0.0510 μs |  0.89 | Faster          |    0.02 |      52 B |        0.18 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_HGet             | HGET                 | 107.397 μs | 1.1540 μs | 1.7272 μs |  1.00 | Baseline        |    0.02 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 107.982 μs | 0.5022 μs | 0.7517 μs |  1.01 | Same            |    0.02 |      48 B |        0.09 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_HSet             | HSET                 | 109.111 μs | 0.6430 μs | 0.9625 μs |  1.00 | Baseline        |    0.01 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 108.222 μs | 0.7221 μs | 1.0809 μs |  0.99 | Same            |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Incr             | INCR                 | 108.766 μs | 0.4290 μs | 0.6421 μs |  1.00 | Baseline        |    0.01 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 107.925 μs | 0.5532 μs | 0.8109 μs |  0.99 | Same            |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 207.837 μs | 1.9702 μs | 2.9488 μs |  1.00 | Baseline        |    0.02 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 202.397 μs | 1.1360 μs | 1.6652 μs |  0.97 | Same            |    0.02 |     256 B |        0.34 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Ping             | PING                 | 107.422 μs | 0.5306 μs | 0.7941 μs |  1.00 | Baseline        |    0.01 |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 106.521 μs | 0.7594 μs | 1.1366 μs |  0.99 | Same            |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential |  92.442 μs | 0.9676 μs | 1.4483 μs |  1.00 | Baseline        |    0.02 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential |  86.219 μs | 1.2443 μs | 1.8624 μs |  0.93 | Same            |    0.02 |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_SAdd             | SADD                 | 107.639 μs | 0.9489 μs | 1.4203 μs |  1.00 | Baseline        |    0.02 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 104.576 μs | 0.4937 μs | 0.7081 μs |  0.97 | Same            |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 111.075 μs | 0.7520 μs | 1.1256 μs |  1.00 | Baseline        |    0.01 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 109.147 μs | 0.4416 μs | 0.6473 μs |  0.98 | Same            |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 107.626 μs | 0.4760 μs | 0.7124 μs |  1.00 | Baseline        |    0.01 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 105.216 μs | 0.5021 μs | 0.7359 μs |  0.98 | Same            |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 106.251 μs | 0.7431 μs | 1.1122 μs |  1.00 | Baseline        |    0.01 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 106.012 μs | 0.6744 μs | 1.0094 μs |  1.00 | Same            |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  |  91.594 μs | 0.8735 μs | 1.3074 μs |  1.00 | Baseline        |    0.02 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  |  87.842 μs | 0.7841 μs | 1.1736 μs |  0.96 | Same            |    0.02 |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 202.928 μs | 1.5601 μs | 2.3351 μs |  1.00 | Baseline        |    0.02 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 197.025 μs | 1.2804 μs | 1.9164 μs |  0.97 | Same            |    0.01 |     200 B |        0.31 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 105.796 μs | 0.7726 μs | 1.1564 μs |  1.00 | Baseline        |    0.02 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 103.988 μs | 1.8350 μs | 2.7466 μs |  0.98 | Same            |    0.03 |         - |        0.00 |

## net8.0

### ClientSideCachingBenchmarks

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
AMD EPYC 7763 2.45GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 8.0.31 (8.0.31, 8.0.3126.42015), X64 RyuJIT x86-64-v3
  Job-IDGKZI : .NET 8.0.31 (8.0.31, 8.0.3126.42015), X64 RyuJIT x86-64-v3

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                              | Categories      | Mean         | Error       | StdDev      | Op/s        | Ratio | MannWhitney(5%) | Gen0   | Allocated | Alloc Ratio |
|------------------------------------ |---------------- |-------------:|------------:|------------:|------------:|------:|---------------- |-------:|----------:|------------:|
| StackExchange_Exists_ServerRead     | EXISTS hot      | 187,086.9 ns |   841.31 ns | 1,259.23 ns |     5,345.1 | 1.000 | Baseline        |      - |     296 B |        1.00 |
| Respire_Exists_ServerRead           | EXISTS hot      | 187,251.6 ns |   988.43 ns | 1,448.83 ns |     5,340.4 | 1.001 | Same            |      - |         - |        0.00 |
| Respire_Exists_ClientCacheHit       | EXISTS hot      |     672.9 ns |     1.03 ns |     1.37 ns | 1,486,175.0 | 0.004 | Faster          |      - |         - |        0.00 |
|                                     |                 |              |             |             |             |       |                 |        |           |             |
| StackExchange_Get_ServerRead        | GET hot         | 193,296.6 ns | 1,232.05 ns | 1,844.08 ns |     5,173.4 | 1.000 | Baseline        |      - |     528 B |        1.00 |
| Respire_Get_ServerRead              | GET hot         | 188,717.1 ns |   926.05 ns | 1,357.39 ns |     5,298.9 | 0.976 | Same            |      - |      64 B |        0.12 |
| Respire_Get_ClientCacheHit          | GET hot         |     385.7 ns |     6.33 ns |     9.08 ns | 2,592,799.5 | 0.002 | Faster          | 0.0038 |      64 B |        0.12 |
|                                     |                 |              |             |             |             |       |                 |        |           |             |
| StackExchange_GetMissing_ServerRead | GET missing hot | 190,610.4 ns | 1,096.79 ns | 1,607.66 ns |     5,246.3 | 1.000 | Baseline        |      - |     414 B |        1.00 |
| Respire_GetMissing_ServerRead       | GET missing hot | 186,209.3 ns |   858.09 ns | 1,284.35 ns |     5,370.3 | 0.977 | Same            |      - |         - |        0.00 |
| Respire_GetMissing_ClientCacheHit   | GET missing hot |     419.9 ns |    11.95 ns |    16.75 ns | 2,381,394.6 | 0.002 | Faster          |      - |         - |        0.00 |
|                                     |                 |              |             |             |             |       |                 |        |           |             |
| StackExchange_HGet_ServerRead       | HGET hot        | 193,926.6 ns |   806.80 ns | 1,207.58 ns |     5,156.6 | 1.000 | Baseline        |      - |     544 B |        1.00 |
| Respire_HGet_ServerRead             | HGET hot        | 187,230.3 ns | 1,036.49 ns | 1,551.37 ns |     5,341.0 | 0.966 | Same            |      - |      64 B |        0.12 |
| Respire_HGet_ClientCacheHit         | HGET hot        |     770.4 ns |     1.80 ns |     2.46 ns | 1,298,092.6 | 0.004 | Faster          | 0.0038 |      64 B |        0.12 |

### CommonOperationsBenchmarks

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
AMD EPYC 7763 2.45GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 8.0.31 (8.0.31, 8.0.3126.42015), X64 RyuJIT x86-64-v3
  Job-IDGKZI : .NET 8.0.31 (8.0.31, 8.0.3126.42015), X64 RyuJIT x86-64-v3

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                         | Categories           | Mean       | Error     | StdDev    | Ratio | MannWhitney(5%) | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|----------:|----------:|------:|---------------- |--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 187.057 μs | 0.9789 μs | 1.4651 μs |  1.00 | Baseline        |    0.01 |      - |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 186.393 μs | 0.9469 μs | 1.3879 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get              | GET                  | 191.065 μs | 1.2243 μs | 1.8324 μs |  1.00 | Baseline        |    0.01 |      - |     504 B |        1.00 |
| Respire_Get                    | GET                  | 187.217 μs | 1.0240 μs | 1.5327 μs |  0.98 | Same            |    0.01 |      - |      48 B |        0.10 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 175.522 μs | 1.3381 μs | 2.0028 μs |  1.00 | Baseline        |    0.02 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 172.008 μs | 1.1393 μs | 1.6339 μs |  0.98 | Same            |    0.01 |      - |      50 B |        0.15 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Pipelined    | GET x200 pipelined   |   2.444 μs | 0.0703 μs | 0.1052 μs |  1.00 | Baseline        |    0.06 | 0.0098 |     289 B |        1.00 |
| Respire_Get_Pipelined          | GET x200 pipelined   |   2.178 μs | 0.0172 μs | 0.0252 μs |  0.89 | Faster          |    0.04 |      - |      60 B |        0.21 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.395 μs | 0.0546 μs | 0.0800 μs |  1.00 | Baseline        |    0.02 |      - |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   5.125 μs | 0.0185 μs | 0.0254 μs |  0.95 | Same            |    0.01 |      - |      52 B |        0.18 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HGet             | HGET                 | 193.094 μs | 0.9707 μs | 1.4529 μs |  1.00 | Baseline        |    0.01 |      - |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 186.478 μs | 0.8257 μs | 1.2358 μs |  0.97 | Same            |    0.01 |      - |      48 B |        0.09 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HSet             | HSET                 | 189.865 μs | 0.6455 μs | 0.9462 μs |  1.00 | Baseline        |    0.01 |      - |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 188.822 μs | 0.7728 μs | 1.1567 μs |  0.99 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Incr             | INCR                 | 187.551 μs | 0.8629 μs | 1.2916 μs |  1.00 | Baseline        |    0.01 |      - |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 187.237 μs | 0.9711 μs | 1.4234 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 376.697 μs | 2.4177 μs | 3.6188 μs |  1.00 | Baseline        |    0.01 |      - |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 363.751 μs | 1.6875 μs | 2.5258 μs |  0.97 | Same            |    0.01 |      - |     256 B |        0.34 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping             | PING                 | 185.137 μs | 0.8010 μs | 1.1740 μs |  1.00 | Baseline        |    0.01 |      - |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 183.693 μs | 1.1135 μs | 1.6666 μs |  0.99 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 172.717 μs | 1.4026 μs | 2.0994 μs |  1.00 | Baseline        |    0.02 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 169.274 μs | 1.5440 μs | 2.3110 μs |  0.98 | Same            |    0.02 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 187.602 μs | 0.8707 μs | 1.2763 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 187.364 μs | 0.7379 μs | 1.0583 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 198.854 μs | 0.7887 μs | 1.1561 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 197.725 μs | 0.6930 μs | 0.9939 μs |  0.99 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 189.211 μs | 0.5717 μs | 0.8557 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 189.125 μs | 1.0215 μs | 1.5290 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 190.361 μs | 0.8789 μs | 1.3155 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 189.772 μs | 0.9551 μs | 1.4295 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 177.927 μs | 1.1628 μs | 1.7404 μs |  1.00 | Baseline        |    0.01 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 174.619 μs | 1.0232 μs | 1.5315 μs |  0.98 | Same            |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 370.024 μs | 1.6116 μs | 2.3623 μs |  1.00 | Baseline        |    0.01 |      - |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 360.349 μs | 2.6163 μs | 3.9159 μs |  0.97 | Same            |    0.01 |      - |     200 B |        0.31 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 187.753 μs | 0.7653 μs | 1.1455 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 187.263 μs | 0.8344 μs | 1.2489 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
