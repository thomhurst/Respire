---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

import ComparisonBarChart from '@site/src/components/ComparisonBarChart';

# Benchmarks

:::info Automated results
Generated 2026-11-08 10:19 UTC from commit `7dbbee6a7609`. These results are refreshed weekly.
:::

StackExchange.Redis is the baseline. A ratio below `1.00` means Respire completed the operation faster.

Every benchmark ran 3 times, each time on a separate machine. Both clients in a run share that machine, so each run gives its own ratio. Read the median ratio: a single run on a shared machine can move by several percent.

## Visual comparison

Each pair of bars comes from the run whose Respire / StackExchange.Redis ratio is the median of all runs.

StackExchange.Redis has no built-in server-assisted client cache, so its values are ordinary server reads. Respire server reads are included for a like-for-like uncached comparison.

<ComparisonBarChart
  title="Client-cache hit time — net10.0"
  description="StackExchange.Redis and Respire server reads vs Respire client-cache hit. Shorter bars are faster."
  format="duration-ns"
  respireLabel="Respire cache hit"
  scale="group"
  showRatio
  data={[{"label":"EXISTS hot","other":95086.17,"respire":238.95,"respireServer":92608.83},{"label":"GET hot","other":99054.11,"respire":88.61,"respireServer":94655.61},{"label":"GET missing hot","other":96846.26,"respire":89.18,"respireServer":93483.43},{"label":"HGET hot","other":98277.46,"respire":251.69,"respireServer":96767.1}]}
/>

<ComparisonBarChart
  title="Selected operation time — net10.0"
  description="Mean time. Shorter bars are faster."
  format="duration-ns"
  scale="group"
  showRatio
  data={[{"label":"GET","other":178458.0,"respire":172170.0},{"label":"GET x200 pipelined","other":1386.0,"respire":1239.0},{"label":"GET x50 concurrent","other":2701.0,"respire":2485.0},{"label":"HGET","other":176772.0,"respire":171612.0},{"label":"HSET","other":96518.0,"respire":92084.0},{"label":"LPUSH+LPOP","other":185059.0,"respire":183241.0},{"label":"SET 1KB","other":175820.0,"respire":174854.0}]}
/>

## Ratio across runs

- Run 1: Intel Xeon 6973P-C 3.32GHz
- Run 2: AMD EPYC 7763 2.65GHz
- Run 3: unknown CPU

### ClientSideCachingBenchmarks

| Method | Categories | Run 1 | Run 2 | Run 3 | Median |
|---|---|---:|---:|---:|---:|
| Respire_Exists_ServerRead | EXISTS hot | 0.97 | 1.00 | 0.98 | **0.98** |
| Respire_Exists_ClientCacheHit | EXISTS hot | 0.00 | 0.00 | 0.00 | **0.00** |
| Respire_Get_ServerRead | GET hot | 0.96 | 0.97 | 0.97 | **0.97** |
| Respire_Get_ClientCacheHit | GET hot | 0.00 | 0.00 | 0.00 | **0.00** |
| Respire_GetMissing_ServerRead | GET missing hot | 0.97 | 0.98 | 1.00 | **0.98** |
| Respire_GetMissing_ClientCacheHit | GET missing hot | 0.00 | 0.00 | 0.00 | **0.00** |
| Respire_HGet_ServerRead | HGET hot | 0.98 | 0.97 | 0.95 | **0.97** |
| Respire_HGet_ClientCacheHit | HGET hot | 0.00 | 0.00 | 0.00 | **0.00** |

### CommonOperationsBenchmarks

