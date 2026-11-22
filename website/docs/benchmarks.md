---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

import ComparisonBarChart from '@site/src/components/ComparisonBarChart';

# Benchmarks

:::info Automated results
Generated 2026-11-22 11:12 UTC from commit `97d61931f5d4`. These results are refreshed weekly.
:::

StackExchange.Redis is the baseline. A ratio below `1.00` means Respire completed the operation faster.

Every benchmark ran 3 times, each time on a separate machine. Both clients in a run share that machine, so each run gives its own ratio. Read the median ratio: a single run on a shared machine can move by several percent.

## Visual comparison

Each pair of bars comes from the run whose Respire / StackExchange.Redis ratio is the median of all runs.

StackExchange.Redis has no built-in server-assisted client cache, so its values are ordinary server reads. Respire server reads are included for a like-for-like uncached comparison.

<ComparisonBarChart
  title="Client-cache hit time — net11.0"
  description="StackExchange.Redis and Respire server reads vs Respire client-cache hit. Shorter bars are faster."
  format="duration-ns"
  respireLabel="Respire cache hit"
  scale="group"
  showRatio
  data={[{"label":"EXISTS hot","other":81378.1,"respire":330.2,"respireServer":78534.7},{"label":"GET hot","other":83886.2,"respire":143.0,"respireServer":76269.5},{"label":"GET missing hot","other":81489.9,"respire":137.8,"respireServer":78373.7},{"label":"HGET hot","other":83486.4,"respire":328.4,"respireServer":76305.4}]}
/>

<ComparisonBarChart
  title="Selected operation time — net11.0"
  description="Mean time. Shorter bars are faster."
  format="duration-ns"
  scale="group"
  showRatio
  data={[{"label":"GET","other":143878.0,"respire":134101.0},{"label":"GET x200 pipelined","other":1462.0,"respire":1268.0},{"label":"GET x50 concurrent","other":3413.0,"respire":3296.0},{"label":"HGET","other":151048.0,"respire":137034.0},{"label":"HSET","other":143195.0,"respire":136026.0},{"label":"LPUSH+LPOP","other":172864.0,"respire":159934.0},{"label":"SET 1KB","other":145162.0,"respire":135932.0}]}
/>

## Ratio across runs

- Run 1: AMD EPYC 7763 3.12GHz
- Run 2: Intel Xeon 6975P-C 3.18GHz
- Run 3: unknown CPU

### ClientSideCachingBenchmarks

| Method | Categories | Run 1 | Run 2 | Run 3 | Median |
|---|---|---:|---:|---:|---:|
| Respire_Exists_ServerRead | EXISTS hot | 0.96 | 0.79 | 0.97 | **0.96** |
| Respire_Exists_ClientCacheHit | EXISTS hot | 0.00 | 0.00 | 0.00 | **0.00** |
| Respire_Get_ServerRead | GET hot | 0.94 | 0.82 | 0.91 | **0.91** |
| Respire_Get_ClientCacheHit | GET hot | 0.00 | 0.00 | 0.00 | **0.00** |
| Respire_GetMissing_ServerRead | GET missing hot | 0.95 | 0.81 | 0.96 | **0.95** |
| Respire_GetMissing_ClientCacheHit | GET missing hot | 0.00 | 0.00 | 0.00 | **0.00** |
| Respire_HGet_ServerRead | HGET hot | 0.94 | 0.80 | 0.91 | **0.91** |
| Respire_HGet_ClientCacheHit | HGET hot | 0.00 | 0.00 | 0.00 | **0.00** |

### CommonOperationsBenchmarks

| Method | Categories | Run 1 | Run 2 | Run 3 | Median |
|---|---|---:|---:|---:|---:|
| Respire_Exists | EXISTS | 0.95 | 0.85 | 0.98 | **0.95** |
| Respire_Get | GET | 0.93 | 0.77 | 0.94 | **0.93** |
| Respire_GetLong | GET long | 0.94 | 0.80 | 0.95 | **0.94** |
| Respire_Get_SteadyState | GET x100 sequential | 0.96 | 0.82 | 0.92 | **0.92** |
| Respire_Get_Pipelined | GET x200 pipelined | 0.93 | 0.87 | 0.86 | **0.87** |
| Respire_Get_Concurrent | GET x50 concurrent | 1.02 | 0.84 | 0.97 | **0.97** |
| Respire_HGet | HGET | 0.91 | 0.81 | 0.95 | **0.91** |
| Respire_HSet | HSET | 0.95 | 0.81 | 0.97 | **0.95** |
| Respire_Incr | INCR | 0.96 | 0.81 | 0.97 | **0.96** |
| Respire_LPushLPop | LPUSH+LPOP | 0.94 | 0.81 | 0.93 | **0.93** |
| Respire_Ping | PING | 0.97 | 0.79 | 0.97 | **0.97** |
| Respire_Ping_SteadyState | PING x100 sequential | 0.97 | 0.82 | 0.98 | **0.97** |
| Respire_SAdd | SADD | 0.95 | 0.79 | 0.94 | **0.94** |
| Respire_Set_10KB | SET 10KB | 0.93 | 0.76 | 0.88 | **0.88** |
| Respire_Set_Small | SET 13B | 0.94 | 0.82 | 0.96 | **0.94** |
| Respire_Set_1KB | SET 1KB | 0.94 | 0.80 | 0.95 | **0.94** |
| Respire_Set_SteadyState | SET x100 sequential | 0.96 | 0.81 | 0.96 | **0.96** |
| Respire_SetDel | SET+DEL | 0.94 | 0.83 | 0.95 | **0.94** |
| Respire_SIsMember | SISMEMBER | 0.96 | 0.79 | 0.96 | **0.96** |

