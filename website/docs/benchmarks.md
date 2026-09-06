---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

import ComparisonBarChart from '@site/src/components/ComparisonBarChart';

# Benchmarks

:::info Automated results
Generated 2026-09-06 03:38 UTC from commit `01d5b94b4f55`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/34008027371) for logs and downloadable artifacts.
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
  data={[{"label":"EXISTS hot","other":196301.8,"respire":438.5,"respireServer":194872.4},{"label":"GET hot","other":198136.0,"respire":244.4,"respireServer":194894.2},{"label":"GET missing hot","other":197439.3,"respire":192.3,"respireServer":194024.8},{"label":"HGET hot","other":198858.7,"respire":581.8,"respireServer":194729.0}]}
/>

<ComparisonBarChart
  title="Selected operation time — net10.0"
  description="Mean time. Shorter bars are faster."
  format="duration-ns"
  scale="group"
  showRatio
  data={[{"label":"GET","other":197403.0,"respire":194484.0},{"label":"GET x200 pipelined","other":2481.0,"respire":2221.0},{"label":"GET x50 concurrent","other":5438.0,"respire":5523.0},{"label":"HGET","other":198162.0,"respire":194557.0},{"label":"HSET","other":197582.0,"respire":195718.0},{"label":"LPUSH+LPOP","other":382417.0,"respire":376822.0},{"label":"SET 1KB","other":198578.0,"respire":197412.0}]}
/>

## net10.0

### ClientSideCachingBenchmarks

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD EPYC 7763 2.45GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.400
  [Host]     : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3
  Job-IDGKZI : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                              | Categories      | Mean         | Error     | StdDev      | Op/s        | Ratio | MannWhitney(5%) | Gen0   | Allocated | Alloc Ratio |
|------------------------------------ |---------------- |-------------:|----------:|------------:|------------:|------:|---------------- |-------:|----------:|------------:|
| StackExchange_Exists_ServerRead     | EXISTS hot      | 196,301.8 ns | 536.95 ns |   787.06 ns |     5,094.2 | 1.000 | Baseline        |      - |     296 B |        1.00 |
| Respire_Exists_ServerRead           | EXISTS hot      | 194,872.4 ns | 743.00 ns | 1,112.09 ns |     5,131.6 | 0.993 | Same            |      - |         - |        0.00 |
| Respire_Exists_ClientCacheHit       | EXISTS hot      |     438.5 ns |   1.31 ns |     1.84 ns | 2,280,270.1 | 0.002 | Faster          |      - |         - |        0.00 |
|                                     |                 |              |           |             |             |       |                 |        |           |             |
| StackExchange_Get_ServerRead        | GET hot         | 198,136.0 ns | 877.77 ns | 1,313.80 ns |     5,047.0 | 1.000 | Baseline        |      - |     528 B |        1.00 |
| Respire_Get_ServerRead              | GET hot         | 194,894.2 ns | 782.87 ns | 1,171.76 ns |     5,131.0 | 0.984 | Same            |      - |      64 B |        0.12 |
| Respire_Get_ClientCacheHit          | GET hot         |     244.4 ns |  15.48 ns |    23.16 ns | 4,091,475.1 | 0.001 | Faster          | 0.0038 |      64 B |        0.12 |
|                                     |                 |              |           |             |             |       |                 |        |           |             |
| StackExchange_GetMissing_ServerRead | GET missing hot | 197,439.3 ns | 661.26 ns |   989.75 ns |     5,064.8 | 1.000 | Baseline        |      - |     416 B |        1.00 |
| Respire_GetMissing_ServerRead       | GET missing hot | 194,024.8 ns | 613.33 ns |   899.01 ns |     5,154.0 | 0.983 | Same            |      - |         - |        0.00 |
| Respire_GetMissing_ClientCacheHit   | GET missing hot |     192.3 ns |   1.06 ns |     1.55 ns | 5,201,021.7 | 0.001 | Faster          |      - |         - |        0.00 |
|                                     |                 |              |           |             |             |       |                 |        |           |             |
| StackExchange_HGet_ServerRead       | HGET hot        | 198,858.7 ns | 771.67 ns | 1,154.99 ns |     5,028.7 | 1.000 | Baseline        |      - |     544 B |        1.00 |
| Respire_HGet_ServerRead             | HGET hot        | 194,729.0 ns | 685.46 ns | 1,004.74 ns |     5,135.3 | 0.979 | Same            |      - |      64 B |        0.12 |
| Respire_HGet_ClientCacheHit         | HGET hot        |     581.8 ns |  47.20 ns |    67.70 ns | 1,718,796.6 | 0.003 | Faster          | 0.0038 |      64 B |        0.12 |

