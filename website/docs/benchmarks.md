---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

import ComparisonBarChart from '@site/src/components/ComparisonBarChart';

# Benchmarks

:::info Automated results
Generated 2026-10-04 15:45 UTC from commit `ee84269630d2`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/37211932127) for logs and downloadable artifacts.
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
  data={[{"label":"EXISTS hot","other":90825.6,"respire":342.2,"respireServer":95906.6},{"label":"GET hot","other":107159.1,"respire":167.6,"respireServer":98227.6},{"label":"GET missing hot","other":104258.7,"respire":160.4,"respireServer":98232.9},{"label":"HGET hot","other":104892.4,"respire":434.0,"respireServer":98460.2}]}
/>

<ComparisonBarChart
  title="Selected operation time — net10.0"
  description="Mean time. Shorter bars are faster."
  format="duration-ns"
  scale="group"
  showRatio
  data={[{"label":"GET","other":104327.0,"respire":95881.0},{"label":"GET x200 pipelined","other":1481.0,"respire":1419.0},{"label":"GET x50 concurrent","other":3330.0,"respire":3075.0},{"label":"HGET","other":107347.0,"respire":94292.0},{"label":"HSET","other":94040.0,"respire":96437.0},{"label":"LPUSH+LPOP","other":205141.0,"respire":192719.0},{"label":"SET 1KB","other":100071.0,"respire":102049.0}]}
/>

## net10.0

### ClientSideCachingBenchmarks

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
AMD EPYC 9V74 3.69GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  Job-IDGKZI : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                              | Categories      | Mean         | Error       | StdDev      | Median       | Op/s        | Ratio | MannWhitney(5%) | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------------ |---------------- |-------------:|------------:|------------:|-------------:|------------:|------:|---------------- |--------:|-------:|----------:|------------:|
| StackExchange_Exists_ServerRead     | EXISTS hot      |  90,825.6 ns | 2,571.21 ns | 3,848.46 ns |  91,146.9 ns |    11,010.1 | 1.002 | Baseline        |    0.06 |      - |     295 B |        1.00 |
| Respire_Exists_ServerRead           | EXISTS hot      |  95,906.6 ns | 3,538.71 ns | 5,296.58 ns |  98,184.3 ns |    10,426.8 | 1.058 | Same            |    0.07 |      - |      32 B |        0.11 |
| Respire_Exists_ClientCacheHit       | EXISTS hot      |     342.2 ns |     0.59 ns |     0.81 ns |     342.3 ns | 2,922,351.8 | 0.004 | Faster          |    0.00 |      - |         - |        0.00 |
|                                     |                 |              |             |             |              |             |       |                 |         |        |           |             |
| StackExchange_Get_ServerRead        | GET hot         | 107,159.1 ns | 1,329.93 ns | 1,990.58 ns | 107,273.8 ns |     9,331.9 | 1.000 | Baseline        |    0.03 |      - |     525 B |        1.00 |
| Respire_Get_ServerRead              | GET hot         |  98,227.6 ns | 3,384.75 ns | 5,066.13 ns |  99,122.4 ns |    10,180.4 | 0.917 | Faster          |    0.05 |      - |      96 B |        0.18 |
| Respire_Get_ClientCacheHit          | GET hot         |     167.6 ns |     0.64 ns |     0.94 ns |     167.6 ns | 5,966,749.7 | 0.002 | Faster          |    0.00 | 0.0038 |      64 B |        0.12 |
|                                     |                 |              |             |             |              |             |       |                 |         |        |           |             |
| StackExchange_GetMissing_ServerRead | GET missing hot | 104,258.7 ns | 1,356.94 ns | 2,031.01 ns | 104,596.2 ns |     9,591.5 | 1.000 | Baseline        |    0.03 |      - |     415 B |        1.00 |
| Respire_GetMissing_ServerRead       | GET missing hot |  98,232.9 ns | 1,794.84 ns | 2,686.44 ns |  98,830.7 ns |    10,179.9 | 0.943 | Same            |    0.03 |      - |      32 B |        0.08 |
| Respire_GetMissing_ClientCacheHit   | GET missing hot |     160.4 ns |     2.07 ns |     3.03 ns |     158.5 ns | 6,235,514.7 | 0.002 | Faster          |    0.00 |      - |         - |        0.00 |
|                                     |                 |              |             |             |              |             |       |                 |         |        |           |             |
| StackExchange_HGet_ServerRead       | HGET hot        | 104,892.4 ns | 2,689.94 ns | 4,026.18 ns | 105,518.2 ns |     9,533.6 | 1.001 | Baseline        |    0.05 |      - |     540 B |        1.00 |
| Respire_HGet_ServerRead             | HGET hot        |  98,460.2 ns | 3,223.25 ns | 4,824.41 ns |  99,553.2 ns |    10,156.4 | 0.940 | Same            |    0.06 |      - |      96 B |        0.18 |
| Respire_HGet_ClientCacheHit         | HGET hot        |     434.0 ns |     2.70 ns |     3.88 ns |     433.7 ns | 2,304,243.7 | 0.004 | Faster          |    0.00 | 0.0038 |      64 B |        0.12 |