### PubSubComparisonBenchmarks

| Method | Run 1 | Run 2 | Run 3 | Median |
|---|---:|---:|---:|---:|
| Respire_PublishAndConsume | 1.01 | 0.96 | 0.95 | **0.96** |

## net11.0

### ClientSideCachingBenchmarks

#### Run 1

```

BenchmarkDotNet v0.16.0-preview.2, Linux Ubuntu 26.04.1 LTS (Resolute Raccoon)
AMD EPYC 7763 3.12GHz, 1 CPU, 4 logical and 2 physical cores
Memory: 15.61 GB Total, 11.78 GB Available
.NET SDK 11.0.100
  [Host]     : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v3
  Job-IDGKZI : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v3

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                              | Categories      | Mean         | Error       | StdDev      | Median       | Op/s        | Ratio | MannWhitney(5%) | RatioSD | Allocated | Alloc Ratio |
|------------------------------------ |---------------- |-------------:|------------:|------------:|-------------:|------------:|------:|---------------- |--------:|----------:|------------:|
| StackExchange_Exists_ServerRead     | EXISTS hot      | 141,837.4 ns | 1,235.20 ns | 1,810.54 ns | 141,895.9 ns |     7,050.3 | 1.000 | Baseline        |    0.00 |     232 B |        1.00 |
| Respire_Exists_ServerRead           | EXISTS hot      | 135,688.6 ns |   745.73 ns | 1,045.40 ns | 135,767.5 ns |     7,369.8 | 0.957 | Same            |    0.01 |         - |        0.00 |
| Respire_Exists_ClientCacheHit       | EXISTS hot      |     372.6 ns |     3.60 ns |     5.17 ns |     374.4 ns | 2,684,058.3 | 0.003 | Faster          |    0.00 |         - |        0.00 |
|                                     |                 |              |             |             |              |             |       |                 |         |           |             |
| StackExchange_Get_ServerRead        | GET hot         | 145,013.6 ns | 1,655.16 ns | 2,265.60 ns | 144,966.9 ns |     6,895.9 | 1.000 | Baseline        |    0.00 |     464 B |        1.00 |
| Respire_Get_ServerRead              | GET hot         | 135,707.6 ns | 1,019.92 ns | 1,462.74 ns | 135,854.9 ns |     7,368.8 | 0.936 | Same            |    0.02 |      64 B |        0.14 |
| Respire_Get_ClientCacheHit          | GET hot         |     153.5 ns |     0.50 ns |     0.71 ns |     153.6 ns | 6,513,319.2 | 0.001 | Faster          |    0.00 |         - |        0.00 |
|                                     |                 |              |             |             |              |             |       |                 |         |           |             |
| StackExchange_GetMissing_ServerRead | GET missing hot | 141,972.4 ns |   861.88 ns | 1,208.24 ns | 141,530.4 ns |     7,043.6 | 1.000 | Baseline        |    0.00 |     352 B |        1.00 |
| Respire_GetMissing_ServerRead       | GET missing hot | 135,023.7 ns |   721.85 ns | 1,011.93 ns | 135,108.9 ns |     7,406.1 | 0.951 | Same            |    0.01 |         - |        0.00 |
| Respire_GetMissing_ClientCacheHit   | GET missing hot |     161.9 ns |     4.53 ns |     6.50 ns |     167.0 ns | 6,176,999.8 | 0.001 | Faster          |    0.00 |         - |        0.00 |
|                                     |                 |              |             |             |              |             |       |                 |         |           |             |
| StackExchange_HGet_ServerRead       | HGET hot        | 145,064.5 ns |   838.86 ns | 1,203.07 ns | 145,260.6 ns |     6,893.5 | 1.000 | Baseline        |    0.00 |     480 B |        1.00 |
| Respire_HGet_ServerRead             | HGET hot        | 136,189.5 ns | 1,076.14 ns | 1,473.03 ns | 136,048.3 ns |     7,342.7 | 0.939 | Faster          |    0.01 |      64 B |        0.13 |
| Respire_HGet_ClientCacheHit         | HGET hot        |     446.7 ns |    24.91 ns |    35.73 ns |     447.3 ns | 2,238,631.1 | 0.003 | Faster          |    0.00 |         - |        0.00 |

#### Run 2

```

