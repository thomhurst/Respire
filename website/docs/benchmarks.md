---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

import ComparisonBarChart from '@site/src/components/ComparisonBarChart';

# Benchmarks

:::info Automated results
Generated 2026-09-13 03:38 UTC from commit `4e5b3825afd2`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/34734537249) for logs and downloadable artifacts.
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
  data={[{"label":"EXISTS hot","other":186036.6,"respire":447.6,"respireServer":186735.0},{"label":"GET hot","other":188442.4,"respire":226.5,"respireServer":187307.0},{"label":"GET missing hot","other":187738.9,"respire":203.6,"respireServer":185734.4},{"label":"HGET hot","other":189372.6,"respire":561.0,"respireServer":188743.9}]}
/>

<ComparisonBarChart
  title="Selected operation time — net10.0"
  description="Mean time. Shorter bars are faster."
  format="duration-ns"
  scale="group"
  showRatio
  data={[{"label":"GET","other":187553.0,"respire":186058.0},{"label":"GET x200 pipelined","other":2428.0,"respire":2168.0},{"label":"GET x50 concurrent","other":5224.0,"respire":5372.0},{"label":"HGET","other":188364.0,"respire":187562.0},{"label":"HSET","other":188174.0,"respire":188747.0},{"label":"LPUSH+LPOP","other":365665.0,"respire":363492.0},{"label":"SET 1KB","other":189449.0,"respire":188531.0}]}
/>

## net10.0

### ClientSideCachingBenchmarks

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
AMD EPYC 7763 2.45GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  Job-IDGKZI : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                              | Categories      | Mean         | Error     | StdDev      | Median       | Op/s        | Ratio | MannWhitney(5%) | Gen0   | Allocated | Alloc Ratio |
|------------------------------------ |---------------- |-------------:|----------:|------------:|-------------:|------------:|------:|---------------- |-------:|----------:|------------:|
| StackExchange_Exists_ServerRead     | EXISTS hot      | 186,036.6 ns | 540.43 ns |   792.15 ns | 186,311.1 ns |     5,375.3 | 1.000 | Baseline        |      - |     296 B |        1.00 |
| Respire_Exists_ServerRead           | EXISTS hot      | 186,735.0 ns | 585.50 ns |   876.35 ns | 186,854.8 ns |     5,355.2 | 1.004 | Same            |      - |         - |        0.00 |
| Respire_Exists_ClientCacheHit       | EXISTS hot      |     447.6 ns |   2.33 ns |     3.35 ns |     449.2 ns | 2,234,246.4 | 0.002 | Faster          |      - |         - |        0.00 |
|                                     |                 |              |           |             |              |             |       |                 |        |           |             |
| StackExchange_Get_ServerRead        | GET hot         | 188,442.4 ns | 643.98 ns |   943.94 ns | 188,545.5 ns |     5,306.7 | 1.000 | Baseline        |      - |     528 B |        1.00 |
| Respire_Get_ServerRead              | GET hot         | 187,307.0 ns | 756.61 ns | 1,132.46 ns | 187,368.2 ns |     5,338.8 | 0.994 | Same            |      - |      64 B |        0.12 |
| Respire_Get_ClientCacheHit          | GET hot         |     226.5 ns |   5.59 ns |     8.20 ns |     222.9 ns | 4,415,371.9 | 0.001 | Faster          | 0.0038 |      64 B |        0.12 |
|                                     |                 |              |           |             |              |             |       |                 |        |           |             |
| StackExchange_GetMissing_ServerRead | GET missing hot | 187,738.9 ns | 464.60 ns |   681.01 ns | 187,669.8 ns |     5,326.5 | 1.000 | Baseline        |      - |     416 B |        1.00 |
| Respire_GetMissing_ServerRead       | GET missing hot | 185,734.4 ns | 730.88 ns | 1,093.95 ns | 185,813.7 ns |     5,384.0 | 0.989 | Same            |      - |         - |        0.00 |
| Respire_GetMissing_ClientCacheHit   | GET missing hot |     203.6 ns |   6.49 ns |     9.52 ns |     212.2 ns | 4,911,120.4 | 0.001 | Faster          |      - |         - |        0.00 |
|                                     |                 |              |           |             |              |             |       |                 |        |           |             |
| StackExchange_HGet_ServerRead       | HGET hot        | 189,372.6 ns | 633.73 ns |   888.40 ns | 189,536.6 ns |     5,280.6 | 1.000 | Baseline        |      - |     544 B |        1.00 |
| Respire_HGet_ServerRead             | HGET hot        | 188,743.9 ns | 687.55 ns | 1,007.81 ns | 188,672.7 ns |     5,298.2 | 0.997 | Same            |      - |      64 B |        0.12 |
| Respire_HGet_ClientCacheHit         | HGET hot        |     561.0 ns |  47.47 ns |    66.55 ns |     508.5 ns | 1,782,383.8 | 0.003 | Faster          | 0.0038 |      64 B |        0.12 |