### CommonOperationsBenchmarks

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
AMD EPYC 9V74 3.69GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  Job-IDGKZI : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                         | Categories           | Mean       | Error     | StdDev    | Ratio | MannWhitney(5%) | RatioSD | Gen0   | Gen1   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|----------:|----------:|------:|---------------- |--------:|-------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               |  92.161 μs | 2.5636 μs | 3.8371 μs |  1.00 | Baseline        |    0.06 |      - |      - |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               |  93.353 μs | 3.1445 μs | 4.7066 μs |  1.01 | Same            |    0.07 |      - |      - |      32 B |        0.11 |
|                                |                      |            |           |           |       |                 |         |        |        |           |             |
| StackExchange_Get              | GET                  | 104.327 μs | 2.0399 μs | 3.0532 μs |  1.00 | Baseline        |    0.04 |      - |      - |     503 B |        1.00 |
| Respire_Get                    | GET                  |  95.881 μs | 2.3918 μs | 3.5799 μs |  0.92 | Same            |    0.04 |      - |      - |      80 B |        0.16 |
|                                |                      |            |           |           |       |                 |         |        |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  |  81.386 μs | 1.8416 μs | 2.7564 μs |  1.00 | Baseline        |    0.05 |      - |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  |  78.968 μs | 0.5656 μs | 0.8290 μs |  0.97 | Same            |    0.03 |      - |      - |      50 B |        0.15 |
|                                |                      |            |           |           |       |                 |         |        |        |           |             |
| StackExchange_Get_Pipelined    | GET x200 pipelined   |   1.481 μs | 0.0333 μs | 0.0498 μs |  1.00 | Baseline        |    0.05 | 0.0171 | 0.0024 |     289 B |        1.00 |
| Respire_Get_Pipelined          | GET x200 pipelined   |   1.419 μs | 0.0123 μs | 0.0172 μs |  0.96 | Same            |    0.03 |      - |      - |      61 B |        0.21 |
|                                |                      |            |           |           |       |                 |         |        |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   3.330 μs | 0.0640 μs | 0.0958 μs |  1.00 | Baseline        |    0.04 | 0.0098 |      - |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   3.075 μs | 0.0186 μs | 0.0266 μs |  0.92 | Faster          |    0.03 |      - |      - |      52 B |        0.18 |
|                                |                      |            |           |           |       |                 |         |        |        |           |             |
| StackExchange_HGet             | HGET                 | 107.347 μs | 0.7884 μs | 1.1801 μs |  1.00 | Baseline        |    0.02 |      - |      - |     518 B |        1.00 |
| Respire_HGet                   | HGET                 |  94.292 μs | 3.2461 μs | 4.8586 μs |  0.88 | Faster          |    0.05 |      - |      - |      80 B |        0.15 |
|                                |                      |            |           |           |       |                 |         |        |        |           |             |
| StackExchange_HSet             | HSET                 |  94.040 μs | 2.0475 μs | 3.0647 μs |  1.00 | Baseline        |    0.05 |      - |      - |     328 B |        1.00 |
| Respire_HSet                   | HSET                 |  96.437 μs | 3.3734 μs | 5.0492 μs |  1.03 | Same            |    0.06 |      - |      - |      32 B |        0.10 |
|                                |                      |            |           |           |       |                 |         |        |        |           |             |
| StackExchange_Incr             | INCR                 |  95.677 μs | 2.9840 μs | 4.4664 μs |  1.00 | Baseline        |    0.07 |      - |      - |     296 B |        1.00 |
| Respire_Incr                   | INCR                 |  93.893 μs | 4.3393 μs | 6.4949 μs |  0.98 | Same            |    0.08 |      - |      - |      32 B |        0.11 |
|                                |                      |            |           |           |       |                 |         |        |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 205.141 μs | 2.9276 μs | 4.3819 μs |  1.00 | Baseline        |    0.03 |      - |      - |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 192.719 μs | 1.4306 μs | 2.0969 μs |  0.94 | Same            |    0.02 |      - |      - |     288 B |        0.38 |
|                                |                      |            |           |           |       |                 |         |        |        |           |             |
| StackExchange_Ping             | PING                 |  93.969 μs | 1.6803 μs | 2.5150 μs |  1.00 | Baseline        |    0.04 |      - |      - |     303 B |        1.00 |
| Respire_Ping                   | PING                 |  92.343 μs | 1.1225 μs | 1.6454 μs |  0.98 | Same            |    0.03 |      - |      - |      32 B |        0.11 |
|                                |                      |            |           |           |       |                 |         |        |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential |  80.866 μs | 1.0101 μs | 1.5118 μs |  1.00 | Baseline        |    0.03 |      - |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential |  78.741 μs | 0.6809 μs | 1.0191 μs |  0.97 | Same            |    0.02 |      - |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |        |           |             |
| StackExchange_SAdd             | SADD                 |  96.601 μs | 1.2458 μs | 1.8261 μs |  1.00 | Baseline        |    0.03 |      - |      - |     310 B |        1.00 |
| Respire_SAdd                   | SADD                 |  94.302 μs | 2.8261 μs | 4.2300 μs |  0.98 | Same            |    0.05 |      - |      - |      32 B |        0.10 |
|                                |                      |            |           |           |       |                 |         |        |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 118.241 μs | 2.9484 μs | 4.2285 μs |  1.00 | Baseline        |    0.05 |      - |      - |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 114.895 μs | 0.8845 μs | 1.3239 μs |  0.97 | Same            |    0.04 |      - |      - |      32 B |        0.10 |
|                                |                      |            |           |           |       |                 |         |        |        |           |             |
| StackExchange_Set_Small        | SET 13B              |  94.862 μs | 3.2333 μs | 4.7393 μs |  1.00 | Baseline        |    0.07 |      - |      - |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 100.436 μs | 1.8709 μs | 2.8003 μs |  1.06 | Same            |    0.06 |      - |      - |      32 B |        0.10 |
|                                |                      |            |           |           |       |                 |         |        |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 100.071 μs | 1.8885 μs | 2.8266 μs |  1.00 | Baseline        |    0.04 |      - |      - |     306 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 102.049 μs | 2.8740 μs | 4.3017 μs |  1.02 | Same            |    0.05 |      - |      - |      32 B |        0.10 |
|                                |                      |            |           |           |       |                 |         |        |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  |  83.532 μs | 0.7500 μs | 1.0993 μs |  1.00 | Baseline        |    0.02 |      - |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  |  80.383 μs | 0.5423 μs | 0.7949 μs |  0.96 | Same            |    0.02 |      - |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 197.029 μs | 2.6678 μs | 3.9104 μs |  1.00 | Baseline        |    0.03 |      - |      - |     646 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 183.280 μs | 3.8953 μs | 5.8303 μs |  0.93 | Same            |    0.03 |      - |      - |     232 B |        0.36 |
|                                |                      |            |           |           |       |                 |         |        |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            |  93.533 μs | 1.9207 μs | 2.8748 μs |  1.00 | Baseline        |    0.04 |      - |      - |     311 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            |  92.919 μs | 2.8616 μs | 4.2832 μs |  0.99 | Same            |    0.05 |      - |      - |      32 B |        0.10 |

