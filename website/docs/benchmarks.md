---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

import ComparisonBarChart from '@site/src/components/ComparisonBarChart';

# Benchmarks

:::info Automated results
Generated 2026-10-11 03:45 UTC from commit `5c324a586d18`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/38107444332) for logs and downloadable artifacts.
:::

StackExchange.Redis is the baseline. A ratio below `1.00` means Respire completed the operation faster.

## Visual comparison

StackExchange.Redis has no built-in server-assisted client cache, so its values are ordinary server reads. Respire server reads are included for a like-for-like uncached comparison.

<ComparisonBarChart
  title="Client-cache hit time — unknown framework"
  description="StackExchange.Redis and Respire server reads vs Respire client-cache hit. Shorter bars are faster."
  format="duration-ns"
  respireLabel="Respire cache hit"
  scale="group"
  showRatio
  data={[{"label":"EXISTS hot","other":186553.6,"respire":476.6,"respireServer":187672.2},{"label":"GET hot","other":189273.2,"respire":221.8,"respireServer":188580.8},{"label":"GET missing hot","other":189143.2,"respire":229.8,"respireServer":189336.4},{"label":"HGET hot","other":191947.0,"respire":608.9,"respireServer":189650.5}]}
/>

<ComparisonBarChart
  title="Selected operation time — unknown framework"
  description="Mean time. Shorter bars are faster."
  format="duration-ns"
  scale="group"
  showRatio
  data={[{"label":"GET","other":189076.0,"respire":188317.0},{"label":"GET x200 pipelined","other":2468.0,"respire":2263.0},{"label":"GET x50 concurrent","other":5289.0,"respire":5135.0},{"label":"HGET","other":190763.0,"respire":189669.0},{"label":"HSET","other":188862.0,"respire":189983.0},{"label":"LPUSH+LPOP","other":368064.0,"respire":364789.0},{"label":"SET 1KB","other":189596.0,"respire":190299.0}]}
/>

## unknown framework

### ClientSideCachingBenchmarks

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
AMD EPYC 7763 3.03GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  Job-IDGKZI : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                              | Categories      | Mean         | Error     | StdDev      | Median       | Op/s        | Ratio | MannWhitney(5%) | Gen0   | Allocated | Alloc Ratio |
|------------------------------------ |---------------- |-------------:|----------:|------------:|-------------:|------------:|------:|---------------- |-------:|----------:|------------:|
| StackExchange_Exists_ServerRead     | EXISTS hot      | 186,553.6 ns | 748.72 ns | 1,073.80 ns | 186,855.2 ns |     5,360.4 | 1.000 | Baseline        |      - |     296 B |        1.00 |
| Respire_Exists_ServerRead           | EXISTS hot      | 187,672.2 ns | 735.89 ns | 1,078.65 ns | 187,646.9 ns |     5,328.4 | 1.006 | Same            |      - |         - |        0.00 |
| Respire_Exists_ClientCacheHit       | EXISTS hot      |     476.6 ns |   2.27 ns |     3.03 ns |     477.9 ns | 2,098,053.0 | 0.003 | Faster          |      - |         - |        0.00 |
|                                     |                 |              |           |             |              |             |       |                 |        |           |             |
| StackExchange_Get_ServerRead        | GET hot         | 189,273.2 ns | 680.26 ns |   997.12 ns | 189,218.1 ns |     5,283.4 | 1.000 | Baseline        |      - |     528 B |        1.00 |
| Respire_Get_ServerRead              | GET hot         | 188,580.8 ns | 560.38 ns |   821.39 ns | 188,633.1 ns |     5,302.8 | 0.996 | Same            |      - |      64 B |        0.12 |
| Respire_Get_ClientCacheHit          | GET hot         |     221.8 ns |   1.26 ns |     1.77 ns |     221.2 ns | 4,509,556.3 | 0.001 | Faster          |      - |         - |        0.00 |
|                                     |                 |              |           |             |              |             |       |                 |        |           |             |
| StackExchange_GetMissing_ServerRead | GET missing hot | 189,143.2 ns | 524.55 ns |   768.88 ns | 189,104.7 ns |     5,287.0 | 1.000 | Baseline        |      - |     416 B |        1.00 |
| Respire_GetMissing_ServerRead       | GET missing hot | 189,336.4 ns | 440.15 ns |   631.24 ns | 189,398.1 ns |     5,281.6 | 1.001 | Same            |      - |         - |        0.00 |
| Respire_GetMissing_ClientCacheHit   | GET missing hot |     229.8 ns |   3.82 ns |     5.48 ns |     233.7 ns | 4,351,716.6 | 0.001 | Faster          |      - |         - |        0.00 |
|                                     |                 |              |           |             |              |             |       |                 |        |           |             |
| StackExchange_HGet_ServerRead       | HGET hot        | 191,947.0 ns | 488.56 ns |   700.68 ns | 191,926.2 ns |     5,209.8 | 1.000 | Baseline        |      - |     544 B |        1.00 |
| Respire_HGet_ServerRead             | HGET hot        | 189,650.5 ns | 432.23 ns |   633.55 ns | 189,511.9 ns |     5,272.9 | 0.988 | Same            |      - |      64 B |        0.12 |
| Respire_HGet_ClientCacheHit         | HGET hot        |     608.9 ns |   3.04 ns |     4.06 ns |     609.1 ns | 1,642,428.0 | 0.003 | Faster          | 0.0038 |      64 B |        0.12 |

