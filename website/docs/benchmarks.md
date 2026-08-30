---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

import ComparisonBarChart from '@site/src/components/ComparisonBarChart';

# Benchmarks

:::info Automated results
Generated 2026-08-30 03:36 UTC from commit `44d94b47f0ea`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/33289390933) for logs and downloadable artifacts.
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
  data={[{"label":"EXISTS hot","other":130282.2,"respire":458.1,"respireServer":134240.7},{"label":"GET hot","other":133432.7,"respire":213.5,"respireServer":136429.0},{"label":"GET missing hot","other":129676.8,"respire":201.2,"respireServer":135339.3},{"label":"HGET hot","other":134658.6,"respire":527.4,"respireServer":136346.9}]}
/>

<ComparisonBarChart
  title="Selected operation time — net10.0"
  description="Mean time. Shorter bars are faster."
  format="duration-ns"
  scale="group"
  showRatio
  data={[{"label":"GET","other":134148.0,"respire":135360.0},{"label":"GET x200 pipelined","other":1941.0,"respire":1884.0},{"label":"GET x50 concurrent","other":4452.0,"respire":4718.0},{"label":"HGET","other":132305.0,"respire":134507.0},{"label":"HSET","other":132457.0,"respire":136277.0},{"label":"LPUSH+LPOP","other":272155.0,"respire":263992.0},{"label":"SET 1KB","other":130062.99999999999,"respire":137512.0}]}
/>

## net10.0

### ClientSideCachingBenchmarks

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD EPYC 9V74 2.60GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.400
  [Host]     : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3
  Job-IDGKZI : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                              | Categories      | Mean         | Error       | StdDev      | Op/s        | Ratio | MannWhitney(5%) | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------------ |---------------- |-------------:|------------:|------------:|------------:|------:|---------------- |--------:|-------:|----------:|------------:|
| StackExchange_Exists_ServerRead     | EXISTS hot      | 130,282.2 ns | 1,354.99 ns | 2,028.09 ns |     7,675.6 | 1.000 | Baseline        |    0.02 |      - |     296 B |        1.00 |
| Respire_Exists_ServerRead           | EXISTS hot      | 134,240.7 ns | 1,084.19 ns | 1,622.76 ns |     7,449.3 | 1.031 | Same            |    0.02 |      - |         - |        0.00 |
| Respire_Exists_ClientCacheHit       | EXISTS hot      |     458.1 ns |     0.83 ns |     1.20 ns | 2,182,718.3 | 0.004 | Faster          |    0.00 |      - |         - |        0.00 |
|                                     |                 |              |             |             |             |       |                 |         |        |           |             |
| StackExchange_Get_ServerRead        | GET hot         | 133,432.7 ns | 3,135.32 ns | 4,692.80 ns |     7,494.4 | 1.001 | Baseline        |    0.05 |      - |     528 B |        1.00 |
| Respire_Get_ServerRead              | GET hot         | 136,429.0 ns | 1,545.76 ns | 2,313.62 ns |     7,329.8 | 1.024 | Same            |    0.04 |      - |      64 B |        0.12 |
| Respire_Get_ClientCacheHit          | GET hot         |     213.5 ns |     0.70 ns |     1.00 ns | 4,684,132.2 | 0.002 | Faster          |    0.00 | 0.0038 |      64 B |        0.12 |
|                                     |                 |              |             |             |             |       |                 |         |        |           |             |
| StackExchange_GetMissing_ServerRead | GET missing hot | 129,676.8 ns | 3,559.68 ns | 5,327.96 ns |     7,711.5 | 1.002 | Baseline        |    0.06 |      - |     416 B |        1.00 |
| Respire_GetMissing_ServerRead       | GET missing hot | 135,339.3 ns | 2,171.29 ns | 3,249.89 ns |     7,388.8 | 1.045 | Same            |    0.05 |      - |         - |        0.00 |
| Respire_GetMissing_ClientCacheHit   | GET missing hot |     201.2 ns |     0.89 ns |     1.30 ns | 4,969,342.9 | 0.002 | Faster          |    0.00 |      - |         - |        0.00 |
|                                     |                 |              |             |             |             |       |                 |         |        |           |             |
| StackExchange_HGet_ServerRead       | HGET hot        | 134,658.6 ns | 2,616.32 ns | 3,915.98 ns |     7,426.2 | 1.001 | Baseline        |    0.04 |      - |     544 B |        1.00 |
| Respire_HGet_ServerRead             | HGET hot        | 136,346.9 ns | 1,586.91 ns | 2,375.21 ns |     7,334.2 | 1.013 | Same            |    0.03 |      - |      64 B |        0.12 |
| Respire_HGet_ClientCacheHit         | HGET hot        |     527.4 ns |     1.31 ns |     1.88 ns | 1,896,201.4 | 0.004 | Faster          |    0.00 | 0.0038 |      64 B |        0.12 |