BenchmarkDotNet v0.16.0-preview.2, Linux Ubuntu 26.04.1 LTS (Resolute Raccoon)
Intel Xeon 6975P-C 3.18GHz, 1 CPU, 4 logical and 2 physical cores
Memory: 15.33 GB Total, 11.8 GB Available
.NET SDK 11.0.100
  [Host]     : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v4
  Job-IDGKZI : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v4

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                              | Categories      | Mean        | Error     | StdDev      | Median      | Op/s        | Ratio | MannWhitney(5%) | RatioSD | Allocated | Alloc Ratio |
|------------------------------------ |---------------- |------------:|----------:|------------:|------------:|------------:|------:|---------------- |--------:|----------:|------------:|
| StackExchange_Exists_ServerRead     | EXISTS hot      | 54,948.8 ns | 959.01 ns | 1,435.40 ns | 54,913.7 ns |    18,198.8 | 1.000 | Baseline        |    0.00 |     232 B |        1.00 |
| Respire_Exists_ServerRead           | EXISTS hot      | 43,181.0 ns | 727.15 ns | 1,065.85 ns | 43,524.5 ns |    23,158.3 | 0.786 | Faster          |    0.03 |         - |        0.00 |
| Respire_Exists_ClientCacheHit       | EXISTS hot      |    258.8 ns |   4.09 ns |     6.00 ns |    262.0 ns | 3,863,674.1 | 0.005 | Faster          |    0.00 |         - |        0.00 |
|                                     |                 |             |           |             |             |             |       |                 |         |           |             |
| StackExchange_Get_ServerRead        | GET hot         | 56,025.5 ns | 756.70 ns | 1,109.16 ns | 56,068.4 ns |    17,849.0 | 1.000 | Baseline        |    0.00 |     464 B |        1.00 |
| Respire_Get_ServerRead              | GET hot         | 45,675.7 ns | 652.33 ns |   956.18 ns | 45,693.0 ns |    21,893.5 | 0.816 | Faster          |    0.02 |      64 B |        0.14 |
| Respire_Get_ClientCacheHit          | GET hot         |    110.7 ns |   0.14 ns |     0.21 ns |    110.7 ns | 9,034,207.6 | 0.002 | Faster          |    0.00 |         - |        0.00 |
|                                     |                 |             |           |             |             |             |       |                 |         |           |             |
| StackExchange_GetMissing_ServerRead | GET missing hot | 56,206.9 ns | 981.48 ns | 1,469.04 ns | 55,860.9 ns |    17,791.4 | 1.000 | Baseline        |    0.00 |     352 B |        1.00 |
| Respire_GetMissing_ServerRead       | GET missing hot | 45,304.3 ns | 752.94 ns | 1,103.64 ns | 45,269.1 ns |    22,073.0 | 0.807 | Faster          |    0.03 |         - |        0.00 |
| Respire_GetMissing_ClientCacheHit   | GET missing hot |    106.7 ns |   0.25 ns |     0.38 ns |    106.7 ns | 9,369,588.4 | 0.002 | Faster          |    0.00 |         - |        0.00 |
|                                     |                 |             |           |             |             |             |       |                 |         |           |             |
| StackExchange_HGet_ServerRead       | HGET hot        | 55,764.3 ns | 703.23 ns | 1,052.56 ns | 55,634.1 ns |    17,932.6 | 1.000 | Baseline        |    0.00 |     480 B |        1.00 |
| Respire_HGet_ServerRead             | HGET hot        | 44,841.2 ns | 500.98 ns |   718.49 ns | 44,912.3 ns |    22,300.9 | 0.804 | Faster          |    0.02 |      64 B |        0.13 |
| Respire_HGet_ClientCacheHit         | HGET hot        |    257.5 ns |   1.38 ns |     2.03 ns |    257.1 ns | 3,884,021.4 | 0.005 | Faster          |    0.00 |         - |        0.00 |

#### Run 3

