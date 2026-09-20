---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

import ComparisonBarChart from '@site/src/components/ComparisonBarChart';

# Benchmarks

:::info Automated results
Generated 2026-09-20 03:38 UTC from commit `03fbf671eee7`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/35485576833) for logs and downloadable artifacts.
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
  data={[{"label":"EXISTS hot","other":66956.7,"respire":240.9,"respireServer":65832.7},{"label":"GET hot","other":65053.1,"respire":116.2,"respireServer":66015.0},{"label":"GET missing hot","other":66106.5,"respire":107.8,"respireServer":66779.3},{"label":"HGET hot","other":67062.0,"respire":271.1,"respireServer":66346.6}]}
/>

<ComparisonBarChart
  title="Selected operation time — net10.0"
  description="Mean time. Shorter bars are faster."
  format="duration-ns"
  scale="group"
  showRatio
  data={[{"label":"GET","other":65770.0,"respire":65573.0},{"label":"GET x200 pipelined","other":1118.0,"respire":1036.0},{"label":"GET x50 concurrent","other":2227.0,"respire":2271.0},{"label":"HGET","other":65768.0,"respire":64230.99999999999},{"label":"HSET","other":66422.0,"respire":64143.0},{"label":"LPUSH+LPOP","other":122238.0,"respire":129794.99999999999},{"label":"SET 1KB","other":66103.0,"respire":67945.0}]}
/>

## net10.0

### ClientSideCachingBenchmarks

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
AMD EPYC 9V45 2.60GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  Job-IDGKZI : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                              | Categories      | Mean        | Error       | StdDev      | Op/s        | Ratio | MannWhitney(5%) | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------------ |---------------- |------------:|------------:|------------:|------------:|------:|---------------- |--------:|-------:|----------:|------------:|
| StackExchange_Exists_ServerRead     | EXISTS hot      | 66,956.7 ns |   633.74 ns |   948.56 ns |    14,935.0 | 1.000 | Baseline        |    0.02 |      - |     293 B |        1.00 |
| Respire_Exists_ServerRead           | EXISTS hot      | 65,832.7 ns | 1,642.82 ns | 2,303.01 ns |    15,190.0 | 0.983 | Same            |    0.04 |      - |         - |        0.00 |
| Respire_Exists_ClientCacheHit       | EXISTS hot      |    240.9 ns |     3.15 ns |     4.52 ns | 4,151,730.7 | 0.004 | Faster          |    0.00 |      - |         - |        0.00 |
|                                     |                 |             |             |             |             |       |                 |         |        |           |             |
| StackExchange_Get_ServerRead        | GET hot         | 65,053.1 ns | 1,067.37 ns | 1,564.54 ns |    15,372.0 | 1.001 | Baseline        |    0.03 |      - |     519 B |        1.00 |
| Respire_Get_ServerRead              | GET hot         | 66,015.0 ns | 1,334.39 ns | 1,997.25 ns |    15,148.1 | 1.015 | Same            |    0.04 |      - |      64 B |        0.12 |
| Respire_Get_ClientCacheHit          | GET hot         |    116.2 ns |     2.09 ns |     3.07 ns | 8,609,141.9 | 0.002 | Faster          |    0.00 | 0.0038 |      64 B |        0.12 |
|                                     |                 |             |             |             |             |       |                 |         |        |           |             |
| StackExchange_GetMissing_ServerRead | GET missing hot | 66,106.5 ns | 1,698.08 ns | 2,541.60 ns |    15,127.1 | 1.001 | Baseline        |    0.05 |      - |     412 B |        1.00 |
| Respire_GetMissing_ServerRead       | GET missing hot | 66,779.3 ns | 1,487.64 ns | 2,133.53 ns |    14,974.7 | 1.012 | Same            |    0.05 |      - |         - |        0.00 |
| Respire_GetMissing_ClientCacheHit   | GET missing hot |    107.8 ns |     1.01 ns |     1.51 ns | 9,277,351.6 | 0.002 | Faster          |    0.00 |      - |         - |        0.00 |
|                                     |                 |             |             |             |             |       |                 |         |        |           |             |
| StackExchange_HGet_ServerRead       | HGET hot        | 67,062.0 ns | 1,781.58 ns | 2,611.41 ns |    14,911.6 | 1.002 | Baseline        |    0.06 |      - |     534 B |        1.00 |
| Respire_HGet_ServerRead             | HGET hot        | 66,346.6 ns | 1,437.18 ns | 2,106.60 ns |    15,072.4 | 0.991 | Same            |    0.05 |      - |      64 B |        0.12 |
| Respire_HGet_ClientCacheHit         | HGET hot        |    271.1 ns |     2.60 ns |     3.81 ns | 3,689,007.2 | 0.004 | Faster          |    0.00 | 0.0038 |      64 B |        0.12 |