### CommonOperationsBenchmarks

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD EPYC 7763 2.45GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.400
  [Host]     : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3
  Job-IDGKZI : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                         | Categories           | Mean       | Error     | StdDev    | Ratio | MannWhitney(5%) | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|----------:|----------:|------:|---------------- |--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 194.564 μs | 0.7376 μs | 1.1040 μs |  1.00 | Baseline        |    0.01 |      - |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 194.822 μs | 0.7267 μs | 1.0877 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get              | GET                  | 197.403 μs | 0.6745 μs | 1.0095 μs |  1.00 | Baseline        |    0.01 |      - |     504 B |        1.00 |
| Respire_Get                    | GET                  | 194.484 μs | 0.7033 μs | 1.0309 μs |  0.99 | Same            |    0.01 |      - |      48 B |        0.10 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 179.617 μs | 0.9960 μs | 1.4908 μs |  1.00 | Baseline        |    0.01 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 177.857 μs | 1.0927 μs | 1.6356 μs |  0.99 | Same            |    0.01 |      - |      50 B |        0.15 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Pipelined    | GET x200 pipelined   |   2.481 μs | 0.0468 μs | 0.0685 μs |  1.00 | Baseline        |    0.04 | 0.0098 |     289 B |        1.00 |
| Respire_Get_Pipelined          | GET x200 pipelined   |   2.221 μs | 0.0100 μs | 0.0144 μs |  0.90 | Faster          |    0.03 |      - |      60 B |        0.21 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.438 μs | 0.0353 μs | 0.0517 μs |  1.00 | Baseline        |    0.01 |      - |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   5.523 μs | 0.0480 μs | 0.0718 μs |  1.02 | Same            |    0.02 |      - |      52 B |        0.18 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HGet             | HGET                 | 198.162 μs | 0.7509 μs | 1.1239 μs |  1.00 | Baseline        |    0.01 |      - |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 194.557 μs | 0.8565 μs | 1.2555 μs |  0.98 | Same            |    0.01 |      - |      48 B |        0.09 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HSet             | HSET                 | 197.582 μs | 0.5239 μs | 0.7841 μs |  1.00 | Baseline        |    0.01 |      - |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 195.718 μs | 1.0692 μs | 1.6003 μs |  0.99 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Incr             | INCR                 | 195.770 μs | 0.7083 μs | 1.0601 μs |  1.00 | Baseline        |    0.01 |      - |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 194.583 μs | 1.0108 μs | 1.5129 μs |  0.99 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 382.417 μs | 1.1002 μs | 1.6127 μs |  1.00 | Baseline        |    0.01 |      - |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 376.822 μs | 1.2167 μs | 1.7449 μs |  0.99 | Same            |    0.01 |      - |     256 B |        0.34 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping             | PING                 | 193.405 μs | 0.4991 μs | 0.7316 μs |  1.00 | Baseline        |    0.01 |      - |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 194.295 μs | 0.6139 μs | 0.9188 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 177.902 μs | 1.2842 μs | 1.9221 μs |  1.00 | Baseline        |    0.02 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 175.702 μs | 1.3154 μs | 1.9688 μs |  0.99 | Same            |    0.02 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 196.683 μs | 0.7396 μs | 1.1070 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 194.887 μs | 0.7721 μs | 1.1557 μs |  0.99 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 208.112 μs | 0.6625 μs | 0.9501 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 209.620 μs | 0.6486 μs | 0.9508 μs |  1.01 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 197.575 μs | 0.6943 μs | 1.0178 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 196.920 μs | 0.6410 μs | 0.9396 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 198.578 μs | 0.4348 μs | 0.6509 μs |  1.00 | Baseline        |    0.00 |      - |     311 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 197.412 μs | 0.7728 μs | 1.1327 μs |  0.99 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 181.601 μs | 0.8080 μs | 1.1844 μs |  1.00 | Baseline        |    0.01 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 179.949 μs | 0.9757 μs | 1.4302 μs |  0.99 | Same            |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 378.074 μs | 0.9462 μs | 1.4163 μs |  1.00 | Baseline        |    0.01 |      - |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 375.252 μs | 0.7384 μs | 1.0823 μs |  0.99 | Same            |    0.00 |      - |     200 B |        0.31 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 195.561 μs | 0.9884 μs | 1.4793 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 194.593 μs | 0.6784 μs | 1.0154 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |

## net8.0

### ClientSideCachingBenchmarks

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD EPYC 7763 2.45GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.400
  [Host]     : .NET 8.0.30 (8.0.30, 8.0.3026.36720), X64 RyuJIT x86-64-v3
  Job-IDGKZI : .NET 8.0.30 (8.0.30, 8.0.3026.36720), X64 RyuJIT x86-64-v3

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                              | Categories      | Mean         | Error       | StdDev      | Median       | Op/s        | Ratio | MannWhitney(5%) | Gen0   | Allocated | Alloc Ratio |
|------------------------------------ |---------------- |-------------:|------------:|------------:|-------------:|------------:|------:|---------------- |-------:|----------:|------------:|
| StackExchange_Exists_ServerRead     | EXISTS hot      | 188,896.3 ns |   712.78 ns | 1,066.85 ns | 188,966.6 ns |     5,293.9 | 1.000 | Baseline        |      - |     296 B |        1.00 |
| Respire_Exists_ServerRead           | EXISTS hot      | 187,700.4 ns |   885.80 ns | 1,270.38 ns | 187,847.7 ns |     5,327.6 | 0.994 | Same            |      - |         - |        0.00 |
| Respire_Exists_ClientCacheHit       | EXISTS hot      |     643.7 ns |    23.54 ns |    32.22 ns |     644.7 ns | 1,553,513.5 | 0.003 | Faster          |      - |         - |        0.00 |
|                                     |                 |              |             |             |              |             |       |                 |        |           |             |
| StackExchange_Get_ServerRead        | GET hot         | 193,515.3 ns |   629.38 ns |   942.02 ns | 193,718.0 ns |     5,167.5 | 1.000 | Baseline        |      - |     528 B |        1.00 |
| Respire_Get_ServerRead              | GET hot         | 188,083.4 ns |   794.84 ns | 1,189.68 ns | 188,166.7 ns |     5,316.8 | 0.972 | Same            |      - |      64 B |        0.12 |
| Respire_Get_ClientCacheHit          | GET hot         |     419.7 ns |     1.69 ns |     2.47 ns |     419.4 ns | 2,382,644.4 | 0.002 | Faster          | 0.0038 |      64 B |        0.12 |
|                                     |                 |              |             |             |              |             |       |                 |        |           |             |
| StackExchange_GetMissing_ServerRead | GET missing hot | 192,851.3 ns | 1,265.14 ns | 1,854.43 ns | 193,097.7 ns |     5,185.3 | 1.000 | Baseline        |      - |     416 B |        1.00 |
| Respire_GetMissing_ServerRead       | GET missing hot | 189,159.2 ns | 1,336.45 ns | 2,000.33 ns | 188,944.3 ns |     5,286.6 | 0.981 | Same            |      - |         - |        0.00 |
| Respire_GetMissing_ClientCacheHit   | GET missing hot |     411.8 ns |     7.11 ns |    10.42 ns |     404.0 ns | 2,428,131.1 | 0.002 | Faster          |      - |         - |        0.00 |
|                                     |                 |              |             |             |              |             |       |                 |        |           |             |
| StackExchange_HGet_ServerRead       | HGET hot        | 196,920.7 ns |   798.23 ns | 1,194.75 ns | 197,007.6 ns |     5,078.2 | 1.000 | Baseline        |      - |     544 B |        1.00 |
| Respire_HGet_ServerRead             | HGET hot        | 191,489.8 ns |   999.14 ns | 1,432.93 ns | 191,873.4 ns |     5,222.2 | 0.972 | Same            |      - |      64 B |        0.12 |
| Respire_HGet_ClientCacheHit         | HGET hot        |     743.6 ns |     4.00 ns |     5.99 ns |     743.6 ns | 1,344,776.8 | 0.004 | Faster          | 0.0038 |      64 B |        0.12 |