```

BenchmarkDotNet v0.16.0-preview.2, Linux Ubuntu 26.04.1 LTS (Resolute Raccoon)
INTEL XEON PLATINUM 8573C 3.01GHz, 1 CPU, 4 logical and 2 physical cores
Memory: 15.61 GB Total, 11.64 GB Available
.NET SDK 11.0.100
  [Host]     : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v4
  Job-IDGKZI : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v4

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                              | Categories      | Mean        | Error       | StdDev      | Median      | Op/s        | Ratio | MannWhitney(5%) | RatioSD | Allocated | Alloc Ratio |
|------------------------------------ |---------------- |------------:|------------:|------------:|------------:|------------:|------:|---------------- |--------:|----------:|------------:|
| StackExchange_Exists_ServerRead     | EXISTS hot      | 81,378.1 ns |   635.90 ns |   951.79 ns | 81,153.8 ns |    12,288.3 | 1.000 | Baseline        |    0.00 |     232 B |        1.00 |
| Respire_Exists_ServerRead           | EXISTS hot      | 78,534.7 ns |   513.81 ns |   736.89 ns | 78,503.9 ns |    12,733.2 | 0.965 | Same            |    0.01 |         - |        0.00 |
| Respire_Exists_ClientCacheHit       | EXISTS hot      |    330.2 ns |     2.45 ns |     3.51 ns |    331.2 ns | 3,028,215.0 | 0.004 | Faster          |    0.00 |         - |        0.00 |
|                                     |                 |             |             |             |             |             |       |                 |         |           |             |
| StackExchange_Get_ServerRead        | GET hot         | 83,886.2 ns |   524.07 ns |   717.35 ns | 83,759.7 ns |    11,920.9 | 1.000 | Baseline        |    0.00 |     464 B |        1.00 |
| Respire_Get_ServerRead              | GET hot         | 76,269.5 ns | 1,426.73 ns | 2,000.07 ns | 76,698.6 ns |    13,111.4 | 0.909 | Faster          |    0.02 |      64 B |        0.14 |
| Respire_Get_ClientCacheHit          | GET hot         |    143.0 ns |     5.22 ns |     7.49 ns |    142.9 ns | 6,995,047.5 | 0.002 | Faster          |    0.00 |         - |        0.00 |
|                                     |                 |             |             |             |             |             |       |                 |         |           |             |
| StackExchange_GetMissing_ServerRead | GET missing hot | 81,489.9 ns |   880.81 ns | 1,318.36 ns | 81,432.4 ns |    12,271.5 | 1.000 | Baseline        |    0.00 |     352 B |        1.00 |
| Respire_GetMissing_ServerRead       | GET missing hot | 78,373.7 ns |   432.38 ns |   606.13 ns | 78,293.2 ns |    12,759.4 | 0.962 | Same            |    0.02 |         - |        0.00 |
| Respire_GetMissing_ClientCacheHit   | GET missing hot |    137.8 ns |     1.80 ns |     2.52 ns |    139.3 ns | 7,256,414.2 | 0.002 | Faster          |    0.00 |         - |        0.00 |
|                                     |                 |             |             |             |             |             |       |                 |         |           |             |
| StackExchange_HGet_ServerRead       | HGET hot        | 83,486.4 ns | 1,204.17 ns | 1,802.34 ns | 83,845.4 ns |    11,978.0 | 1.000 | Baseline        |    0.00 |     480 B |        1.00 |
| Respire_HGet_ServerRead             | HGET hot        | 76,305.4 ns | 1,928.05 ns | 2,765.15 ns | 75,733.2 ns |    13,105.2 | 0.914 | Same            |    0.04 |      64 B |        0.13 |
| Respire_HGet_ClientCacheHit         | HGET hot        |    328.4 ns |     1.00 ns |     1.44 ns |    327.9 ns | 3,044,925.0 | 0.004 | Faster          |    0.00 |         - |        0.00 |

### CommonOperationsBenchmarks

#### Run 1

```