### CommonOperationsBenchmarks

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
AMD EPYC 9V45 2.60GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  Job-IDGKZI : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                         | Categories           | Mean       | Error     | StdDev    | Ratio | MannWhitney(5%) | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|----------:|----------:|------:|---------------- |--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               |  66.765 μs | 0.8990 μs | 1.2894 μs |  1.00 | Baseline        |    0.03 |      - |     295 B |        1.00 |
| Respire_Exists                 | EXISTS               |  66.687 μs | 1.4211 μs | 2.0381 μs |  1.00 | Same            |    0.04 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get              | GET                  |  65.770 μs | 2.1215 μs | 2.9740 μs |  1.00 | Baseline        |    0.07 |      - |     495 B |        1.00 |
| Respire_Get                    | GET                  |  65.573 μs | 1.0974 μs | 1.6085 μs |  1.00 | Same            |    0.05 |      - |      48 B |        0.10 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  |  55.627 μs | 0.8574 μs | 1.2567 μs |  1.00 | Baseline        |    0.03 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  |  58.923 μs | 1.1469 μs | 1.7166 μs |  1.06 | Same            |    0.04 |      - |      50 B |        0.15 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Pipelined    | GET x200 pipelined   |   1.118 μs | 0.0338 μs | 0.0496 μs |  1.00 | Baseline        |    0.06 | 0.0146 |     289 B |        1.00 |
| Respire_Get_Pipelined          | GET x200 pipelined   |   1.036 μs | 0.0145 μs | 0.0213 μs |  0.93 | Same            |    0.04 | 0.0024 |      60 B |        0.21 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   2.227 μs | 0.0368 μs | 0.0551 μs |  1.00 | Baseline        |    0.03 | 0.0146 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   2.271 μs | 0.0296 μs | 0.0442 μs |  1.02 | Same            |    0.03 |      - |      52 B |        0.18 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HGet             | HGET                 |  65.768 μs | 1.6297 μs | 2.3887 μs |  1.00 | Baseline        |    0.05 |      - |     510 B |        1.00 |
| Respire_HGet                   | HGET                 |  64.231 μs | 0.8445 μs | 1.1839 μs |  0.98 | Same            |    0.04 |      - |      48 B |        0.09 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HSet             | HSET                 |  66.422 μs | 0.8911 μs | 1.3338 μs |  1.00 | Baseline        |    0.03 |      - |     324 B |        1.00 |
| Respire_HSet                   | HSET                 |  64.143 μs | 1.3694 μs | 2.0073 μs |  0.97 | Same            |    0.04 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Incr             | INCR                 |  66.807 μs | 0.3596 μs | 0.5382 μs |  1.00 | Baseline        |    0.01 |      - |     296 B |        1.00 |
| Respire_Incr                   | INCR                 |  65.024 μs | 1.1344 μs | 1.6270 μs |  0.97 | Same            |    0.03 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 122.238 μs | 2.2868 μs | 3.3519 μs |  1.00 | Baseline        |    0.04 |      - |     758 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 129.795 μs | 1.8604 μs | 2.7269 μs |  1.06 | Same            |    0.04 |      - |     254 B |        0.34 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping             | PING                 |  65.984 μs | 1.1707 μs | 1.7160 μs |  1.00 | Baseline        |    0.04 |      - |     303 B |        1.00 |
| Respire_Ping                   | PING                 |  64.430 μs | 1.1657 μs | 1.7087 μs |  0.98 | Same            |    0.04 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential |  54.917 μs | 1.0200 μs | 1.5267 μs |  1.00 | Baseline        |    0.04 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential |  57.037 μs | 1.1387 μs | 1.6691 μs |  1.04 | Same            |    0.04 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SAdd             | SADD                 |  66.656 μs | 0.7864 μs | 1.1527 μs |  1.00 | Baseline        |    0.02 |      - |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 |  63.769 μs | 0.9105 μs | 1.3346 μs |  0.96 | Same            |    0.03 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             |  70.163 μs | 1.0569 μs | 1.4817 μs |  1.00 | Baseline        |    0.03 |      - |     311 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             |  71.232 μs | 1.2350 μs | 1.8486 μs |  1.02 | Same            |    0.03 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              |  67.146 μs | 0.5995 μs | 0.8787 μs |  1.00 | Baseline        |    0.02 |      - |     307 B |        1.00 |
| Respire_Set_Small              | SET 13B              |  65.947 μs | 1.3423 μs | 2.0090 μs |  0.98 | Same            |    0.03 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              |  66.103 μs | 1.4207 μs | 2.0825 μs |  1.00 | Baseline        |    0.04 |      - |     311 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              |  67.945 μs | 1.5801 μs | 2.3650 μs |  1.03 | Same            |    0.05 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  |  55.584 μs | 0.8958 μs | 1.2847 μs |  1.00 | Baseline        |    0.03 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  |  57.668 μs | 1.2087 μs | 1.7717 μs |  1.04 | Same            |    0.04 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 121.564 μs | 2.2577 μs | 3.3792 μs |  1.00 | Baseline        |    0.04 |      - |     647 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 127.412 μs | 1.3742 μs | 2.0143 μs |  1.05 | Same            |    0.03 |      - |     198 B |        0.31 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            |  66.389 μs | 0.9023 μs | 1.3506 μs |  1.00 | Baseline        |    0.03 |      - |     310 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            |  64.066 μs | 1.0491 μs | 1.5702 μs |  0.97 | Same            |    0.03 |      - |         - |        0.00 |

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
| Method                              | Categories      | Mean         | Error       | StdDev      | Median       | Op/s        | Ratio | MannWhitney(5%) | Gen0   | Allocated | Alloc Ratio |
|------------------------------------ |---------------- |-------------:|------------:|------------:|-------------:|------------:|------:|---------------- |-------:|----------:|------------:|
| StackExchange_Exists_ServerRead     | EXISTS hot      | 189,880.9 ns |   993.16 ns | 1,486.51 ns | 190,106.2 ns |     5,266.5 | 1.000 | Baseline        |      - |     296 B |        1.00 |
| Respire_Exists_ServerRead           | EXISTS hot      | 191,330.3 ns |   638.91 ns |   936.50 ns | 191,313.9 ns |     5,226.6 | 1.008 | Same            |      - |         - |        0.00 |
| Respire_Exists_ClientCacheHit       | EXISTS hot      |     668.7 ns |    16.87 ns |    23.09 ns |     687.9 ns | 1,495,526.8 | 0.004 | Faster          |      - |         - |        0.00 |
|                                     |                 |              |             |             |              |             |       |                 |        |           |             |
| StackExchange_Get_ServerRead        | GET hot         | 194,653.8 ns |   813.41 ns | 1,192.28 ns | 194,682.3 ns |     5,137.3 | 1.000 | Baseline        |      - |     528 B |        1.00 |
| Respire_Get_ServerRead              | GET hot         | 191,848.4 ns | 1,313.54 ns | 1,966.04 ns | 192,311.7 ns |     5,212.4 | 0.986 | Same            |      - |      64 B |        0.12 |
| Respire_Get_ClientCacheHit          | GET hot         |     424.8 ns |     1.93 ns |     2.64 ns |     426.0 ns | 2,354,305.9 | 0.002 | Faster          | 0.0038 |      64 B |        0.12 |
|                                     |                 |              |             |             |              |             |       |                 |        |           |             |
| StackExchange_GetMissing_ServerRead | GET missing hot | 193,764.3 ns |   797.00 ns | 1,192.92 ns | 193,712.7 ns |     5,160.9 | 1.000 | Baseline        |      - |     416 B |        1.00 |
| Respire_GetMissing_ServerRead       | GET missing hot | 191,831.5 ns |   552.84 ns |   810.35 ns | 191,953.1 ns |     5,212.9 | 0.990 | Same            |      - |         - |        0.00 |
| Respire_GetMissing_ClientCacheHit   | GET missing hot |     380.3 ns |    14.76 ns |    19.19 ns |     380.4 ns | 2,629,621.1 | 0.002 | Faster          |      - |         - |        0.00 |
|                                     |                 |              |             |             |              |             |       |                 |        |           |             |
| StackExchange_HGet_ServerRead       | HGET hot        | 196,651.5 ns |   736.13 ns | 1,101.80 ns | 196,668.7 ns |     5,085.1 | 1.000 | Baseline        |      - |     544 B |        1.00 |
| Respire_HGet_ServerRead             | HGET hot        | 192,186.8 ns |   676.15 ns | 1,012.03 ns | 192,383.1 ns |     5,203.3 | 0.977 | Same            |      - |      64 B |        0.12 |
| Respire_HGet_ClientCacheHit         | HGET hot        |     775.9 ns |    45.30 ns |    66.40 ns |     832.6 ns | 1,288,811.6 | 0.004 | Faster          | 0.0038 |      64 B |        0.12 |

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
| StackExchange_Exists           | EXISTS               | 189.626 μs | 0.8840 μs | 1.3231 μs |  1.00 | Baseline        |    0.01 |      - |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 191.948 μs | 0.6451 μs | 0.9456 μs |  1.01 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get              | GET                  | 193.378 μs | 0.9593 μs | 1.4061 μs |  1.00 | Baseline        |    0.01 |      - |     504 B |        1.00 |
| Respire_Get                    | GET                  | 190.399 μs | 0.8573 μs | 1.2566 μs |  0.98 | Same            |    0.01 |      - |      48 B |        0.10 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 177.667 μs | 1.4740 μs | 2.2063 μs |  1.00 | Baseline        |    0.02 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 176.583 μs | 1.2059 μs | 1.8049 μs |  0.99 | Same            |    0.02 |      - |      50 B |        0.15 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Pipelined    | GET x200 pipelined   |   2.525 μs | 0.0605 μs | 0.0887 μs |  1.00 | Baseline        |    0.05 | 0.0098 |     289 B |        1.00 |
| Respire_Get_Pipelined          | GET x200 pipelined   |   2.293 μs | 0.0118 μs | 0.0173 μs |  0.91 | Faster          |    0.03 |      - |      61 B |        0.21 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.533 μs | 0.0416 μs | 0.0610 μs |  1.00 | Baseline        |    0.02 |      - |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   5.705 μs | 0.0451 μs | 0.0647 μs |  1.03 | Same            |    0.02 |      - |      52 B |        0.18 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HGet             | HGET                 | 195.071 μs | 1.0862 μs | 1.6257 μs |  1.00 | Baseline        |    0.01 |      - |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 189.325 μs | 1.2656 μs | 1.8551 μs |  0.97 | Same            |    0.01 |      - |      48 B |        0.09 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HSet             | HSET                 | 191.496 μs | 0.7317 μs | 1.0951 μs |  1.00 | Baseline        |    0.01 |      - |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 193.781 μs | 0.7631 μs | 1.1185 μs |  1.01 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Incr             | INCR                 | 190.454 μs | 0.7144 μs | 1.0693 μs |  1.00 | Baseline        |    0.01 |      - |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 191.959 μs | 0.7251 μs | 1.0628 μs |  1.01 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 379.484 μs | 2.6195 μs | 3.9207 μs |  1.00 | Baseline        |    0.01 |      - |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 375.543 μs | 1.3219 μs | 1.9377 μs |  0.99 | Same            |    0.01 |      - |     256 B |        0.34 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping             | PING                 | 188.312 μs | 0.7859 μs | 1.1764 μs |  1.00 | Baseline        |    0.01 |      - |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 190.548 μs | 0.5857 μs | 0.8766 μs |  1.01 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 176.014 μs | 1.2774 μs | 1.9120 μs |  1.00 | Baseline        |    0.02 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 173.176 μs | 1.0639 μs | 1.5925 μs |  0.98 | Same            |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 190.907 μs | 0.5754 μs | 0.8612 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 192.833 μs | 0.8368 μs | 1.2525 μs |  1.01 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 203.532 μs | 0.7102 μs | 1.0409 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 209.025 μs | 0.5603 μs | 0.8386 μs |  1.03 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 192.352 μs | 0.6901 μs | 1.0330 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 194.928 μs | 0.7032 μs | 1.0307 μs |  1.01 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 193.990 μs | 0.9649 μs | 1.4442 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 194.798 μs | 0.8686 μs | 1.3000 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 180.517 μs | 1.0926 μs | 1.6353 μs |  1.00 | Baseline        |    0.01 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 178.931 μs | 1.0393 μs | 1.5234 μs |  0.99 | Same            |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 377.535 μs | 1.3169 μs | 1.9303 μs |  1.00 | Baseline        |    0.01 |      - |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 374.803 μs | 1.3555 μs | 2.0288 μs |  0.99 | Same            |    0.01 |      - |     200 B |        0.31 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 190.325 μs | 0.8264 μs | 1.1852 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 191.759 μs | 0.7375 μs | 1.0810 μs |  1.01 | Same            |    0.01 |      - |         - |        0.00 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
