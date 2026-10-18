---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

import ComparisonBarChart from '@site/src/components/ComparisonBarChart';

# Benchmarks

:::info Automated results
Generated 2026-10-18 03:41 UTC from commit `37af85aa0dac`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/39092253736) for logs and downloadable artifacts.
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
  data={[{"label":"EXISTS hot","other":190116.5,"respire":351.8,"respireServer":188368.8},{"label":"GET hot","other":192608.2,"respire":141.7,"respireServer":188794.6},{"label":"GET missing hot","other":191642.6,"respire":147.9,"respireServer":188739.6},{"label":"HGET hot","other":193749.2,"respire":400.2,"respireServer":191837.7}]}
/>

<ComparisonBarChart
  title="Selected operation time — net10.0"
  description="Mean time. Shorter bars are faster."
  format="duration-ns"
  scale="group"
  showRatio
  data={[{"label":"GET","other":191310.0,"respire":188880.0},{"label":"GET x200 pipelined","other":2468.0,"respire":2098.0},{"label":"GET x50 concurrent","other":5359.0,"respire":4928.0},{"label":"HGET","other":193032.0,"respire":189179.0},{"label":"HSET","other":192435.0,"respire":193927.0},{"label":"LPUSH+LPOP","other":372787.0,"respire":369107.0},{"label":"SET 1KB","other":193607.0,"respire":191975.0}]}
/>

## net10.0

### ClientSideCachingBenchmarks

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
AMD EPYC 7763 2.45GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.402
  [Host]     : .NET 10.0.13 (10.0.13, 10.0.1326.50504), X64 RyuJIT x86-64-v3
  Job-IDGKZI : .NET 10.0.13 (10.0.13, 10.0.1326.50504), X64 RyuJIT x86-64-v3

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                              | Categories      | Mean         | Error       | StdDev      | Op/s        | Ratio | MannWhitney(5%) | RatioSD | Allocated | Alloc Ratio |
|------------------------------------ |---------------- |-------------:|------------:|------------:|------------:|------:|---------------- |--------:|----------:|------------:|
| StackExchange_Exists_ServerRead     | EXISTS hot      | 190,116.5 ns |   654.26 ns |   979.27 ns |     5,259.9 | 1.000 | Baseline        |    0.01 |     296 B |        1.00 |
| Respire_Exists_ServerRead           | EXISTS hot      | 188,368.8 ns | 2,173.11 ns | 3,185.32 ns |     5,308.7 | 0.991 | Same            |    0.02 |         - |        0.00 |
| Respire_Exists_ClientCacheHit       | EXISTS hot      |     351.8 ns |     0.78 ns |     1.09 ns | 2,842,420.3 | 0.002 | Faster          |    0.00 |         - |        0.00 |
|                                     |                 |              |             |             |             |       |                 |         |           |             |
| StackExchange_Get_ServerRead        | GET hot         | 192,608.2 ns |   576.98 ns |   863.59 ns |     5,191.9 | 1.000 | Baseline        |    0.01 |     528 B |        1.00 |
| Respire_Get_ServerRead              | GET hot         | 188,794.6 ns | 1,413.86 ns | 2,072.42 ns |     5,296.8 | 0.980 | Same            |    0.01 |      64 B |        0.12 |
| Respire_Get_ClientCacheHit          | GET hot         |     141.7 ns |     0.37 ns |     0.50 ns | 7,056,461.9 | 0.001 | Faster          |    0.00 |         - |        0.00 |
|                                     |                 |              |             |             |             |       |                 |         |           |             |
| StackExchange_GetMissing_ServerRead | GET missing hot | 191,642.6 ns | 1,346.07 ns | 2,014.73 ns |     5,218.0 | 1.000 | Baseline        |    0.01 |     416 B |        1.00 |
| Respire_GetMissing_ServerRead       | GET missing hot | 188,739.6 ns |   658.81 ns |   965.67 ns |     5,298.3 | 0.985 | Same            |    0.01 |         - |        0.00 |
| Respire_GetMissing_ClientCacheHit   | GET missing hot |     147.9 ns |     2.58 ns |     3.36 ns | 6,760,121.2 | 0.001 | Faster          |    0.00 |         - |        0.00 |
|                                     |                 |              |             |             |             |       |                 |         |           |             |
| StackExchange_HGet_ServerRead       | HGET hot        | 193,749.2 ns |   445.93 ns |   653.64 ns |     5,161.3 | 1.000 | Baseline        |    0.00 |     544 B |        1.00 |
| Respire_HGet_ServerRead             | HGET hot        | 191,837.7 ns | 1,311.51 ns | 1,963.00 ns |     5,212.7 | 0.990 | Same            |    0.01 |      64 B |        0.12 |
| Respire_HGet_ClientCacheHit         | HGET hot        |     400.2 ns |     3.08 ns |     4.00 ns | 2,498,721.3 | 0.002 | Faster          |    0.00 |         - |        0.00 |