BenchmarkDotNet v0.16.0-preview.2, Linux Ubuntu 26.04.1 LTS (Resolute Raccoon)
AMD EPYC 7763 3.12GHz, 1 CPU, 4 logical and 2 physical cores
Memory: 15.61 GB Total, 11.78 GB Available
.NET SDK 11.0.100
  [Host]     : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v3
  Job-IDGKZI : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v3

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                         | Categories           | Mean       | Error     | StdDev    | Ratio | MannWhitney(5%) | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|----------:|----------:|------:|---------------- |--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 141.970 μs | 2.0368 μs | 2.8554 μs |  1.00 | Baseline        |    0.00 |      - |     232 B |        1.00 |
| Respire_Exists                 | EXISTS               | 134.282 μs | 0.9428 μs | 1.3819 μs |  0.95 | Same            |    0.02 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get              | GET                  | 143.878 μs | 0.9081 μs | 1.2431 μs |  1.00 | Baseline        |    0.00 |      - |     440 B |        1.00 |
| Respire_Get                    | GET                  | 134.101 μs | 0.8605 μs | 1.2612 μs |  0.93 | Faster          |    0.01 |      - |      48 B |        0.11 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_GetLong          | GET long             | 142.665 μs | 1.1716 μs | 1.7173 μs |  1.00 | Baseline        |    0.00 |      - |     352 B |        1.00 |
| Respire_GetLong                | GET long             | 134.143 μs | 0.6754 μs | 0.9686 μs |  0.94 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 140.180 μs | 0.8936 μs | 1.2816 μs |  1.00 | Baseline        |    0.00 |      - |     337 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 134.319 μs | 0.8262 μs | 1.1849 μs |  0.96 | Same            |    0.01 |      - |      49 B |        0.15 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Pipelined    | GET x200 pipelined   |   2.242 μs | 0.0113 μs | 0.0151 μs |  1.00 | Baseline        |    0.00 | 0.0171 |     289 B |        1.00 |
| Respire_Get_Pipelined          | GET x200 pipelined   |   2.075 μs | 0.0138 μs | 0.0193 μs |  0.93 | Faster          |    0.01 | 0.0024 |      49 B |        0.17 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   4.915 μs | 0.0251 μs | 0.0361 μs |  1.00 | Baseline        |    0.00 | 0.0098 |     290 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   5.023 μs | 0.0220 μs | 0.0316 μs |  1.02 | Same            |    0.01 |      - |      51 B |        0.18 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HGet             | HGET                 | 151.048 μs | 1.4816 μs | 1.9265 μs |  1.00 | Baseline        |    0.00 |      - |     456 B |        1.00 |
| Respire_HGet                   | HGET                 | 137.034 μs | 1.2480 μs | 1.7898 μs |  0.91 | Faster          |    0.02 |      - |      48 B |        0.11 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HSet             | HSET                 | 143.195 μs | 0.8670 μs | 1.2154 μs |  1.00 | Baseline        |    0.00 |      - |     264 B |        1.00 |
| Respire_HSet                   | HSET                 | 136.026 μs | 0.6370 μs | 0.8930 μs |  0.95 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Incr             | INCR                 | 140.718 μs | 1.1474 μs | 1.6084 μs |  1.00 | Baseline        |    0.00 |      - |     232 B |        1.00 |
| Respire_Incr                   | INCR                 | 135.442 μs | 0.7899 μs | 1.0813 μs |  0.96 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 296.760 μs | 2.2537 μs | 3.3732 μs |  1.00 | Baseline        |    0.00 |      - |     696 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 278.070 μs | 2.2242 μs | 3.1179 μs |  0.94 | Faster          |    0.01 |      - |     192 B |        0.28 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping             | PING                 | 137.543 μs | 1.2760 μs | 1.9098 μs |  1.00 | Baseline        |    0.00 |      - |     240 B |        1.00 |
| Respire_Ping                   | PING                 | 133.428 μs | 1.0118 μs | 1.3507 μs |  0.97 | Same            |    0.02 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 136.854 μs | 1.1506 μs | 1.6502 μs |  1.00 | Baseline        |    0.00 |      - |     241 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 133.068 μs | 0.6815 μs | 0.9774 μs |  0.97 | Same            |    0.01 |      - |       1 B |       0.004 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 142.178 μs | 1.2868 μs | 1.8861 μs |  1.00 | Baseline        |    0.00 |      - |     248 B |        1.00 |
| Respire_SAdd                   | SADD                 | 135.055 μs | 0.9620 μs | 1.4101 μs |  0.95 | Same            |    0.02 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 153.954 μs | 1.6996 μs | 2.5439 μs |  1.00 | Baseline        |    0.00 |      - |     248 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 143.755 μs | 1.0240 μs | 1.5009 μs |  0.93 | Faster          |    0.02 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 146.003 μs | 1.9118 μs | 2.7419 μs |  1.00 | Baseline        |    0.00 |      - |     248 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 137.047 μs | 1.0364 μs | 1.5192 μs |  0.94 | Same            |    0.02 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 145.162 μs | 1.3322 μs | 1.8676 μs |  1.00 | Baseline        |    0.00 |      - |     248 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 135.932 μs | 0.8916 μs | 1.2787 μs |  0.94 | Faster          |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 141.756 μs | 1.2995 μs | 1.8637 μs |  1.00 | Baseline        |    0.00 |      - |     249 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 135.938 μs | 0.7428 μs | 1.0888 μs |  0.96 | Same            |    0.01 |      - |       1 B |       0.004 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 290.185 μs | 2.3875 μs | 3.3469 μs |  1.00 | Baseline        |    0.00 |      - |     584 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 271.891 μs | 1.7367 μs | 2.3772 μs |  0.94 | Faster          |    0.01 |      - |     136 B |        0.23 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 140.758 μs | 0.9547 μs | 1.3993 μs |  1.00 | Baseline        |    0.00 |      - |     248 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 135.627 μs | 1.4060 μs | 1.9710 μs |  0.96 | Same            |    0.02 |      - |         - |        0.00 |

#### Run 2

