---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

import ComparisonBarChart from '@site/src/components/ComparisonBarChart';

# Benchmarks

:::info Automated results
Generated 2026-11-15 10:44 UTC from commit `4a2ad0e1677f`. These results are refreshed weekly.
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
  data={[{"label":"EXISTS hot","other":89331.4,"respire":299.8,"respireServer":82672.2},{"label":"GET hot","other":92278.7,"respire":124.5,"respireServer":84262.3},{"label":"GET missing hot","other":91066.9,"respire":124.7,"respireServer":83366.0},{"label":"HGET hot","other":93627.8,"respire":341.1,"respireServer":83748.2}]}
/>

<ComparisonBarChart
  title="Selected operation time — net11.0"
  description="Mean time. Shorter bars are faster."
  format="duration-ns"
  scale="group"
  showRatio
  data={[{"label":"GET","other":92553.0,"respire":83133.0},{"label":"GET x200 pipelined","other":1583.0,"respire":1353.0},{"label":"GET x50 concurrent","other":3289.0,"respire":3081.0},{"label":"HGET","other":92691.0,"respire":82607.0},{"label":"HSET","other":91847.0,"respire":83131.0},{"label":"LPUSH+LPOP","other":190191.0,"respire":168031.0},{"label":"SET 1KB","other":91819.0,"respire":83381.0}]}
/>

## Ratio across runs

- Run 1: AMD EPYC 7763 3.19GHz
- Run 2: AMD EPYC 9V74 3.68GHz
- Run 3: AMD EPYC 9V74 3.68GHz

### ClientSideCachingBenchmarks

| Method | Categories | Run 1 | Run 2 | Run 3 | Median |
|---|---|---:|---:|---:|---:|
| Respire_Exists_ServerRead | EXISTS hot | 0.96 | 0.93 | 0.94 | **0.94** |
| Respire_Exists_ClientCacheHit | EXISTS hot | 0.00 | 0.00 | 0.00 | **0.00** |
| Respire_Get_ServerRead | GET hot | 0.93 | 0.90 | 0.91 | **0.91** |
| Respire_Get_ClientCacheHit | GET hot | 0.00 | 0.00 | 0.00 | **0.00** |
| Respire_GetMissing_ServerRead | GET missing hot | 0.95 | 0.92 | 0.92 | **0.92** |
| Respire_GetMissing_ClientCacheHit | GET missing hot | 0.00 | 0.00 | 0.00 | **0.00** |
| Respire_HGet_ServerRead | HGET hot | 0.93 | 0.89 | 0.89 | **0.89** |
| Respire_HGet_ClientCacheHit | HGET hot | 0.00 | 0.00 | 0.00 | **0.00** |

### CommonOperationsBenchmarks

| Method | Categories | Run 1 | Run 2 | Run 3 | Median |
|---|---|---:|---:|---:|---:|
| Respire_Exists | EXISTS | 0.96 | 0.93 | 0.93 | **0.93** |
| Respire_Get | GET | 0.93 | 0.89 | 0.90 | **0.90** |
| Respire_GetLong | GET long | 0.94 | 0.91 | 0.89 | **0.91** |
| Respire_Get_SteadyState | GET x100 sequential | 0.96 | 0.91 | 0.92 | **0.92** |
| Respire_Get_Pipelined | GET x200 pipelined | 0.88 | 0.85 | 0.85 | **0.85** |
| Respire_Get_Concurrent | GET x50 concurrent | 0.99 | 0.94 | 0.94 | **0.94** |
| Respire_HGet | HGET | 0.95 | 0.89 | 0.88 | **0.89** |
| Respire_HSet | HSET | 0.96 | 0.89 | 0.91 | **0.91** |
| Respire_Incr | INCR | 0.96 | 0.92 | 0.92 | **0.92** |
| Respire_LPushLPop | LPUSH+LPOP | 0.94 | 0.88 | 0.88 | **0.88** |
| Respire_Ping | PING | 0.98 | 0.94 | 0.93 | **0.94** |
| Respire_Ping_SteadyState | PING x100 sequential | 0.99 | 0.93 | 0.93 | **0.93** |
| Respire_SAdd | SADD | 0.96 | 0.94 | 0.92 | **0.94** |
| Respire_Set_10KB | SET 10KB | 0.93 | 0.88 | 0.87 | **0.88** |
| Respire_Set_Small | SET 13B | 0.99 | 0.92 | 0.91 | **0.92** |
| Respire_Set_1KB | SET 1KB | 0.94 | 0.91 | 0.90 | **0.91** |
| Respire_Set_SteadyState | SET x100 sequential | 0.96 | 0.93 | 0.92 | **0.93** |
| Respire_SetDel | SET+DEL | 0.92 | 0.89 | 0.90 | **0.90** |
| Respire_SIsMember | SISMEMBER | 0.95 | 0.91 | 0.92 | **0.92** |

