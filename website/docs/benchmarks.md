---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

import ComparisonBarChart from '@site/src/components/ComparisonBarChart';

# Benchmarks

:::info Automated results
Generated 2026-10-12 18:42 UTC from commit `6d666d82d677`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/38288554529) for logs and downloadable artifacts.
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
  data={[{"label":"EXISTS hot","other":186982.6,"respire":482.2,"respireServer":188727.9},{"label":"GET hot","other":189675.6,"respire":226.3,"respireServer":190819.6},{"label":"GET missing hot","other":187677.2,"respire":231.6,"respireServer":187804.9},{"label":"HGET hot","other":189629.9,"respire":607.6,"respireServer":188655.8}]}
/>

<ComparisonBarChart
  title="Selected operation time — net10.0"
  description="Mean time. Shorter bars are faster."
  format="duration-ns"
  scale="group"
  showRatio
  data={[{"label":"GET","other":187631.0,"respire":189147.0},{"label":"GET x200 pipelined","other":2445.0,"respire":2286.0},{"label":"GET x50 concurrent","other":5223.0,"respire":5081.0},{"label":"HGET","other":191612.0,"respire":186657.0},{"label":"HSET","other":187943.0,"respire":190351.0},{"label":"LPUSH+LPOP","other":367379.0,"respire":387748.0},{"label":"SET 1KB","other":229367.0,"respire":232937.0}]}
/>

## net10.0

### ClientSideCachingBenchmarks

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
AMD EPYC 7763 2.45GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  Job-IDGKZI : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                              | Categories      | Mean         | Error       | StdDev      | Op/s        | Ratio | MannWhitney(5%) | Gen0   | Allocated | Alloc Ratio |
|------------------------------------ |---------------- |-------------:|------------:|------------:|------------:|------:|---------------- |-------:|----------:|------------:|
| StackExchange_Exists_ServerRead     | EXISTS hot      | 186,982.6 ns |   626.76 ns |   918.70 ns |     5,348.1 | 1.000 | Baseline        |      - |     296 B |        1.00 |
| Respire_Exists_ServerRead           | EXISTS hot      | 188,727.9 ns |   464.38 ns |   665.99 ns |     5,298.6 | 1.009 | Same            |      - |         - |        0.00 |
| Respire_Exists_ClientCacheHit       | EXISTS hot      |     482.2 ns |     2.61 ns |     3.57 ns | 2,074,028.6 | 0.003 | Faster          |      - |         - |        0.00 |
|                                     |                 |              |             |             |             |       |                 |        |           |             |
| StackExchange_Get_ServerRead        | GET hot         | 189,675.6 ns | 1,065.70 ns | 1,595.09 ns |     5,272.2 | 1.000 | Baseline        |      - |     528 B |        1.00 |
| Respire_Get_ServerRead              | GET hot         | 190,819.6 ns |   868.77 ns | 1,300.33 ns |     5,240.6 | 1.006 | Same            |      - |      64 B |        0.12 |
| Respire_Get_ClientCacheHit          | GET hot         |     226.3 ns |     2.66 ns |     3.64 ns | 4,418,780.5 | 0.001 | Faster          |      - |         - |        0.00 |
|                                     |                 |              |             |             |             |       |                 |        |           |             |
| StackExchange_GetMissing_ServerRead | GET missing hot | 187,677.2 ns |   780.03 ns | 1,167.51 ns |     5,328.3 | 1.000 | Baseline        |      - |     416 B |        1.00 |
| Respire_GetMissing_ServerRead       | GET missing hot | 187,804.9 ns |   637.38 ns |   953.99 ns |     5,324.7 | 1.001 | Same            |      - |         - |        0.00 |
| Respire_GetMissing_ClientCacheHit   | GET missing hot |     231.6 ns |     1.95 ns |     2.67 ns | 4,318,291.0 | 0.001 | Faster          |      - |         - |        0.00 |
|                                     |                 |              |             |             |             |       |                 |        |           |             |
| StackExchange_HGet_ServerRead       | HGET hot        | 189,629.9 ns |   633.82 ns |   909.00 ns |     5,273.4 | 1.000 | Baseline        |      - |     544 B |        1.00 |
| Respire_HGet_ServerRead             | HGET hot        | 188,655.8 ns |   740.82 ns | 1,085.88 ns |     5,300.7 | 0.995 | Same            |      - |      64 B |        0.12 |
| Respire_HGet_ClientCacheHit         | HGET hot        |     607.6 ns |     2.78 ns |     3.90 ns | 1,645,823.5 | 0.003 | Faster          | 0.0038 |      64 B |        0.12 |

