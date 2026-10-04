---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

import ComparisonBarChart from '@site/src/components/ComparisonBarChart';

# Benchmarks

:::info Automated results
Generated 2026-10-04 04:36 UTC from commit `79814ecd838c`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/37175707703) for logs and downloadable artifacts.
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
  data={[{"label":"EXISTS hot","other":100867.6,"respire":338.5,"respireServer":109822.9},{"label":"GET hot","other":111696.4,"respire":168.2,"respireServer":110928.0},{"label":"GET missing hot","other":109575.2,"respire":158.1,"respireServer":109767.0},{"label":"HGET hot","other":112406.2,"respire":415.0,"respireServer":111479.2}]}
/>

<ComparisonBarChart
  title="Selected operation time — net10.0"
  description="Mean time. Shorter bars are faster."
  format="duration-ns"
  scale="group"
  showRatio
  data={[{"label":"GET","other":111085.0,"respire":109394.0},{"label":"GET x200 pipelined","other":1538.0,"respire":1496.0},{"label":"GET x50 concurrent","other":3289.0,"respire":3324.0},{"label":"HGET","other":111674.0,"respire":109765.0},{"label":"HSET","other":105737.0,"respire":111011.0},{"label":"LPUSH+LPOP","other":211429.0,"respire":215257.0},{"label":"SET 1KB","other":106227.0,"respire":112444.0}]}
/>

## net10.0

### ClientSideCachingBenchmarks

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
AMD EPYC 9V74 2.60GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  Job-IDGKZI : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                              | Categories      | Mean         | Error       | StdDev      | Median       | Op/s        | Ratio | MannWhitney(5%) | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------------ |---------------- |-------------:|------------:|------------:|-------------:|------------:|------:|---------------- |--------:|-------:|----------:|------------:|
| StackExchange_Exists_ServerRead     | EXISTS hot      | 100,867.6 ns | 2,423.84 ns | 3,627.88 ns | 101,806.8 ns |     9,914.0 | 1.001 | Baseline        |    0.05 |      - |     294 B |        1.00 |
| Respire_Exists_ServerRead           | EXISTS hot      | 109,822.9 ns |   652.11 ns |   935.24 ns | 109,748.3 ns |     9,105.6 | 1.090 | Slower          |    0.04 |      - |         - |        0.00 |
| Respire_Exists_ClientCacheHit       | EXISTS hot      |     338.5 ns |     1.69 ns |     2.32 ns |     339.9 ns | 2,953,969.1 | 0.003 | Faster          |    0.00 |      - |         - |        0.00 |
|                                     |                 |              |             |             |              |             |       |                 |         |        |           |             |
| StackExchange_Get_ServerRead        | GET hot         | 111,696.4 ns |   769.13 ns | 1,151.20 ns | 111,792.5 ns |     8,952.8 | 1.000 | Baseline        |    0.01 |      - |     528 B |        1.00 |
| Respire_Get_ServerRead              | GET hot         | 110,928.0 ns |   416.51 ns |   610.52 ns | 110,985.0 ns |     9,014.9 | 0.993 | Same            |    0.01 |      - |      64 B |        0.12 |
| Respire_Get_ClientCacheHit          | GET hot         |     168.2 ns |     0.56 ns |     0.78 ns |     168.0 ns | 5,946,697.2 | 0.002 | Faster          |    0.00 | 0.0038 |      64 B |        0.12 |
|                                     |                 |              |             |             |              |             |       |                 |         |        |           |             |
| StackExchange_GetMissing_ServerRead | GET missing hot | 109,575.2 ns |   915.52 ns | 1,370.31 ns | 109,890.1 ns |     9,126.2 | 1.000 | Baseline        |    0.02 |      - |     416 B |        1.00 |
| Respire_GetMissing_ServerRead       | GET missing hot | 109,767.0 ns |   698.59 ns | 1,045.61 ns | 109,846.2 ns |     9,110.2 | 1.002 | Same            |    0.02 |      - |         - |        0.00 |
| Respire_GetMissing_ClientCacheHit   | GET missing hot |     158.1 ns |     0.27 ns |     0.38 ns |     158.1 ns | 6,323,436.7 | 0.001 | Faster          |    0.00 |      - |         - |        0.00 |
|                                     |                 |              |             |             |              |             |       |                 |         |        |           |             |
| StackExchange_HGet_ServerRead       | HGET hot        | 112,406.2 ns |   572.63 ns |   821.24 ns | 112,514.9 ns |     8,896.3 | 1.000 | Baseline        |    0.01 |      - |     544 B |        1.00 |
| Respire_HGet_ServerRead             | HGET hot        | 111,479.2 ns |   723.97 ns | 1,083.60 ns | 111,543.8 ns |     8,970.3 | 0.992 | Same            |    0.01 |      - |      64 B |        0.12 |
| Respire_HGet_ClientCacheHit         | HGET hot        |     415.0 ns |     6.32 ns |     8.66 ns |     408.8 ns | 2,409,685.5 | 0.004 | Faster          |    0.00 | 0.0038 |      64 B |        0.12 |