### PubSubComparisonBenchmarks

| Method | Run 1 | Run 2 | Run 3 | Median |
|---|---:|---:|---:|---:|
| Respire_PublishAndConsume | 0.96 | 0.99 | 0.98 | **0.98** |

## net11.0

### ClientSideCachingBenchmarks

#### Run 1

```

BenchmarkDotNet v0.16.0-preview.2, Linux Ubuntu 26.04.1 LTS (Resolute Raccoon)
AMD EPYC 7763 3.19GHz, 1 CPU, 4 logical and 2 physical cores
Memory: 15.61 GB Total, 11.83 GB Available
.NET SDK 11.0.100
  [Host]     : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v3
  Job-IDGKZI : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v3

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                              | Categories      | Mean         | Error       | StdDev      | Median       | Op/s        | Ratio | MannWhitney(5%) | Allocated | Alloc Ratio |
|------------------------------------ |---------------- |-------------:|------------:|------------:|-------------:|------------:|------:|---------------- |----------:|------------:|
| StackExchange_Exists_ServerRead     | EXISTS hot      | 141,656.8 ns |   957.98 ns | 1,311.30 ns | 141,659.3 ns |     7,059.3 | 1.000 | Baseline        |     232 B |        1.00 |
| Respire_Exists_ServerRead           | EXISTS hot      | 135,898.8 ns | 1,071.87 ns | 1,571.13 ns | 135,653.0 ns |     7,358.4 | 0.959 | Same            |         - |        0.00 |
| Respire_Exists_ClientCacheHit       | EXISTS hot      |     398.6 ns |     1.75 ns |     2.51 ns |     398.8 ns | 2,509,012.5 | 0.003 | Faster          |         - |        0.00 |
|                                     |                 |              |             |             |              |             |       |                 |           |             |
| StackExchange_Get_ServerRead        | GET hot         | 145,705.0 ns | 1,342.06 ns | 1,881.38 ns | 146,075.5 ns |     6,863.2 | 1.000 | Baseline        |     464 B |        1.00 |
| Respire_Get_ServerRead              | GET hot         | 135,160.1 ns |   950.10 ns | 1,362.61 ns | 135,094.9 ns |     7,398.6 | 0.928 | Faster          |      64 B |        0.14 |
| Respire_Get_ClientCacheHit          | GET hot         |     154.4 ns |     0.45 ns |     0.63 ns |     154.3 ns | 6,478,659.4 | 0.001 | Faster          |         - |        0.00 |
|                                     |                 |              |             |             |              |             |       |                 |           |             |
| StackExchange_GetMissing_ServerRead | GET missing hot | 142,048.3 ns |   598.38 ns |   798.83 ns | 141,855.1 ns |     7,039.9 | 1.000 | Baseline        |     352 B |        1.00 |
| Respire_GetMissing_ServerRead       | GET missing hot | 135,438.7 ns |   861.65 ns | 1,235.76 ns | 135,510.8 ns |     7,383.4 | 0.953 | Same            |         - |        0.00 |
| Respire_GetMissing_ClientCacheHit   | GET missing hot |     158.7 ns |     0.69 ns |     0.97 ns |     158.9 ns | 6,299,783.6 | 0.001 | Faster          |         - |        0.00 |
|                                     |                 |              |             |             |              |             |       |                 |           |             |
| StackExchange_HGet_ServerRead       | HGET hot        | 146,668.1 ns | 1,041.22 ns | 1,526.21 ns | 146,705.5 ns |     6,818.1 | 1.000 | Baseline        |     480 B |        1.00 |
| Respire_HGet_ServerRead             | HGET hot        | 136,774.6 ns |   943.61 ns | 1,353.29 ns | 136,677.4 ns |     7,311.3 | 0.933 | Faster          |      64 B |        0.13 |
| Respire_HGet_ClientCacheHit         | HGET hot        |     401.9 ns |     2.80 ns |     4.11 ns |     404.4 ns | 2,488,450.1 | 0.003 | Faster          |         - |        0.00 |

#### Run 2

```