| Method | Categories | Run 1 | Run 2 | Run 3 | Median |
|---|---|---:|---:|---:|---:|
| Respire_Exists | EXISTS | 0.98 | 1.00 | 0.99 | **0.99** |
| Respire_Get | GET | 0.95 | 0.96 | 0.99 | **0.96** |
| Respire_GetLong | GET long | 0.97 | 0.96 | 1.00 | **0.97** |
| Respire_Get_SteadyState | GET x100 sequential | 0.92 | 0.92 | 0.96 | **0.92** |
| Respire_Get_Pipelined | GET x200 pipelined | 0.89 | 0.88 | 0.96 | **0.89** |
| Respire_Get_Concurrent | GET x50 concurrent | 0.92 | 0.91 | 0.92 | **0.92** |
| Respire_HGet | HGET | 0.97 | 0.97 | 0.96 | **0.97** |
| Respire_HSet | HSET | 0.95 | 1.01 | 0.95 | **0.95** |
| Respire_Incr | INCR | 1.02 | 0.99 | 1.00 | **1.00** |
| Respire_LPushLPop | LPUSH+LPOP | 0.99 | 1.00 | 0.98 | **0.99** |
| Respire_Ping | PING | 0.98 | 1.00 | 0.97 | **0.98** |
| Respire_Ping_SteadyState | PING x100 sequential | 0.96 | 0.96 | 0.95 | **0.96** |
| Respire_SAdd | SADD | 1.00 | 0.99 | 0.98 | **0.99** |
| Respire_Set_10KB | SET 10KB | 0.97 | 0.99 | 1.00 | **0.99** |
| Respire_Set_Small | SET 13B | 0.97 | 1.00 | 0.97 | **0.97** |
| Respire_Set_1KB | SET 1KB | 0.99 | 0.99 | 1.00 | **0.99** |
| Respire_Set_SteadyState | SET x100 sequential | 0.97 | 0.94 | 0.99 | **0.97** |
| Respire_SetDel | SET+DEL | 0.95 | 0.99 | 0.99 | **0.99** |
| Respire_SIsMember | SISMEMBER | 0.97 | 0.99 | 0.99 | **0.99** |

### PubSubComparisonBenchmarks

| Method | Run 1 | Run 2 | Run 3 | Median |
|---|---:|---:|---:|---:|
| Respire_PublishAndConsume | 1.01 | 1.03 | 1.03 | **1.03** |

## net10.0

### ClientSideCachingBenchmarks

#### Run 1

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
Intel Xeon 6973P-C 3.32GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.403
  [Host]     : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v4
  Job-IDGKZI : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v4

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                              | Categories      | Mean         | Error        | StdDev       | Op/s         | Ratio | MannWhitney(5%) | RatioSD | Allocated | Alloc Ratio |
|------------------------------------ |---------------- |-------------:|-------------:|-------------:|-------------:|------:|---------------- |--------:|----------:|------------:|
| StackExchange_Exists_ServerRead     | EXISTS hot      | 95,086.17 ns | 1,199.426 ns | 1,758.104 ns |     10,516.8 | 1.000 | Baseline        |    0.03 |     296 B |        1.00 |
| Respire_Exists_ServerRead           | EXISTS hot      | 92,608.83 ns | 2,576.096 ns | 3,855.780 ns |     10,798.1 | 0.974 | Same            |    0.04 |         - |        0.00 |
| Respire_Exists_ClientCacheHit       | EXISTS hot      |    238.95 ns |     3.848 ns |     5.519 ns |  4,184,900.7 | 0.003 | Faster          |    0.00 |         - |        0.00 |
|                                     |                 |              |              |              |              |       |                 |         |           |             |
| StackExchange_Get_ServerRead        | GET hot         | 99,054.11 ns | 1,361.704 ns | 1,995.969 ns |     10,095.5 | 1.000 | Baseline        |    0.03 |     528 B |        1.00 |
| Respire_Get_ServerRead              | GET hot         | 94,655.61 ns | 1,877.335 ns | 2,751.773 ns |     10,564.6 | 0.956 | Same            |    0.03 |      64 B |        0.12 |
| Respire_Get_ClientCacheHit          | GET hot         |     88.61 ns |     1.778 ns |     2.492 ns | 11,285,671.9 | 0.001 | Faster          |    0.00 |         - |        0.00 |
|                                     |                 |              |              |              |              |       |                 |         |           |             |
| StackExchange_GetMissing_ServerRead | GET missing hot | 96,846.26 ns | 1,743.373 ns | 2,555.414 ns |     10,325.6 | 1.001 | Baseline        |    0.04 |     416 B |        1.00 |
| Respire_GetMissing_ServerRead       | GET missing hot | 93,483.43 ns | 1,922.246 ns | 2,877.127 ns |     10,697.1 | 0.966 | Same            |    0.04 |         - |        0.00 |
| Respire_GetMissing_ClientCacheHit   | GET missing hot |     89.18 ns |     2.668 ns |     3.652 ns | 11,213,789.1 | 0.001 | Faster          |    0.00 |         - |        0.00 |
|                                     |                 |              |              |              |              |       |                 |         |           |             |
| StackExchange_HGet_ServerRead       | HGET hot        | 98,277.46 ns | 2,067.456 ns | 3,094.470 ns |     10,175.3 | 1.001 | Baseline        |    0.04 |     544 B |        1.00 |
| Respire_HGet_ServerRead             | HGET hot        | 96,767.10 ns | 2,316.802 ns | 3,467.680 ns |     10,334.1 | 0.986 | Same            |    0.05 |      64 B |        0.12 |
| Respire_HGet_ClientCacheHit         | HGET hot        |    251.69 ns |     6.516 ns |     9.135 ns |  3,973,213.8 | 0.003 | Faster          |    0.00 |         - |        0.00 |