### CommonOperationsBenchmarks

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD EPYC 9V74 2.60GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.400
  [Host]     : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3
  Job-IDGKZI : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                         | Categories           | Mean       | Error     | StdDev    | Ratio | MannWhitney(5%) | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|----------:|----------:|------:|---------------- |--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 126.499 μs | 2.5677 μs | 3.8432 μs |  1.00 | Baseline        |    0.04 |      - |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 134.411 μs | 0.8507 μs | 1.2733 μs |  1.06 | Same            |    0.03 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get              | GET                  | 134.148 μs | 2.7244 μs | 4.0778 μs |  1.00 | Baseline        |    0.04 |      - |     504 B |        1.00 |
| Respire_Get                    | GET                  | 135.360 μs | 1.5207 μs | 2.2761 μs |  1.01 | Same            |    0.03 |      - |      48 B |        0.10 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 111.350 μs | 1.6423 μs | 2.4581 μs |  1.00 | Baseline        |    0.03 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 109.733 μs | 1.6044 μs | 2.4013 μs |  0.99 | Same            |    0.03 |      - |      50 B |        0.15 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Pipelined    | GET x200 pipelined   |   1.941 μs | 0.0425 μs | 0.0636 μs |  1.00 | Baseline        |    0.05 | 0.0098 |     289 B |        1.00 |
| Respire_Get_Pipelined          | GET x200 pipelined   |   1.884 μs | 0.0197 μs | 0.0289 μs |  0.97 | Same            |    0.04 |      - |      62 B |        0.21 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   4.452 μs | 0.0453 μs | 0.0650 μs |  1.00 | Baseline        |    0.02 | 0.0098 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   4.718 μs | 0.0498 μs | 0.0730 μs |  1.06 | Same            |    0.02 |      - |      52 B |        0.18 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HGet             | HGET                 | 132.305 μs | 2.4926 μs | 3.7308 μs |  1.00 | Baseline        |    0.04 |      - |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 134.507 μs | 1.7928 μs | 2.6834 μs |  1.02 | Same            |    0.03 |      - |      48 B |        0.09 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HSet             | HSET                 | 132.457 μs | 2.0833 μs | 3.1182 μs |  1.00 | Baseline        |    0.03 |      - |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 136.277 μs | 2.0739 μs | 3.1040 μs |  1.03 | Same            |    0.03 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Incr             | INCR                 | 127.231 μs | 1.7058 μs | 2.5004 μs |  1.00 | Baseline        |    0.03 |      - |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 132.038 μs | 1.5366 μs | 2.1541 μs |  1.04 | Same            |    0.03 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 272.155 μs | 3.9309 μs | 5.8836 μs |  1.00 | Baseline        |    0.03 |      - |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 263.992 μs | 2.4705 μs | 3.5432 μs |  0.97 | Same            |    0.02 |      - |     255 B |        0.34 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping             | PING                 | 127.594 μs | 2.7811 μs | 4.1626 μs |  1.00 | Baseline        |    0.05 |      - |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 133.105 μs | 1.2595 μs | 1.8462 μs |  1.04 | Same            |    0.04 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 107.418 μs | 1.8102 μs | 2.6534 μs |  1.00 | Baseline        |    0.03 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 105.672 μs | 1.7820 μs | 2.6672 μs |  0.98 | Same            |    0.03 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 131.865 μs | 2.2440 μs | 3.3588 μs |  1.00 | Baseline        |    0.04 |      - |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 133.733 μs | 1.3613 μs | 1.9954 μs |  1.01 | Same            |    0.03 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 149.082 μs | 2.1305 μs | 3.0554 μs |  1.00 | Baseline        |    0.03 |      - |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 152.405 μs | 1.9991 μs | 2.9922 μs |  1.02 | Same            |    0.03 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 132.760 μs | 2.7176 μs | 4.0676 μs |  1.00 | Baseline        |    0.04 |      - |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 135.037 μs | 1.2264 μs | 1.8356 μs |  1.02 | Same            |    0.03 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 130.063 μs | 2.2541 μs | 3.3738 μs |  1.00 | Baseline        |    0.04 |      - |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 137.512 μs | 1.2917 μs | 1.9333 μs |  1.06 | Same            |    0.03 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 112.851 μs | 1.9447 μs | 2.8505 μs |  1.00 | Baseline        |    0.04 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 111.622 μs | 1.8088 μs | 2.7073 μs |  0.99 | Same            |    0.03 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 258.719 μs | 4.9500 μs | 7.4090 μs |  1.00 | Baseline        |    0.04 |      - |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 265.934 μs | 3.0451 μs | 4.5577 μs |  1.03 | Same            |    0.03 |      - |     200 B |        0.31 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 127.158 μs | 2.0629 μs | 3.0876 μs |  1.00 | Baseline        |    0.03 |      - |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 135.209 μs | 1.3235 μs | 1.8982 μs |  1.06 | Same            |    0.03 |      - |         - |        0.00 |