BenchmarkDotNet v0.16.0-preview.2, Linux Ubuntu 26.04.1 LTS (Resolute Raccoon)
AMD EPYC 9V74 3.68GHz, 1 CPU, 4 logical and 2 physical cores
Memory: 15.61 GB Total, 11.85 GB Available
.NET SDK 11.0.100
  [Host]     : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v4
  Job-IDGKZI : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v4

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                              | Categories      | Mean        | Error       | StdDev      | Median      | Op/s        | Ratio | MannWhitney(5%) | RatioSD | Allocated | Alloc Ratio |
|------------------------------------ |---------------- |------------:|------------:|------------:|------------:|------------:|------:|---------------- |--------:|----------:|------------:|
| StackExchange_Exists_ServerRead     | EXISTS hot      | 89,331.4 ns |   782.74 ns | 1,122.58 ns | 89,336.7 ns |    11,194.3 | 1.000 | Baseline        |    0.00 |     232 B |        1.00 |
| Respire_Exists_ServerRead           | EXISTS hot      | 82,672.2 ns |   305.62 ns |   408.00 ns | 82,721.1 ns |    12,096.0 | 0.926 | Faster          |    0.01 |         - |        0.00 |
| Respire_Exists_ClientCacheHit       | EXISTS hot      |    299.8 ns |     1.36 ns |     1.81 ns |    298.9 ns | 3,335,723.7 | 0.003 | Faster          |    0.00 |         - |        0.00 |
|                                     |                 |             |             |             |             |             |       |                 |         |           |             |
| StackExchange_Get_ServerRead        | GET hot         | 91,810.0 ns | 1,025.37 ns | 1,470.55 ns | 91,552.3 ns |    10,892.1 | 1.000 | Baseline        |    0.00 |     464 B |        1.00 |
| Respire_Get_ServerRead              | GET hot         | 82,912.4 ns |   593.66 ns |   851.41 ns | 82,899.3 ns |    12,060.9 | 0.903 | Faster          |    0.02 |      64 B |        0.14 |
| Respire_Get_ClientCacheHit          | GET hot         |    128.5 ns |     3.22 ns |     4.51 ns |    124.8 ns | 7,784,351.4 | 0.001 | Faster          |    0.00 |         - |        0.00 |
|                                     |                 |             |             |             |             |             |       |                 |         |           |             |
| StackExchange_GetMissing_ServerRead | GET missing hot | 90,256.4 ns |   823.16 ns | 1,180.55 ns | 90,053.7 ns |    11,079.5 | 1.000 | Baseline        |    0.00 |     352 B |        1.00 |
| Respire_GetMissing_ServerRead       | GET missing hot | 82,917.7 ns |   513.04 ns |   767.90 ns | 82,703.0 ns |    12,060.2 | 0.919 | Faster          |    0.01 |         - |        0.00 |
| Respire_GetMissing_ClientCacheHit   | GET missing hot |    123.6 ns |     0.22 ns |     0.29 ns |    123.5 ns | 8,092,170.9 | 0.001 | Faster          |    0.00 |         - |        0.00 |
|                                     |                 |             |             |             |             |             |       |                 |         |           |             |
| StackExchange_HGet_ServerRead       | HGET hot        | 93,627.8 ns |   647.59 ns |   949.23 ns | 93,635.4 ns |    10,680.6 | 1.000 | Baseline        |    0.00 |     480 B |        1.00 |
| Respire_HGet_ServerRead             | HGET hot        | 83,748.2 ns |   406.40 ns |   595.70 ns | 83,688.6 ns |    11,940.6 | 0.895 | Faster          |    0.01 |      64 B |        0.13 |
| Respire_HGet_ClientCacheHit         | HGET hot        |    341.1 ns |     2.17 ns |     3.04 ns |    342.6 ns | 2,931,906.2 | 0.004 | Faster          |    0.00 |         - |        0.00 |

#### Run 3