#### Run 2

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
AMD EPYC 7763 2.65GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.403
  [Host]     : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v3
  Job-IDGKZI : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v3

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                              | Categories      | Mean         | Error       | StdDev      | Op/s        | Ratio | MannWhitney(5%) | Allocated | Alloc Ratio |
|------------------------------------ |---------------- |-------------:|------------:|------------:|------------:|------:|---------------- |----------:|------------:|
| StackExchange_Exists_ServerRead     | EXISTS hot      | 172,563.3 ns |   753.98 ns | 1,128.52 ns |     5,795.0 | 1.000 | Baseline        |     295 B |        1.00 |
| Respire_Exists_ServerRead           | EXISTS hot      | 171,827.7 ns |   879.83 ns | 1,289.64 ns |     5,819.8 | 0.996 | Same            |         - |        0.00 |
| Respire_Exists_ClientCacheHit       | EXISTS hot      |     355.4 ns |     1.19 ns |     1.74 ns | 2,814,055.6 | 0.002 | Faster          |         - |        0.00 |
|                                     |                 |              |             |             |             |       |                 |           |             |
| StackExchange_Get_ServerRead        | GET hot         | 176,723.7 ns |   990.19 ns | 1,388.11 ns |     5,658.6 | 1.000 | Baseline        |     527 B |        1.00 |
| Respire_Get_ServerRead              | GET hot         | 171,466.0 ns |   767.98 ns | 1,149.47 ns |     5,832.1 | 0.970 | Same            |      64 B |        0.12 |
| Respire_Get_ClientCacheHit          | GET hot         |     139.2 ns |     0.48 ns |     0.70 ns | 7,183,524.5 | 0.001 | Faster          |         - |        0.00 |
|                                     |                 |              |             |             |             |       |                 |           |             |
| StackExchange_GetMissing_ServerRead | GET missing hot | 174,759.4 ns |   676.15 ns |   969.71 ns |     5,722.2 | 1.000 | Baseline        |     416 B |        1.00 |
| Respire_GetMissing_ServerRead       | GET missing hot | 170,693.3 ns | 1,419.97 ns | 2,081.37 ns |     5,858.5 | 0.977 | Same            |         - |        0.00 |
| Respire_GetMissing_ClientCacheHit   | GET missing hot |     143.9 ns |     0.49 ns |     0.74 ns | 6,949,004.8 | 0.001 | Faster          |         - |        0.00 |
|                                     |                 |              |             |             |             |       |                 |           |             |
| StackExchange_HGet_ServerRead       | HGET hot        | 178,586.9 ns |   870.77 ns | 1,276.37 ns |     5,599.5 | 1.000 | Baseline        |     544 B |        1.00 |
| Respire_HGet_ServerRead             | HGET hot        | 173,362.5 ns | 1,160.03 ns | 1,700.36 ns |     5,768.3 | 0.971 | Same            |      64 B |        0.12 |
| Respire_HGet_ClientCacheHit         | HGET hot        |     420.9 ns |     3.61 ns |     5.30 ns | 2,375,740.5 | 0.002 | Faster          |         - |        0.00 |

