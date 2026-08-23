---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

import ComparisonBarChart from '@site/src/components/ComparisonBarChart';

# Benchmarks

:::info Automated results
Generated 2026-08-23 03:54 UTC from commit `ffb1409f9f0e`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/32615046669) for logs and downloadable artifacts.
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
  data={[{"label":"EXISTS hot","other":190345.5,"respire":455.1,"respireServer":190039.2},{"label":"GET hot","other":193318.1,"respire":212.5,"respireServer":190555.0},{"label":"GET missing hot","other":191781.3,"respire":194.6,"respireServer":190284.2},{"label":"HGET hot","other":193623.0,"respire":534.1,"respireServer":191619.8}]}
/>

<ComparisonBarChart
  title="Selected operation time — net10.0"
  description="Mean time. Shorter bars are faster."
  format="duration-ns"
  scale="group"
  showRatio
  data={[{"label":"GET","other":192151.0,"respire":201283.0},{"label":"GET x200 pipelined","other":2349.0,"respire":2200.0},{"label":"GET x50 concurrent","other":5307.0,"respire":5527.0},{"label":"HGET","other":192715.0,"respire":190232.0},{"label":"HSET","other":192069.0,"respire":191523.0},{"label":"LPUSH+LPOP","other":374051.0,"respire":369920.0},{"label":"SET 1KB","other":193352.0,"respire":191280.0}]}
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
| StackExchange_Exists_ServerRead     | EXISTS hot      | 190,345.5 ns | 593.40 ns |   888.17 ns |     5,253.6 | 1.000 | Baseline        |      - |     296 B |        1.00 |
| Respire_Exists_ServerRead           | EXISTS hot      | 190,039.2 ns | 895.00 ns | 1,283.58 ns |     5,262.1 | 0.998 | Same            |      - |         - |        0.00 |
| Respire_Exists_ClientCacheHit       | EXISTS hot      |     455.1 ns |   7.30 ns |     9.99 ns | 2,197,481.2 | 0.002 | Faster          |      - |         - |        0.00 |
|                                     |                 |              |           |             |             |       |                 |        |           |             |
| StackExchange_Get_ServerRead        | GET hot         | 193,318.1 ns | 774.72 ns | 1,111.08 ns |     5,172.8 | 1.000 | Baseline        |      - |     528 B |        1.00 |
| Respire_Get_ServerRead              | GET hot         | 190,555.0 ns | 934.21 ns | 1,398.28 ns |     5,247.8 | 0.986 | Same            |      - |      64 B |        0.12 |
| Respire_Get_ClientCacheHit          | GET hot         |     212.5 ns |   0.74 ns |     1.07 ns | 4,704,994.8 | 0.001 | Faster          | 0.0038 |      64 B |        0.12 |
|                                     |                 |              |           |             |             |       |                 |        |           |             |
| StackExchange_GetMissing_ServerRead | GET missing hot | 191,781.3 ns | 745.89 ns | 1,116.41 ns |     5,214.3 | 1.000 | Baseline        |      - |     416 B |        1.00 |
| Respire_GetMissing_ServerRead       | GET missing hot | 190,284.2 ns | 630.85 ns |   944.22 ns |     5,255.3 | 0.992 | Same            |      - |         - |        0.00 |
| Respire_GetMissing_ClientCacheHit   | GET missing hot |     194.6 ns |   2.25 ns |     3.36 ns | 5,139,046.8 | 0.001 | Faster          |      - |         - |        0.00 |
|                                     |                 |              |           |             |             |       |                 |        |           |             |
| StackExchange_HGet_ServerRead       | HGET hot        | 193,623.0 ns | 484.13 ns |   724.62 ns |     5,164.7 | 1.000 | Baseline        |      - |     544 B |        1.00 |
| Respire_HGet_ServerRead             | HGET hot        | 191,619.8 ns | 775.53 ns | 1,160.77 ns |     5,218.7 | 0.990 | Same            |      - |      64 B |        0.12 |
| Respire_HGet_ClientCacheHit         | HGET hot        |     534.1 ns |   5.16 ns |     7.73 ns | 1,872,167.7 | 0.003 | Faster          | 0.0038 |      64 B |        0.12 |