```

BenchmarkDotNet v0.16.0-preview.2, Linux Ubuntu 26.04.1 LTS (Resolute Raccoon)
AMD EPYC 9V74 3.68GHz, 1 CPU, 4 logical and 2 physical cores
Memory: 15.61 GB Total, 11.84 GB Available
.NET SDK 11.0.100
  [Host]     : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v4
  Job-IDGKZI : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v4

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                              | Categories      | Mean        | Error       | StdDev      | Op/s        | Ratio | MannWhitney(5%) | RatioSD | Allocated | Alloc Ratio |
|------------------------------------ |---------------- |------------:|------------:|------------:|------------:|------:|---------------- |--------:|----------:|------------:|
| StackExchange_Exists_ServerRead     | EXISTS hot      | 89,271.3 ns |   607.01 ns |   870.56 ns |    11,201.8 | 1.000 | Baseline        |    0.00 |     232 B |        1.00 |
| Respire_Exists_ServerRead           | EXISTS hot      | 83,495.1 ns |   554.84 ns |   813.28 ns |    11,976.7 | 0.935 | Faster          |    0.01 |         - |        0.00 |
| Respire_Exists_ClientCacheHit       | EXISTS hot      |    306.2 ns |     4.90 ns |     7.33 ns | 3,265,633.8 | 0.003 | Faster          |    0.00 |         - |        0.00 |
|                                     |                 |             |             |             |             |       |                 |         |           |             |
| StackExchange_Get_ServerRead        | GET hot         | 92,278.7 ns | 1,816.28 ns | 2,486.15 ns |    10,836.7 | 1.000 | Baseline        |    0.00 |     464 B |        1.00 |
| Respire_Get_ServerRead              | GET hot         | 84,262.3 ns |   707.02 ns | 1,036.34 ns |    11,867.7 | 0.914 | Faster          |    0.03 |      64 B |        0.14 |
| Respire_Get_ClientCacheHit          | GET hot         |    124.5 ns |     0.30 ns |     0.44 ns | 8,032,940.7 | 0.001 | Faster          |    0.00 |         - |        0.00 |
|                                     |                 |             |             |             |             |       |                 |         |           |             |
| StackExchange_GetMissing_ServerRead | GET missing hot | 91,066.9 ns |   543.88 ns |   797.21 ns |    10,980.9 | 1.000 | Baseline        |    0.00 |     352 B |        1.00 |
| Respire_GetMissing_ServerRead       | GET missing hot | 83,366.0 ns |   594.40 ns |   852.46 ns |    11,995.3 | 0.916 | Faster          |    0.01 |         - |        0.00 |
| Respire_GetMissing_ClientCacheHit   | GET missing hot |    124.7 ns |     0.16 ns |     0.22 ns | 8,018,558.0 | 0.001 | Faster          |    0.00 |         - |        0.00 |
|                                     |                 |             |             |             |             |       |                 |         |           |             |
| StackExchange_HGet_ServerRead       | HGET hot        | 94,228.2 ns |   800.74 ns | 1,173.72 ns |    10,612.5 | 1.000 | Baseline        |    0.00 |     480 B |        1.00 |
| Respire_HGet_ServerRead             | HGET hot        | 83,615.0 ns |   674.29 ns |   967.04 ns |    11,959.6 | 0.888 | Faster          |    0.01 |      64 B |        0.13 |
| Respire_HGet_ClientCacheHit         | HGET hot        |    344.0 ns |     5.82 ns |     8.35 ns | 2,907,003.4 | 0.004 | Faster          |    0.00 |         - |        0.00 |

### CommonOperationsBenchmarks

#### Run 1

```

