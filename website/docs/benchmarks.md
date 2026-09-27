---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

import ComparisonBarChart from '@site/src/components/ComparisonBarChart';

# Benchmarks

:::info Automated results
Generated 2026-09-27 03:38 UTC from commit `647409c32cb3`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/36290292942) for logs and downloadable artifacts.
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
  data={[{"label":"EXISTS hot","other":191471.9,"respire":434.2,"respireServer":190821.8},{"label":"GET hot","other":192951.6,"respire":213.6,"respireServer":190738.5},{"label":"GET missing hot","other":192233.5,"respire":196.6,"respireServer":191436.5},{"label":"HGET hot","other":194907.3,"respire":517.1,"respireServer":191037.6}]}
/>

<ComparisonBarChart
  title="Selected operation time — net10.0"
  description="Mean time. Shorter bars are faster."
  format="duration-ns"
  scale="group"
  showRatio
  data={[{"label":"GET","other":192931.0,"respire":189725.0},{"label":"GET x200 pipelined","other":2467.0,"respire":2189.0},{"label":"GET x50 concurrent","other":5350.0,"respire":5556.0},{"label":"HGET","other":193623.0,"respire":191029.0},{"label":"HSET","other":193918.0,"respire":192910.0},{"label":"LPUSH+LPOP","other":374434.0,"respire":372084.0},{"label":"SET 1KB","other":194475.0,"respire":192498.0}]}
/>

## net10.0

### ClientSideCachingBenchmarks

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
AMD EPYC 7763 2.71GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  Job-IDGKZI : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                              | Categories      | Mean         | Error       | StdDev      | Op/s        | Ratio | MannWhitney(5%) | Gen0   | Allocated | Alloc Ratio |
|------------------------------------ |---------------- |-------------:|------------:|------------:|------------:|------:|---------------- |-------:|----------:|------------:|
| StackExchange_Exists_ServerRead     | EXISTS hot      | 191,471.9 ns |   751.98 ns | 1,102.24 ns |     5,222.7 | 1.000 | Baseline        |      - |     296 B |        1.00 |
| Respire_Exists_ServerRead           | EXISTS hot      | 190,821.8 ns |   788.65 ns | 1,180.42 ns |     5,240.5 | 0.997 | Same            |      - |         - |        0.00 |
| Respire_Exists_ClientCacheHit       | EXISTS hot      |     434.2 ns |     3.61 ns |     5.07 ns | 2,303,182.6 | 0.002 | Faster          |      - |         - |        0.00 |
|                                     |                 |              |             |             |             |       |                 |        |           |             |
| StackExchange_Get_ServerRead        | GET hot         | 192,951.6 ns | 1,390.91 ns | 2,038.78 ns |     5,182.6 | 1.000 | Baseline        |      - |     528 B |        1.00 |
| Respire_Get_ServerRead              | GET hot         | 190,738.5 ns | 1,254.91 ns | 1,878.29 ns |     5,242.8 | 0.989 | Same            |      - |      64 B |        0.12 |
| Respire_Get_ClientCacheHit          | GET hot         |     213.6 ns |     3.22 ns |     4.72 ns | 4,682,633.6 | 0.001 | Faster          | 0.0038 |      64 B |        0.12 |
|                                     |                 |              |             |             |             |       |                 |        |           |             |
| StackExchange_GetMissing_ServerRead | GET missing hot | 192,233.5 ns |   766.50 ns | 1,147.27 ns |     5,202.0 | 1.000 | Baseline        |      - |     416 B |        1.00 |
| Respire_GetMissing_ServerRead       | GET missing hot | 191,436.5 ns |   738.18 ns | 1,104.87 ns |     5,223.7 | 0.996 | Same            |      - |         - |        0.00 |
| Respire_GetMissing_ClientCacheHit   | GET missing hot |     196.6 ns |     1.06 ns |     1.49 ns | 5,087,312.8 | 0.001 | Faster          |      - |         - |        0.00 |
|                                     |                 |              |             |             |             |       |                 |        |           |             |
| StackExchange_HGet_ServerRead       | HGET hot        | 194,907.3 ns |   657.64 ns |   963.96 ns |     5,130.6 | 1.000 | Baseline        |      - |     543 B |        1.00 |
| Respire_HGet_ServerRead             | HGET hot        | 191,037.6 ns |   945.39 ns | 1,415.02 ns |     5,234.6 | 0.980 | Same            |      - |      64 B |        0.12 |
| Respire_HGet_ClientCacheHit         | HGET hot        |     517.1 ns |     6.95 ns |    10.19 ns | 1,933,899.8 | 0.003 | Faster          | 0.0038 |      64 B |        0.12 |