#### Run 3

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
INTEL XEON PLATINUM 8573C 2.87GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.403
  [Host]     : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v4
  Job-IDGKZI : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v4

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                              | Categories      | Mean         | Error       | StdDev      | Median       | Op/s        | Ratio | MannWhitney(5%) | RatioSD | Allocated | Alloc Ratio |
|------------------------------------ |---------------- |-------------:|------------:|------------:|-------------:|------------:|------:|---------------- |--------:|----------:|------------:|
| StackExchange_Exists_ServerRead     | EXISTS hot      | 102,605.7 ns |   655.69 ns |   981.41 ns | 102,209.0 ns |     9,746.1 | 1.000 | Baseline        |    0.01 |     296 B |        1.00 |
| Respire_Exists_ServerRead           | EXISTS hot      | 101,050.4 ns |   977.04 ns | 1,462.39 ns | 101,410.0 ns |     9,896.1 | 0.985 | Same            |    0.02 |         - |        0.00 |
| Respire_Exists_ClientCacheHit       | EXISTS hot      |     312.9 ns |     1.48 ns |     2.12 ns |     313.2 ns | 3,195,473.4 | 0.003 | Faster          |    0.00 |         - |        0.00 |
|                                     |                 |              |             |             |              |             |       |                 |         |           |             |
| StackExchange_Get_ServerRead        | GET hot         | 104,383.8 ns |   508.07 ns |   760.46 ns | 104,466.6 ns |     9,580.0 | 1.000 | Baseline        |    0.01 |     528 B |        1.00 |
| Respire_Get_ServerRead              | GET hot         | 101,070.0 ns |   833.44 ns | 1,221.64 ns | 101,110.1 ns |     9,894.1 | 0.968 | Same            |    0.01 |      64 B |        0.12 |
| Respire_Get_ClientCacheHit          | GET hot         |     125.8 ns |     5.41 ns |     7.94 ns |     132.7 ns | 7,947,123.5 | 0.001 | Faster          |    0.00 |         - |        0.00 |
|                                     |                 |              |             |             |              |             |       |                 |         |           |             |
| StackExchange_GetMissing_ServerRead | GET missing hot | 100,610.8 ns | 1,230.17 ns | 1,841.27 ns | 101,271.7 ns |     9,939.3 | 1.000 | Baseline        |    0.03 |     416 B |        1.00 |
| Respire_GetMissing_ServerRead       | GET missing hot | 100,868.7 ns |   906.24 ns | 1,356.42 ns | 100,899.9 ns |     9,913.9 | 1.003 | Same            |    0.02 |         - |        0.00 |
| Respire_GetMissing_ClientCacheHit   | GET missing hot |     123.5 ns |     0.98 ns |     1.41 ns |     123.5 ns | 8,095,498.8 | 0.001 | Faster          |    0.00 |         - |        0.00 |
|                                     |                 |              |             |             |              |             |       |                 |         |           |             |
| StackExchange_HGet_ServerRead       | HGET hot        | 104,357.0 ns |   836.84 ns | 1,252.55 ns | 104,616.7 ns |     9,582.5 | 1.000 | Baseline        |    0.02 |     544 B |        1.00 |
| Respire_HGet_ServerRead             | HGET hot        |  99,044.6 ns | 1,425.06 ns | 2,132.96 ns |  98,630.2 ns |    10,096.5 | 0.949 | Same            |    0.02 |      64 B |        0.12 |
| Respire_HGet_ClientCacheHit         | HGET hot        |     335.4 ns |     2.98 ns |     4.18 ns |     337.9 ns | 2,981,785.4 | 0.003 | Faster          |    0.00 |         - |        0.00 |

### CommonOperationsBenchmarks