### CommonOperationsBenchmarks

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD EPYC 7763 2.45GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.400
  [Host]     : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3
  Job-IDGKZI : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                         | Categories           | Mean       | Error      | StdDev     | Median     | Ratio | MannWhitney(5%) | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|-----------:|-----------:|-----------:|------:|---------------- |--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 189.833 μs |  0.6950 μs |  1.0402 μs | 190.000 μs |  1.00 | Baseline        |    0.01 |      - |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 190.196 μs |  0.6564 μs |  0.9621 μs | 190.413 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |            |            |            |       |                 |         |        |           |             |
| StackExchange_Get              | GET                  | 192.151 μs |  0.5679 μs |  0.8500 μs | 192.313 μs |  1.00 | Baseline        |    0.01 |      - |     504 B |        1.00 |
| Respire_Get                    | GET                  | 201.283 μs | 11.4752 μs | 16.4575 μs | 191.334 μs |  1.05 | Same            |    0.08 |      - |      48 B |        0.10 |
|                                |                      |            |            |            |            |       |                 |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 173.581 μs |  1.0887 μs |  1.5614 μs | 173.693 μs |  1.00 | Baseline        |    0.01 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 172.320 μs |  0.7294 μs |  1.0918 μs | 172.473 μs |  0.99 | Same            |    0.01 |      - |      50 B |        0.15 |
|                                |                      |            |            |            |            |       |                 |         |        |           |             |
| StackExchange_Get_Pipelined    | GET x200 pipelined   |   2.349 μs |  0.0388 μs |  0.0556 μs |   2.368 μs |  1.00 | Baseline        |    0.03 | 0.0098 |     289 B |        1.00 |
| Respire_Get_Pipelined          | GET x200 pipelined   |   2.200 μs |  0.0145 μs |  0.0213 μs |   2.199 μs |  0.94 | Same            |    0.02 |      - |      60 B |        0.21 |
|                                |                      |            |            |            |            |       |                 |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.307 μs |  0.0451 μs |  0.0675 μs |   5.299 μs |  1.00 | Baseline        |    0.02 |      - |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   5.527 μs |  0.0234 μs |  0.0328 μs |   5.532 μs |  1.04 | Same            |    0.01 |      - |      52 B |        0.18 |
|                                |                      |            |            |            |            |       |                 |         |        |           |             |
| StackExchange_HGet             | HGET                 | 192.715 μs |  0.8384 μs |  1.2289 μs | 192.664 μs |  1.00 | Baseline        |    0.01 |      - |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 190.232 μs |  0.6286 μs |  0.9214 μs | 190.355 μs |  0.99 | Same            |    0.01 |      - |      48 B |        0.09 |
|                                |                      |            |            |            |            |       |                 |         |        |           |             |
| StackExchange_HSet             | HSET                 | 192.069 μs |  0.5757 μs |  0.8439 μs | 192.111 μs |  1.00 | Baseline        |    0.01 |      - |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 191.523 μs |  0.7873 μs |  1.1541 μs | 191.602 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |            |            |            |       |                 |         |        |           |             |
| StackExchange_Incr             | INCR                 | 190.770 μs |  0.8252 μs |  1.1835 μs | 191.061 μs |  1.00 | Baseline        |    0.01 |      - |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 190.581 μs |  0.7379 μs |  1.1045 μs | 190.713 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |            |            |            |       |                 |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 374.051 μs |  0.8936 μs |  1.2815 μs | 374.273 μs |  1.00 | Baseline        |    0.00 |      - |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 369.920 μs |  0.7932 μs |  1.1627 μs | 370.013 μs |  0.99 | Same            |    0.00 |      - |     256 B |        0.34 |
|                                |                      |            |            |            |            |       |                 |         |        |           |             |
| StackExchange_Ping             | PING                 | 187.983 μs |  0.5726 μs |  0.8571 μs | 188.197 μs |  1.00 | Baseline        |    0.01 |      - |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 188.289 μs |  0.6377 μs |  0.9146 μs | 188.422 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |            |            |            |       |                 |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 172.565 μs |  1.0821 μs |  1.6196 μs | 173.207 μs |  1.00 | Baseline        |    0.01 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 171.133 μs |  0.7773 μs |  1.1148 μs | 171.059 μs |  0.99 | Same            |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |            |            |            |       |                 |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 191.021 μs |  0.4361 μs |  0.6392 μs | 190.931 μs |  1.00 | Baseline        |    0.00 |      - |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 190.563 μs |  0.6452 μs |  0.9457 μs | 190.862 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |            |            |            |       |                 |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 201.571 μs |  0.5377 μs |  0.7881 μs | 201.602 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 205.028 μs |  0.6132 μs |  0.9178 μs | 204.980 μs |  1.02 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |            |            |            |       |                 |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 191.986 μs |  0.4548 μs |  0.6667 μs | 192.110 μs |  1.00 | Baseline        |    0.00 |      - |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 191.211 μs |  0.7589 μs |  1.1124 μs | 191.271 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |            |            |            |       |                 |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 193.352 μs |  0.6475 μs |  0.9287 μs | 193.681 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 191.280 μs |  1.0169 μs |  1.5220 μs | 191.253 μs |  0.99 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |            |            |            |       |                 |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 176.704 μs |  0.7323 μs |  1.0961 μs | 176.796 μs |  1.00 | Baseline        |    0.01 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 174.457 μs |  0.7856 μs |  1.1758 μs | 174.909 μs |  0.99 | Same            |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |            |            |            |       |                 |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 370.205 μs |  0.8852 μs |  1.2976 μs | 370.494 μs |  1.00 | Baseline        |    0.00 |      - |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 367.071 μs |  0.6976 μs |  1.0225 μs | 367.116 μs |  0.99 | Same            |    0.00 |      - |     200 B |        0.31 |
|                                |                      |            |            |            |            |       |                 |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 189.917 μs |  0.5803 μs |  0.8685 μs | 190.026 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 190.685 μs |  0.7587 μs |  1.1355 μs | 190.718 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |

## net8.0

### ClientSideCachingBenchmarks

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD EPYC 9V74 2.60GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.400
  [Host]     : .NET 8.0.30 (8.0.30, 8.0.3026.36720), X64 RyuJIT x86-64-v3
  Job-IDGKZI : .NET 8.0.30 (8.0.30, 8.0.3026.36720), X64 RyuJIT x86-64-v3

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                              | Categories      | Mean         | Error       | StdDev      | Median       | Op/s        | Ratio | MannWhitney(5%) | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------------ |---------------- |-------------:|------------:|------------:|-------------:|------------:|------:|---------------- |--------:|-------:|----------:|------------:|
| StackExchange_Exists_ServerRead     | EXISTS hot      | 132,426.7 ns | 3,836.87 ns | 5,742.85 ns | 132,981.6 ns |     7,551.3 | 1.002 | Baseline        |    0.06 |      - |     294 B |        1.00 |
| Respire_Exists_ServerRead           | EXISTS hot      | 142,223.8 ns | 2,110.64 ns | 3,159.11 ns | 143,090.0 ns |     7,031.2 | 1.076 | Same            |    0.05 |      - |         - |        0.00 |
| Respire_Exists_ClientCacheHit       | EXISTS hot      |     605.1 ns |     2.97 ns |     4.07 ns |     607.3 ns | 1,652,697.5 | 0.005 | Faster          |    0.00 |      - |         - |        0.00 |
|                                     |                 |              |             |             |              |             |       |                 |         |        |           |             |
| StackExchange_Get_ServerRead        | GET hot         | 142,880.1 ns | 4,740.07 ns | 7,094.71 ns | 145,248.6 ns |     6,998.9 | 1.003 | Baseline        |    0.07 |      - |     519 B |        1.00 |
| Respire_Get_ServerRead              | GET hot         | 141,899.8 ns | 3,317.08 ns | 4,964.85 ns | 142,551.1 ns |     7,047.2 | 0.996 | Same            |    0.06 |      - |      64 B |        0.12 |
| Respire_Get_ClientCacheHit          | GET hot         |     392.0 ns |     5.81 ns |     8.34 ns |     386.5 ns | 2,551,129.6 | 0.003 | Faster          |    0.00 | 0.0038 |      64 B |        0.12 |
|                                     |                 |              |             |             |              |             |       |                 |         |        |           |             |
| StackExchange_GetMissing_ServerRead | GET missing hot | 141,104.4 ns | 3,622.22 ns | 5,309.40 ns | 142,447.7 ns |     7,087.0 | 1.002 | Baseline        |    0.06 |      - |     409 B |        1.00 |
| Respire_GetMissing_ServerRead       | GET missing hot | 139,104.5 ns | 2,834.33 ns | 4,242.29 ns | 140,306.0 ns |     7,188.8 | 0.987 | Same            |    0.05 |      - |         - |        0.00 |
| Respire_GetMissing_ClientCacheHit   | GET missing hot |     371.9 ns |     1.07 ns |     1.42 ns |     372.5 ns | 2,688,755.2 | 0.003 | Faster          |    0.00 |      - |         - |        0.00 |
|                                     |                 |              |             |             |              |             |       |                 |         |        |           |             |
| StackExchange_HGet_ServerRead       | HGET hot        | 146,213.6 ns | 3,391.31 ns | 5,075.96 ns | 146,611.4 ns |     6,839.3 | 1.001 | Baseline        |    0.05 |      - |     544 B |        1.00 |
| Respire_HGet_ServerRead             | HGET hot        | 143,672.3 ns | 2,914.86 ns | 4,272.56 ns | 145,090.9 ns |     6,960.3 | 0.984 | Same            |    0.05 |      - |      64 B |        0.12 |
| Respire_HGet_ClientCacheHit         | HGET hot        |     688.5 ns |     3.34 ns |     4.79 ns |     689.0 ns | 1,452,426.8 | 0.005 | Faster          |    0.00 | 0.0038 |      64 B |        0.12 |