BenchmarkDotNet v0.16.0-preview.2, Linux Ubuntu 26.04.1 LTS (Resolute Raccoon)
AMD EPYC 7763 3.19GHz, 1 CPU, 4 logical and 2 physical cores
Memory: 15.61 GB Total, 11.83 GB Available
.NET SDK 11.0.100
  [Host]     : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v3
  Job-IDGKZI : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v3

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                         | Categories           | Mean       | Error     | StdDev    | Ratio | MannWhitney(5%) | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|----------:|----------:|------:|---------------- |--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 140.077 μs | 1.1218 μs | 1.6791 μs |  1.00 | Baseline        |    0.00 |      - |     232 B |        1.00 |
| Respire_Exists                 | EXISTS               | 135.033 μs | 1.1750 μs | 1.6472 μs |  0.96 | Same            |    0.02 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get              | GET                  | 144.819 μs | 0.6897 μs | 0.9669 μs |  1.00 | Baseline        |    0.00 |      - |     440 B |        1.00 |
| Respire_Get                    | GET                  | 134.981 μs | 0.8861 μs | 1.2708 μs |  0.93 | Faster          |    0.01 |      - |      48 B |        0.11 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_GetLong          | GET long             | 143.724 μs | 1.0309 μs | 1.4451 μs |  1.00 | Baseline        |    0.00 |      - |     352 B |        1.00 |
| Respire_GetLong                | GET long             | 135.117 μs | 1.3019 μs | 1.9082 μs |  0.94 | Same            |    0.02 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 140.184 μs | 1.2325 μs | 1.7677 μs |  1.00 | Baseline        |    0.00 |      - |     337 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 133.909 μs | 0.8679 μs | 1.2447 μs |  0.96 | Same            |    0.01 |      - |      49 B |        0.15 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Pipelined    | GET x200 pipelined   |   2.213 μs | 0.0101 μs | 0.0139 μs |  1.00 | Baseline        |    0.00 | 0.0171 |     289 B |        1.00 |
| Respire_Get_Pipelined          | GET x200 pipelined   |   1.940 μs | 0.0098 μs | 0.0137 μs |  0.88 | Faster          |    0.01 | 0.0024 |      49 B |        0.17 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   4.868 μs | 0.0299 μs | 0.0438 μs |  1.00 | Baseline        |    0.00 | 0.0098 |     290 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   4.823 μs | 0.0192 μs | 0.0263 μs |  0.99 | Same            |    0.01 |      - |      51 B |        0.18 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HGet             | HGET                 | 143.267 μs | 0.8375 μs | 1.2011 μs |  1.00 | Baseline        |    0.00 |      - |     456 B |        1.00 |
| Respire_HGet                   | HGET                 | 136.048 μs | 1.4526 μs | 2.0363 μs |  0.95 | Same            |    0.02 |      - |      48 B |        0.11 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HSet             | HSET                 | 141.637 μs | 0.7731 μs | 1.1572 μs |  1.00 | Baseline        |    0.00 |      - |     264 B |        1.00 |
| Respire_HSet                   | HSET                 | 135.701 μs | 1.1586 μs | 1.5859 μs |  0.96 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Incr             | INCR                 | 139.884 μs | 0.8848 μs | 1.2970 μs |  1.00 | Baseline        |    0.00 |      - |     232 B |        1.00 |
| Respire_Incr                   | INCR                 | 133.764 μs | 0.5952 μs | 0.8147 μs |  0.96 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 293.283 μs | 2.2367 μs | 3.3478 μs |  1.00 | Baseline        |    0.00 |      - |     696 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 275.967 μs | 2.0678 μs | 2.8304 μs |  0.94 | Same            |    0.01 |      - |     192 B |        0.28 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping             | PING                 | 134.919 μs | 1.5809 μs | 2.3173 μs |  1.00 | Baseline        |    0.00 |      - |     240 B |        1.00 |
| Respire_Ping                   | PING                 | 132.474 μs | 0.8017 μs | 1.1498 μs |  0.98 | Same            |    0.02 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 133.837 μs | 1.0904 μs | 1.6320 μs |  1.00 | Baseline        |    0.00 |      - |     241 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 132.231 μs | 0.6825 μs | 0.9788 μs |  0.99 | Same            |    0.01 |      - |       1 B |       0.004 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 139.423 μs | 0.9180 μs | 1.3166 μs |  1.00 | Baseline        |    0.00 |      - |     248 B |        1.00 |
| Respire_SAdd                   | SADD                 | 134.041 μs | 0.8669 μs | 1.2153 μs |  0.96 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 153.007 μs | 2.2209 μs | 3.1851 μs |  1.00 | Baseline        |    0.00 |      - |     248 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 141.827 μs | 0.7534 μs | 1.0312 μs |  0.93 | Faster          |    0.02 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 141.060 μs | 1.1709 μs | 1.7163 μs |  1.00 | Baseline        |    0.00 |      - |     248 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 140.157 μs | 2.7183 μs | 4.0686 μs |  0.99 | Same            |    0.03 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 147.981 μs | 0.9823 μs | 1.3770 μs |  1.00 | Baseline        |    0.00 |      - |     248 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 138.633 μs | 1.3981 μs | 2.0493 μs |  0.94 | Faster          |    0.02 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 143.158 μs | 0.8845 μs | 1.3238 μs |  1.00 | Baseline        |    0.00 |      - |     249 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 136.724 μs | 1.0025 μs | 1.4378 μs |  0.96 | Same            |    0.01 |      - |       1 B |       0.004 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 294.933 μs | 4.4024 μs | 6.3137 μs |  1.00 | Baseline        |    0.00 |      - |     584 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 272.741 μs | 1.9678 μs | 2.7585 μs |  0.93 | Faster          |    0.02 |      - |     136 B |        0.23 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 141.850 μs | 0.8141 μs | 1.1412 μs |  1.00 | Baseline        |    0.00 |      - |     248 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 134.897 μs | 0.8737 μs | 1.2248 μs |  0.95 | Same            |    0.01 |      - |         - |        0.00 |

#### Run 2