#### Run 1

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
Intel Xeon 6973P-C 3.32GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.403
  [Host]     : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v4
  Job-IDGKZI : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v4

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                         | Categories           | Mean       | Error     | StdDev    | Ratio | MannWhitney(5%) | RatioSD | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|----------:|----------:|------:|---------------- |--------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               |  92.296 μs | 1.1826 μs | 1.7334 μs |  1.00 | Baseline        |    0.03 |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               |  90.460 μs | 2.2415 μs | 3.2856 μs |  0.98 | Same            |    0.04 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Get              | GET                  |  94.420 μs | 1.2819 μs | 1.9187 μs |  1.00 | Baseline        |    0.03 |     504 B |        1.00 |
| Respire_Get                    | GET                  |  89.416 μs | 2.4029 μs | 3.5221 μs |  0.95 | Same            |    0.04 |      48 B |        0.10 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_GetLong          | GET long             |  94.720 μs | 1.2950 μs | 1.8982 μs |  1.00 | Baseline        |    0.03 |     416 B |        1.00 |
| Respire_GetLong                | GET long             |  91.423 μs | 0.9894 μs | 1.4502 μs |  0.97 | Same            |    0.02 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  |  79.394 μs | 1.8209 μs | 2.6114 μs |  1.00 | Baseline        |    0.05 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  |  73.214 μs | 2.5775 μs | 3.6132 μs |  0.92 | Same            |    0.05 |      50 B |        0.15 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Get_Pipelined    | GET x200 pipelined   |   1.386 μs | 0.0302 μs | 0.0433 μs |  1.00 | Baseline        |    0.04 |     289 B |        1.00 |
| Respire_Get_Pipelined          | GET x200 pipelined   |   1.239 μs | 0.0452 μs | 0.0663 μs |  0.89 | Faster          |    0.05 |      49 B |        0.17 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   2.701 μs | 0.0743 μs | 0.1112 μs |  1.00 | Baseline        |    0.06 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   2.485 μs | 0.0194 μs | 0.0279 μs |  0.92 | Same            |    0.04 |      52 B |        0.18 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_HGet             | HGET                 |  97.004 μs | 2.0432 μs | 3.0581 μs |  1.00 | Baseline        |    0.04 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 |  94.214 μs | 1.0609 μs | 1.5880 μs |  0.97 | Same            |    0.03 |      48 B |        0.09 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_HSet             | HSET                 |  96.518 μs | 2.0560 μs | 2.9487 μs |  1.00 | Baseline        |    0.04 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 |  92.084 μs | 1.7113 μs | 2.5613 μs |  0.95 | Same            |    0.04 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Incr             | INCR                 |  93.434 μs | 1.4199 μs | 2.1252 μs |  1.00 | Baseline        |    0.03 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 |  95.645 μs | 1.2497 μs | 1.8704 μs |  1.02 | Same            |    0.03 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 185.059 μs | 3.2164 μs | 4.8142 μs |  1.00 | Baseline        |    0.04 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 183.241 μs | 4.0779 μs | 5.8485 μs |  0.99 | Same            |    0.04 |     256 B |        0.34 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Ping             | PING                 |  93.949 μs | 2.1421 μs | 3.1398 μs |  1.00 | Baseline        |    0.05 |     304 B |        1.00 |
| Respire_Ping                   | PING                 |  92.269 μs | 1.6656 μs | 2.3888 μs |  0.98 | Same            |    0.04 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential |  79.951 μs | 1.9777 μs | 2.9601 μs |  1.00 | Baseline        |    0.05 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential |  76.437 μs | 1.3100 μs | 1.9608 μs |  0.96 | Same            |    0.04 |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_SAdd             | SADD                 |  88.724 μs | 0.8371 μs | 1.2270 μs |  1.00 | Baseline        |    0.02 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 |  88.407 μs | 0.8704 μs | 1.2483 μs |  1.00 | Same            |    0.02 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 101.797 μs | 3.8883 μs | 5.8198 μs |  1.00 | Baseline        |    0.08 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             |  99.036 μs | 1.6612 μs | 2.4864 μs |  0.98 | Same            |    0.06 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Set_Small        | SET 13B              |  95.628 μs | 2.6178 μs | 3.9182 μs |  1.00 | Baseline        |    0.06 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              |  92.586 μs | 1.6382 μs | 2.4519 μs |  0.97 | Same            |    0.05 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              |  94.585 μs | 0.9596 μs | 1.3762 μs |  1.00 | Baseline        |    0.02 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              |  93.751 μs | 1.2139 μs | 1.7017 μs |  0.99 | Same            |    0.02 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  |  80.536 μs | 1.2751 μs | 1.7454 μs |  1.00 | Baseline        |    0.03 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  |  77.897 μs | 1.0045 μs | 1.4724 μs |  0.97 | Same            |    0.03 |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 180.713 μs | 4.3913 μs | 6.5727 μs |  1.00 | Baseline        |    0.05 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 171.858 μs | 2.8459 μs | 4.0814 μs |  0.95 | Same            |    0.04 |     200 B |        0.31 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            |  93.101 μs | 0.8562 μs | 1.2550 μs |  1.00 | Baseline        |    0.02 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            |  90.506 μs | 1.5224 μs | 2.2786 μs |  0.97 | Same            |    0.03 |         - |        0.00 |

