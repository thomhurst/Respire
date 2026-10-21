---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

import ComparisonBarChart from '@site/src/components/ComparisonBarChart';

# Benchmarks

:::info Automated results
Generated 2026-10-21 20:09 UTC from commit `5e0a6f4cdd0c`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/39662115530) for logs and downloadable artifacts.
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
  data={[{"label":"EXISTS hot","other":191705.0,"respire":354.9,"respireServer":190642.7},{"label":"GET hot","other":195148.9,"respire":141.7,"respireServer":190957.5},{"label":"GET missing hot","other":193142.7,"respire":144.4,"respireServer":190169.7},{"label":"HGET hot","other":193769.9,"respire":410.7,"respireServer":191643.8}]}
/>

<ComparisonBarChart
  title="Selected operation time — net10.0"
  description="Mean time. Shorter bars are faster."
  format="duration-ns"
  scale="group"
  showRatio
  data={[{"label":"GET","other":192795.0,"respire":189123.0},{"label":"GET x200 pipelined","other":2467.0,"respire":2109.0},{"label":"GET x50 concurrent","other":5374.0,"respire":4949.0},{"label":"HGET","other":193258.0,"respire":190284.0},{"label":"HSET","other":192353.0,"respire":192529.0},{"label":"LPUSH+LPOP","other":375451.0,"respire":374602.0},{"label":"SET 1KB","other":193569.0,"respire":192214.0}]}
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
| Method                              | Categories      | Mean         | Error       | StdDev      | Median       | Op/s        | Ratio | MannWhitney(5%) | Allocated | Alloc Ratio |
|------------------------------------ |---------------- |-------------:|------------:|------------:|-------------:|------------:|------:|---------------- |----------:|------------:|
| StackExchange_Exists_ServerRead     | EXISTS hot      | 191,705.0 ns |   626.59 ns |   937.85 ns | 191,579.6 ns |     5,216.3 | 1.000 | Baseline        |     296 B |        1.00 |
| Respire_Exists_ServerRead           | EXISTS hot      | 190,642.7 ns |   368.24 ns |   539.76 ns | 190,676.7 ns |     5,245.4 | 0.994 | Same            |         - |        0.00 |
| Respire_Exists_ClientCacheHit       | EXISTS hot      |     354.9 ns |     3.33 ns |     4.98 ns |     355.4 ns | 2,817,602.7 | 0.002 | Faster          |         - |        0.00 |
|                                     |                 |              |             |             |              |             |       |                 |           |             |
| StackExchange_Get_ServerRead        | GET hot         | 195,148.9 ns |   721.25 ns | 1,057.20 ns | 195,158.4 ns |     5,124.3 | 1.000 | Baseline        |     528 B |        1.00 |
| Respire_Get_ServerRead              | GET hot         | 190,957.5 ns |   562.10 ns |   823.91 ns | 191,000.5 ns |     5,236.8 | 0.979 | Same            |      64 B |        0.12 |
| Respire_Get_ClientCacheHit          | GET hot         |     141.7 ns |     0.62 ns |     0.89 ns |     142.0 ns | 7,057,809.1 | 0.001 | Faster          |         - |        0.00 |
|                                     |                 |              |             |             |              |             |       |                 |           |             |
| StackExchange_GetMissing_ServerRead | GET missing hot | 193,142.7 ns |   434.65 ns |   650.56 ns | 193,137.1 ns |     5,177.5 | 1.000 | Baseline        |     415 B |        1.00 |
| Respire_GetMissing_ServerRead       | GET missing hot | 190,169.7 ns | 1,261.99 ns | 1,888.89 ns | 190,617.4 ns |     5,258.5 | 0.985 | Same            |         - |        0.00 |
| Respire_GetMissing_ClientCacheHit   | GET missing hot |     144.4 ns |     0.20 ns |     0.28 ns |     144.4 ns | 6,927,207.8 | 0.001 | Faster          |         - |        0.00 |
|                                     |                 |              |             |             |              |             |       |                 |           |             |
| StackExchange_HGet_ServerRead       | HGET hot        | 193,769.9 ns |   836.94 ns | 1,226.77 ns | 193,823.3 ns |     5,160.8 | 1.000 | Baseline        |     544 B |        1.00 |
| Respire_HGet_ServerRead             | HGET hot        | 191,643.8 ns |   811.85 ns | 1,215.13 ns | 191,322.8 ns |     5,218.0 | 0.989 | Same            |      64 B |        0.12 |
| Respire_HGet_ClientCacheHit         | HGET hot        |     410.7 ns |     5.58 ns |     8.18 ns |     416.3 ns | 2,435,104.6 | 0.002 | Faster          |         - |        0.00 |

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
| StackExchange_Exists           | EXISTS               | 190.179 μs | 0.9924 μs | 1.4854 μs |  1.00 | Baseline        |    0.01 |      - |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 188.692 μs | 0.4193 μs | 0.6014 μs |  0.99 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get              | GET                  | 192.795 μs | 0.6563 μs | 0.9620 μs |  1.00 | Baseline        |    0.01 |      - |     504 B |        1.00 |
| Respire_Get                    | GET                  | 189.123 μs | 0.7067 μs | 1.0359 μs |  0.98 | Same            |    0.01 |      - |      48 B |        0.10 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_GetLong          | GET long             | 192.136 μs | 0.5825 μs | 0.8538 μs |  1.00 | Baseline        |    0.01 |      - |     416 B |        1.00 |
| Respire_GetLong                | GET long             | 188.769 μs | 0.9352 μs | 1.3997 μs |  0.98 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 175.072 μs | 0.9364 μs | 1.3430 μs |  1.00 | Baseline        |    0.01 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 173.297 μs | 0.7669 μs | 1.1479 μs |  0.99 | Same            |    0.01 |      - |      50 B |        0.15 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Pipelined    | GET x200 pipelined   |   2.467 μs | 0.0379 μs | 0.0544 μs |  1.00 | Baseline        |    0.03 | 0.0098 |     289 B |        1.00 |
| Respire_Get_Pipelined          | GET x200 pipelined   |   2.109 μs | 0.0122 μs | 0.0170 μs |  0.86 | Faster          |    0.02 |      - |      49 B |        0.17 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.374 μs | 0.0454 μs | 0.0679 μs |  1.00 | Baseline        |    0.02 |      - |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   4.949 μs | 0.0252 μs | 0.0353 μs |  0.92 | Faster          |    0.01 |      - |      52 B |        0.18 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HGet             | HGET                 | 193.258 μs | 0.4720 μs | 0.7065 μs |  1.00 | Baseline        |    0.01 |      - |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 190.284 μs | 0.5933 μs | 0.8880 μs |  0.98 | Same            |    0.01 |      - |      48 B |        0.09 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HSet             | HSET                 | 192.353 μs | 0.5787 μs | 0.8661 μs |  1.00 | Baseline        |    0.01 |      - |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 192.529 μs | 0.9027 μs | 1.2946 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Incr             | INCR                 | 190.774 μs | 0.6586 μs | 0.9858 μs |  1.00 | Baseline        |    0.01 |      - |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 189.908 μs | 0.3774 μs | 0.5531 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 375.451 μs | 1.1339 μs | 1.6971 μs |  1.00 | Baseline        |    0.01 |      - |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 374.602 μs | 1.1593 μs | 1.5868 μs |  1.00 | Same            |    0.01 |      - |     256 B |        0.34 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping             | PING                 | 189.407 μs | 0.7632 μs | 1.1424 μs |  1.00 | Baseline        |    0.01 |      - |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 188.138 μs | 0.4860 μs | 0.7124 μs |  0.99 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 172.982 μs | 1.0153 μs | 1.4882 μs |  1.00 | Baseline        |    0.01 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 171.762 μs | 0.8362 μs | 1.2515 μs |  0.99 | Same            |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 191.815 μs | 0.8788 μs | 1.3154 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 189.919 μs | 0.6920 μs | 0.9472 μs |  0.99 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 202.477 μs | 0.5772 μs | 0.8639 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 203.422 μs | 0.7022 μs | 1.0511 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 192.207 μs | 0.7983 μs | 1.1450 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 190.560 μs | 0.8334 μs | 1.2474 μs |  0.99 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 193.569 μs | 0.5805 μs | 0.8326 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 192.214 μs | 0.6000 μs | 0.8795 μs |  0.99 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 177.797 μs | 0.9549 μs | 1.3996 μs |  1.00 | Baseline        |    0.01 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 176.931 μs | 1.0164 μs | 1.4898 μs |  1.00 | Same            |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 373.159 μs | 1.1137 μs | 1.6670 μs |  1.00 | Baseline        |    0.01 |      - |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 368.936 μs | 0.8840 μs | 1.2957 μs |  0.99 | Same            |    0.01 |      - |     200 B |        0.31 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 192.122 μs | 0.6492 μs | 0.9717 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 190.968 μs | 0.3970 μs | 0.5819 μs |  0.99 | Same            |    0.01 |      - |         - |        0.00 |

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
| StackExchange_PublishAndConsume | 227.0 μs | 1.25 μs | 1.79 μs |  1.00 | Baseline        |     672 B |        1.00 |
| Respire_PublishAndConsume       | 228.6 μs | 0.86 μs | 1.26 μs |  1.01 | Same            |     400 B |        0.60 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