```

BenchmarkDotNet v0.16.0-preview.2, Linux Ubuntu 26.04.1 LTS (Resolute Raccoon)
AMD EPYC 9V74 3.68GHz, 1 CPU, 4 logical and 2 physical cores
Memory: 15.61 GB Total, 11.85 GB Available
.NET SDK 11.0.100
  [Host]     : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v4
  Job-IDGKZI : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v4

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                         | Categories           | Mean       | Error     | StdDev    | Ratio | MannWhitney(5%) | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|----------:|----------:|------:|---------------- |--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               |  88.110 μs | 0.4778 μs | 0.6853 μs |  1.00 | Baseline        |    0.00 |      - |     232 B |        1.00 |
| Respire_Exists                 | EXISTS               |  82.044 μs | 0.3150 μs | 0.4415 μs |  0.93 | Faster          |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get              | GET                  |  92.492 μs | 0.8625 μs | 1.2909 μs |  1.00 | Baseline        |    0.00 |      - |     440 B |        1.00 |
| Respire_Get                    | GET                  |  82.376 μs | 0.3522 μs | 0.5052 μs |  0.89 | Faster          |    0.01 |      - |      48 B |        0.11 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_GetLong          | GET long             |  90.825 μs | 0.6536 μs | 0.9163 μs |  1.00 | Baseline        |    0.00 |      - |     352 B |        1.00 |
| Respire_GetLong                | GET long             |  82.425 μs | 0.6696 μs | 0.9815 μs |  0.91 | Faster          |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  |  90.119 μs | 0.7041 μs | 1.0098 μs |  1.00 | Baseline        |    0.00 |      - |     337 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  |  82.106 μs | 0.3641 μs | 0.4984 μs |  0.91 | Faster          |    0.01 |      - |      49 B |        0.15 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Pipelined    | GET x200 pipelined   |   1.589 μs | 0.0095 μs | 0.0142 μs |  1.00 | Baseline        |    0.00 | 0.0171 |     289 B |        1.00 |
| Respire_Get_Pipelined          | GET x200 pipelined   |   1.358 μs | 0.0040 μs | 0.0057 μs |  0.85 | Faster          |    0.01 | 0.0024 |      49 B |        0.17 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   3.293 μs | 0.0158 μs | 0.0236 μs |  1.00 | Baseline        |    0.00 | 0.0146 |     290 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   3.084 μs | 0.0188 μs | 0.0264 μs |  0.94 | Faster          |    0.01 |      - |      51 B |        0.18 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HGet             | HGET                 |  92.691 μs | 0.7059 μs | 1.0347 μs |  1.00 | Baseline        |    0.00 |      - |     456 B |        1.00 |
| Respire_HGet                   | HGET                 |  82.607 μs | 0.4618 μs | 0.6474 μs |  0.89 | Faster          |    0.01 |      - |      48 B |        0.11 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HSet             | HSET                 |  92.445 μs | 0.7326 μs | 1.0739 μs |  1.00 | Baseline        |    0.00 |      - |     264 B |        1.00 |
| Respire_HSet                   | HSET                 |  82.624 μs | 0.4575 μs | 0.6414 μs |  0.89 | Faster          |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Incr             | INCR                 |  89.606 μs | 0.8308 μs | 1.1915 μs |  1.00 | Baseline        |    0.00 |      - |     232 B |        1.00 |
| Respire_Incr                   | INCR                 |  82.224 μs | 0.2963 μs | 0.3955 μs |  0.92 | Faster          |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 190.191 μs | 1.3585 μs | 1.9044 μs |  1.00 | Baseline        |    0.00 |      - |     696 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 168.031 μs | 0.6392 μs | 0.8961 μs |  0.88 | Faster          |    0.01 |      - |     192 B |        0.28 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping             | PING                 |  86.078 μs | 0.5194 μs | 0.7613 μs |  1.00 | Baseline        |    0.00 |      - |     240 B |        1.00 |
| Respire_Ping                   | PING                 |  80.739 μs | 0.4145 μs | 0.5810 μs |  0.94 | Faster          |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential |  86.335 μs | 0.8893 μs | 1.3036 μs |  1.00 | Baseline        |    0.00 |      - |     241 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential |  80.393 μs | 0.3751 μs | 0.5134 μs |  0.93 | Faster          |    0.01 |      - |       1 B |       0.004 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SAdd             | SADD                 |  87.670 μs | 0.3273 μs | 0.4480 μs |  1.00 | Baseline        |    0.00 |      - |     248 B |        1.00 |
| Respire_SAdd                   | SADD                 |  82.248 μs | 0.5490 μs | 0.7696 μs |  0.94 | Faster          |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 102.411 μs | 0.8723 μs | 1.2510 μs |  1.00 | Baseline        |    0.00 |      - |     248 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             |  90.103 μs | 0.5992 μs | 0.8593 μs |  0.88 | Faster          |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              |  90.488 μs | 0.6223 μs | 0.8925 μs |  1.00 | Baseline        |    0.00 |      - |     248 B |        1.00 |
| Respire_Set_Small              | SET 13B              |  83.019 μs | 0.2612 μs | 0.3829 μs |  0.92 | Faster          |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              |  91.819 μs | 0.4673 μs | 0.6849 μs |  1.00 | Baseline        |    0.00 |      - |     248 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              |  83.381 μs | 0.3379 μs | 0.4736 μs |  0.91 | Faster          |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  |  89.851 μs | 0.7994 μs | 1.1718 μs |  1.00 | Baseline        |    0.00 |      - |     249 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  |  83.383 μs | 0.8956 μs | 1.2845 μs |  0.93 | Faster          |    0.02 |      - |       1 B |       0.004 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 187.063 μs | 1.4634 μs | 2.1451 μs |  1.00 | Baseline        |    0.00 |      - |     584 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 166.008 μs | 0.7265 μs | 1.0420 μs |  0.89 | Faster          |    0.01 |      - |     136 B |        0.23 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            |  89.787 μs | 0.6444 μs | 0.9446 μs |  1.00 | Baseline        |    0.00 |      - |     248 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            |  81.927 μs | 0.3461 μs | 0.4852 μs |  0.91 | Faster          |    0.01 |      - |         - |        0.00 |

#### Run 3

```