#### Run 2

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
AMD EPYC 7763 2.65GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.403
  [Host]     : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v3
  Job-IDGKZI : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v3

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                         | Categories           | Mean       | Error     | StdDev    | Ratio | MannWhitney(5%) | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|----------:|----------:|------:|---------------- |--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 171.903 μs | 0.7231 μs | 1.0823 μs |  1.00 | Baseline        |    0.01 |      - |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 172.441 μs | 0.8462 μs | 1.2403 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get              | GET                  | 178.458 μs | 1.2173 μs | 1.7842 μs |  1.00 | Baseline        |    0.01 |      - |     504 B |        1.00 |
| Respire_Get                    | GET                  | 172.170 μs | 0.5308 μs | 0.7780 μs |  0.96 | Same            |    0.01 |      - |      48 B |        0.10 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_GetLong          | GET long             | 178.778 μs | 1.4571 μs | 2.1358 μs |  1.00 | Baseline        |    0.02 |      - |     415 B |        1.00 |
| Respire_GetLong                | GET long             | 172.386 μs | 0.5747 μs | 0.8057 μs |  0.96 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 159.924 μs | 1.2956 μs | 1.8582 μs |  1.00 | Baseline        |    0.02 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 146.881 μs | 1.5037 μs | 2.2041 μs |  0.92 | Faster          |    0.02 |      - |      50 B |        0.15 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Pipelined    | GET x200 pipelined   |   2.315 μs | 0.0614 μs | 0.0900 μs |  1.00 | Baseline        |    0.06 | 0.0098 |     289 B |        1.00 |
| Respire_Get_Pipelined          | GET x200 pipelined   |   2.044 μs | 0.0101 μs | 0.0139 μs |  0.88 | Faster          |    0.04 |      - |      49 B |        0.17 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.171 μs | 0.0366 μs | 0.0525 μs |  1.00 | Baseline        |    0.01 |      - |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   4.727 μs | 0.0256 μs | 0.0367 μs |  0.91 | Faster          |    0.01 |      - |      52 B |        0.18 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HGet             | HGET                 | 176.772 μs | 0.9518 μs | 1.4246 μs |  1.00 | Baseline        |    0.01 |      - |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 171.612 μs | 0.6138 μs | 0.9188 μs |  0.97 | Same            |    0.01 |      - |      48 B |        0.09 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HSet             | HSET                 | 174.481 μs | 0.6720 μs | 1.0059 μs |  1.00 | Baseline        |    0.01 |      - |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 175.836 μs | 1.4757 μs | 2.2088 μs |  1.01 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Incr             | INCR                 | 173.240 μs | 0.8140 μs | 1.2184 μs |  1.00 | Baseline        |    0.01 |      - |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 172.322 μs | 0.7663 μs | 1.0742 μs |  0.99 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 351.512 μs | 2.7063 μs | 3.9669 μs |  1.00 | Baseline        |    0.02 |      - |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 351.756 μs | 1.4366 μs | 2.1058 μs |  1.00 | Same            |    0.01 |      - |     256 B |        0.34 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping             | PING                 | 172.875 μs | 0.6380 μs | 0.9351 μs |  1.00 | Baseline        |    0.01 |      - |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 172.373 μs | 0.7273 μs | 1.0661 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 153.124 μs | 1.2621 μs | 1.8500 μs |  1.00 | Baseline        |    0.02 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 146.418 μs | 0.6654 μs | 0.9960 μs |  0.96 | Same            |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 174.039 μs | 0.9281 μs | 1.3604 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 173.097 μs | 0.8360 μs | 1.1720 μs |  0.99 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 186.767 μs | 0.9389 μs | 1.3762 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 185.206 μs | 1.0988 μs | 1.6446 μs |  0.99 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 174.012 μs | 0.7485 μs | 1.1204 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 173.150 μs | 0.8673 μs | 1.2713 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 175.820 μs | 0.8826 μs | 1.3210 μs |  1.00 | Baseline        |    0.01 |      - |     311 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 174.854 μs | 0.6681 μs | 0.9793 μs |  0.99 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 158.182 μs | 1.6987 μs | 2.5426 μs |  1.00 | Baseline        |    0.02 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 149.469 μs | 1.0561 μs | 1.5480 μs |  0.95 | Same            |    0.02 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 344.088 μs | 1.8864 μs | 2.8234 μs |  1.00 | Baseline        |    0.01 |      - |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 339.703 μs | 1.4575 μs | 2.0903 μs |  0.99 | Same            |    0.01 |      - |     200 B |        0.31 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 172.122 μs | 0.7409 μs | 1.0860 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 170.874 μs | 0.4895 μs | 0.7327 μs |  0.99 | Same            |    0.01 |      - |         - |        0.00 |