### CommonOperationsBenchmarks

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
AMD EPYC 7763 2.45GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.402
  [Host]     : .NET 10.0.13 (10.0.13, 10.0.1326.50504), X64 RyuJIT x86-64-v3
  Job-IDGKZI : .NET 10.0.13 (10.0.13, 10.0.1326.50504), X64 RyuJIT x86-64-v3

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                         | Categories           | Mean       | Error     | StdDev    | Ratio | MannWhitney(5%) | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|----------:|----------:|------:|---------------- |--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 189.400 μs | 0.6333 μs | 0.9283 μs |  1.00 | Baseline        |    0.01 |      - |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 188.834 μs | 0.4503 μs | 0.6740 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get              | GET                  | 191.310 μs | 0.5196 μs | 0.7616 μs |  1.00 | Baseline        |    0.01 |      - |     504 B |        1.00 |
| Respire_Get                    | GET                  | 188.880 μs | 0.4207 μs | 0.6167 μs |  0.99 | Same            |    0.01 |      - |      48 B |        0.10 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_GetLong          | GET long             | 191.579 μs | 0.5711 μs | 0.8548 μs |  1.00 | Baseline        |    0.01 |      - |     416 B |        1.00 |
| Respire_GetLong                | GET long             | 189.069 μs | 0.4683 μs | 0.6864 μs |  0.99 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 175.338 μs | 1.5099 μs | 2.2600 μs |  1.00 | Baseline        |    0.02 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 173.618 μs | 0.8369 μs | 1.2526 μs |  0.99 | Same            |    0.01 |      - |      50 B |        0.15 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Pipelined    | GET x200 pipelined   |   2.468 μs | 0.0243 μs | 0.0364 μs |  1.00 | Baseline        |    0.02 | 0.0098 |     289 B |        1.00 |
| Respire_Get_Pipelined          | GET x200 pipelined   |   2.098 μs | 0.0067 μs | 0.0096 μs |  0.85 | Faster          |    0.01 |      - |      49 B |        0.17 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.359 μs | 0.0423 μs | 0.0633 μs |  1.00 | Baseline        |    0.02 |      - |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   4.928 μs | 0.0251 μs | 0.0344 μs |  0.92 | Faster          |    0.01 |      - |      52 B |        0.18 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HGet             | HGET                 | 193.032 μs | 0.4706 μs | 0.7043 μs |  1.00 | Baseline        |    0.01 |      - |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 189.179 μs | 0.4476 μs | 0.6561 μs |  0.98 | Same            |    0.00 |      - |      48 B |        0.09 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HSet             | HSET                 | 192.435 μs | 0.5183 μs | 0.7597 μs |  1.00 | Baseline        |    0.01 |      - |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 193.927 μs | 1.1796 μs | 1.7290 μs |  1.01 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Incr             | INCR                 | 190.427 μs | 0.5835 μs | 0.8733 μs |  1.00 | Baseline        |    0.01 |      - |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 188.922 μs | 0.5656 μs | 0.8112 μs |  0.99 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 372.787 μs | 0.8317 μs | 1.2449 μs |  1.00 | Baseline        |    0.00 |      - |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 369.107 μs | 1.0952 μs | 1.6053 μs |  0.99 | Same            |    0.01 |      - |     256 B |        0.34 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping             | PING                 | 188.250 μs | 0.4137 μs | 0.6064 μs |  1.00 | Baseline        |    0.00 |      - |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 187.454 μs | 0.4068 μs | 0.5703 μs |  1.00 | Same            |    0.00 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 172.847 μs | 1.2623 μs | 1.8893 μs |  1.00 | Baseline        |    0.02 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 171.198 μs | 0.7137 μs | 1.0683 μs |  0.99 | Same            |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 190.797 μs | 0.4578 μs | 0.6852 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 188.979 μs | 0.8832 μs | 1.3219 μs |  0.99 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 202.189 μs | 0.5506 μs | 0.8240 μs |  1.00 | Baseline        |    0.01 |      - |     311 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 202.020 μs | 0.4789 μs | 0.6868 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 192.332 μs | 0.5173 μs | 0.7419 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 191.007 μs | 0.6426 μs | 0.9618 μs |  0.99 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 193.607 μs | 0.4553 μs | 0.6815 μs |  1.00 | Baseline        |    0.00 |      - |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 191.975 μs | 1.0756 μs | 1.5766 μs |  0.99 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 176.163 μs | 1.0490 μs | 1.5376 μs |  1.00 | Baseline        |    0.01 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 174.901 μs | 1.5910 μs | 2.3814 μs |  0.99 | Same            |    0.02 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 368.485 μs | 0.6819 μs | 1.0207 μs |  1.00 | Baseline        |    0.00 |      - |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 365.317 μs | 0.9630 μs | 1.4414 μs |  0.99 | Same            |    0.00 |      - |     200 B |        0.31 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 190.319 μs | 0.3427 μs | 0.5024 μs |  1.00 | Baseline        |    0.00 |      - |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 189.110 μs | 0.5430 μs | 0.7960 μs |  0.99 | Same            |    0.00 |      - |         - |        0.00 |

### PubSubComparisonBenchmarks

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
AMD EPYC 7763 2.45GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.402
  [Host]     : .NET 10.0.13 (10.0.13, 10.0.1326.50504), X64 RyuJIT x86-64-v3
  Job-IDGKZI : .NET 10.0.13 (10.0.13, 10.0.1326.50504), X64 RyuJIT x86-64-v3

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                          | Mean     | Error   | StdDev  | Ratio | MannWhitney(5%) | Allocated | Alloc Ratio |
|-------------------------------- |---------:|--------:|--------:|------:|---------------- |----------:|------------:|
| StackExchange_PublishAndConsume | 225.3 μs | 0.80 μs | 1.12 μs |  1.00 | Baseline        |     672 B |        1.00 |
| Respire_PublishAndConsume       | 227.9 μs | 0.60 μs | 0.86 μs |  1.01 | Same            |     400 B |        0.60 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