BenchmarkDotNet v0.16.0-preview.2, Linux Ubuntu 26.04.1 LTS (Resolute Raccoon)
AMD EPYC 9V74 3.68GHz, 1 CPU, 4 logical and 2 physical cores
Memory: 15.61 GB Total, 11.84 GB Available
.NET SDK 11.0.100
  [Host]     : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v4
  Job-IDGKZI : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v4

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                         | Categories           | Mean       | Error     | StdDev    | Ratio | MannWhitney(5%) | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|----------:|----------:|------:|---------------- |--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               |  89.285 μs | 0.9386 μs | 1.3460 μs |  1.00 | Baseline        |    0.00 |      - |     232 B |        1.00 |
| Respire_Exists                 | EXISTS               |  82.593 μs | 0.5256 μs | 0.7369 μs |  0.93 | Faster          |    0.02 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get              | GET                  |  92.553 μs | 0.5057 μs | 0.7252 μs |  1.00 | Baseline        |    0.00 |      - |     440 B |        1.00 |
| Respire_Get                    | GET                  |  83.133 μs | 0.9704 μs | 1.3283 μs |  0.90 | Faster          |    0.02 |      - |      48 B |        0.11 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_GetLong          | GET long             |  92.811 μs | 0.6775 μs | 0.9930 μs |  1.00 | Baseline        |    0.00 |      - |     352 B |        1.00 |
| Respire_GetLong                | GET long             |  82.712 μs | 0.2754 μs | 0.3677 μs |  0.89 | Faster          |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  |  90.237 μs | 0.9424 μs | 1.3814 μs |  1.00 | Baseline        |    0.00 |      - |     337 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  |  82.595 μs | 0.3351 μs | 0.4698 μs |  0.92 | Faster          |    0.01 |      - |      49 B |        0.15 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Pipelined    | GET x200 pipelined   |   1.583 μs | 0.0103 μs | 0.0150 μs |  1.00 | Baseline        |    0.00 | 0.0171 |     289 B |        1.00 |
| Respire_Get_Pipelined          | GET x200 pipelined   |   1.353 μs | 0.0048 μs | 0.0067 μs |  0.85 | Faster          |    0.01 | 0.0024 |      49 B |        0.17 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   3.289 μs | 0.0255 μs | 0.0366 μs |  1.00 | Baseline        |    0.00 | 0.0146 |     290 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   3.081 μs | 0.0173 μs | 0.0259 μs |  0.94 | Faster          |    0.01 |      - |      51 B |        0.18 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HGet             | HGET                 |  93.097 μs | 0.7339 μs | 1.0985 μs |  1.00 | Baseline        |    0.00 |      - |     456 B |        1.00 |
| Respire_HGet                   | HGET                 |  82.131 μs | 0.9603 μs | 1.3772 μs |  0.88 | Faster          |    0.02 |      - |      48 B |        0.11 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HSet             | HSET                 |  91.847 μs | 0.8252 μs | 1.2096 μs |  1.00 | Baseline        |    0.00 |      - |     264 B |        1.00 |
| Respire_HSet                   | HSET                 |  83.131 μs | 0.8683 μs | 1.2172 μs |  0.91 | Faster          |    0.02 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Incr             | INCR                 |  89.423 μs | 0.3987 μs | 0.5590 μs |  1.00 | Baseline        |    0.00 |      - |     232 B |        1.00 |
| Respire_Incr                   | INCR                 |  82.614 μs | 0.2923 μs | 0.3902 μs |  0.92 | Faster          |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 193.259 μs | 0.9051 μs | 1.2688 μs |  1.00 | Baseline        |    0.00 |      - |     696 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 170.008 μs | 0.9695 μs | 1.3905 μs |  0.88 | Faster          |    0.01 |      - |     192 B |        0.28 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping             | PING                 |  88.213 μs | 0.7384 μs | 1.1052 μs |  1.00 | Baseline        |    0.00 |      - |     240 B |        1.00 |
| Respire_Ping                   | PING                 |  81.675 μs | 0.4216 μs | 0.6046 μs |  0.93 | Faster          |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential |  86.790 μs | 0.6324 μs | 0.9069 μs |  1.00 | Baseline        |    0.00 |      - |     241 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential |  80.637 μs | 0.2721 μs | 0.3633 μs |  0.93 | Faster          |    0.01 |      - |       1 B |       0.004 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SAdd             | SADD                 |  89.933 μs | 0.5117 μs | 0.7338 μs |  1.00 | Baseline        |    0.00 |      - |     248 B |        1.00 |
| Respire_SAdd                   | SADD                 |  82.351 μs | 0.2492 μs | 0.3326 μs |  0.92 | Faster          |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 103.854 μs | 0.8463 μs | 1.2405 μs |  1.00 | Baseline        |    0.00 |      - |     248 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             |  90.396 μs | 0.4734 μs | 0.6637 μs |  0.87 | Faster          |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              |  91.127 μs | 0.6850 μs | 0.9603 μs |  1.00 | Baseline        |    0.00 |      - |     248 B |        1.00 |
| Respire_Set_Small              | SET 13B              |  83.281 μs | 0.4460 μs | 0.6396 μs |  0.91 | Faster          |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              |  93.032 μs | 0.7757 μs | 1.1370 μs |  1.00 | Baseline        |    0.00 |      - |     248 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              |  83.859 μs | 0.3957 μs | 0.5547 μs |  0.90 | Faster          |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  |  90.973 μs | 0.6233 μs | 0.8939 μs |  1.00 | Baseline        |    0.00 |      - |     249 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  |  83.340 μs | 0.2476 μs | 0.3389 μs |  0.92 | Faster          |    0.01 |      - |       1 B |       0.004 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 186.920 μs | 2.4868 μs | 3.4861 μs |  1.00 | Baseline        |    0.00 |      - |     584 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 167.497 μs | 0.7102 μs | 0.9956 μs |  0.90 | Faster          |    0.02 |      - |     136 B |        0.23 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            |  90.441 μs | 0.4237 μs | 0.5800 μs |  1.00 | Baseline        |    0.00 |      - |     248 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            |  82.811 μs | 0.6274 μs | 0.8795 μs |  0.92 | Faster          |    0.01 |      - |         - |        0.00 |