#### Run 3

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
INTEL XEON PLATINUM 8573C 2.87GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.403
  [Host]     : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v4
  Job-IDGKZI : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v4

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                         | Categories           | Mean       | Error     | StdDev    | Median     | Ratio | MannWhitney(5%) | RatioSD | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|----------:|----------:|-----------:|------:|---------------- |--------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               |  99.044 μs | 1.0781 μs | 1.6136 μs |  98.925 μs |  1.00 | Baseline        |    0.02 |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               |  98.545 μs | 0.4038 μs | 0.5661 μs |  98.657 μs |  1.00 | Same            |    0.02 |         - |        0.00 |
|                                |                      |            |           |           |            |       |                 |         |           |             |
| StackExchange_Get              | GET                  |  99.531 μs | 1.1023 μs | 1.6158 μs |  99.105 μs |  1.00 | Baseline        |    0.02 |     504 B |        1.00 |
| Respire_Get                    | GET                  |  98.233 μs | 0.5311 μs | 0.7949 μs |  98.303 μs |  0.99 | Same            |    0.02 |      48 B |        0.10 |
|                                |                      |            |           |           |            |       |                 |         |           |             |
| StackExchange_GetLong          | GET long             |  99.824 μs | 0.9865 μs | 1.4766 μs | 100.077 μs |  1.00 | Baseline        |    0.02 |     416 B |        1.00 |
| Respire_GetLong                | GET long             |  99.661 μs | 1.5939 μs | 2.3857 μs |  99.992 μs |  1.00 | Same            |    0.03 |         - |        0.00 |
|                                |                      |            |           |           |            |       |                 |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  |  85.726 μs | 0.6154 μs | 0.9211 μs |  85.699 μs |  1.00 | Baseline        |    0.01 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  |  82.462 μs | 1.0063 μs | 1.5062 μs |  82.679 μs |  0.96 | Same            |    0.02 |      50 B |        0.15 |
|                                |                      |            |           |           |            |       |                 |         |           |             |
| StackExchange_Get_Pipelined    | GET x200 pipelined   |   1.733 μs | 0.0856 μs | 0.1282 μs |   1.760 μs |  1.01 | Baseline        |    0.11 |     289 B |        1.00 |
| Respire_Get_Pipelined          | GET x200 pipelined   |   1.669 μs | 0.0151 μs | 0.0221 μs |   1.671 μs |  0.97 | Same            |    0.08 |      49 B |        0.17 |
|                                |                      |            |           |           |            |       |                 |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   3.340 μs | 0.0698 μs | 0.1045 μs |   3.332 μs |  1.00 | Baseline        |    0.04 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   3.086 μs | 0.0345 μs | 0.0495 μs |   3.091 μs |  0.92 | Faster          |    0.03 |      52 B |        0.18 |
|                                |                      |            |           |           |            |       |                 |         |           |             |
| StackExchange_HGet             | HGET                 | 102.718 μs | 1.0725 μs | 1.6053 μs | 102.425 μs |  1.00 | Baseline        |    0.02 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 |  99.069 μs | 1.5225 μs | 2.2788 μs |  97.833 μs |  0.96 | Same            |    0.03 |      48 B |        0.09 |
|                                |                      |            |           |           |            |       |                 |         |           |             |
| StackExchange_HSet             | HSET                 | 105.670 μs | 0.6482 μs | 0.9702 μs | 105.810 μs |  1.00 | Baseline        |    0.01 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 100.619 μs | 1.3171 μs | 1.9713 μs | 100.425 μs |  0.95 | Same            |    0.02 |         - |        0.00 |
|                                |                      |            |           |           |            |       |                 |         |           |             |
| StackExchange_Incr             | INCR                 | 100.942 μs | 1.7364 μs | 2.5452 μs | 101.385 μs |  1.00 | Baseline        |    0.04 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 101.043 μs | 0.8479 μs | 1.2691 μs | 101.385 μs |  1.00 | Same            |    0.03 |         - |        0.00 |
|                                |                      |            |           |           |            |       |                 |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 199.472 μs | 1.5085 μs | 2.2112 μs | 199.751 μs |  1.00 | Baseline        |    0.02 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 196.297 μs | 1.0315 μs | 1.4460 μs | 196.267 μs |  0.98 | Same            |    0.01 |     256 B |        0.34 |
|                                |                      |            |           |           |            |       |                 |         |           |             |
| StackExchange_Ping             | PING                 | 100.462 μs | 0.8631 μs | 1.2918 μs | 100.193 μs |  1.00 | Baseline        |    0.02 |     304 B |        1.00 |
| Respire_Ping                   | PING                 |  97.233 μs | 0.9390 μs | 1.4054 μs |  96.928 μs |  0.97 | Same            |    0.02 |         - |        0.00 |
|                                |                      |            |           |           |            |       |                 |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential |  85.464 μs | 0.7269 μs | 1.0880 μs |  85.566 μs |  1.00 | Baseline        |    0.02 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential |  81.470 μs | 1.0514 μs | 1.5737 μs |  81.766 μs |  0.95 | Same            |    0.02 |       2 B |       0.008 |
|                                |                      |            |           |           |            |       |                 |         |           |             |
| StackExchange_SAdd             | SADD                 | 101.926 μs | 0.5435 μs | 0.8135 μs | 101.691 μs |  1.00 | Baseline        |    0.01 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 100.179 μs | 1.1593 μs | 1.6627 μs | 100.425 μs |  0.98 | Same            |    0.02 |         - |        0.00 |
|                                |                      |            |           |           |            |       |                 |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 106.104 μs | 0.8786 μs | 1.3150 μs | 106.134 μs |  1.00 | Baseline        |    0.02 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 106.379 μs | 0.7976 μs | 1.1938 μs | 106.501 μs |  1.00 | Same            |    0.02 |         - |        0.00 |
|                                |                      |            |           |           |            |       |                 |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 103.341 μs | 0.3531 μs | 0.5064 μs | 103.418 μs |  1.00 | Baseline        |    0.01 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 100.716 μs | 0.8633 μs | 1.2921 μs | 100.690 μs |  0.97 | Same            |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |            |       |                 |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 100.952 μs | 1.4235 μs | 2.1306 μs | 100.688 μs |  1.00 | Baseline        |    0.03 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 100.476 μs | 1.7244 μs | 2.5810 μs | 100.278 μs |  1.00 | Same            |    0.03 |         - |        0.00 |
|                                |                      |            |           |           |            |       |                 |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  |  88.216 μs | 1.1634 μs | 1.7413 μs |  88.011 μs |  1.00 | Baseline        |    0.03 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  |  87.473 μs | 0.6769 μs | 0.9921 μs |  87.660 μs |  0.99 | Same            |    0.02 |       2 B |       0.008 |
|                                |                      |            |           |           |            |       |                 |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 197.809 μs | 1.9560 μs | 2.9276 μs | 198.168 μs |  1.00 | Baseline        |    0.02 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 195.711 μs | 1.5027 μs | 2.2026 μs | 196.275 μs |  0.99 | Same            |    0.02 |     200 B |        0.31 |
|                                |                      |            |           |           |            |       |                 |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 103.008 μs | 0.3276 μs | 0.4802 μs | 103.073 μs |  1.00 | Baseline        |    0.01 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 101.951 μs | 0.6759 μs | 1.0117 μs | 102.069 μs |  0.99 | Same            |    0.01 |         - |        0.00 |

