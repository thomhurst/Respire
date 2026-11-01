---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

import ComparisonBarChart from '@site/src/components/ComparisonBarChart';

# Benchmarks

:::info Automated results
Generated 2026-11-01 18:33 UTC from commit `28442b837555`. These results are refreshed weekly.
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
  data={[{"label":"EXISTS hot","other":166750.7,"respire":351.0,"respireServer":166359.2},{"label":"GET hot","other":169025.2,"respire":138.5,"respireServer":166710.9},{"label":"GET missing hot","other":167744.1,"respire":144.6,"respireServer":166678.4},{"label":"HGET hot","other":170012.7,"respire":473.8,"respireServer":166657.8}]}
/>

<ComparisonBarChart
  title="Selected operation time — net10.0"
  description="Mean time. Shorter bars are faster."
  format="duration-ns"
  scale="group"
  showRatio
  data={[{"label":"GET","other":168846.0,"respire":166737.0},{"label":"GET x200 pipelined","other":2261.0,"respire":2015.0000000000002},{"label":"GET x50 concurrent","other":5017.0,"respire":4564.0},{"label":"HGET","other":168795.0,"respire":166375.0},{"label":"HSET","other":166950.0,"respire":166847.0},{"label":"LPUSH+LPOP","other":340662.0,"respire":336491.0},{"label":"SET 1KB","other":167776.0,"respire":167412.0}]}
/>

## Ratio across runs

- Run 1: AMD EPYC 7763 3.06GHz
- Run 2: AMD EPYC 7763 2.81GHz
- Run 3: AMD EPYC 9V74 3.67GHz

### ClientSideCachingBenchmarks

| Method | Categories | Run 1 | Run 2 | Run 3 | Median |
|---|---|---:|---:|---:|---:|
| Respire_Exists_ServerRead | EXISTS hot | 1.00 | 1.00 | 1.00 | **1.00** |
| Respire_Exists_ClientCacheHit | EXISTS hot | 0.00 | 0.00 | 0.00 | **0.00** |
| Respire_Get_ServerRead | GET hot | 0.98 | 0.99 | 0.97 | **0.98** |
| Respire_Get_ClientCacheHit | GET hot | 0.00 | 0.00 | 0.00 | **0.00** |
| Respire_GetMissing_ServerRead | GET missing hot | 0.99 | 0.99 | 1.03 | **0.99** |
| Respire_GetMissing_ClientCacheHit | GET missing hot | 0.00 | 0.00 | 0.00 | **0.00** |
| Respire_HGet_ServerRead | HGET hot | 0.98 | 0.98 | 1.01 | **0.98** |
| Respire_HGet_ClientCacheHit | HGET hot | 0.00 | 0.00 | 0.00 | **0.00** |

### CommonOperationsBenchmarks

| Method | Categories | Run 1 | Run 2 | Run 3 | Median |
|---|---|---:|---:|---:|---:|
| Respire_Exists | EXISTS | 1.00 | 1.00 | 0.99 | **1.00** |
| Respire_Get | GET | 0.99 | 0.99 | 1.05 | **0.99** |
| Respire_GetLong | GET long | 0.99 | 0.98 | 1.02 | **0.99** |
| Respire_Get_SteadyState | GET x100 sequential | 0.94 | 0.93 | 1.00 | **0.94** |
| Respire_Get_Pipelined | GET x200 pipelined | 0.89 | 0.88 | 0.96 | **0.89** |
| Respire_Get_Concurrent | GET x50 concurrent | 0.91 | 0.90 | 0.97 | **0.91** |
| Respire_HGet | HGET | 0.98 | 0.99 | 0.99 | **0.99** |
| Respire_HSet | HSET | 1.00 | 1.00 | 0.99 | **1.00** |
| Respire_Incr | INCR | 1.00 | 1.00 | 0.98 | **1.00** |
| Respire_LPushLPop | LPUSH+LPOP | 0.99 | 0.99 | 1.04 | **0.99** |
| Respire_Ping | PING | 1.00 | 1.00 | 1.01 | **1.00** |
| Respire_Ping_SteadyState | PING x100 sequential | 0.95 | 0.97 | 1.00 | **0.97** |
| Respire_SAdd | SADD | 1.00 | 1.00 | 0.99 | **1.00** |
| Respire_Set_10KB | SET 10KB | 1.00 | 1.00 | 1.00 | **1.00** |
| Respire_Set_Small | SET 13B | 1.00 | 1.00 | 0.99 | **1.00** |
| Respire_Set_1KB | SET 1KB | 1.00 | 1.00 | 0.99 | **1.00** |
| Respire_Set_SteadyState | SET x100 sequential | 0.93 | 0.92 | 1.01 | **0.93** |
| Respire_SetDel | SET+DEL | 0.98 | 0.99 | 0.98 | **0.98** |
| Respire_SIsMember | SISMEMBER | 1.00 | 1.00 | 0.98 | **1.00** |