## net8.0

### ClientSideCachingBenchmarks

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
INTEL XEON PLATINUM 8573C 2.30GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.400
  [Host]     : .NET 8.0.30 (8.0.30, 8.0.3026.36720), X64 RyuJIT x86-64-v4
  Job-IDGKZI : .NET 8.0.30 (8.0.30, 8.0.3026.36720), X64 RyuJIT x86-64-v4

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                              | Categories      | Mean        | Error       | StdDev      | Median      | Op/s        | Ratio | MannWhitney(5%) | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------------ |---------------- |------------:|------------:|------------:|------------:|------------:|------:|---------------- |--------:|-------:|----------:|------------:|
| StackExchange_Exists_ServerRead     | EXISTS hot      | 90,032.9 ns | 2,442.02 ns | 3,655.10 ns | 90,653.9 ns |    11,107.1 | 1.002 | Baseline        |    0.06 |      - |     296 B |        1.00 |
| Respire_Exists_ServerRead           | EXISTS hot      | 86,626.6 ns | 1,573.34 ns | 2,306.19 ns | 87,303.3 ns |    11,543.8 | 0.964 | Same            |    0.05 |      - |         - |        0.00 |
| Respire_Exists_ClientCacheHit       | EXISTS hot      |    549.8 ns |    24.44 ns |    35.05 ns |    573.1 ns | 1,818,903.3 | 0.006 | Faster          |    0.00 |      - |         - |        0.00 |
|                                     |                 |             |             |             |             |             |       |                 |         |        |           |             |
| StackExchange_Get_ServerRead        | GET hot         | 92,498.7 ns |   669.38 ns | 1,001.90 ns | 92,654.8 ns |    10,811.0 | 1.000 | Baseline        |    0.02 |      - |     528 B |        1.00 |
| Respire_Get_ServerRead              | GET hot         | 89,315.8 ns |   986.19 ns | 1,476.08 ns | 89,720.9 ns |    11,196.2 | 0.966 | Same            |    0.02 |      - |      64 B |        0.12 |
| Respire_Get_ClientCacheHit          | GET hot         |    331.3 ns |     1.71 ns |     2.57 ns |    331.7 ns | 3,018,706.9 | 0.004 | Faster          |    0.00 | 0.0005 |      64 B |        0.12 |
|                                     |                 |             |             |             |             |             |       |                 |         |        |           |             |
| StackExchange_GetMissing_ServerRead | GET missing hot | 91,922.5 ns | 1,100.72 ns | 1,647.51 ns | 92,183.7 ns |    10,878.7 | 1.000 | Baseline        |    0.03 |      - |     416 B |        1.00 |
| Respire_GetMissing_ServerRead       | GET missing hot | 87,528.3 ns |   744.26 ns | 1,090.93 ns | 87,786.6 ns |    11,424.9 | 0.953 | Same            |    0.02 |      - |         - |        0.00 |
| Respire_GetMissing_ClientCacheHit   | GET missing hot |    311.8 ns |     1.50 ns |     2.05 ns |    312.2 ns | 3,207,377.0 | 0.003 | Faster          |    0.00 |      - |         - |        0.00 |
|                                     |                 |             |             |             |             |             |       |                 |         |        |           |             |
| StackExchange_HGet_ServerRead       | HGET hot        | 92,581.1 ns | 1,505.54 ns | 2,253.41 ns | 92,353.6 ns |    10,801.3 | 1.001 | Baseline        |    0.03 |      - |     544 B |        1.00 |
| Respire_HGet_ServerRead             | HGET hot        | 88,211.2 ns |   456.16 ns |   682.76 ns | 88,234.6 ns |    11,336.4 | 0.953 | Same            |    0.02 |      - |      64 B |        0.12 |
| Respire_HGet_ClientCacheHit         | HGET hot        |    572.6 ns |     3.93 ns |     5.51 ns |    575.1 ns | 1,746,527.7 | 0.006 | Faster          |    0.00 |      - |      64 B |        0.12 |