### CommonOperationsBenchmarks

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
AMD EPYC 7763 2.45GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  Job-IDGKZI : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                         | Categories           | Mean       | Error     | StdDev    | Ratio | MannWhitney(5%) | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|----------:|----------:|------:|---------------- |--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 184.992 μs | 0.6073 μs | 0.9089 μs |  1.00 | Baseline        |    0.01 |      - |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 187.480 μs | 0.7076 μs | 1.0372 μs |  1.01 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get              | GET                  | 187.553 μs | 0.7123 μs | 1.0441 μs |  1.00 | Baseline        |    0.01 |      - |     504 B |        1.00 |
| Respire_Get                    | GET                  | 186.058 μs | 1.2604 μs | 1.8866 μs |  0.99 | Same            |    0.01 |      - |      48 B |        0.10 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 171.955 μs | 1.1608 μs | 1.7374 μs |  1.00 | Baseline        |    0.01 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 170.400 μs | 0.7144 μs | 1.0692 μs |  0.99 | Same            |    0.01 |      - |      50 B |        0.15 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Pipelined    | GET x200 pipelined   |   2.428 μs | 0.0333 μs | 0.0489 μs |  1.00 | Baseline        |    0.03 | 0.0098 |     289 B |        1.00 |
| Respire_Get_Pipelined          | GET x200 pipelined   |   2.168 μs | 0.0159 μs | 0.0234 μs |  0.89 | Faster          |    0.02 |      - |      60 B |        0.21 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.224 μs | 0.0439 μs | 0.0643 μs |  1.00 | Baseline        |    0.02 |      - |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   5.372 μs | 0.0292 μs | 0.0437 μs |  1.03 | Same            |    0.02 |      - |      52 B |        0.18 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HGet             | HGET                 | 188.364 μs | 0.5778 μs | 0.8286 μs |  1.00 | Baseline        |    0.01 |      - |     519 B |        1.00 |
| Respire_HGet                   | HGET                 | 187.562 μs | 0.7286 μs | 1.0680 μs |  1.00 | Same            |    0.01 |      - |      48 B |        0.09 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HSet             | HSET                 | 188.174 μs | 0.5662 μs | 0.8475 μs |  1.00 | Baseline        |    0.01 |      - |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 188.747 μs | 0.5352 μs | 0.8011 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Incr             | INCR                 | 186.692 μs | 0.5445 μs | 0.7981 μs |  1.00 | Baseline        |    0.01 |      - |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 187.602 μs | 0.4916 μs | 0.7205 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 365.665 μs | 0.8800 μs | 1.2899 μs |  1.00 | Baseline        |    0.00 |      - |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 363.492 μs | 0.9374 μs | 1.3443 μs |  0.99 | Same            |    0.00 |      - |     256 B |        0.34 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping             | PING                 | 183.900 μs | 0.4075 μs | 0.5973 μs |  1.00 | Baseline        |    0.00 |      - |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 185.260 μs | 0.6381 μs | 0.9353 μs |  1.01 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 170.068 μs | 0.8655 μs | 1.2955 μs |  1.00 | Baseline        |    0.01 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 167.511 μs | 1.0540 μs | 1.5775 μs |  0.99 | Same            |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 186.381 μs | 0.4810 μs | 0.7199 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 187.862 μs | 0.4338 μs | 0.6358 μs |  1.01 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 201.208 μs | 1.3117 μs | 1.9633 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 201.726 μs | 0.6679 μs | 0.9997 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 188.467 μs | 0.5935 μs | 0.8699 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 186.705 μs | 0.7039 μs | 1.0096 μs |  0.99 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 189.449 μs | 0.5613 μs | 0.8401 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 188.531 μs | 0.8931 μs | 1.3368 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 173.344 μs | 0.9137 μs | 1.3676 μs |  1.00 | Baseline        |    0.01 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 171.677 μs | 0.8773 μs | 1.3131 μs |  0.99 | Same            |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 362.766 μs | 0.9744 μs | 1.4584 μs |  1.00 | Baseline        |    0.01 |      - |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 361.030 μs | 0.9169 μs | 1.3440 μs |  1.00 | Same            |    0.01 |      - |     200 B |        0.31 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 186.203 μs | 0.5310 μs | 0.7616 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 186.367 μs | 1.5007 μs | 2.2462 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |

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
| Method                              | Categories      | Mean         | Error       | StdDev      | Op/s        | Ratio | MannWhitney(5%) | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------------ |---------------- |-------------:|------------:|------------:|------------:|------:|---------------- |--------:|-------:|----------:|------------:|
| StackExchange_Exists_ServerRead     | EXISTS hot      | 189,563.6 ns | 1,192.07 ns | 1,709.63 ns |     5,275.3 | 1.000 | Baseline        |    0.01 |      - |     296 B |        1.00 |
| Respire_Exists_ServerRead           | EXISTS hot      | 188,911.2 ns |   754.25 ns | 1,128.92 ns |     5,293.5 | 0.997 | Same            |    0.01 |      - |         - |        0.00 |
| Respire_Exists_ClientCacheHit       | EXISTS hot      |     667.7 ns |    14.82 ns |    21.25 ns | 1,497,681.5 | 0.004 | Faster          |    0.00 |      - |         - |        0.00 |
|                                     |                 |              |             |             |             |       |                 |         |        |           |             |
| StackExchange_Get_ServerRead        | GET hot         | 194,231.7 ns |   934.90 ns | 1,399.32 ns |     5,148.5 | 1.000 | Baseline        |    0.01 |      - |     528 B |        1.00 |
| Respire_Get_ServerRead              | GET hot         | 187,533.8 ns | 1,960.36 ns | 2,934.17 ns |     5,332.4 | 0.966 | Same            |    0.02 |      - |      64 B |        0.12 |
| Respire_Get_ClientCacheHit          | GET hot         |     410.3 ns |    21.56 ns |    32.26 ns | 2,437,531.4 | 0.002 | Faster          |    0.00 | 0.0038 |      64 B |        0.12 |
|                                     |                 |              |             |             |             |       |                 |         |        |           |             |
| StackExchange_GetMissing_ServerRead | GET missing hot | 191,978.9 ns |   915.21 ns | 1,369.85 ns |     5,208.9 | 1.000 | Baseline        |    0.01 |      - |     416 B |        1.00 |
| Respire_GetMissing_ServerRead       | GET missing hot | 187,536.2 ns |   903.60 ns | 1,352.47 ns |     5,332.3 | 0.977 | Same            |    0.01 |      - |         - |        0.00 |
| Respire_GetMissing_ClientCacheHit   | GET missing hot |     426.5 ns |     2.70 ns |     3.87 ns | 2,344,467.8 | 0.002 | Faster          |    0.00 |      - |         - |        0.00 |
|                                     |                 |              |             |             |             |       |                 |         |        |           |             |
| StackExchange_HGet_ServerRead       | HGET hot        | 196,282.2 ns |   969.02 ns | 1,450.39 ns |     5,094.7 | 1.000 | Baseline        |    0.01 |      - |     544 B |        1.00 |
| Respire_HGet_ServerRead             | HGET hot        | 190,617.7 ns | 1,216.17 ns | 1,820.31 ns |     5,246.1 | 0.971 | Same            |    0.01 |      - |      64 B |        0.12 |
| Respire_HGet_ClientCacheHit         | HGET hot        |     784.5 ns |     2.98 ns |     4.46 ns | 1,274,649.4 | 0.004 | Faster          |    0.00 | 0.0038 |      64 B |        0.12 |

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
| StackExchange_Exists           | EXISTS               | 188.862 μs | 0.7833 μs | 1.1724 μs |  1.00 | Baseline        |    0.01 |      - |     295 B |        1.00 |
| Respire_Exists                 | EXISTS               | 188.664 μs | 0.6470 μs | 0.9484 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get              | GET                  | 194.517 μs | 1.1393 μs | 1.6699 μs |  1.00 | Baseline        |    0.01 |      - |     504 B |        1.00 |
| Respire_Get                    | GET                  | 189.001 μs | 0.7735 μs | 1.1338 μs |  0.97 | Same            |    0.01 |      - |      48 B |        0.10 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 177.862 μs | 1.2545 μs | 1.8777 μs |  1.00 | Baseline        |    0.01 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 175.956 μs | 1.0131 μs | 1.5164 μs |  0.99 | Same            |    0.01 |      - |      50 B |        0.15 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Pipelined    | GET x200 pipelined   |   2.477 μs | 0.0589 μs | 0.0863 μs |  1.00 | Baseline        |    0.05 | 0.0098 |     289 B |        1.00 |
| Respire_Get_Pipelined          | GET x200 pipelined   |   2.258 μs | 0.0106 μs | 0.0142 μs |  0.91 | Faster          |    0.03 |      - |      61 B |        0.21 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.468 μs | 0.0416 μs | 0.0596 μs |  1.00 | Baseline        |    0.02 |      - |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   5.584 μs | 0.0469 μs | 0.0687 μs |  1.02 | Same            |    0.02 |      - |      52 B |        0.18 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HGet             | HGET                 | 195.274 μs | 0.8941 μs | 1.3382 μs |  1.00 | Baseline        |    0.01 |      - |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 188.864 μs | 0.8279 μs | 1.1873 μs |  0.97 | Same            |    0.01 |      - |      48 B |        0.09 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HSet             | HSET                 | 191.402 μs | 0.5375 μs | 0.7879 μs |  1.00 | Baseline        |    0.01 |      - |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 191.500 μs | 0.6796 μs | 1.0172 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Incr             | INCR                 | 189.843 μs | 0.6998 μs | 1.0474 μs |  1.00 | Baseline        |    0.01 |      - |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 189.962 μs | 0.8725 μs | 1.2514 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 379.630 μs | 1.6683 μs | 2.4970 μs |  1.00 | Baseline        |    0.01 |      - |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 377.081 μs | 1.3378 μs | 2.0024 μs |  0.99 | Same            |    0.01 |      - |     256 B |        0.34 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping             | PING                 | 189.105 μs | 0.8354 μs | 1.2503 μs |  1.00 | Baseline        |    0.01 |      - |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 189.098 μs | 0.9210 μs | 1.3786 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 175.847 μs | 1.4400 μs | 2.1554 μs |  1.00 | Baseline        |    0.02 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 172.981 μs | 1.6639 μs | 2.4905 μs |  0.98 | Same            |    0.02 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 191.813 μs | 0.9327 μs | 1.3960 μs |  1.00 | Baseline        |    0.01 |      - |     311 B |        1.00 |
| Respire_SAdd                   | SADD                 | 192.364 μs | 0.7349 μs | 1.1000 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 206.046 μs | 2.3148 μs | 3.4646 μs |  1.00 | Baseline        |    0.02 |      - |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 208.613 μs | 1.0108 μs | 1.4817 μs |  1.01 | Same            |    0.02 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 192.074 μs | 1.1543 μs | 1.6920 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 193.051 μs | 1.0115 μs | 1.4506 μs |  1.01 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 194.245 μs | 0.9405 μs | 1.4076 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 195.164 μs | 0.9278 μs | 1.3600 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 180.709 μs | 0.8740 μs | 1.2534 μs |  1.00 | Baseline        |    0.01 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 177.559 μs | 1.1752 μs | 1.7225 μs |  0.98 | Same            |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 373.892 μs | 1.2855 μs | 1.8842 μs |  1.00 | Baseline        |    0.01 |      - |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 372.739 μs | 1.3841 μs | 2.0287 μs |  1.00 | Same            |    0.01 |      - |     200 B |        0.31 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 189.025 μs | 0.9568 μs | 1.4025 μs |  1.00 | Baseline        |    0.01 |      - |     311 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 189.217 μs | 1.0147 μs | 1.5188 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