### PubSubComparisonBenchmarks

| Method | Run 1 | Run 2 | Run 3 | Median |
|---|---:|---:|---:|---:|
| Respire_PublishAndConsume | 1.01 | 1.00 | 0.99 | **1.00** |

## net10.0

### ClientSideCachingBenchmarks

#### Run 1

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
AMD EPYC 7763 3.06GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.403
  [Host]     : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v3
  Job-IDGKZI : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v3

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                              | Categories      | Mean         | Error       | StdDev      | Median       | Op/s        | Ratio | MannWhitney(5%) | Allocated | Alloc Ratio |
|------------------------------------ |---------------- |-------------:|------------:|------------:|-------------:|------------:|------:|---------------- |----------:|------------:|
| StackExchange_Exists_ServerRead     | EXISTS hot      | 167,859.0 ns |   530.92 ns |   761.43 ns | 167,798.1 ns |     5,957.4 | 1.000 | Baseline        |     295 B |        1.00 |
| Respire_Exists_ServerRead           | EXISTS hot      | 168,109.8 ns |   452.88 ns |   634.88 ns | 168,225.0 ns |     5,948.5 | 1.002 | Same            |         - |        0.00 |
| Respire_Exists_ClientCacheHit       | EXISTS hot      |     352.6 ns |     2.24 ns |     3.21 ns |     353.2 ns | 2,835,962.5 | 0.002 | Faster          |         - |        0.00 |
|                                     |                 |              |             |             |              |             |       |                 |           |             |
| StackExchange_Get_ServerRead        | GET hot         | 171,202.6 ns |   662.19 ns |   970.64 ns | 171,290.0 ns |     5,841.0 | 1.000 | Baseline        |     528 B |        1.00 |
| Respire_Get_ServerRead              | GET hot         | 167,978.8 ns |   561.58 ns |   840.55 ns | 167,963.0 ns |     5,953.1 | 0.981 | Same            |      64 B |        0.12 |
| Respire_Get_ClientCacheHit          | GET hot         |     139.9 ns |     0.92 ns |     1.35 ns |     139.3 ns | 7,146,740.5 | 0.001 | Faster          |         - |        0.00 |
|                                     |                 |              |             |             |              |             |       |                 |           |             |
| StackExchange_GetMissing_ServerRead | GET missing hot | 168,677.4 ns |   576.75 ns |   808.52 ns | 168,741.2 ns |     5,928.5 | 1.000 | Baseline        |     416 B |        1.00 |
| Respire_GetMissing_ServerRead       | GET missing hot | 166,365.4 ns | 1,147.15 ns | 1,645.21 ns | 166,572.2 ns |     6,010.9 | 0.986 | Same            |         - |        0.00 |
| Respire_GetMissing_ClientCacheHit   | GET missing hot |     145.0 ns |     1.48 ns |     2.08 ns |     146.6 ns | 6,896,535.8 | 0.001 | Faster          |         - |        0.00 |
|                                     |                 |              |             |             |              |             |       |                 |           |             |
| StackExchange_HGet_ServerRead       | HGET hot        | 170,295.5 ns |   893.24 ns | 1,309.29 ns | 170,511.3 ns |     5,872.1 | 1.000 | Baseline        |     544 B |        1.00 |
| Respire_HGet_ServerRead             | HGET hot        | 167,381.8 ns |   971.22 ns | 1,361.52 ns | 167,499.6 ns |     5,974.4 | 0.983 | Same            |      64 B |        0.12 |
| Respire_HGet_ClientCacheHit         | HGET hot        |     408.8 ns |     4.77 ns |     6.53 ns |     412.2 ns | 2,446,445.2 | 0.002 | Faster          |         - |        0.00 |