### CommonOperationsBenchmarks

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
AMD EPYC 9V74 2.60GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  Job-IDGKZI : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                         | Categories           | Mean       | Error     | StdDev    | Ratio | MannWhitney(5%) | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|----------:|----------:|------:|---------------- |--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               |  99.081 μs | 3.2365 μs | 4.8442 μs |  1.00 | Baseline        |    0.07 |      - |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 109.176 μs | 0.4000 μs | 0.5987 μs |  1.10 | Slower          |    0.05 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get              | GET                  | 111.085 μs | 0.9167 μs | 1.3721 μs |  1.00 | Baseline        |    0.02 |      - |     504 B |        1.00 |
| Respire_Get                    | GET                  | 109.394 μs | 0.4644 μs | 0.6950 μs |  0.98 | Same            |    0.01 |      - |      48 B |        0.10 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  |  84.586 μs | 1.0750 μs | 1.5757 μs |  1.00 | Baseline        |    0.03 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  |  83.913 μs | 0.5150 μs | 0.7386 μs |  0.99 | Same            |    0.02 |      - |      50 B |        0.15 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Pipelined    | GET x200 pipelined   |   1.538 μs | 0.0512 μs | 0.0766 μs |  1.00 | Baseline        |    0.07 | 0.0146 |     289 B |        1.00 |
| Respire_Get_Pipelined          | GET x200 pipelined   |   1.496 μs | 0.0174 μs | 0.0254 μs |  0.97 | Same            |    0.05 |      - |      62 B |        0.21 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   3.289 μs | 0.0447 μs | 0.0669 μs |  1.00 | Baseline        |    0.03 | 0.0146 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   3.324 μs | 0.0614 μs | 0.0918 μs |  1.01 | Same            |    0.03 |      - |      52 B |        0.18 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HGet             | HGET                 | 111.674 μs | 0.8059 μs | 1.1813 μs |  1.00 | Baseline        |    0.01 |      - |     519 B |        1.00 |
| Respire_HGet                   | HGET                 | 109.765 μs | 0.5645 μs | 0.8449 μs |  0.98 | Same            |    0.01 |      - |      48 B |        0.09 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HSet             | HSET                 | 105.737 μs | 0.6150 μs | 0.9205 μs |  1.00 | Baseline        |    0.01 |      - |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 111.011 μs | 0.5666 μs | 0.8480 μs |  1.05 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Incr             | INCR                 | 104.250 μs | 1.2415 μs | 1.8582 μs |  1.00 | Baseline        |    0.03 |      - |     295 B |        1.00 |
| Respire_Incr                   | INCR                 | 110.113 μs | 0.4317 μs | 0.6328 μs |  1.06 | Same            |    0.02 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 211.429 μs | 2.6727 μs | 4.0003 μs |  1.00 | Baseline        |    0.03 |      - |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 215.257 μs | 0.8419 μs | 1.2340 μs |  1.02 | Same            |    0.02 |      - |     256 B |        0.34 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping             | PING                 |  95.945 μs | 2.6256 μs | 3.9298 μs |  1.00 | Baseline        |    0.06 |      - |     303 B |        1.00 |
| Respire_Ping                   | PING                 | 106.621 μs | 0.5742 μs | 0.8417 μs |  1.11 | Slower          |    0.05 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential |  82.761 μs | 0.8132 μs | 1.2171 μs |  1.00 | Baseline        |    0.02 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential |  82.314 μs | 0.3646 μs | 0.5344 μs |  0.99 | Same            |    0.02 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SAdd             | SADD                 |  97.167 μs | 3.6067 μs | 5.2867 μs |  1.00 | Baseline        |    0.08 |      - |     308 B |        1.00 |
| Respire_SAdd                   | SADD                 | 109.412 μs | 0.5450 μs | 0.8157 μs |  1.13 | Slower          |    0.06 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 116.928 μs | 0.6344 μs | 0.9299 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 122.187 μs | 1.2531 μs | 1.8368 μs |  1.05 | Same            |    0.02 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 105.280 μs | 1.0327 μs | 1.5458 μs |  1.00 | Baseline        |    0.02 |      - |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 110.822 μs | 0.5374 μs | 0.8044 μs |  1.05 | Same            |    0.02 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 106.227 μs | 1.2960 μs | 1.9398 μs |  1.00 | Baseline        |    0.03 |      - |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 112.444 μs | 0.5556 μs | 0.8317 μs |  1.06 | Same            |    0.02 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  |  87.055 μs | 1.7436 μs | 2.6098 μs |  1.00 | Baseline        |    0.04 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  |  85.495 μs | 0.9387 μs | 1.3462 μs |  0.98 | Same            |    0.03 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 208.138 μs | 1.4774 μs | 2.2114 μs |  1.00 | Baseline        |    0.01 |      - |     647 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 213.215 μs | 0.7519 μs | 1.1253 μs |  1.02 | Same            |    0.01 |      - |     200 B |        0.31 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 102.391 μs | 1.3270 μs | 1.9451 μs |  1.00 | Baseline        |    0.03 |      - |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 110.229 μs | 0.4680 μs | 0.7004 μs |  1.08 | Slower          |    0.02 |      - |         - |        0.00 |

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
| Method                              | Categories      | Mean         | Error       | StdDev      | Median       | Op/s        | Ratio | MannWhitney(5%) | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------------ |---------------- |-------------:|------------:|------------:|-------------:|------------:|------:|---------------- |--------:|-------:|----------:|------------:|
| StackExchange_Exists_ServerRead     | EXISTS hot      | 189,298.1 ns |   640.58 ns |   938.95 ns | 189,259.3 ns |     5,282.7 | 1.000 | Baseline        |    0.01 |      - |     296 B |        1.00 |
| Respire_Exists_ServerRead           | EXISTS hot      | 188,163.3 ns |   732.39 ns | 1,073.53 ns | 188,311.9 ns |     5,314.5 | 0.994 | Same            |    0.01 |      - |         - |        0.00 |
| Respire_Exists_ClientCacheHit       | EXISTS hot      |     642.2 ns |     3.35 ns |     4.80 ns |     641.7 ns | 1,557,230.1 | 0.003 | Faster          |    0.00 |      - |         - |        0.00 |
|                                     |                 |              |             |             |              |             |       |                 |         |        |           |             |
| StackExchange_Get_ServerRead        | GET hot         | 193,101.2 ns | 2,758.35 ns | 4,128.56 ns | 194,074.3 ns |     5,178.6 | 1.000 | Baseline        |    0.03 |      - |     528 B |        1.00 |
| Respire_Get_ServerRead              | GET hot         | 189,726.3 ns | 1,137.82 ns | 1,631.83 ns | 189,432.7 ns |     5,270.7 | 0.983 | Same            |    0.02 |      - |      64 B |        0.12 |
| Respire_Get_ClientCacheHit          | GET hot         |     402.0 ns |    13.84 ns |    19.41 ns |     418.6 ns | 2,487,666.9 | 0.002 | Faster          |    0.00 | 0.0038 |      64 B |        0.12 |
|                                     |                 |              |             |             |              |             |       |                 |         |        |           |             |
| StackExchange_GetMissing_ServerRead | GET missing hot | 192,998.0 ns |   882.53 ns | 1,320.93 ns | 193,276.9 ns |     5,181.4 | 1.000 | Baseline        |    0.01 |      - |     416 B |        1.00 |
| Respire_GetMissing_ServerRead       | GET missing hot | 188,301.4 ns |   922.43 ns | 1,380.65 ns | 188,371.1 ns |     5,310.6 | 0.976 | Same            |    0.01 |      - |         - |        0.00 |
| Respire_GetMissing_ClientCacheHit   | GET missing hot |     394.3 ns |    20.70 ns |    29.69 ns |     420.5 ns | 2,536,180.7 | 0.002 | Faster          |    0.00 |      - |         - |        0.00 |
|                                     |                 |              |             |             |              |             |       |                 |         |        |           |             |
| StackExchange_HGet_ServerRead       | HGET hot        | 193,874.7 ns | 1,246.90 ns | 1,866.30 ns | 194,360.1 ns |     5,158.0 | 1.000 | Baseline        |    0.01 |      - |     544 B |        1.00 |
| Respire_HGet_ServerRead             | HGET hot        | 187,440.4 ns | 1,016.09 ns | 1,520.84 ns | 187,431.1 ns |     5,335.0 | 0.967 | Same            |    0.01 |      - |      64 B |        0.12 |
| Respire_HGet_ClientCacheHit         | HGET hot        |     725.9 ns |     4.78 ns |     6.38 ns |     729.8 ns | 1,377,622.7 | 0.004 | Faster          |    0.00 | 0.0038 |      64 B |        0.12 |