### CommonOperationsBenchmarks

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD EPYC 9V74 2.60GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.400
  [Host]     : .NET 8.0.30 (8.0.30, 8.0.3026.36720), X64 RyuJIT x86-64-v3
  Job-IDGKZI : .NET 8.0.30 (8.0.30, 8.0.3026.36720), X64 RyuJIT x86-64-v3

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                         | Categories           | Mean       | Error     | StdDev    | Ratio | MannWhitney(5%) | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|----------:|----------:|------:|---------------- |--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 126.931 μs | 3.2281 μs | 4.8316 μs |  1.00 | Baseline        |    0.05 |      - |     294 B |        1.00 |
| Respire_Exists                 | EXISTS               | 138.653 μs | 3.8342 μs | 5.7388 μs |  1.09 | Same            |    0.06 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get              | GET                  | 137.352 μs | 3.8374 μs | 5.7436 μs |  1.00 | Baseline        |    0.06 |      - |     501 B |        1.00 |
| Respire_Get                    | GET                  | 141.676 μs | 2.1925 μs | 3.2817 μs |  1.03 | Same            |    0.05 |      - |      48 B |        0.10 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 119.196 μs | 1.7661 μs | 2.5888 μs |  1.00 | Baseline        |    0.03 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 116.744 μs | 1.4000 μs | 2.0955 μs |  0.98 | Same            |    0.03 |      - |      50 B |        0.15 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Pipelined    | GET x200 pipelined   |   2.037 μs | 0.0935 μs | 0.1400 μs |  1.00 | Baseline        |    0.10 | 0.0098 |     289 B |        1.00 |
| Respire_Get_Pipelined          | GET x200 pipelined   |   2.004 μs | 0.0235 μs | 0.0352 μs |  0.99 | Same            |    0.07 |      - |      61 B |        0.21 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   4.627 μs | 0.0884 μs | 0.1295 μs |  1.00 | Baseline        |    0.04 | 0.0098 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   4.774 μs | 0.0895 μs | 0.1312 μs |  1.03 | Same            |    0.04 |      - |      52 B |        0.18 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HGet             | HGET                 | 140.292 μs | 2.8717 μs | 4.2983 μs |  1.00 | Baseline        |    0.04 |      - |     519 B |        1.00 |
| Respire_HGet                   | HGET                 | 137.651 μs | 3.0930 μs | 4.6294 μs |  0.98 | Same            |    0.04 |      - |      48 B |        0.09 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HSet             | HSET                 | 136.710 μs | 2.2245 μs | 3.3295 μs |  1.00 | Baseline        |    0.03 |      - |     327 B |        1.00 |
| Respire_HSet                   | HSET                 | 144.776 μs | 1.7249 μs | 2.5817 μs |  1.06 | Same            |    0.03 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Incr             | INCR                 | 134.187 μs | 3.3499 μs | 5.0140 μs |  1.00 | Baseline        |    0.05 |      - |     293 B |        1.00 |
| Respire_Incr                   | INCR                 | 141.667 μs | 1.5119 μs | 2.2161 μs |  1.06 | Same            |    0.04 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 303.466 μs | 5.3171 μs | 7.7937 μs |  1.00 | Baseline        |    0.04 |      - |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 287.771 μs | 5.7949 μs | 8.6736 μs |  0.95 | Same            |    0.04 |      - |     254 B |        0.33 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping             | PING                 | 126.897 μs | 2.1396 μs | 3.1362 μs |  1.00 | Baseline        |    0.03 |      - |     301 B |        1.00 |
| Respire_Ping                   | PING                 | 132.883 μs | 3.2430 μs | 4.8540 μs |  1.05 | Same            |    0.05 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 116.261 μs | 1.4808 μs | 2.2164 μs |  1.00 | Baseline        |    0.03 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 112.290 μs | 1.7862 μs | 2.6735 μs |  0.97 | Same            |    0.03 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 133.850 μs | 1.9331 μs | 2.8335 μs |  1.00 | Baseline        |    0.03 |      - |     310 B |        1.00 |
| Respire_SAdd                   | SADD                 | 140.762 μs | 1.8398 μs | 2.7537 μs |  1.05 | Same            |    0.03 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 172.067 μs | 0.8083 μs | 1.1332 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 162.645 μs | 1.0466 μs | 1.5665 μs |  0.95 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 139.459 μs | 2.9683 μs | 4.4428 μs |  1.00 | Baseline        |    0.05 |      - |     311 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 144.857 μs | 2.2281 μs | 3.2659 μs |  1.04 | Same            |    0.04 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 136.929 μs | 4.6847 μs | 6.7186 μs |  1.00 | Baseline        |    0.07 |      - |     309 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 146.760 μs | 1.8566 μs | 2.7214 μs |  1.07 | Same            |    0.06 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 118.575 μs | 2.4584 μs | 3.6796 μs |  1.00 | Baseline        |    0.04 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 119.185 μs | 1.5112 μs | 2.2619 μs |  1.01 | Same            |    0.04 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 287.158 μs | 4.2813 μs | 6.4081 μs |  1.00 | Baseline        |    0.03 |      - |     647 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 278.607 μs | 3.4725 μs | 5.0899 μs |  0.97 | Same            |    0.03 |      - |     200 B |        0.31 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 132.827 μs | 3.4080 μs | 4.9954 μs |  1.00 | Baseline        |    0.06 |      - |     308 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 139.673 μs | 1.9528 μs | 2.9229 μs |  1.05 | Same            |    0.05 |      - |         - |        0.00 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