#### Run 2

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
AMD EPYC 7763 2.81GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.403
  [Host]     : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v3
  Job-IDGKZI : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v3

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                              | Categories      | Mean         | Error       | StdDev      | Op/s        | Ratio | MannWhitney(5%) | RatioSD | Allocated | Alloc Ratio |
|------------------------------------ |---------------- |-------------:|------------:|------------:|------------:|------:|---------------- |--------:|----------:|------------:|
| StackExchange_Exists_ServerRead     | EXISTS hot      | 166,750.7 ns |   557.74 ns |   799.90 ns |     5,997.0 | 1.000 | Baseline        |    0.01 |     296 B |        1.00 |
| Respire_Exists_ServerRead           | EXISTS hot      | 166,359.2 ns |   481.71 ns |   690.86 ns |     6,011.1 | 0.998 | Same            |    0.01 |         - |        0.00 |
| Respire_Exists_ClientCacheHit       | EXISTS hot      |     351.0 ns |     0.49 ns |     0.66 ns | 2,848,598.3 | 0.002 | Faster          |    0.00 |         - |        0.00 |
|                                     |                 |              |             |             |             |       |                 |         |           |             |
| StackExchange_Get_ServerRead        | GET hot         | 169,025.2 ns | 1,315.43 ns | 1,928.13 ns |     5,916.3 | 1.000 | Baseline        |    0.02 |     527 B |        1.00 |
| Respire_Get_ServerRead              | GET hot         | 166,710.9 ns |   674.99 ns |   968.06 ns |     5,998.4 | 0.986 | Same            |    0.01 |      64 B |        0.12 |
| Respire_Get_ClientCacheHit          | GET hot         |     138.5 ns |     0.39 ns |     0.53 ns | 7,221,845.6 | 0.001 | Faster          |    0.00 |         - |        0.00 |
|                                     |                 |              |             |             |             |       |                 |         |           |             |
| StackExchange_GetMissing_ServerRead | GET missing hot | 167,744.1 ns |   541.31 ns |   776.33 ns |     5,961.5 | 1.000 | Baseline        |    0.01 |     415 B |        1.00 |
| Respire_GetMissing_ServerRead       | GET missing hot | 166,678.4 ns |   421.34 ns |   617.60 ns |     5,999.6 | 0.994 | Same            |    0.01 |         - |        0.00 |
| Respire_GetMissing_ClientCacheHit   | GET missing hot |     144.6 ns |     0.81 ns |     1.14 ns | 6,917,488.1 | 0.001 | Faster          |    0.00 |         - |        0.00 |
|                                     |                 |              |             |             |             |       |                 |         |           |             |
| StackExchange_HGet_ServerRead       | HGET hot        | 170,012.7 ns |   571.85 ns |   820.13 ns |     5,881.9 | 1.000 | Baseline        |    0.01 |     544 B |        1.00 |
| Respire_HGet_ServerRead             | HGET hot        | 166,657.8 ns |   624.07 ns |   914.76 ns |     6,000.3 | 0.980 | Same            |    0.01 |      64 B |        0.12 |
| Respire_HGet_ClientCacheHit         | HGET hot        |     473.8 ns |    35.93 ns |    51.53 ns | 2,110,807.0 | 0.003 | Faster          |    0.00 |         - |        0.00 |