### CommonOperationsBenchmarks

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
AMD EPYC 7763 2.45GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 8.0.31 (8.0.31, 8.0.3126.42015), X64 RyuJIT x86-64-v3
  Job-IDGKZI : .NET 8.0.31 (8.0.31, 8.0.3126.42015), X64 RyuJIT x86-64-v3

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                         | Categories           | Mean       | Error     | StdDev    | Median     | Ratio | MannWhitney(5%) | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|----------:|----------:|-----------:|------:|---------------- |--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 187.493 μs | 0.8789 μs | 1.3154 μs | 187.428 μs |  1.00 | Baseline        |    0.01 |      - |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 186.632 μs | 0.6499 μs | 0.9727 μs | 186.841 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |            |       |                 |         |        |           |             |
| StackExchange_Get              | GET                  | 192.080 μs | 1.1232 μs | 1.6811 μs | 191.851 μs |  1.00 | Baseline        |    0.01 |      - |     504 B |        1.00 |
| Respire_Get                    | GET                  | 185.596 μs | 0.7487 μs | 1.1206 μs | 185.387 μs |  0.97 | Same            |    0.01 |      - |      48 B |        0.10 |
|                                |                      |            |           |           |            |       |                 |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 175.535 μs | 1.2794 μs | 1.8753 μs | 175.681 μs |  1.00 | Baseline        |    0.01 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 172.517 μs | 1.2106 μs | 1.7744 μs | 172.695 μs |  0.98 | Same            |    0.01 |      - |      50 B |        0.15 |
|                                |                      |            |           |           |            |       |                 |         |        |           |             |
| StackExchange_Get_Pipelined    | GET x200 pipelined   |   2.465 μs | 0.0795 μs | 0.1189 μs |   2.484 μs |  1.00 | Baseline        |    0.07 | 0.0098 |     289 B |        1.00 |
| Respire_Get_Pipelined          | GET x200 pipelined   |   2.289 μs | 0.0164 μs | 0.0240 μs |   2.294 μs |  0.93 | Same            |    0.05 |      - |      61 B |        0.21 |
|                                |                      |            |           |           |            |       |                 |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.414 μs | 0.0343 μs | 0.0480 μs |   5.421 μs |  1.00 | Baseline        |    0.01 | 0.0098 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   5.652 μs | 0.0351 μs | 0.0525 μs |   5.660 μs |  1.04 | Same            |    0.01 |      - |      52 B |        0.18 |
|                                |                      |            |           |           |            |       |                 |         |        |           |             |
| StackExchange_HGet             | HGET                 | 193.230 μs | 1.9872 μs | 2.9128 μs | 193.728 μs |  1.00 | Baseline        |    0.02 |      - |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 186.453 μs | 0.8491 μs | 1.2446 μs | 186.542 μs |  0.97 | Same            |    0.02 |      - |      48 B |        0.09 |
|                                |                      |            |           |           |            |       |                 |         |        |           |             |
| StackExchange_HSet             | HSET                 | 189.233 μs | 1.7490 μs | 2.5083 μs | 189.489 μs |  1.00 | Baseline        |    0.02 |      - |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 188.326 μs | 0.7682 μs | 1.1017 μs | 188.438 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |            |       |                 |         |        |           |             |
| StackExchange_Incr             | INCR                 | 188.773 μs | 0.7035 μs | 1.0529 μs | 188.882 μs |  1.00 | Baseline        |    0.01 |      - |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 187.202 μs | 0.9759 μs | 1.4607 μs | 187.241 μs |  0.99 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |            |       |                 |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 377.295 μs | 1.8318 μs | 2.6851 μs | 378.176 μs |  1.00 | Baseline        |    0.01 |      - |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 372.010 μs | 1.3685 μs | 2.0483 μs | 372.285 μs |  0.99 | Same            |    0.01 |      - |     256 B |        0.34 |
|                                |                      |            |           |           |            |       |                 |         |        |           |             |
| StackExchange_Ping             | PING                 | 184.934 μs | 0.9700 μs | 1.4519 μs | 185.042 μs |  1.00 | Baseline        |    0.01 |      - |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 184.619 μs | 0.6618 μs | 0.9700 μs | 184.960 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |            |       |                 |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 172.216 μs | 1.5619 μs | 2.3378 μs | 172.588 μs |  1.00 | Baseline        |    0.02 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 169.232 μs | 1.2150 μs | 1.7810 μs | 169.660 μs |  0.98 | Same            |    0.02 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |            |       |                 |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 188.022 μs | 0.7158 μs | 1.0714 μs | 188.043 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 187.226 μs | 0.9170 μs | 1.3726 μs | 187.202 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |            |       |                 |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 201.142 μs | 2.3288 μs | 3.3399 μs | 199.393 μs |  1.00 | Baseline        |    0.02 |      - |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 205.710 μs | 0.8687 μs | 1.2458 μs | 205.624 μs |  1.02 | Same            |    0.02 |      - |         - |        0.00 |
|                                |                      |            |           |           |            |       |                 |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 189.648 μs | 0.7616 μs | 1.1163 μs | 189.617 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 187.946 μs | 0.7870 μs | 1.1780 μs | 188.027 μs |  0.99 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |            |       |                 |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 190.196 μs | 0.9344 μs | 1.3986 μs | 190.407 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 189.050 μs | 0.7202 μs | 1.0780 μs | 189.187 μs |  0.99 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |            |       |                 |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 177.450 μs | 0.9829 μs | 1.4408 μs | 177.614 μs |  1.00 | Baseline        |    0.01 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 176.283 μs | 1.5256 μs | 2.2835 μs | 176.806 μs |  0.99 | Same            |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |            |       |                 |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 374.530 μs | 2.6403 μs | 3.9518 μs | 374.460 μs |  1.00 | Baseline        |    0.01 |      - |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 365.439 μs | 1.7221 μs | 2.5775 μs | 365.823 μs |  0.98 | Same            |    0.01 |      - |     200 B |        0.31 |
|                                |                      |            |           |           |            |       |                 |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 185.427 μs | 1.0991 μs | 1.6451 μs | 185.542 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 186.326 μs | 0.5926 μs | 0.8687 μs | 186.641 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