## net8.0

### ClientSideCachingBenchmarks

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
INTEL XEON PLATINUM 8573C 2.30GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 8.0.31 (8.0.31, 8.0.3126.42015), X64 RyuJIT x86-64-v4
  Job-IDGKZI : .NET 8.0.31 (8.0.31, 8.0.3126.42015), X64 RyuJIT x86-64-v4

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                              | Categories      | Mean        | Error       | StdDev      | Op/s        | Ratio | MannWhitney(5%) | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------------ |---------------- |------------:|------------:|------------:|------------:|------:|---------------- |--------:|-------:|----------:|------------:|
| StackExchange_Exists_ServerRead     | EXISTS hot      | 93,916.9 ns |   813.15 ns | 1,191.91 ns |    10,647.7 | 1.000 | Baseline        |    0.02 |      - |     296 B |        1.00 |
| Respire_Exists_ServerRead           | EXISTS hot      | 91,653.0 ns |   863.16 ns | 1,291.94 ns |    10,910.7 | 0.976 | Same            |    0.02 |      - |      32 B |        0.11 |
| Respire_Exists_ClientCacheHit       | EXISTS hot      |    522.0 ns |     4.11 ns |     6.16 ns | 1,915,569.9 | 0.006 | Faster          |    0.00 |      - |         - |        0.00 |
|                                     |                 |             |             |             |             |       |                 |         |        |           |             |
| StackExchange_Get_ServerRead        | GET hot         | 95,194.0 ns |   976.03 ns | 1,399.80 ns |    10,504.9 | 1.000 | Baseline        |    0.02 |      - |     528 B |        1.00 |
| Respire_Get_ServerRead              | GET hot         | 93,994.3 ns |   983.97 ns | 1,472.76 ns |    10,638.9 | 0.988 | Same            |    0.02 |      - |      96 B |        0.18 |
| Respire_Get_ClientCacheHit          | GET hot         |    325.7 ns |     7.31 ns |    10.48 ns | 3,069,949.3 | 0.003 | Faster          |    0.00 | 0.0005 |      64 B |        0.12 |
|                                     |                 |             |             |             |             |       |                 |         |        |           |             |
| StackExchange_GetMissing_ServerRead | GET missing hot | 94,527.6 ns | 2,060.23 ns | 3,083.66 ns |    10,578.9 | 1.001 | Baseline        |    0.05 |      - |     416 B |        1.00 |
| Respire_GetMissing_ServerRead       | GET missing hot | 92,674.5 ns | 1,407.43 ns | 2,106.58 ns |    10,790.5 | 0.981 | Same            |    0.04 |      - |      32 B |        0.08 |
| Respire_GetMissing_ClientCacheHit   | GET missing hot |    339.3 ns |     0.74 ns |     1.06 ns | 2,947,624.6 | 0.004 | Faster          |    0.00 |      - |         - |        0.00 |
|                                     |                 |             |             |             |             |       |                 |         |        |           |             |
| StackExchange_HGet_ServerRead       | HGET hot        | 95,481.1 ns | 1,179.73 ns | 1,765.77 ns |    10,473.3 | 1.000 | Baseline        |    0.03 |      - |     544 B |        1.00 |
| Respire_HGet_ServerRead             | HGET hot        | 91,829.6 ns |   831.99 ns | 1,245.29 ns |    10,889.7 | 0.962 | Same            |    0.02 |      - |      96 B |        0.18 |
| Respire_HGet_ClientCacheHit         | HGET hot        |    595.7 ns |     3.99 ns |     5.72 ns | 1,678,567.3 | 0.006 | Faster          |    0.00 |      - |      64 B |        0.12 |

