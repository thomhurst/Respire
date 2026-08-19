---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

import ComparisonBarChart from '@site/src/components/ComparisonBarChart';

# Benchmarks

:::info Automated results
Generated 2026-08-16 03:54 UTC from commit `3bfb048670b9`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31923908249) for logs and downloadable artifacts.
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
  data={[
    {"label":"EXISTS hot","other":141172.8,"respire":462.9,"respireServer":143008.8},
    {"label":"GET hot","other":148628.4,"respire":216.5,"respireServer":144572.8},
    {"label":"GET missing hot","other":146276.3,"respire":204.3,"respireServer":143698.3},
    {"label":"HGET hot","other":149972.6,"respire":528.7,"respireServer":143865.4}
  ]}
/>

<ComparisonBarChart
  title="Selected operation time — net10.0"
  description="Mean time. Shorter bars are faster."
  format="duration-ns"
  scale="group"
  showRatio
  data={[
    {"label":"GET","other":147461,"respire":143686},
    {"label":"GET x200 pipelined","other":1967,"respire":1945},
    {"label":"GET x50 concurrent","other":4750,"respire":4875},
    {"label":"HGET","other":149467,"respire":143299},
    {"label":"HSET","other":143524,"respire":144752},
    {"label":"LPUSH+LPOP","other":291163,"respire":283581},
    {"label":"SET 1KB","other":148194,"respire":145042}
  ]}
/>

## net10.0

### ClientSideCachingBenchmarks

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD EPYC 9V74 2.85GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.400
  [Host]     : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3
  Job-IDGKZI : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                              | Categories      | Mean         | Error       | StdDev      | Median       | Op/s        | Ratio | MannWhitney(5%) | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------------ |---------------- |-------------:|------------:|------------:|-------------:|------------:|------:|---------------- |--------:|-------:|----------:|------------:|
| StackExchange_Exists_ServerRead     | EXISTS hot      | 141,172.8 ns | 1,144.09 ns | 1,712.42 ns | 141,564.8 ns |     7,083.5 | 1.000 | Baseline        |    0.02 |      - |     295 B |        1.00 |
| Respire_Exists_ServerRead           | EXISTS hot      | 143,008.8 ns |   709.90 ns | 1,040.56 ns | 143,170.3 ns |     6,992.6 | 1.013 | Same            |    0.01 |      - |         - |        0.00 |
| Respire_Exists_ClientCacheHit       | EXISTS hot      |     462.9 ns |     2.92 ns |     3.99 ns |     463.4 ns | 2,160,290.8 | 0.003 | Faster          |    0.00 |      - |         - |        0.00 |
|                                     |                 |              |             |             |              |             |       |                 |         |        |           |             |
| StackExchange_Get_ServerRead        | GET hot         | 148,628.4 ns | 1,291.11 ns | 1,851.67 ns | 149,031.0 ns |     6,728.2 | 1.000 | Baseline        |    0.02 |      - |     527 B |        1.00 |
| Respire_Get_ServerRead              | GET hot         | 144,572.8 ns | 1,043.21 ns | 1,496.15 ns | 144,816.4 ns |     6,916.9 | 0.973 | Same            |    0.02 |      - |      64 B |        0.12 |
| Respire_Get_ClientCacheHit          | GET hot         |     216.5 ns |     2.60 ns |     3.73 ns |     213.7 ns | 4,618,326.7 | 0.001 | Faster          |    0.00 | 0.0038 |      64 B |        0.12 |
|                                     |                 |              |             |             |              |             |       |                 |         |        |           |             |
| StackExchange_GetMissing_ServerRead | GET missing hot | 146,276.3 ns | 1,185.24 ns | 1,737.31 ns | 146,213.8 ns |     6,836.4 | 1.000 | Baseline        |    0.02 |      - |     416 B |        1.00 |
| Respire_GetMissing_ServerRead       | GET missing hot | 143,698.3 ns | 1,261.16 ns | 1,808.72 ns | 144,108.8 ns |     6,959.0 | 0.983 | Same            |    0.02 |      - |         - |        0.00 |
| Respire_GetMissing_ClientCacheHit   | GET missing hot |     204.3 ns |     2.26 ns |     3.24 ns |     204.5 ns | 4,895,875.3 | 0.001 | Faster          |    0.00 |      - |         - |        0.00 |
|                                     |                 |              |             |             |              |             |       |                 |         |        |           |             |
| StackExchange_HGet_ServerRead       | HGET hot        | 149,972.6 ns |   941.74 ns | 1,380.39 ns | 149,844.8 ns |     6,667.9 | 1.000 | Baseline        |    0.01 |      - |     543 B |        1.00 |
| Respire_HGet_ServerRead             | HGET hot        | 143,865.4 ns |   828.80 ns | 1,214.84 ns | 144,029.0 ns |     6,950.9 | 0.959 | Same            |    0.01 |      - |      64 B |        0.12 |
| Respire_HGet_ClientCacheHit         | HGET hot        |     528.7 ns |     1.64 ns |     2.40 ns |     528.4 ns | 1,891,305.2 | 0.004 | Faster          |    0.00 | 0.0038 |      64 B |        0.12 |