```

BenchmarkDotNet v0.16.0-preview.2, Linux Ubuntu 26.04.1 LTS (Resolute Raccoon)
Intel Xeon 6975P-C 3.18GHz, 1 CPU, 4 logical and 2 physical cores
Memory: 15.33 GB Total, 11.8 GB Available
.NET SDK 11.0.100
  [Host]     : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v4
  Job-IDGKZI : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v4

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                         | Categories           | Mean       | Error     | StdDev    | Ratio | MannWhitney(5%) | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|----------:|----------:|------:|---------------- |--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               |  51.616 μs | 0.5804 μs | 0.8687 μs |  1.00 | Baseline        |    0.00 |      - |     232 B |        1.00 |
| Respire_Exists                 | EXISTS               |  44.122 μs | 0.8873 μs | 1.3007 μs |  0.86 | Faster          |    0.03 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get              | GET                  |  55.454 μs | 1.3793 μs | 2.0645 μs |  1.00 | Baseline        |    0.00 |      - |     440 B |        1.00 |
| Respire_Get                    | GET                  |  42.828 μs | 1.2272 μs | 1.7988 μs |  0.77 | Faster          |    0.04 |      - |      48 B |        0.11 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_GetLong          | GET long             |  53.046 μs | 0.5603 μs | 0.8386 μs |  1.00 | Baseline        |    0.00 |      - |     352 B |        1.00 |
| Respire_GetLong                | GET long             |  42.479 μs | 0.5832 μs | 0.8729 μs |  0.80 | Faster          |    0.02 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  |  53.394 μs | 0.9143 μs | 1.3685 μs |  1.00 | Baseline        |    0.00 |      - |     337 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  |  43.766 μs | 1.0822 μs | 1.6198 μs |  0.82 | Faster          |    0.04 |      - |      49 B |        0.15 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Pipelined    | GET x200 pipelined   |   1.462 μs | 0.0143 μs | 0.0214 μs |  1.00 | Baseline        |    0.00 | 0.0024 |     289 B |        1.00 |
| Respire_Get_Pipelined          | GET x200 pipelined   |   1.268 μs | 0.0089 μs | 0.0127 μs |  0.87 | Faster          |    0.02 |      - |      49 B |        0.17 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   2.752 μs | 0.0292 μs | 0.0428 μs |  1.00 | Baseline        |    0.00 |      - |     290 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   2.323 μs | 0.0225 μs | 0.0337 μs |  0.84 | Faster          |    0.02 |      - |      51 B |        0.18 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HGet             | HGET                 |  53.467 μs | 0.5589 μs | 0.8016 μs |  1.00 | Baseline        |    0.00 |      - |     456 B |        1.00 |
| Respire_HGet                   | HGET                 |  43.469 μs | 0.4592 μs | 0.6731 μs |  0.81 | Faster          |    0.02 |      - |      51 B |        0.11 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HSet             | HSET                 |  53.000 μs | 0.5927 μs | 0.8871 μs |  1.00 | Baseline        |    0.00 |      - |     264 B |        1.00 |
| Respire_HSet                   | HSET                 |  42.854 μs | 0.4798 μs | 0.7181 μs |  0.81 | Faster          |    0.02 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Incr             | INCR                 |  52.065 μs | 0.6085 μs | 0.8920 μs |  1.00 | Baseline        |    0.00 |      - |     232 B |        1.00 |
| Respire_Incr                   | INCR                 |  42.159 μs | 0.5129 μs | 0.7677 μs |  0.81 | Faster          |    0.02 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 110.902 μs | 1.7342 μs | 2.5957 μs |  1.00 | Baseline        |    0.00 |      - |     696 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           |  90.275 μs | 1.5944 μs | 2.3370 μs |  0.81 | Faster          |    0.03 |      - |     192 B |        0.28 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping             | PING                 |  51.730 μs | 0.7525 μs | 1.1264 μs |  1.00 | Baseline        |    0.00 |      - |     240 B |        1.00 |
| Respire_Ping                   | PING                 |  41.012 μs | 0.4799 μs | 0.7035 μs |  0.79 | Faster          |    0.02 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential |  50.904 μs | 0.8919 μs | 1.3074 μs |  1.00 | Baseline        |    0.00 |      - |     241 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential |  41.782 μs | 0.6673 μs | 0.9988 μs |  0.82 | Faster          |    0.03 |      - |       1 B |       0.004 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SAdd             | SADD                 |  55.194 μs | 0.5716 μs | 0.8556 μs |  1.00 | Baseline        |    0.00 |      - |     248 B |        1.00 |
| Respire_SAdd                   | SADD                 |  43.578 μs | 0.6864 μs | 0.9845 μs |  0.79 | Faster          |    0.02 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             |  64.670 μs | 1.3075 μs | 1.9570 μs |  1.00 | Baseline        |    0.00 |      - |     248 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             |  48.857 μs | 0.5843 μs | 0.8565 μs |  0.76 | Faster          |    0.03 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              |  55.550 μs | 0.9803 μs | 1.4673 μs |  1.00 | Baseline        |    0.00 |      - |     248 B |        1.00 |
| Respire_Set_Small              | SET 13B              |  45.582 μs | 0.4983 μs | 0.7459 μs |  0.82 | Faster          |    0.03 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              |  57.413 μs | 0.5407 μs | 0.7579 μs |  1.00 | Baseline        |    0.00 |      - |     248 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              |  46.190 μs | 0.6725 μs | 1.0065 μs |  0.80 | Faster          |    0.02 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  |  56.646 μs | 1.1756 μs | 1.7596 μs |  1.00 | Baseline        |    0.00 |      - |     249 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  |  46.139 μs | 0.4609 μs | 0.6756 μs |  0.82 | Faster          |    0.03 |      - |       1 B |       0.004 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 118.571 μs | 1.5151 μs | 2.2677 μs |  1.00 | Baseline        |    0.00 |      - |     584 B |        1.00 |
| Respire_SetDel                 | SET+DEL              |  98.581 μs | 1.1686 μs | 1.7491 μs |  0.83 | Faster          |    0.02 |      - |     136 B |        0.23 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            |  55.155 μs | 0.6730 μs | 0.9865 μs |  1.00 | Baseline        |    0.00 |      - |     248 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            |  43.683 μs | 0.6958 μs | 0.9979 μs |  0.79 | Faster          |    0.02 |      - |         - |        0.00 |

#### Run 3

```