### CommonOperationsBenchmarks

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
INTEL XEON PLATINUM 8573C 2.30GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 8.0.31 (8.0.31, 8.0.3126.42015), X64 RyuJIT x86-64-v4
  Job-IDGKZI : .NET 8.0.31 (8.0.31, 8.0.3126.42015), X64 RyuJIT x86-64-v4

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                         | Categories           | Mean       | Error     | StdDev    | Ratio | MannWhitney(5%) | RatioSD | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|----------:|----------:|------:|---------------- |--------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               |  92.324 μs | 1.8958 μs | 2.7189 μs |  1.00 | Baseline        |    0.04 |     295 B |        1.00 |
| Respire_Exists                 | EXISTS               |  91.526 μs | 1.3394 μs | 1.9209 μs |  0.99 | Same            |    0.04 |      32 B |        0.11 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Get              | GET                  |  96.266 μs | 0.6633 μs | 0.9928 μs |  1.00 | Baseline        |    0.01 |     504 B |        1.00 |
| Respire_Get                    | GET                  |  91.383 μs | 1.9753 μs | 2.7690 μs |  0.95 | Same            |    0.03 |      80 B |        0.16 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  |  78.101 μs | 0.9524 μs | 1.3960 μs |  1.00 | Baseline        |    0.02 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  |  77.570 μs | 0.4465 μs | 0.6545 μs |  0.99 | Same            |    0.02 |      50 B |        0.15 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Get_Pipelined    | GET x200 pipelined   |   1.582 μs | 0.0989 μs | 0.1480 μs |  1.01 | Baseline        |    0.13 |     289 B |        1.00 |
| Respire_Get_Pipelined          | GET x200 pipelined   |   1.487 μs | 0.0093 μs | 0.0137 μs |  0.95 | Same            |    0.09 |      60 B |        0.21 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   3.035 μs | 0.1002 μs | 0.1499 μs |  1.00 | Baseline        |    0.07 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   2.845 μs | 0.0229 μs | 0.0335 μs |  0.94 | Same            |    0.05 |      52 B |        0.18 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_HGet             | HGET                 |  97.442 μs | 0.6414 μs | 0.9199 μs |  1.00 | Baseline        |    0.01 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 |  92.192 μs | 2.2929 μs | 3.4320 μs |  0.95 | Same            |    0.04 |      80 B |        0.15 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_HSet             | HSET                 |  96.061 μs | 0.6901 μs | 1.0329 μs |  1.00 | Baseline        |    0.01 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 |  95.530 μs | 0.8113 μs | 1.1892 μs |  0.99 | Same            |    0.02 |      32 B |        0.10 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Incr             | INCR                 |  94.251 μs | 0.6758 μs | 0.9906 μs |  1.00 | Baseline        |    0.01 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 |  95.834 μs | 1.5042 μs | 2.2514 μs |  1.02 | Same            |    0.03 |      32 B |        0.11 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 180.912 μs | 3.6189 μs | 5.4166 μs |  1.00 | Baseline        |    0.04 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 172.388 μs | 1.2677 μs | 1.8974 μs |  0.95 | Same            |    0.03 |     288 B |        0.38 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Ping             | PING                 |  94.584 μs | 0.7323 μs | 1.0961 μs |  1.00 | Baseline        |    0.02 |     304 B |        1.00 |
| Respire_Ping                   | PING                 |  90.564 μs | 1.0774 μs | 1.6125 μs |  0.96 | Same            |    0.02 |      32 B |        0.11 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential |  78.391 μs | 0.4314 μs | 0.6323 μs |  1.00 | Baseline        |    0.01 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential |  76.580 μs | 0.5513 μs | 0.8252 μs |  0.98 | Same            |    0.01 |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_SAdd             | SADD                 |  94.552 μs | 0.5787 μs | 0.8662 μs |  1.00 | Baseline        |    0.01 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 |  94.168 μs | 0.7517 μs | 1.1018 μs |  1.00 | Same            |    0.01 |      32 B |        0.10 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 100.898 μs | 0.5990 μs | 0.8966 μs |  1.00 | Baseline        |    0.01 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 100.415 μs | 0.7442 μs | 1.0673 μs |  1.00 | Same            |    0.01 |      32 B |        0.10 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Set_Small        | SET 13B              |  95.337 μs | 1.3100 μs | 1.9608 μs |  1.00 | Baseline        |    0.03 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              |  95.021 μs | 0.6884 μs | 0.9189 μs |  1.00 | Same            |    0.02 |      32 B |        0.10 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              |  98.979 μs | 0.7732 μs | 1.1333 μs |  1.00 | Baseline        |    0.02 |     311 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              |  96.318 μs | 1.1005 μs | 1.6471 μs |  0.97 | Same            |    0.02 |      32 B |        0.10 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  |  81.311 μs | 1.4805 μs | 2.2160 μs |  1.00 | Baseline        |    0.04 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  |  80.546 μs | 0.3936 μs | 0.5892 μs |  0.99 | Same            |    0.03 |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 182.411 μs | 1.6247 μs | 2.4317 μs |  1.00 | Baseline        |    0.02 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 176.731 μs | 1.6433 μs | 2.4596 μs |  0.97 | Same            |    0.02 |     232 B |        0.36 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            |  95.109 μs | 1.4285 μs | 2.1381 μs |  1.00 | Baseline        |    0.03 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            |  95.662 μs | 0.8460 μs | 1.2133 μs |  1.01 | Same            |    0.03 |      32 B |        0.10 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