### PubSubComparisonBenchmarks

#### Run 1

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
Intel Xeon 6973P-C 3.32GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.403
  [Host]     : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v4
  Job-IDGKZI : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v4

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                          | Mean     | Error   | StdDev  | Ratio | MannWhitney(5%) | RatioSD | Allocated | Alloc Ratio |
|-------------------------------- |---------:|--------:|--------:|------:|---------------- |--------:|----------:|------------:|
| StackExchange_PublishAndConsume | 105.6 μs | 1.23 μs | 1.68 μs |  1.00 | Baseline        |    0.02 |     672 B |        1.00 |
| Respire_PublishAndConsume       | 107.0 μs | 2.05 μs | 3.01 μs |  1.01 | Same            |    0.03 |     400 B |        0.60 |

#### Run 2

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
AMD EPYC 7763 2.65GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.403
  [Host]     : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v3
  Job-IDGKZI : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v3

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                          | Mean     | Error   | StdDev  | Ratio | MannWhitney(5%) | Allocated | Alloc Ratio |
|-------------------------------- |---------:|--------:|--------:|------:|---------------- |----------:|------------:|
| StackExchange_PublishAndConsume | 204.3 μs | 0.94 μs | 1.38 μs |  1.00 | Baseline        |     671 B |        1.00 |
| Respire_PublishAndConsume       | 210.0 μs | 1.18 μs | 1.69 μs |  1.03 | Same            |     399 B |        0.59 |

#### Run 3

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
INTEL XEON PLATINUM 8573C 2.87GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.403
  [Host]     : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v4
  Job-IDGKZI : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v4

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                          | Mean     | Error   | StdDev  | Ratio | MannWhitney(5%) | RatioSD | Allocated | Alloc Ratio |
|-------------------------------- |---------:|--------:|--------:|------:|---------------- |--------:|----------:|------------:|
| StackExchange_PublishAndConsume | 113.4 μs | 2.15 μs | 3.21 μs |  1.00 | Baseline        |    0.04 |     672 B |        1.00 |
| Respire_PublishAndConsume       | 117.0 μs | 0.66 μs | 0.97 μs |  1.03 | Same            |    0.03 |     400 B |        0.60 |

## Reading the results

These runs use shared Linux machines and a local Redis container, so expect some variation between runs. Before making an important decision, measure with your own payload sizes, concurrency, Redis deployment, and network.