#### Run 3

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
AMD EPYC 9V74 3.67GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.403
  [Host]     : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v4
  Job-IDGKZI : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v4

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                              | Categories      | Mean        | Error       | StdDev      | Median      | Op/s        | Ratio | MannWhitney(5%) | RatioSD | Allocated | Alloc Ratio |
|------------------------------------ |---------------- |------------:|------------:|------------:|------------:|------------:|------:|---------------- |--------:|----------:|------------:|
| StackExchange_Exists_ServerRead     | EXISTS hot      | 85,562.4 ns | 2,473.14 ns | 3,625.09 ns | 85,275.8 ns |    11,687.4 | 1.002 | Baseline        |    0.06 |     293 B |        1.00 |
| Respire_Exists_ServerRead           | EXISTS hot      | 85,255.8 ns | 1,452.78 ns | 2,129.47 ns | 85,925.9 ns |    11,729.4 | 0.998 | Same            |    0.05 |         - |        0.00 |
| Respire_Exists_ClientCacheHit       | EXISTS hot      |    304.0 ns |     9.15 ns |    12.22 ns |    315.1 ns | 3,289,533.1 | 0.004 | Faster          |    0.00 |         - |        0.00 |
|                                     |                 |             |             |             |             |             |       |                 |         |           |             |
| StackExchange_Get_ServerRead        | GET hot         | 85,043.4 ns | 3,781.49 ns | 5,659.95 ns | 86,403.1 ns |    11,758.7 | 1.005 | Baseline        |    0.10 |     510 B |        1.00 |
| Respire_Get_ServerRead              | GET hot         | 82,908.0 ns | 2,428.70 ns | 3,635.17 ns | 82,715.3 ns |    12,061.6 | 0.979 | Same            |    0.08 |      64 B |        0.13 |
| Respire_Get_ClientCacheHit          | GET hot         |    116.6 ns |     1.59 ns |     2.17 ns |    118.3 ns | 8,575,802.2 | 0.001 | Faster          |    0.00 |         - |        0.00 |
|                                     |                 |             |             |             |             |             |       |                 |         |           |             |
| StackExchange_GetMissing_ServerRead | GET missing hot | 83,408.5 ns | 3,905.98 ns | 5,846.29 ns | 84,583.6 ns |    11,989.2 | 1.005 | Baseline        |    0.10 |     398 B |        1.00 |
| Respire_GetMissing_ServerRead       | GET missing hot | 85,948.0 ns | 1,399.19 ns | 2,094.25 ns | 86,146.5 ns |    11,634.9 | 1.036 | Same            |    0.08 |         - |        0.00 |
| Respire_GetMissing_ClientCacheHit   | GET missing hot |    113.1 ns |     0.18 ns |     0.27 ns |    113.1 ns | 8,842,243.4 | 0.001 | Faster          |    0.00 |         - |        0.00 |
|                                     |                 |             |             |             |             |             |       |                 |         |           |             |
| StackExchange_HGet_ServerRead       | HGET hot        | 85,475.7 ns | 3,756.58 ns | 5,622.67 ns | 86,037.1 ns |    11,699.2 | 1.004 | Baseline        |    0.09 |     537 B |        1.00 |
| Respire_HGet_ServerRead             | HGET hot        | 86,514.5 ns | 2,217.59 ns | 3,319.18 ns | 86,704.7 ns |    11,558.8 | 1.017 | Same            |    0.08 |      64 B |        0.12 |
| Respire_HGet_ClientCacheHit         | HGET hot        |    333.6 ns |     1.13 ns |     1.66 ns |    334.1 ns | 2,997,240.7 | 0.004 | Faster          |    0.00 |         - |        0.00 |

### CommonOperationsBenchmarks