### CommonOperationsBenchmarks

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
AMD EPYC 7763 3.03GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  Job-IDGKZI : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                         | Categories           | Mean       | Error     | StdDev    | Ratio | MannWhitney(5%) | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|----------:|----------:|------:|---------------- |--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 187.511 μs | 0.5673 μs | 0.8316 μs |  1.00 | Baseline        |    0.01 |      - |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 189.325 μs | 0.6779 μs | 0.9937 μs |  1.01 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get              | GET                  | 189.076 μs | 0.6666 μs | 0.9978 μs |  1.00 | Baseline        |    0.01 |      - |     504 B |        1.00 |
| Respire_Get                    | GET                  | 188.317 μs | 0.4606 μs | 0.6751 μs |  1.00 | Same            |    0.01 |      - |      48 B |        0.10 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_GetLong          | GET long             | 189.678 μs | 0.6110 μs | 0.8957 μs |  1.00 | Baseline        |    0.01 |      - |     416 B |        1.00 |
| Respire_GetLong                | GET long             | 190.785 μs | 0.5115 μs | 0.7498 μs |  1.01 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 171.952 μs | 1.0159 μs | 1.5206 μs |  1.00 | Baseline        |    0.01 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 170.479 μs | 1.2136 μs | 1.8164 μs |  0.99 | Same            |    0.01 |      - |      50 B |        0.15 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Pipelined    | GET x200 pipelined   |   2.468 μs | 0.0235 μs | 0.0344 μs |  1.00 | Baseline        |    0.02 | 0.0098 |     289 B |        1.00 |
| Respire_Get_Pipelined          | GET x200 pipelined   |   2.263 μs | 0.0098 μs | 0.0134 μs |  0.92 | Faster          |    0.01 |      - |      49 B |        0.17 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.289 μs | 0.0301 μs | 0.0432 μs |  1.00 | Baseline        |    0.01 |      - |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   5.135 μs | 0.0448 μs | 0.0656 μs |  0.97 | Same            |    0.01 |      - |      52 B |        0.18 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HGet             | HGET                 | 190.763 μs | 0.7069 μs | 1.0580 μs |  1.00 | Baseline        |    0.01 |      - |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 189.669 μs | 0.6379 μs | 0.9351 μs |  0.99 | Same            |    0.01 |      - |      48 B |        0.09 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HSet             | HSET                 | 188.862 μs | 0.5046 μs | 0.7553 μs |  1.00 | Baseline        |    0.01 |      - |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 189.983 μs | 0.4586 μs | 0.6865 μs |  1.01 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Incr             | INCR                 | 187.740 μs | 0.4943 μs | 0.7398 μs |  1.00 | Baseline        |    0.01 |      - |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 189.107 μs | 0.5110 μs | 0.7491 μs |  1.01 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 368.064 μs | 0.8681 μs | 1.2724 μs |  1.00 | Baseline        |    0.00 |      - |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 364.789 μs | 1.3523 μs | 2.0240 μs |  0.99 | Same            |    0.01 |      - |     256 B |        0.34 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping             | PING                 | 184.951 μs | 0.5764 μs | 0.8448 μs |  1.00 | Baseline        |    0.01 |      - |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 186.003 μs | 0.5505 μs | 0.8240 μs |  1.01 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 171.384 μs | 0.8641 μs | 1.2933 μs |  1.00 | Baseline        |    0.01 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 169.234 μs | 1.0731 μs | 1.5730 μs |  0.99 | Same            |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 188.146 μs | 0.8178 μs | 1.2241 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 187.525 μs | 0.4654 μs | 0.6966 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 199.652 μs | 0.4896 μs | 0.7328 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 200.447 μs | 0.5215 μs | 0.7644 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 188.794 μs | 0.7931 μs | 1.1871 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 190.058 μs | 0.5117 μs | 0.7659 μs |  1.01 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 189.596 μs | 0.6366 μs | 0.9130 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 190.299 μs | 0.5054 μs | 0.7564 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 173.763 μs | 0.9355 μs | 1.3417 μs |  1.00 | Baseline        |    0.01 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 173.237 μs | 0.7881 μs | 1.1796 μs |  1.00 | Same            |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 363.964 μs | 1.1385 μs | 1.7041 μs |  1.00 | Baseline        |    0.01 |      - |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 363.047 μs | 0.9549 μs | 1.4292 μs |  1.00 | Same            |    0.01 |      - |     200 B |        0.31 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 187.609 μs | 0.4896 μs | 0.7176 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 187.975 μs | 0.5227 μs | 0.7662 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