### CommonOperationsBenchmarks

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
AMD EPYC 7763 2.71GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  Job-IDGKZI : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                         | Categories           | Mean       | Error     | StdDev    | Ratio | MannWhitney(5%) | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|----------:|----------:|------:|---------------- |--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 190.355 μs | 0.7221 μs | 1.0356 μs |  1.00 | Baseline        |    0.01 |      - |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 190.428 μs | 0.9147 μs | 1.3690 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get              | GET                  | 192.931 μs | 0.8017 μs | 1.1999 μs |  1.00 | Baseline        |    0.01 |      - |     504 B |        1.00 |
| Respire_Get                    | GET                  | 189.725 μs | 0.9719 μs | 1.4547 μs |  0.98 | Same            |    0.01 |      - |      48 B |        0.10 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 175.192 μs | 1.0001 μs | 1.4659 μs |  1.00 | Baseline        |    0.01 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 173.705 μs | 0.7121 μs | 1.0437 μs |  0.99 | Same            |    0.01 |      - |      50 B |        0.15 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Pipelined    | GET x200 pipelined   |   2.467 μs | 0.0413 μs | 0.0605 μs |  1.00 | Baseline        |    0.03 | 0.0098 |     289 B |        1.00 |
| Respire_Get_Pipelined          | GET x200 pipelined   |   2.189 μs | 0.0122 μs | 0.0175 μs |  0.89 | Faster          |    0.02 |      - |      60 B |        0.21 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.350 μs | 0.0413 μs | 0.0619 μs |  1.00 | Baseline        |    0.02 |      - |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   5.556 μs | 0.0228 μs | 0.0335 μs |  1.04 | Same            |    0.01 |      - |      52 B |        0.18 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HGet             | HGET                 | 193.623 μs | 0.7942 μs | 1.1887 μs |  1.00 | Baseline        |    0.01 |      - |     519 B |        1.00 |
| Respire_HGet                   | HGET                 | 191.029 μs | 0.7177 μs | 1.0292 μs |  0.99 | Same            |    0.01 |      - |      48 B |        0.09 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HSet             | HSET                 | 193.918 μs | 0.9199 μs | 1.3483 μs |  1.00 | Baseline        |    0.01 |      - |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 192.910 μs | 0.5744 μs | 0.8598 μs |  0.99 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Incr             | INCR                 | 192.161 μs | 0.6681 μs | 1.0000 μs |  1.00 | Baseline        |    0.01 |      - |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 191.117 μs | 0.9528 μs | 1.4261 μs |  0.99 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 374.434 μs | 1.4598 μs | 2.0465 μs |  1.00 | Baseline        |    0.01 |      - |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 372.084 μs | 1.3242 μs | 1.9820 μs |  0.99 | Same            |    0.01 |      - |     256 B |        0.34 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping             | PING                 | 188.978 μs | 0.7126 μs | 1.0445 μs |  1.00 | Baseline        |    0.01 |      - |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 189.233 μs | 0.6629 μs | 0.9922 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 174.188 μs | 0.9477 μs | 1.4184 μs |  1.00 | Baseline        |    0.01 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 171.748 μs | 1.5590 μs | 2.3334 μs |  0.99 | Same            |    0.02 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 191.341 μs | 0.9458 μs | 1.4156 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 191.796 μs | 0.6218 μs | 0.9307 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 203.177 μs | 0.6380 μs | 0.9150 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 206.809 μs | 0.6404 μs | 0.9585 μs |  1.02 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 192.606 μs | 0.8295 μs | 1.2415 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 191.913 μs | 1.1304 μs | 1.6919 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 194.475 μs | 0.8676 μs | 1.2718 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 192.498 μs | 0.8423 μs | 1.2607 μs |  0.99 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 177.408 μs | 0.8272 μs | 1.2382 μs |  1.00 | Baseline        |    0.01 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 175.631 μs | 1.0698 μs | 1.5682 μs |  0.99 | Same            |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 370.356 μs | 1.2168 μs | 1.8213 μs |  1.00 | Baseline        |    0.01 |      - |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 368.462 μs | 1.5797 μs | 2.3155 μs |  0.99 | Same            |    0.01 |      - |     200 B |        0.31 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 190.487 μs | 0.8394 μs | 1.2303 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 189.504 μs | 1.0748 μs | 1.6087 μs |  0.99 | Same            |    0.01 |      - |         - |        0.00 |

## net8.0