BenchmarkDotNet v0.16.0-preview.2, Linux Ubuntu 26.04.1 LTS (Resolute Raccoon)
INTEL XEON PLATINUM 8573C 3.01GHz, 1 CPU, 4 logical and 2 physical cores
Memory: 15.61 GB Total, 11.64 GB Available
.NET SDK 11.0.100
  [Host]     : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v4
  Job-IDGKZI : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v4

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                         | Categories           | Mean       | Error     | StdDev    | Ratio | MannWhitney(5%) | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|----------:|----------:|------:|---------------- |--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               |  80.038 μs | 0.7928 μs | 1.1621 μs |  1.00 | Baseline        |    0.00 |      - |     232 B |        1.00 |
| Respire_Exists                 | EXISTS               |  78.285 μs | 0.4125 μs | 0.5783 μs |  0.98 | Same            |    0.02 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get              | GET                  |  83.261 μs | 0.5432 μs | 0.7791 μs |  1.00 | Baseline        |    0.00 |      - |     440 B |        1.00 |
| Respire_Get                    | GET                  |  77.892 μs | 0.3246 μs | 0.4758 μs |  0.94 | Faster          |    0.01 |      - |      48 B |        0.11 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_GetLong          | GET long             |  82.142 μs | 0.4931 μs | 0.6750 μs |  1.00 | Baseline        |    0.00 |      - |     352 B |        1.00 |
| Respire_GetLong                | GET long             |  77.990 μs | 0.3027 μs | 0.4144 μs |  0.95 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  |  81.743 μs | 0.4892 μs | 0.6857 μs |  1.00 | Baseline        |    0.00 |      - |     337 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  |  75.557 μs | 1.8560 μs | 2.7780 μs |  0.92 | Same            |    0.03 |      - |      49 B |        0.15 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Pipelined    | GET x200 pipelined   |   1.870 μs | 0.0144 μs | 0.0206 μs |  1.00 | Baseline        |    0.00 | 0.0024 |     289 B |        1.00 |
| Respire_Get_Pipelined          | GET x200 pipelined   |   1.605 μs | 0.0084 μs | 0.0121 μs |  0.86 | Faster          |    0.01 |      - |      49 B |        0.17 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   3.413 μs | 0.0230 μs | 0.0322 μs |  1.00 | Baseline        |    0.00 |      - |     290 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   3.296 μs | 0.0263 μs | 0.0394 μs |  0.97 | Same            |    0.01 |      - |      51 B |        0.18 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HGet             | HGET                 |  82.804 μs | 1.2258 μs | 1.8347 μs |  1.00 | Baseline        |    0.00 |      - |     456 B |        1.00 |
| Respire_HGet                   | HGET                 |  78.414 μs | 0.5418 μs | 0.7770 μs |  0.95 | Same            |    0.02 |      - |      48 B |        0.11 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HSet             | HSET                 |  81.158 μs | 0.8932 μs | 1.3093 μs |  1.00 | Baseline        |    0.00 |      - |     264 B |        1.00 |
| Respire_HSet                   | HSET                 |  78.758 μs | 0.3287 μs | 0.4608 μs |  0.97 | Same            |    0.02 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Incr             | INCR                 |  80.958 μs | 0.4361 μs | 0.6114 μs |  1.00 | Baseline        |    0.00 |      - |     232 B |        1.00 |
| Respire_Incr                   | INCR                 |  78.142 μs | 0.2923 μs | 0.4192 μs |  0.97 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 172.864 μs | 0.9598 μs | 1.4069 μs |  1.00 | Baseline        |    0.00 |      - |     696 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 159.934 μs | 1.0409 μs | 1.5580 μs |  0.93 | Faster          |    0.01 |      - |     192 B |        0.28 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping             | PING                 |  79.843 μs | 0.5438 μs | 0.8140 μs |  1.00 | Baseline        |    0.00 |      - |     240 B |        1.00 |
| Respire_Ping                   | PING                 |  77.635 μs | 0.4112 μs | 0.5897 μs |  0.97 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential |  79.762 μs | 0.3976 μs | 0.5574 μs |  1.00 | Baseline        |    0.00 |      - |     241 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential |  78.369 μs | 0.5236 μs | 0.7340 μs |  0.98 | Same            |    0.01 |      - |       1 B |       0.004 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SAdd             | SADD                 |  80.505 μs | 1.2656 μs | 1.8551 μs |  1.00 | Baseline        |    0.00 |      - |     248 B |        1.00 |
| Respire_SAdd                   | SADD                 |  75.530 μs | 1.9633 μs | 2.8157 μs |  0.94 | Same            |    0.04 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             |  94.703 μs | 0.6056 μs | 0.9065 μs |  1.00 | Baseline        |    0.00 |      - |     248 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             |  82.981 μs | 0.3526 μs | 0.4826 μs |  0.88 | Faster          |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              |  82.157 μs | 0.3849 μs | 0.5642 μs |  1.00 | Baseline        |    0.00 |      - |     248 B |        1.00 |
| Respire_Set_Small              | SET 13B              |  78.720 μs | 0.3488 μs | 0.4775 μs |  0.96 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              |  83.694 μs | 0.6017 μs | 0.8820 μs |  1.00 | Baseline        |    0.00 |      - |     248 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              |  79.297 μs | 0.4566 μs | 0.6549 μs |  0.95 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  |  82.298 μs | 0.6122 μs | 0.8973 μs |  1.00 | Baseline        |    0.00 |      - |     249 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  |  78.817 μs | 0.5087 μs | 0.7457 μs |  0.96 | Same            |    0.01 |      - |       1 B |       0.004 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 166.269 μs | 0.7325 μs | 1.0269 μs |  1.00 | Baseline        |    0.00 |      - |     584 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 157.993 μs | 1.1448 μs | 1.5671 μs |  0.95 | Same            |    0.01 |      - |     136 B |        0.23 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            |  81.550 μs | 0.2911 μs | 0.4175 μs |  1.00 | Baseline        |    0.00 |      - |     248 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            |  78.319 μs | 0.7295 μs | 1.0463 μs |  0.96 | Same            |    0.01 |      - |         - |        0.00 |