### PubSubComparisonBenchmarks

#### Run 1

```

BenchmarkDotNet v0.16.0-preview.2, Linux Ubuntu 26.04.1 LTS (Resolute Raccoon)
AMD EPYC 7763 3.19GHz, 1 CPU, 4 logical and 2 physical cores
Memory: 15.61 GB Total, 11.83 GB Available
.NET SDK 11.0.100
  [Host]     : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v3
  Job-IDGKZI : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v3

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                          | Mean     | Error   | StdDev  | Median   | Ratio | MannWhitney(5%) | RatioSD | Allocated | Alloc Ratio |
|-------------------------------- |---------:|--------:|--------:|---------:|------:|---------------- |--------:|----------:|------------:|
| StackExchange_PublishAndConsume | 185.5 μs | 6.16 μs | 8.43 μs | 191.7 μs |  1.00 | Baseline        |    0.00 |     608 B |        1.00 |
| Respire_PublishAndConsume       | 178.4 μs | 0.55 μs | 0.73 μs | 178.3 μs |  0.96 | Same            |    0.04 |     336 B |        0.55 |

#### Run 2

```

BenchmarkDotNet v0.16.0-preview.2, Linux Ubuntu 26.04.1 LTS (Resolute Raccoon)
AMD EPYC 9V74 3.68GHz, 1 CPU, 4 logical and 2 physical cores
Memory: 15.61 GB Total, 11.85 GB Available
.NET SDK 11.0.100
  [Host]     : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v4
  Job-IDGKZI : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v4

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                          | Mean     | Error   | StdDev  | Ratio | MannWhitney(5%) | Allocated | Alloc Ratio |
|-------------------------------- |---------:|--------:|--------:|------:|---------------- |----------:|------------:|
| StackExchange_PublishAndConsume | 113.9 μs | 0.48 μs | 0.67 μs |  1.00 | Baseline        |     608 B |        1.00 |
| Respire_PublishAndConsume       | 112.7 μs | 0.42 μs | 0.60 μs |  0.99 | Same            |     336 B |        0.55 |

#### Run 3

```

BenchmarkDotNet v0.16.0-preview.2, Linux Ubuntu 26.04.1 LTS (Resolute Raccoon)
AMD EPYC 9V74 3.68GHz, 1 CPU, 4 logical and 2 physical cores
Memory: 15.61 GB Total, 11.84 GB Available
.NET SDK 11.0.100
  [Host]     : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v4
  Job-IDGKZI : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v4

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                          | Mean     | Error   | StdDev  | Ratio | MannWhitney(5%) | RatioSD | Allocated | Alloc Ratio |
|-------------------------------- |---------:|--------:|--------:|------:|---------------- |--------:|----------:|------------:|
| StackExchange_PublishAndConsume | 115.4 μs | 0.74 μs | 1.06 μs |  1.00 | Baseline        |    0.00 |     608 B |        1.00 |
| Respire_PublishAndConsume       | 113.5 μs | 1.14 μs | 1.63 μs |  0.98 | Same            |    0.02 |     336 B |        0.55 |

## Reading the results

These runs use shared Linux machines and a local Redis container, so expect some variation between runs. Before making an important decision, measure with your own payload sizes, concurrency, Redis deployment, and network.