### ClientSideCachingBenchmarks

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
AMD EPYC 9V74 2.60GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 8.0.31 (8.0.31, 8.0.3126.42015), X64 RyuJIT x86-64-v4
  Job-IDGKZI : .NET 8.0.31 (8.0.31, 8.0.3126.42015), X64 RyuJIT x86-64-v4

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                              | Categories      | Mean         | Error       | StdDev      | Op/s        | Ratio | MannWhitney(5%) | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------------ |---------------- |-------------:|------------:|------------:|------------:|------:|---------------- |--------:|-------:|----------:|------------:|
| StackExchange_Exists_ServerRead     | EXISTS hot      |  99,057.4 ns | 4,322.67 ns | 6,469.97 ns |    10,095.2 | 1.004 | Baseline        |    0.09 |      - |     294 B |        1.00 |
| Respire_Exists_ServerRead           | EXISTS hot      | 111,507.3 ns | 1,733.88 ns | 2,595.19 ns |     8,968.0 | 1.131 | Slower          |    0.08 |      - |         - |        0.00 |
| Respire_Exists_ClientCacheHit       | EXISTS hot      |     476.3 ns |     5.06 ns |     7.26 ns | 2,099,388.4 | 0.005 | Faster          |    0.00 |      - |         - |        0.00 |
|                                     |                 |              |             |             |             |       |                 |         |        |           |             |
| StackExchange_Get_ServerRead        | GET hot         | 101,103.8 ns | 4,902.69 ns | 7,338.12 ns |     9,890.8 | 1.005 | Baseline        |    0.10 |      - |     517 B |        1.00 |
| Respire_Get_ServerRead              | GET hot         | 108,738.9 ns | 2,778.87 ns | 4,159.28 ns |     9,196.3 | 1.081 | Same            |    0.09 |      - |      64 B |        0.12 |
| Respire_Get_ClientCacheHit          | GET hot         |     300.3 ns |     1.72 ns |     2.41 ns | 3,330,325.7 | 0.003 | Faster          |    0.00 | 0.0038 |      64 B |        0.12 |
|                                     |                 |              |             |             |             |       |                 |         |        |           |             |
| StackExchange_GetMissing_ServerRead | GET missing hot |  98,099.7 ns | 4,660.86 ns | 6,976.15 ns |    10,193.7 | 1.005 | Baseline        |    0.10 |      - |     401 B |        1.00 |
| Respire_GetMissing_ServerRead       | GET missing hot | 105,930.0 ns | 2,143.39 ns | 3,208.13 ns |     9,440.2 | 1.086 | Same            |    0.09 |      - |         - |        0.00 |
| Respire_GetMissing_ClientCacheHit   | GET missing hot |     292.1 ns |     0.86 ns |     1.18 ns | 3,423,977.4 | 0.003 | Faster          |    0.00 |      - |         - |        0.00 |
|                                     |                 |              |             |             |             |       |                 |         |        |           |             |
| StackExchange_HGet_ServerRead       | HGET hot        | 107,458.9 ns | 3,904.96 ns | 5,844.75 ns |     9,305.9 | 1.003 | Baseline        |    0.08 |      - |     538 B |        1.00 |
| Respire_HGet_ServerRead             | HGET hot        | 111,029.3 ns | 1,630.68 ns | 2,440.72 ns |     9,006.6 | 1.036 | Same            |    0.06 |      - |      64 B |        0.12 |
| Respire_HGet_ClientCacheHit         | HGET hot        |     541.0 ns |     1.67 ns |     2.29 ns | 1,848,355.1 | 0.005 | Faster          |    0.00 | 0.0038 |      64 B |        0.12 |