### CommonOperationsBenchmarks

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
INTEL XEON PLATINUM 8573C 2.30GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.400
  [Host]     : .NET 8.0.30 (8.0.30, 8.0.3026.36720), X64 RyuJIT x86-64-v4
  Job-IDGKZI : .NET 8.0.30 (8.0.30, 8.0.3026.36720), X64 RyuJIT x86-64-v4

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                         | Categories           | Mean       | Error     | StdDev    | Ratio | MannWhitney(5%) | RatioSD | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|----------:|----------:|------:|---------------- |--------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               |  90.389 μs | 0.3671 μs | 0.5146 μs |  1.00 | Baseline        |    0.01 |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               |  87.262 μs | 1.0038 μs | 1.5024 μs |  0.97 | Same            |    0.02 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Get              | GET                  |  90.257 μs | 0.7962 μs | 1.1918 μs |  1.00 | Baseline        |    0.02 |     504 B |        1.00 |
| Respire_Get                    | GET                  |  88.505 μs | 0.6179 μs | 0.9056 μs |  0.98 | Same            |    0.02 |      48 B |        0.10 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  |  77.375 μs | 0.6814 μs | 1.0199 μs |  1.00 | Baseline        |    0.02 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  |  75.835 μs | 0.7618 μs | 1.0926 μs |  0.98 | Same            |    0.02 |      50 B |        0.15 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Get_Pipelined    | GET x200 pipelined   |   1.479 μs | 0.0688 μs | 0.1029 μs |  1.00 | Baseline        |    0.10 |     289 B |        1.00 |
| Respire_Get_Pipelined          | GET x200 pipelined   |   1.427 μs | 0.0107 μs | 0.0161 μs |  0.97 | Same            |    0.06 |      58 B |        0.20 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   2.773 μs | 0.0598 μs | 0.0895 μs |  1.00 | Baseline        |    0.05 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   2.732 μs | 0.0545 μs | 0.0815 μs |  0.99 | Same            |    0.04 |      52 B |        0.18 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_HGet             | HGET                 |  90.963 μs | 0.9391 μs | 1.4056 μs |  1.00 | Baseline        |    0.02 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 |  87.067 μs | 0.7463 μs | 1.1170 μs |  0.96 | Same            |    0.02 |      48 B |        0.09 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_HSet             | HSET                 |  90.898 μs | 1.4988 μs | 2.1969 μs |  1.00 | Baseline        |    0.03 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 |  89.698 μs | 0.7733 μs | 1.1334 μs |  0.99 | Same            |    0.03 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Incr             | INCR                 |  90.090 μs | 1.1671 μs | 1.7468 μs |  1.00 | Baseline        |    0.03 |     295 B |        1.00 |
| Respire_Incr                   | INCR                 |  87.530 μs | 0.6772 μs | 1.0137 μs |  0.97 | Same            |    0.02 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 174.753 μs | 0.9471 μs | 1.4176 μs |  1.00 | Baseline        |    0.01 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 167.172 μs | 1.2556 μs | 1.8405 μs |  0.96 | Same            |    0.01 |     256 B |        0.34 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Ping             | PING                 |  88.123 μs | 1.0519 μs | 1.5418 μs |  1.00 | Baseline        |    0.02 |     304 B |        1.00 |
| Respire_Ping                   | PING                 |  86.798 μs | 0.9819 μs | 1.4697 μs |  0.99 | Same            |    0.02 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential |  74.450 μs | 1.1983 μs | 1.7565 μs |  1.00 | Baseline        |    0.03 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential |  75.051 μs | 0.3954 μs | 0.5543 μs |  1.01 | Same            |    0.02 |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_SAdd             | SADD                 |  90.115 μs | 0.7295 μs | 1.0692 μs |  1.00 | Baseline        |    0.02 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 |  88.314 μs | 0.5617 μs | 0.8408 μs |  0.98 | Same            |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             |  97.846 μs | 0.6516 μs | 0.9552 μs |  1.00 | Baseline        |    0.01 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             |  94.055 μs | 0.3794 μs | 0.5441 μs |  0.96 | Same            |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Set_Small        | SET 13B              |  92.571 μs | 0.5478 μs | 0.7679 μs |  1.00 | Baseline        |    0.01 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              |  89.827 μs | 0.7445 μs | 1.0913 μs |  0.97 | Same            |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              |  93.906 μs | 0.7766 μs | 1.1384 μs |  1.00 | Baseline        |    0.02 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              |  91.184 μs | 0.6392 μs | 0.9167 μs |  0.97 | Same            |    0.02 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  |  76.657 μs | 1.5147 μs | 2.2202 μs |  1.00 | Baseline        |    0.04 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  |  76.600 μs | 0.5797 μs | 0.8676 μs |  1.00 | Same            |    0.03 |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 172.954 μs | 3.6611 μs | 5.4798 μs |  1.00 | Baseline        |    0.04 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 164.535 μs | 1.3530 μs | 1.9833 μs |  0.95 | Same            |    0.03 |     200 B |        0.31 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            |  92.665 μs | 1.0114 μs | 1.4825 μs |  1.00 | Baseline        |    0.02 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            |  88.489 μs | 1.0117 μs | 1.5143 μs |  0.96 | Same            |    0.02 |         - |        0.00 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