### PubSubComparisonBenchmarks

#### Run 1

```

BenchmarkDotNet v0.16.0-preview.2, Linux Ubuntu 26.04.1 LTS (Resolute Raccoon)
AMD EPYC 7763 3.12GHz, 1 CPU, 4 logical and 2 physical cores
Memory: 15.61 GB Total, 11.78 GB Available
.NET SDK 11.0.100
  [Host]     : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v3
  Job-IDGKZI : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v3

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                          | Mean     | Error   | StdDev  | Ratio | MannWhitney(5%) | Allocated | Alloc Ratio |
|-------------------------------- |---------:|--------:|--------:|------:|---------------- |----------:|------------:|
| StackExchange_PublishAndConsume | 175.3 μs | 1.32 μs | 1.98 μs |  1.00 | Baseline        |     608 B |        1.00 |
| Respire_PublishAndConsume       | 177.2 μs | 1.19 μs | 1.71 μs |  1.01 | Same            |     336 B |        0.55 |

#### Run 2

```

BenchmarkDotNet v0.16.0-preview.2, Linux Ubuntu 26.04.1 LTS (Resolute Raccoon)
Intel Xeon 6975P-C 3.18GHz, 1 CPU, 4 logical and 2 physical cores
Memory: 15.33 GB Total, 11.8 GB Available
.NET SDK 11.0.100
  [Host]     : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v4
  Job-IDGKZI : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v4

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                          | Mean     | Error    | StdDev   | Ratio | MannWhitney(5%) | RatioSD | Allocated | Alloc Ratio |
|-------------------------------- |---------:|---------:|---------:|------:|---------------- |--------:|----------:|------------:|
| StackExchange_PublishAndConsume | 70.49 μs | 1.014 μs | 1.518 μs |  1.00 | Baseline        |    0.00 |     608 B |        1.00 |
| Respire_PublishAndConsume       | 67.57 μs | 1.300 μs | 1.946 μs |  0.96 | Same            |    0.03 |     336 B |        0.55 |

#### Run 3

```

BenchmarkDotNet v0.16.0-preview.2, Linux Ubuntu 26.04.1 LTS (Resolute Raccoon)
INTEL XEON PLATINUM 8573C 3.01GHz, 1 CPU, 4 logical and 2 physical cores
Memory: 15.61 GB Total, 11.64 GB Available
.NET SDK 11.0.100
  [Host]     : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v4
  Job-IDGKZI : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v4

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                          | Mean     | Error   | StdDev  | Ratio | MannWhitney(5%) | Allocated | Alloc Ratio |
|-------------------------------- |---------:|--------:|--------:|------:|---------------- |----------:|------------:|
| StackExchange_PublishAndConsume | 116.0 μs | 0.58 μs | 0.86 μs |  1.00 | Baseline        |     608 B |        1.00 |
| Respire_PublishAndConsume       | 110.5 μs | 0.37 μs | 0.56 μs |  0.95 | Same            |     336 B |        0.55 |

## Reading the results

These runs use shared Linux machines and a local Redis container, so expect some variation between runs. Before making an important decision, measure with your own payload sizes, concurrency, Redis deployment, and network.