### CommonOperationsBenchmarks

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD EPYC 7763 2.45GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.400
  [Host]     : .NET 8.0.30 (8.0.30, 8.0.3026.36720), X64 RyuJIT x86-64-v3
  Job-IDGKZI : .NET 8.0.30 (8.0.30, 8.0.3026.36720), X64 RyuJIT x86-64-v3

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                         | Categories           | Mean       | Error     | StdDev    | Ratio | MannWhitney(5%) | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|----------:|----------:|------:|---------------- |--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 188.248 μs | 0.8017 μs | 1.1752 μs |  1.00 | Baseline        |    0.01 |      - |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 186.793 μs | 0.7089 μs | 1.0167 μs |  0.99 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get              | GET                  | 192.484 μs | 0.9308 μs | 1.3643 μs |  1.00 | Baseline        |    0.01 |      - |     504 B |        1.00 |
| Respire_Get                    | GET                  | 186.132 μs | 0.7342 μs | 1.0990 μs |  0.97 | Same            |    0.01 |      - |      48 B |        0.10 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 177.095 μs | 1.3897 μs | 2.0801 μs |  1.00 | Baseline        |    0.02 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 173.218 μs | 1.3937 μs | 2.0429 μs |  0.98 | Same            |    0.02 |      - |      50 B |        0.15 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Pipelined    | GET x200 pipelined   |   2.528 μs | 0.0681 μs | 0.1019 μs |  1.00 | Baseline        |    0.06 | 0.0098 |     289 B |        1.00 |
| Respire_Get_Pipelined          | GET x200 pipelined   |   2.289 μs | 0.0158 μs | 0.0221 μs |  0.91 | Faster          |    0.04 |      - |      61 B |        0.21 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.490 μs | 0.0376 μs | 0.0550 μs |  1.00 | Baseline        |    0.01 | 0.0098 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   5.653 μs | 0.0403 μs | 0.0603 μs |  1.03 | Same            |    0.01 |      - |      52 B |        0.18 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HGet             | HGET                 | 193.189 μs | 1.0202 μs | 1.5270 μs |  1.00 | Baseline        |    0.01 |      - |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 186.720 μs | 0.6615 μs | 0.9696 μs |  0.97 | Same            |    0.01 |      - |      48 B |        0.09 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HSet             | HSET                 | 189.916 μs | 0.8428 μs | 1.2614 μs |  1.00 | Baseline        |    0.01 |      - |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 189.553 μs | 1.1513 μs | 1.6875 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Incr             | INCR                 | 188.665 μs | 0.4607 μs | 0.6753 μs |  1.00 | Baseline        |    0.00 |      - |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 187.404 μs | 0.7980 μs | 1.1697 μs |  0.99 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 378.301 μs | 1.2800 μs | 1.8762 μs |  1.00 | Baseline        |    0.01 |      - |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 373.504 μs | 1.0998 μs | 1.6120 μs |  0.99 | Same            |    0.01 |      - |     256 B |        0.34 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping             | PING                 | 186.052 μs | 0.6831 μs | 1.0224 μs |  1.00 | Baseline        |    0.01 |      - |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 188.163 μs | 1.4995 μs | 2.1021 μs |  1.01 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 177.130 μs | 1.5458 μs | 2.3137 μs |  1.00 | Baseline        |    0.02 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 173.740 μs | 0.9304 μs | 1.3926 μs |  0.98 | Same            |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 191.246 μs | 0.6878 μs | 1.0081 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 190.488 μs | 0.9247 μs | 1.3841 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 204.798 μs | 2.8708 μs | 4.2969 μs |  1.00 | Baseline        |    0.03 |      - |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 207.136 μs | 0.8575 μs | 1.2834 μs |  1.01 | Same            |    0.02 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 191.652 μs | 0.7196 μs | 1.0771 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 192.093 μs | 0.9383 μs | 1.4043 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 191.161 μs | 1.3053 μs | 1.9133 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 190.327 μs | 0.9107 μs | 1.3631 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 178.581 μs | 1.1068 μs | 1.6565 μs |  1.00 | Baseline        |    0.01 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 175.652 μs | 0.9365 μs | 1.4016 μs |  0.98 | Same            |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 371.889 μs | 2.0028 μs | 2.9977 μs |  1.00 | Baseline        |    0.01 |      - |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 368.271 μs | 1.1773 μs | 1.6885 μs |  0.99 | Same            |    0.01 |      - |     200 B |        0.31 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 188.141 μs | 0.8055 μs | 1.2056 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 187.567 μs | 0.7318 μs | 1.0953 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