#### Run 1

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
AMD EPYC 7763 3.06GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.403
  [Host]     : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v3
  Job-IDGKZI : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v3

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                         | Categories           | Mean       | Error     | StdDev    | Ratio | MannWhitney(5%) | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|----------:|----------:|------:|---------------- |--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 166.864 μs | 0.5809 μs | 0.8514 μs |  1.00 | Baseline        |    0.01 |      - |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 167.138 μs | 0.4207 μs | 0.6033 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get              | GET                  | 168.846 μs | 0.8441 μs | 1.2373 μs |  1.00 | Baseline        |    0.01 |      - |     504 B |        1.00 |
| Respire_Get                    | GET                  | 166.737 μs | 0.4897 μs | 0.7330 μs |  0.99 | Same            |    0.01 |      - |      48 B |        0.10 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_GetLong          | GET long             | 168.529 μs | 0.6932 μs | 0.9718 μs |  1.00 | Baseline        |    0.01 |      - |     416 B |        1.00 |
| Respire_GetLong                | GET long             | 166.827 μs | 0.6098 μs | 0.8347 μs |  0.99 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 152.254 μs | 1.4506 μs | 2.1712 μs |  1.00 | Baseline        |    0.02 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 142.778 μs | 0.4570 μs | 0.6699 μs |  0.94 | Faster          |    0.01 |      - |      50 B |        0.15 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Pipelined    | GET x200 pipelined   |   2.261 μs | 0.0741 μs | 0.1109 μs |  1.00 | Baseline        |    0.07 | 0.0098 |     289 B |        1.00 |
| Respire_Get_Pipelined          | GET x200 pipelined   |   2.015 μs | 0.0113 μs | 0.0155 μs |  0.89 | Faster          |    0.04 |      - |      49 B |        0.17 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.017 μs | 0.0406 μs | 0.0608 μs |  1.00 | Baseline        |    0.02 | 0.0098 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   4.564 μs | 0.0206 μs | 0.0289 μs |  0.91 | Faster          |    0.01 |      - |      52 B |        0.18 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HGet             | HGET                 | 170.749 μs | 0.7086 μs | 1.0162 μs |  1.00 | Baseline        |    0.01 |      - |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 167.627 μs | 0.6129 μs | 0.8984 μs |  0.98 | Same            |    0.01 |      - |      48 B |        0.09 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HSet             | HSET                 | 168.616 μs | 0.6723 μs | 0.9642 μs |  1.00 | Baseline        |    0.01 |      - |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 168.665 μs | 0.5368 μs | 0.7348 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Incr             | INCR                 | 167.800 μs | 0.2353 μs | 0.3221 μs |  1.00 | Baseline        |    0.00 |      - |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 167.945 μs | 0.3969 μs | 0.5692 μs |  1.00 | Same            |    0.00 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 340.662 μs | 1.6350 μs | 2.3965 μs |  1.00 | Baseline        |    0.01 |      - |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 336.491 μs | 0.9450 μs | 1.3553 μs |  0.99 | Same            |    0.01 |      - |     256 B |        0.34 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping             | PING                 | 167.354 μs | 0.5291 μs | 0.7418 μs |  1.00 | Baseline        |    0.01 |      - |     303 B |        1.00 |
| Respire_Ping                   | PING                 | 167.329 μs | 0.4310 μs | 0.6451 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 149.499 μs | 1.4336 μs | 2.1458 μs |  1.00 | Baseline        |    0.02 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 141.940 μs | 0.7720 μs | 1.1072 μs |  0.95 | Same            |    0.02 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 168.397 μs | 0.4402 μs | 0.6313 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 168.355 μs | 0.3493 μs | 0.5009 μs |  1.00 | Same            |    0.00 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 180.410 μs | 0.7960 μs | 1.1159 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 181.159 μs | 0.3806 μs | 0.5459 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 168.868 μs | 0.5965 μs | 0.8555 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 168.786 μs | 0.5484 μs | 0.7865 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 169.597 μs | 0.7651 μs | 1.1215 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 169.493 μs | 0.5912 μs | 0.8665 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 155.325 μs | 1.8203 μs | 2.7246 μs |  1.00 | Baseline        |    0.02 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 144.098 μs | 0.5776 μs | 0.8467 μs |  0.93 | Faster          |    0.02 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 335.000 μs | 2.3851 μs | 3.5699 μs |  1.00 | Baseline        |    0.01 |      - |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 329.762 μs | 1.6046 μs | 2.4017 μs |  0.98 | Same            |    0.01 |      - |     200 B |        0.31 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 168.044 μs | 0.4385 μs | 0.6289 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 167.847 μs | 0.5049 μs | 0.7078 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |

#### Run 2

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
AMD EPYC 7763 2.81GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.403
  [Host]     : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v3
  Job-IDGKZI : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v3

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                         | Categories           | Mean       | Error     | StdDev    | Ratio | MannWhitney(5%) | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|----------:|----------:|------:|---------------- |--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 166.284 μs | 0.5732 μs | 0.8402 μs |  1.00 | Baseline        |    0.01 |      - |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 166.243 μs | 0.7390 μs | 1.0599 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get              | GET                  | 168.244 μs | 0.5904 μs | 0.8654 μs |  1.00 | Baseline        |    0.01 |      - |     500 B |        1.00 |
| Respire_Get                    | GET                  | 166.124 μs | 0.5010 μs | 0.7343 μs |  0.99 | Same            |    0.01 |      - |      48 B |        0.10 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_GetLong          | GET long             | 168.364 μs | 0.6170 μs | 0.9236 μs |  1.00 | Baseline        |    0.01 |      - |     416 B |        1.00 |
| Respire_GetLong                | GET long             | 165.586 μs | 0.5818 μs | 0.8709 μs |  0.98 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 151.932 μs | 1.5694 μs | 2.3490 μs |  1.00 | Baseline        |    0.02 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 141.740 μs | 0.5317 μs | 0.7794 μs |  0.93 | Faster          |    0.02 |      - |      50 B |        0.15 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Pipelined    | GET x200 pipelined   |   2.267 μs | 0.0675 μs | 0.1011 μs |  1.00 | Baseline        |    0.06 | 0.0098 |     289 B |        1.00 |
| Respire_Get_Pipelined          | GET x200 pipelined   |   1.999 μs | 0.0064 μs | 0.0088 μs |  0.88 | Faster          |    0.04 |      - |      49 B |        0.17 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   4.990 μs | 0.0522 μs | 0.0766 μs |  1.00 | Baseline        |    0.02 |      - |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   4.513 μs | 0.0168 μs | 0.0241 μs |  0.90 | Faster          |    0.01 |      - |      52 B |        0.18 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HGet             | HGET                 | 168.795 μs | 0.9356 μs | 1.3418 μs |  1.00 | Baseline        |    0.01 |      - |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 166.375 μs | 0.6426 μs | 0.9419 μs |  0.99 | Same            |    0.01 |      - |      48 B |        0.09 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HSet             | HSET                 | 166.950 μs | 0.4550 μs | 0.6379 μs |  1.00 | Baseline        |    0.01 |      - |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 166.847 μs | 0.4666 μs | 0.6983 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Incr             | INCR                 | 166.416 μs | 0.4138 μs | 0.5934 μs |  1.00 | Baseline        |    0.00 |      - |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 166.488 μs | 0.3812 μs | 0.5467 μs |  1.00 | Same            |    0.00 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 335.062 μs | 2.6013 μs | 3.8934 μs |  1.00 | Baseline        |    0.02 |      - |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 330.743 μs | 1.3537 μs | 1.9843 μs |  0.99 | Same            |    0.01 |      - |     256 B |        0.34 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping             | PING                 | 165.990 μs | 0.3487 μs | 0.5001 μs |  1.00 | Baseline        |    0.00 |      - |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 165.842 μs | 0.7440 μs | 1.0670 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 145.724 μs | 1.9633 μs | 2.8157 μs |  1.00 | Baseline        |    0.03 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 141.242 μs | 0.5924 μs | 0.8684 μs |  0.97 | Same            |    0.02 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 166.444 μs | 0.5063 μs | 0.7098 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 166.741 μs | 0.4106 μs | 0.6018 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 178.517 μs | 0.7087 μs | 0.9462 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 178.879 μs | 0.5800 μs | 0.8501 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 167.221 μs | 0.3646 μs | 0.5112 μs |  1.00 | Baseline        |    0.00 |      - |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 167.016 μs | 0.3928 μs | 0.5633 μs |  1.00 | Same            |    0.00 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 167.776 μs | 0.7572 μs | 1.1099 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 167.412 μs | 0.6567 μs | 0.9626 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 153.866 μs | 1.7550 μs | 2.5724 μs |  1.00 | Baseline        |    0.02 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 141.267 μs | 0.7467 μs | 1.0945 μs |  0.92 | Faster          |    0.02 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 331.002 μs | 2.7594 μs | 4.0447 μs |  1.00 | Baseline        |    0.02 |      - |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 326.755 μs | 1.1363 μs | 1.6655 μs |  0.99 | Same            |    0.01 |      - |     200 B |        0.31 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 166.567 μs | 0.5471 μs | 0.7846 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 166.163 μs | 0.8063 μs | 1.1037 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |

#### Run 3

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
AMD EPYC 9V74 3.67GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.403
  [Host]     : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v4
  Job-IDGKZI : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v4

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                         | Categories           | Mean       | Error     | StdDev    | Ratio | MannWhitney(5%) | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|----------:|----------:|------:|---------------- |--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               |  83.889 μs | 2.1818 μs | 3.2656 μs |  1.00 | Baseline        |    0.05 |      - |     295 B |        1.00 |
| Respire_Exists                 | EXISTS               |  82.916 μs | 2.2120 μs | 3.3108 μs |  0.99 | Same            |    0.05 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get              | GET                  |  81.497 μs | 3.8244 μs | 5.6058 μs |  1.00 | Baseline        |    0.10 |      - |     499 B |        1.00 |
| Respire_Get                    | GET                  |  85.613 μs | 2.2829 μs | 3.4169 μs |  1.06 | Same            |    0.09 |      - |      48 B |        0.10 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_GetLong          | GET long             |  83.478 μs | 4.1432 μs | 6.2014 μs |  1.01 | Baseline        |    0.11 |      - |     408 B |        1.00 |
| Respire_GetLong                | GET long             |  85.276 μs | 2.8842 μs | 4.3169 μs |  1.03 | Same            |    0.10 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  |  74.032 μs | 1.2186 μs | 1.7863 μs |  1.00 | Baseline        |    0.03 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  |  74.381 μs | 0.4738 μs | 0.6642 μs |  1.01 | Same            |    0.03 |      - |      50 B |        0.15 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Pipelined    | GET x200 pipelined   |   1.496 μs | 0.0270 μs | 0.0378 μs |  1.00 | Baseline        |    0.04 | 0.0171 |     289 B |        1.00 |
| Respire_Get_Pipelined          | GET x200 pipelined   |   1.440 μs | 0.0073 μs | 0.0105 μs |  0.96 | Same            |    0.03 |      - |      49 B |        0.17 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   3.194 μs | 0.0530 μs | 0.0776 μs |  1.00 | Baseline        |    0.03 | 0.0098 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   3.111 μs | 0.0384 μs | 0.0575 μs |  0.97 | Same            |    0.03 |      - |      52 B |        0.18 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HGet             | HGET                 |  87.118 μs | 4.7173 μs | 7.0606 μs |  1.01 | Baseline        |    0.12 |      - |     507 B |        1.00 |
| Respire_HGet                   | HGET                 |  86.136 μs | 1.8378 μs | 2.7507 μs |  1.00 | Same            |    0.09 |      - |      48 B |        0.09 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HSet             | HSET                 |  86.874 μs | 2.9661 μs | 4.4395 μs |  1.00 | Baseline        |    0.07 |      - |     327 B |        1.00 |
| Respire_HSet                   | HSET                 |  86.423 μs | 1.8058 μs | 2.7028 μs |  1.00 | Same            |    0.06 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Incr             | INCR                 |  85.468 μs | 2.4021 μs | 3.5954 μs |  1.00 | Baseline        |    0.06 |      - |     291 B |        1.00 |
| Respire_Incr                   | INCR                 |  84.107 μs | 2.0222 μs | 3.0267 μs |  0.99 | Same            |    0.06 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 164.387 μs | 4.4586 μs | 6.5353 μs |  1.00 | Baseline        |    0.05 |      - |     758 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 170.892 μs | 2.7149 μs | 4.0635 μs |  1.04 | Same            |    0.05 |      - |     255 B |        0.34 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping             | PING                 |  83.022 μs | 1.3741 μs | 2.0567 μs |  1.00 | Baseline        |    0.03 |      - |     300 B |        1.00 |
| Respire_Ping                   | PING                 |  83.545 μs | 2.2663 μs | 3.3921 μs |  1.01 | Same            |    0.05 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential |  73.961 μs | 1.0120 μs | 1.4834 μs |  1.00 | Baseline        |    0.03 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential |  73.816 μs | 0.4346 μs | 0.5949 μs |  1.00 | Same            |    0.02 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SAdd             | SADD                 |  88.058 μs | 1.8316 μs | 2.7415 μs |  1.00 | Baseline        |    0.04 |      - |     308 B |        1.00 |
| Respire_SAdd                   | SADD                 |  86.916 μs | 1.8762 μs | 2.8082 μs |  0.99 | Same            |    0.04 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 101.903 μs | 2.0431 μs | 3.0580 μs |  1.00 | Baseline        |    0.04 |      - |     311 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 102.007 μs | 1.9072 μs | 2.8546 μs |  1.00 | Same            |    0.04 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              |  88.259 μs | 2.8903 μs | 4.3260 μs |  1.00 | Baseline        |    0.07 |      - |     310 B |        1.00 |
| Respire_Set_Small              | SET 13B              |  87.255 μs | 2.3787 μs | 3.5603 μs |  0.99 | Same            |    0.06 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              |  88.390 μs | 3.2478 μs | 4.8612 μs |  1.00 | Baseline        |    0.08 |      - |     310 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              |  87.895 μs | 2.8894 μs | 4.2353 μs |  1.00 | Same            |    0.07 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  |  74.286 μs | 1.5100 μs | 2.2601 μs |  1.00 | Baseline        |    0.04 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  |  75.218 μs | 0.6427 μs | 0.9421 μs |  1.01 | Same            |    0.03 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 168.812 μs | 4.5401 μs | 6.7954 μs |  1.00 | Baseline        |    0.06 |      - |     647 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 166.037 μs | 2.3329 μs | 3.3457 μs |  0.99 | Same            |    0.05 |      - |     200 B |        0.31 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            |  85.910 μs | 2.2254 μs | 3.3309 μs |  1.00 | Baseline        |    0.05 |      - |     310 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            |  84.256 μs | 1.7504 μs | 2.5658 μs |  0.98 | Same            |    0.05 |      - |         - |        0.00 |