### CommonOperationsBenchmarks

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
AMD EPYC 9V74 2.60GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 8.0.31 (8.0.31, 8.0.3126.42015), X64 RyuJIT x86-64-v4
  Job-IDGKZI : .NET 8.0.31 (8.0.31, 8.0.3126.42015), X64 RyuJIT x86-64-v4

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                         | Categories           | Mean       | Error     | StdDev    | Ratio | MannWhitney(5%) | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|----------:|----------:|------:|---------------- |--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 102.291 μs | 1.4676 μs | 2.1511 μs |  1.00 | Baseline        |    0.03 |      - |     293 B |        1.00 |
| Respire_Exists                 | EXISTS               | 109.254 μs | 2.0324 μs | 3.0420 μs |  1.07 | Same            |    0.04 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get              | GET                  | 104.830 μs | 2.7988 μs | 4.1891 μs |  1.00 | Baseline        |    0.06 |      - |     502 B |        1.00 |
| Respire_Get                    | GET                  | 107.487 μs | 1.1539 μs | 1.7271 μs |  1.03 | Same            |    0.04 |      - |      48 B |        0.10 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  |  89.668 μs | 2.3167 μs | 3.4675 μs |  1.00 | Baseline        |    0.05 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  |  87.984 μs | 1.8231 μs | 2.7288 μs |  0.98 | Same            |    0.05 |      - |      50 B |        0.15 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Pipelined    | GET x200 pipelined   |   1.656 μs | 0.0595 μs | 0.0890 μs |  1.00 | Baseline        |    0.08 | 0.0146 |     289 B |        1.00 |
| Respire_Get_Pipelined          | GET x200 pipelined   |   1.614 μs | 0.0144 μs | 0.0206 μs |  0.98 | Same            |    0.05 |      - |      60 B |        0.21 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   3.440 μs | 0.0692 μs | 0.1035 μs |  1.00 | Baseline        |    0.04 | 0.0098 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   3.524 μs | 0.0624 μs | 0.0934 μs |  1.03 | Same            |    0.04 |      - |      52 B |        0.18 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HGet             | HGET                 | 105.020 μs | 4.7606 μs | 7.1254 μs |  1.00 | Baseline        |    0.10 |      - |     512 B |        1.00 |
| Respire_HGet                   | HGET                 | 108.766 μs | 2.0228 μs | 3.0276 μs |  1.04 | Same            |    0.08 |      - |      48 B |        0.09 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HSet             | HSET                 | 107.523 μs | 1.4421 μs | 2.1584 μs |  1.00 | Baseline        |    0.03 |      - |     324 B |        1.00 |
| Respire_HSet                   | HSET                 | 109.545 μs | 3.0446 μs | 4.5570 μs |  1.02 | Same            |    0.05 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Incr             | INCR                 | 101.942 μs | 1.8695 μs | 2.7982 μs |  1.00 | Baseline        |    0.04 |      - |     293 B |        1.00 |
| Respire_Incr                   | INCR                 | 108.660 μs | 1.8596 μs | 2.7833 μs |  1.07 | Same            |    0.04 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 204.613 μs | 4.0302 μs | 6.0323 μs |  1.00 | Baseline        |    0.04 |      - |     756 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 208.371 μs | 3.0289 μs | 4.4398 μs |  1.02 | Same            |    0.04 |      - |     255 B |        0.34 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping             | PING                 | 101.460 μs | 2.3701 μs | 3.5474 μs |  1.00 | Baseline        |    0.05 |      - |     299 B |        1.00 |
| Respire_Ping                   | PING                 | 103.221 μs | 2.2990 μs | 3.4411 μs |  1.02 | Same            |    0.05 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential |  88.803 μs | 1.7202 μs | 2.5746 μs |  1.00 | Baseline        |    0.04 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential |  88.094 μs | 1.1841 μs | 1.7723 μs |  0.99 | Same            |    0.03 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 103.696 μs | 2.2539 μs | 3.3735 μs |  1.00 | Baseline        |    0.05 |      - |     307 B |        1.00 |
| Respire_SAdd                   | SADD                 | 109.207 μs | 2.7365 μs | 4.0959 μs |  1.05 | Same            |    0.05 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 118.253 μs | 1.4768 μs | 2.2104 μs |  1.00 | Baseline        |    0.03 |      - |     311 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 122.588 μs | 1.2340 μs | 1.8088 μs |  1.04 | Same            |    0.02 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 105.187 μs | 1.5249 μs | 2.2351 μs |  1.00 | Baseline        |    0.03 |      - |     310 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 112.998 μs | 1.3014 μs | 1.9478 μs |  1.07 | Same            |    0.03 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 107.358 μs | 1.6036 μs | 2.2998 μs |  1.00 | Baseline        |    0.03 |      - |     309 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 110.849 μs | 2.1715 μs | 3.1830 μs |  1.03 | Same            |    0.04 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  |  92.078 μs | 2.1739 μs | 3.2538 μs |  1.00 | Baseline        |    0.05 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  |  89.392 μs | 1.8778 μs | 2.8106 μs |  0.97 | Same            |    0.05 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 200.096 μs | 5.1884 μs | 7.6051 μs |  1.00 | Baseline        |    0.05 |      - |     647 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 208.992 μs | 2.9851 μs | 4.4680 μs |  1.05 | Same            |    0.04 |      - |     200 B |        0.31 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 104.697 μs | 1.8879 μs | 2.8257 μs |  1.00 | Baseline        |    0.04 |      - |     309 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 111.791 μs | 1.4390 μs | 2.1539 μs |  1.07 | Same            |    0.03 |      - |         - |        0.00 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