### CommonOperationsBenchmarks

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD EPYC 9V74 2.85GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.400
  [Host]     : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3
  Job-IDGKZI : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                         | Categories           | Mean       | Error     | StdDev    | Ratio | MannWhitney(5%) | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|----------:|----------:|------:|---------------- |--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 140.643 μs | 0.9363 μs | 1.3429 μs |  1.00 | Baseline        |    0.01 |      - |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 142.430 μs | 0.7473 μs | 1.0717 μs |  1.01 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get              | GET                  | 147.461 μs | 1.5608 μs | 2.2877 μs |  1.00 | Baseline        |    0.02 |      - |     504 B |        1.00 |
| Respire_Get                    | GET                  | 143.686 μs | 0.8368 μs | 1.2525 μs |  0.97 | Same            |    0.02 |      - |      48 B |        0.10 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 117.839 μs | 1.1738 μs | 1.6834 μs |  1.00 | Baseline        |    0.02 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 116.283 μs | 0.5282 μs | 0.7906 μs |  0.99 | Same            |    0.02 |      - |      50 B |        0.15 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Pipelined    | GET x200 pipelined   |   1.967 μs | 0.0858 μs | 0.1284 μs |  1.00 | Baseline        |    0.09 | 0.0098 |     289 B |        1.00 |
| Respire_Get_Pipelined          | GET x200 pipelined   |   1.945 μs | 0.0168 μs | 0.0246 μs |  0.99 | Same            |    0.07 |      - |      60 B |        0.21 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   4.750 μs | 0.0240 μs | 0.0351 μs |  1.00 | Baseline        |    0.01 | 0.0098 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   4.875 μs | 0.0401 μs | 0.0600 μs |  1.03 | Same            |    0.01 |      - |      52 B |        0.18 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HGet             | HGET                 | 149.467 μs | 1.1352 μs | 1.6280 μs |  1.00 | Baseline        |    0.02 |      - |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 143.299 μs | 1.2347 μs | 1.8099 μs |  0.96 | Same            |    0.02 |      - |      48 B |        0.09 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HSet             | HSET                 | 143.524 μs | 1.0410 μs | 1.5259 μs |  1.00 | Baseline        |    0.01 |      - |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 144.752 μs | 0.7695 μs | 1.1517 μs |  1.01 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Incr             | INCR                 | 142.220 μs | 0.9246 μs | 1.3839 μs |  1.00 | Baseline        |    0.01 |      - |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 142.637 μs | 1.6458 μs | 2.3072 μs |  1.00 | Same            |    0.02 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 291.163 μs | 1.7422 μs | 2.6076 μs |  1.00 | Baseline        |    0.01 |      - |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 283.581 μs | 1.4174 μs | 1.8921 μs |  0.97 | Same            |    0.01 |      - |     256 B |        0.34 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping             | PING                 | 137.311 μs | 0.9347 μs | 1.3701 μs |  1.00 | Baseline        |    0.01 |      - |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 140.786 μs | 1.3395 μs | 1.9634 μs |  1.03 | Same            |    0.02 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 114.903 μs | 0.7330 μs | 1.0744 μs |  1.00 | Baseline        |    0.01 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 113.791 μs | 0.8606 μs | 1.1488 μs |  0.99 | Same            |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 139.424 μs | 0.7712 μs | 1.1304 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 143.535 μs | 0.8322 μs | 1.2457 μs |  1.03 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 158.783 μs | 0.8215 μs | 1.1782 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 160.815 μs | 0.7793 μs | 1.1176 μs |  1.01 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 145.183 μs | 0.5818 μs | 0.8343 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 142.392 μs | 0.6551 μs | 0.9396 μs |  0.98 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 148.194 μs | 0.5941 μs | 0.8708 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 145.042 μs | 1.0035 μs | 1.4068 μs |  0.98 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 119.934 μs | 0.6978 μs | 0.9552 μs |  1.00 | Baseline        |    0.01 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 118.046 μs | 0.5911 μs | 0.8847 μs |  0.98 | Same            |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 282.499 μs | 2.0781 μs | 3.1104 μs |  1.00 | Baseline        |    0.02 |      - |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 279.282 μs | 0.9565 μs | 1.4316 μs |  0.99 | Same            |    0.01 |      - |     200 B |        0.31 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 141.946 μs | 0.6170 μs | 0.9045 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 144.673 μs | 0.8530 μs | 1.2234 μs |  1.02 | Same            |    0.01 |      - |         - |        0.00 |

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
| StackExchange_Exists_ServerRead     | EXISTS hot      | 197,012.7 ns |   617.42 ns |   924.12 ns | 197,021.1 ns |     5,075.8 | 1.000 | Baseline        |      - |     296 B |        1.00 |
| Respire_Exists_ServerRead           | EXISTS hot      | 200,874.6 ns |   832.45 ns | 1,245.98 ns | 200,746.3 ns |     4,978.2 | 1.020 | Same            |      - |         - |        0.00 |
| Respire_Exists_ClientCacheHit       | EXISTS hot      |     620.5 ns |     1.17 ns |     1.57 ns |     619.9 ns | 1,611,525.8 | 0.003 | Faster          |      - |         - |        0.00 |
|                                     |                 |              |             |             |              |             |       |                 |        |           |             |
| StackExchange_Get_ServerRead        | GET hot         | 202,874.5 ns | 1,070.66 ns | 1,602.51 ns | 203,269.8 ns |     4,929.2 | 1.000 | Baseline        |      - |     527 B |        1.00 |
| Respire_Get_ServerRead              | GET hot         | 201,566.4 ns |   598.22 ns |   895.39 ns | 201,583.8 ns |     4,961.1 | 0.994 | Same            |      - |      64 B |        0.12 |
| Respire_Get_ClientCacheHit          | GET hot         |     397.9 ns |    12.76 ns |    18.30 ns |     412.4 ns | 2,513,500.2 | 0.002 | Faster          | 0.0038 |      64 B |        0.12 |
|                                     |                 |              |             |             |              |             |       |                 |        |           |             |
| StackExchange_GetMissing_ServerRead | GET missing hot | 201,190.7 ns |   935.02 ns | 1,399.50 ns | 201,376.6 ns |     4,970.4 | 1.000 | Baseline        |      - |     416 B |        1.00 |
| Respire_GetMissing_ServerRead       | GET missing hot | 200,664.8 ns |   820.54 ns | 1,202.74 ns | 200,623.1 ns |     4,983.4 | 0.997 | Same            |      - |         - |        0.00 |
| Respire_GetMissing_ClientCacheHit   | GET missing hot |     397.3 ns |    25.25 ns |    34.56 ns |     368.3 ns | 2,516,803.0 | 0.002 | Faster          |      - |         - |        0.00 |
|                                     |                 |              |             |             |              |             |       |                 |        |           |             |
| StackExchange_HGet_ServerRead       | HGET hot        | 205,621.3 ns |   779.38 ns | 1,166.54 ns | 205,768.3 ns |     4,863.3 | 1.000 | Baseline        |      - |     544 B |        1.00 |
| Respire_HGet_ServerRead             | HGET hot        | 201,568.6 ns |   911.09 ns | 1,335.47 ns | 201,489.5 ns |     4,961.1 | 0.980 | Same            |      - |      64 B |        0.12 |
| Respire_HGet_ClientCacheHit         | HGET hot        |     740.2 ns |     5.62 ns |     7.87 ns |     737.8 ns | 1,351,053.5 | 0.004 | Faster          | 0.0038 |      64 B |        0.12 |

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
| StackExchange_Exists           | EXISTS               | 196.989 μs | 0.8086 μs | 1.2103 μs |  1.00 | Baseline        |    0.01 |      - |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 200.125 μs | 0.6435 μs | 0.9631 μs |  1.02 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get              | GET                  | 202.638 μs | 0.8534 μs | 1.2508 μs |  1.00 | Baseline        |    0.01 |      - |     504 B |        1.00 |
| Respire_Get                    | GET                  | 200.240 μs | 0.8340 μs | 1.2483 μs |  0.99 | Same            |    0.01 |      - |      48 B |        0.10 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 181.609 μs | 0.7835 μs | 1.1236 μs |  1.00 | Baseline        |    0.01 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 181.849 μs | 1.2577 μs | 1.8824 μs |  1.00 | Same            |    0.01 |      - |      50 B |        0.15 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Pipelined    | GET x200 pipelined   |   2.554 μs | 0.0713 μs | 0.1068 μs |  1.00 | Baseline        |    0.06 | 0.0098 |     289 B |        1.00 |
| Respire_Get_Pipelined          | GET x200 pipelined   |   2.297 μs | 0.0133 μs | 0.0191 μs |  0.90 | Faster          |    0.04 |      - |      61 B |        0.21 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.667 μs | 0.0495 μs | 0.0725 μs |  1.00 | Baseline        |    0.02 |      - |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   5.819 μs | 0.0320 μs | 0.0479 μs |  1.03 | Same            |    0.02 |      - |      52 B |        0.18 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HGet             | HGET                 | 204.357 μs | 0.7614 μs | 1.1396 μs |  1.00 | Baseline        |    0.01 |      - |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 201.110 μs | 0.6202 μs | 0.9282 μs |  0.98 | Same            |    0.01 |      - |      48 B |        0.09 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HSet             | HSET                 | 198.982 μs | 0.4639 μs | 0.6800 μs |  1.00 | Baseline        |    0.00 |      - |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 203.395 μs | 0.5081 μs | 0.7448 μs |  1.02 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Incr             | INCR                 | 198.224 μs | 0.7161 μs | 1.0719 μs |  1.00 | Baseline        |    0.01 |      - |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 200.717 μs | 0.9608 μs | 1.4380 μs |  1.01 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 395.551 μs | 1.3760 μs | 1.9289 μs |  1.00 | Baseline        |    0.01 |      - |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 391.007 μs | 1.5667 μs | 2.3450 μs |  0.99 | Same            |    0.01 |      - |     256 B |        0.34 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping             | PING                 | 194.639 μs | 0.8377 μs | 1.2539 μs |  1.00 | Baseline        |    0.01 |      - |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 197.359 μs | 0.9154 μs | 1.3701 μs |  1.01 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 180.738 μs | 1.4302 μs | 2.1407 μs |  1.00 | Baseline        |    0.02 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 180.440 μs | 1.0386 μs | 1.5223 μs |  1.00 | Same            |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 197.844 μs | 0.7374 μs | 1.1038 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 200.740 μs | 0.5686 μs | 0.8510 μs |  1.01 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 209.497 μs | 1.0159 μs | 1.5206 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 216.268 μs | 0.6293 μs | 0.9419 μs |  1.03 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 198.966 μs | 0.9731 μs | 1.4564 μs |  1.00 | Baseline        |    0.01 |      - |     310 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 202.813 μs | 0.4559 μs | 0.6538 μs |  1.02 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 200.325 μs | 0.7413 μs | 1.0632 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 204.390 μs | 0.6127 μs | 0.9171 μs |  1.02 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 184.033 μs | 1.1550 μs | 1.6930 μs |  1.00 | Baseline        |    0.01 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 184.837 μs | 1.1941 μs | 1.7872 μs |  1.00 | Same            |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 389.661 μs | 1.4803 μs | 2.1231 μs |  1.00 | Baseline        |    0.01 |      - |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 388.894 μs | 1.0429 μs | 1.5287 μs |  1.00 | Same            |    0.01 |      - |     200 B |        0.31 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 196.831 μs | 0.6532 μs | 0.9575 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 200.083 μs | 0.5064 μs | 0.7579 μs |  1.02 | Same            |    0.01 |      - |         - |        0.00 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
