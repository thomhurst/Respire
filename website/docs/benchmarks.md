---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

import ComparisonBarChart from '@site/src/components/ComparisonBarChart';

# Benchmarks

:::info Automated results
Generated 2026-10-25 03:42 UTC from commit `ffae194bb093`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/40156787782) for logs and downloadable artifacts.
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
  data={[{"label":"EXISTS hot","other":190146.9,"respire":353.4,"respireServer":188860.8},{"label":"GET hot","other":192186.7,"respire":141.3,"respireServer":188916.8},{"label":"GET missing hot","other":191057.2,"respire":144.4,"respireServer":189049.2},{"label":"HGET hot","other":194468.1,"respire":400.5,"respireServer":191070.9}]}
/>

<ComparisonBarChart
  title="Selected operation time — net10.0"
  description="Mean time. Shorter bars are faster."
  format="duration-ns"
  scale="group"
  showRatio
  data={[{"label":"GET","other":192906.0,"respire":189932.0},{"label":"GET x200 pipelined","other":2438.0,"respire":2078.0},{"label":"GET x50 concurrent","other":5292.0,"respire":4922.0},{"label":"HGET","other":193632.0,"respire":190603.0},{"label":"HSET","other":192874.0,"respire":194043.0},{"label":"LPUSH+LPOP","other":373981.0,"respire":371711.0},{"label":"SET 1KB","other":194130.0,"respire":192390.0}]}
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
| Method                              | Categories      | Mean         | Error       | StdDev      | Op/s        | Ratio | MannWhitney(5%) | Allocated | Alloc Ratio |
|------------------------------------ |---------------- |-------------:|------------:|------------:|------------:|------:|---------------- |----------:|------------:|
| StackExchange_Exists_ServerRead     | EXISTS hot      | 190,146.9 ns |   377.69 ns |   553.62 ns |     5,259.1 | 1.000 | Baseline        |     296 B |        1.00 |
| Respire_Exists_ServerRead           | EXISTS hot      | 188,860.8 ns |   883.11 ns | 1,294.44 ns |     5,294.9 | 0.993 | Same            |         - |        0.00 |
| Respire_Exists_ClientCacheHit       | EXISTS hot      |     353.4 ns |     2.22 ns |     3.11 ns | 2,829,700.0 | 0.002 | Faster          |         - |        0.00 |
|                                     |                 |              |             |             |             |       |                 |           |             |
| StackExchange_Get_ServerRead        | GET hot         | 192,186.7 ns | 1,067.55 ns | 1,564.80 ns |     5,203.3 | 1.000 | Baseline        |     528 B |        1.00 |
| Respire_Get_ServerRead              | GET hot         | 188,916.8 ns |   803.13 ns | 1,202.09 ns |     5,293.3 | 0.983 | Same            |      64 B |        0.12 |
| Respire_Get_ClientCacheHit          | GET hot         |     141.3 ns |     0.63 ns |     0.86 ns | 7,077,270.7 | 0.001 | Faster          |         - |        0.00 |
|                                     |                 |              |             |             |             |       |                 |           |             |
| StackExchange_GetMissing_ServerRead | GET missing hot | 191,057.2 ns |   782.93 ns | 1,171.86 ns |     5,234.0 | 1.000 | Baseline        |     416 B |       1.000 |
| Respire_GetMissing_ServerRead       | GET missing hot | 189,049.2 ns |   506.78 ns |   742.83 ns |     5,289.6 | 0.990 | Same            |       1 B |       0.002 |
| Respire_GetMissing_ClientCacheHit   | GET missing hot |     144.4 ns |     0.33 ns |     0.46 ns | 6,924,673.8 | 0.001 | Faster          |         - |       0.000 |
|                                     |                 |              |             |             |             |       |                 |           |             |
| StackExchange_HGet_ServerRead       | HGET hot        | 194,468.1 ns |   754.35 ns | 1,129.08 ns |     5,142.2 | 1.000 | Baseline        |     544 B |        1.00 |
| Respire_HGet_ServerRead             | HGET hot        | 191,070.9 ns |   499.10 ns |   715.80 ns |     5,233.7 | 0.983 | Same            |      64 B |        0.12 |
| Respire_HGet_ClientCacheHit         | HGET hot        |     400.5 ns |     1.06 ns |     1.52 ns | 2,496,778.2 | 0.002 | Faster          |         - |        0.00 |

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
| StackExchange_Exists           | EXISTS               | 189.706 μs | 0.7091 μs | 1.0170 μs |  1.00 | Baseline        |    0.01 |      - |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 188.492 μs | 0.8083 μs | 1.2099 μs |  0.99 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get              | GET                  | 192.906 μs | 0.5609 μs | 0.8395 μs |  1.00 | Baseline        |    0.01 |      - |     504 B |        1.00 |
| Respire_Get                    | GET                  | 189.932 μs | 0.4648 μs | 0.6956 μs |  0.98 | Same            |    0.01 |      - |      48 B |        0.10 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_GetLong          | GET long             | 192.732 μs | 0.4516 μs | 0.6620 μs |  1.00 | Baseline        |    0.00 |      - |     416 B |        1.00 |
| Respire_GetLong                | GET long             | 189.366 μs | 1.1261 μs | 1.6149 μs |  0.98 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 175.713 μs | 0.7829 μs | 1.1719 μs |  1.00 | Baseline        |    0.01 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 173.454 μs | 0.8741 μs | 1.2812 μs |  0.99 | Same            |    0.01 |      - |      50 B |        0.15 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Pipelined    | GET x200 pipelined   |   2.438 μs | 0.0432 μs | 0.0646 μs |  1.00 | Baseline        |    0.04 | 0.0098 |     289 B |        1.00 |
| Respire_Get_Pipelined          | GET x200 pipelined   |   2.078 μs | 0.0087 μs | 0.0125 μs |  0.85 | Faster          |    0.02 |      - |      49 B |        0.17 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.292 μs | 0.0297 μs | 0.0436 μs |  1.00 | Baseline        |    0.01 | 0.0098 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   4.922 μs | 0.0301 μs | 0.0442 μs |  0.93 | Faster          |    0.01 |      - |      52 B |        0.18 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HGet             | HGET                 | 193.632 μs | 0.4968 μs | 0.7436 μs |  1.00 | Baseline        |    0.01 |      - |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 190.603 μs | 1.3860 μs | 1.9878 μs |  0.98 | Same            |    0.01 |      - |      48 B |        0.09 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HSet             | HSET                 | 192.874 μs | 0.7213 μs | 1.0797 μs |  1.00 | Baseline        |    0.01 |      - |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 194.043 μs | 1.1103 μs | 1.6275 μs |  1.01 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Incr             | INCR                 | 191.479 μs | 0.5809 μs | 0.8331 μs |  1.00 | Baseline        |    0.01 |      - |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 190.497 μs | 0.5322 μs | 0.7632 μs |  0.99 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 373.981 μs | 1.1159 μs | 1.6357 μs |  1.00 | Baseline        |    0.01 |      - |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 371.711 μs | 0.8653 μs | 1.2684 μs |  0.99 | Same            |    0.01 |      - |     256 B |        0.34 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping             | PING                 | 188.539 μs | 0.4982 μs | 0.7457 μs |  1.00 | Baseline        |    0.01 |      - |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 189.305 μs | 1.0504 μs | 1.5721 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 173.307 μs | 1.0830 μs | 1.6210 μs |  1.00 | Baseline        |    0.01 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 172.405 μs | 0.9033 μs | 1.3521 μs |  0.99 | Same            |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 191.385 μs | 0.5023 μs | 0.7362 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 191.128 μs | 0.7905 μs | 1.1832 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 202.052 μs | 0.5530 μs | 0.7932 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 203.010 μs | 0.6336 μs | 0.9287 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 192.063 μs | 0.5351 μs | 0.7844 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 192.940 μs | 0.9993 μs | 1.4958 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 194.130 μs | 0.6065 μs | 0.9078 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 192.390 μs | 0.6082 μs | 0.9103 μs |  0.99 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 176.370 μs | 1.2109 μs | 1.8124 μs |  1.00 | Baseline        |    0.01 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 175.601 μs | 1.1611 μs | 1.6652 μs |  1.00 | Same            |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 371.038 μs | 1.3279 μs | 1.9876 μs |  1.00 | Baseline        |    0.01 |      - |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 365.899 μs | 1.3008 μs | 1.9066 μs |  0.99 | Same            |    0.01 |      - |     200 B |        0.31 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 191.607 μs | 0.4986 μs | 0.7463 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 190.413 μs | 0.5886 μs | 0.8810 μs |  0.99 | Same            |    0.01 |      - |         - |        0.00 |

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
| StackExchange_PublishAndConsume | 228.2 μs | 0.96 μs | 1.40 μs |  1.00 | Baseline        |     672 B |        1.00 |
| Respire_PublishAndConsume       | 229.3 μs | 1.01 μs | 1.42 μs |  1.00 | Same            |     400 B |        0.60 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