### CommonOperationsBenchmarks

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
AMD EPYC 7763 2.45GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  Job-IDGKZI : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                         | Categories           | Mean       | Error      | StdDev     | Median     | Ratio | MannWhitney(5%) | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|-----------:|-----------:|-----------:|------:|---------------- |--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 185.369 μs |  0.6906 μs |  1.0337 μs | 185.548 μs |  1.00 | Baseline        |    0.01 |      - |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 188.178 μs |  0.9398 μs |  1.4067 μs | 188.594 μs |  1.02 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |            |            |            |       |                 |         |        |           |             |
| StackExchange_Get              | GET                  | 187.631 μs |  0.5044 μs |  0.7550 μs | 187.754 μs |  1.00 | Baseline        |    0.01 |      - |     504 B |        1.00 |
| Respire_Get                    | GET                  | 189.147 μs |  0.7420 μs |  1.1106 μs | 189.314 μs |  1.01 | Same            |    0.01 |      - |      48 B |        0.10 |
|                                |                      |            |            |            |            |       |                 |         |        |           |             |
| StackExchange_GetLong          | GET long             | 187.385 μs |  0.6218 μs |  0.8918 μs | 187.516 μs |  1.00 | Baseline        |    0.01 |      - |     416 B |        1.00 |
| Respire_GetLong                | GET long             | 189.531 μs |  0.6588 μs |  0.9656 μs | 189.739 μs |  1.01 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |            |            |            |       |                 |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 172.136 μs |  1.0839 μs |  1.6223 μs | 172.502 μs |  1.00 | Baseline        |    0.01 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 170.292 μs |  0.6131 μs |  0.8986 μs | 170.505 μs |  0.99 | Same            |    0.01 |      - |      50 B |        0.15 |
|                                |                      |            |            |            |            |       |                 |         |        |           |             |
| StackExchange_Get_Pipelined    | GET x200 pipelined   |   2.445 μs |  0.0240 μs |  0.0359 μs |   2.443 μs |  1.00 | Baseline        |    0.02 | 0.0098 |     289 B |        1.00 |
| Respire_Get_Pipelined          | GET x200 pipelined   |   2.286 μs |  0.0121 μs |  0.0174 μs |   2.288 μs |  0.94 | Faster          |    0.02 |      - |      49 B |        0.17 |
|                                |                      |            |            |            |            |       |                 |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.223 μs |  0.0368 μs |  0.0504 μs |   5.230 μs |  1.00 | Baseline        |    0.01 | 0.0098 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   5.081 μs |  0.0293 μs |  0.0430 μs |   5.079 μs |  0.97 | Same            |    0.01 |      - |      52 B |        0.18 |
|                                |                      |            |            |            |            |       |                 |         |        |           |             |
| StackExchange_HGet             | HGET                 | 191.612 μs |  3.2298 μs |  4.5278 μs | 190.428 μs |  1.00 | Baseline        |    0.03 |      - |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 186.657 μs |  0.8046 μs |  1.1794 μs | 186.898 μs |  0.97 | Same            |    0.02 |      - |      48 B |        0.09 |
|                                |                      |            |            |            |            |       |                 |         |        |           |             |
| StackExchange_HSet             | HSET                 | 187.943 μs |  0.5400 μs |  0.7915 μs | 187.909 μs |  1.00 | Baseline        |    0.01 |      - |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 190.351 μs |  0.6959 μs |  1.0415 μs | 190.200 μs |  1.01 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |            |            |            |       |                 |         |        |           |             |
| StackExchange_Incr             | INCR                 | 186.880 μs |  0.4301 μs |  0.6304 μs | 186.970 μs |  1.00 | Baseline        |    0.00 |      - |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 188.683 μs |  0.5593 μs |  0.8198 μs | 188.828 μs |  1.01 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |            |            |            |       |                 |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 367.379 μs |  1.0082 μs |  1.4778 μs | 367.435 μs |  1.00 | Baseline        |    0.01 |      - |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 387.748 μs | 17.3619 μs | 24.9000 μs | 375.659 μs |  1.06 | Same            |    0.07 |      - |     256 B |        0.34 |
|                                |                      |            |            |            |            |       |                 |         |        |           |             |
| StackExchange_Ping             | PING                 | 229.399 μs | 14.4698 μs | 21.2097 μs | 234.896 μs |  1.01 | Baseline        |    0.14 |      - |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 203.878 μs | 16.0178 μs | 22.9722 μs | 191.831 μs |  0.90 | Same            |    0.14 |      - |         - |        0.00 |
|                                |                      |            |            |            |            |       |                 |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 172.883 μs |  1.8127 μs |  2.6570 μs | 172.404 μs |  1.00 | Baseline        |    0.02 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 169.147 μs |  1.1001 μs |  1.6466 μs | 169.515 μs |  0.98 | Same            |    0.02 |      - |       2 B |       0.008 |
|                                |                      |            |            |            |            |       |                 |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 186.743 μs |  0.9009 μs |  1.3485 μs | 186.954 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 186.966 μs |  0.5717 μs |  0.8200 μs | 187.169 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |            |            |            |       |                 |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 213.857 μs | 11.6853 μs | 17.4901 μs | 204.731 μs |  1.01 | Baseline        |    0.11 |      - |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 245.279 μs | 11.8447 μs | 17.3618 μs | 241.177 μs |  1.15 | Slower          |    0.12 |      - |         - |        0.00 |
|                                |                      |            |            |            |            |       |                 |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 225.290 μs |  7.8021 μs | 11.6779 μs | 222.659 μs |  1.00 | Baseline        |    0.07 |      - |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 238.261 μs | 11.6694 μs | 17.4661 μs | 238.900 μs |  1.06 | Same            |    0.09 |      - |         - |        0.00 |
|                                |                      |            |            |            |            |       |                 |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 229.367 μs | 12.7482 μs | 18.6862 μs | 222.509 μs |  1.01 | Baseline        |    0.11 |      - |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 232.937 μs | 12.0407 μs | 18.0220 μs | 227.325 μs |  1.02 | Same            |    0.11 |      - |         - |        0.00 |
|                                |                      |            |            |            |            |       |                 |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 199.802 μs |  3.6573 μs |  5.3608 μs | 200.328 μs |  1.00 | Baseline        |    0.04 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 205.235 μs |  5.4590 μs |  8.1707 μs | 204.608 μs |  1.03 | Same            |    0.05 |      - |       2 B |       0.008 |
|                                |                      |            |            |            |            |       |                 |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 432.739 μs | 13.0516 μs | 19.5350 μs | 430.122 μs |  1.00 | Baseline        |    0.06 |      - |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 443.519 μs | 15.5965 μs | 22.8612 μs | 445.730 μs |  1.03 | Same            |    0.07 |      - |     200 B |        0.31 |
|                                |                      |            |            |            |            |       |                 |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 212.418 μs | 17.5344 μs | 25.1473 μs | 215.962 μs |  1.01 | Baseline        |    0.17 |      - |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 187.529 μs |  1.7033 μs |  2.3878 μs | 187.077 μs |  0.89 | Same            |    0.10 |      - |         - |        0.00 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