### PubSubComparisonBenchmarks

#### Run 1

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
AMD EPYC 7763 3.06GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.403
  [Host]     : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v3
  Job-IDGKZI : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v3

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                          | Mean     | Error   | StdDev  | Ratio | MannWhitney(5%) | Allocated | Alloc Ratio |
|-------------------------------- |---------:|--------:|--------:|------:|---------------- |----------:|------------:|
| StackExchange_PublishAndConsume | 200.9 μs | 0.78 μs | 1.09 μs |  1.00 | Baseline        |     671 B |        1.00 |
| Respire_PublishAndConsume       | 202.6 μs | 0.78 μs | 1.09 μs |  1.01 | Same            |     399 B |        0.59 |

#### Run 2

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
AMD EPYC 7763 2.81GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.403
  [Host]     : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v3
  Job-IDGKZI : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v3

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                          | Mean     | Error   | StdDev  | Ratio | MannWhitney(5%) | Allocated | Alloc Ratio |
|-------------------------------- |---------:|--------:|--------:|------:|---------------- |----------:|------------:|
| StackExchange_PublishAndConsume | 198.2 μs | 0.75 μs | 1.08 μs |  1.00 | Baseline        |     672 B |        1.00 |
| Respire_PublishAndConsume       | 199.0 μs | 1.06 μs | 1.55 μs |  1.00 | Same            |     399 B |        0.59 |

#### Run 3

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
AMD EPYC 9V74 3.67GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.403
  [Host]     : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v4
  Job-IDGKZI : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v4

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                          | Mean     | Error   | StdDev  | Ratio | MannWhitney(5%) | RatioSD | Allocated | Alloc Ratio |
|-------------------------------- |---------:|--------:|--------:|------:|---------------- |--------:|----------:|------------:|
| StackExchange_PublishAndConsume | 110.1 μs | 4.98 μs | 7.45 μs |  1.00 | Baseline        |    0.10 |     638 B |        1.00 |
| Respire_PublishAndConsume       | 108.9 μs | 3.73 μs | 5.58 μs |  0.99 | Same            |    0.09 |     396 B |        0.62 |

## Reading the results

These runs use shared Linux machines and a local Redis container, so expect some variation between runs. Before making an important decision, measure with your own payload sizes, concurrency, Redis deployment, and network.
