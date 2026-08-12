---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-12 14:37 UTC from commit `f4fa4bf9e216`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31605455568) for logs and downloadable artifacts.
:::

StackExchange.Redis is the baseline. A ratio below `1.00` means Respire completed the operation faster.

## net10.0

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD EPYC 7763 2.45GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.400
  [Host]     : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3
  Job-IDGKZI : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                         | Categories           | Mean       | Error     | StdDev    | Ratio | MannWhitney(5%) | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|----------:|----------:|------:|---------------- |--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 177.629 μs | 0.6825 μs | 1.0215 μs |  1.00 | Baseline        |    0.01 |      - |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 172.465 μs | 0.4609 μs | 0.6898 μs |  0.97 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get              | GET                  | 180.379 μs | 0.4756 μs | 0.7118 μs |  1.00 | Baseline        |    0.01 |      - |     504 B |        1.00 |
| Respire_Get                    | GET                  | 172.923 μs | 0.4517 μs | 0.6621 μs |  0.96 | Same            |    0.01 |      - |      48 B |        0.10 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 161.709 μs | 0.9185 μs | 1.3748 μs |  1.00 | Baseline        |    0.01 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 157.507 μs | 1.5411 μs | 2.2102 μs |  0.97 | Same            |    0.02 |      - |      50 B |        0.15 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Pipelined    | GET x200 pipelined   |   2.286 μs | 0.0684 μs | 0.1024 μs |  1.00 | Baseline        |    0.06 | 0.0098 |     289 B |        1.00 |
| Respire_Get_Pipelined          | GET x200 pipelined   |   1.955 μs | 0.0093 μs | 0.0134 μs |  0.86 | Faster          |    0.04 |      - |      49 B |        0.17 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   4.950 μs | 0.0370 μs | 0.0554 μs |  1.00 | Baseline        |    0.02 |      - |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   4.925 μs | 0.0267 μs | 0.0392 μs |  1.00 | Same            |    0.01 |      - |      52 B |        0.18 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HGet             | HGET                 | 180.935 μs | 0.5267 μs | 0.7883 μs |  1.00 | Baseline        |    0.01 |      - |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 173.545 μs | 0.5672 μs | 0.8134 μs |  0.96 | Same            |    0.01 |      - |      48 B |        0.09 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HSet             | HSET                 | 180.659 μs | 0.3655 μs | 0.5470 μs |  1.00 | Baseline        |    0.00 |      - |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 174.768 μs | 0.3284 μs | 0.4915 μs |  0.97 | Same            |    0.00 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Incr             | INCR                 | 179.330 μs | 0.3724 μs | 0.5574 μs |  1.00 | Baseline        |    0.00 |      - |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 174.010 μs | 0.4095 μs | 0.5873 μs |  0.97 | Same            |    0.00 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 349.645 μs | 3.2499 μs | 4.8644 μs |  1.00 | Baseline        |    0.02 |      - |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 346.121 μs | 1.1105 μs | 1.5926 μs |  0.99 | Same            |    0.01 |      - |     256 B |        0.34 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping             | PING                 | 177.616 μs | 0.8809 μs | 1.3185 μs |  1.00 | Baseline        |    0.01 |      - |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 171.589 μs | 0.4977 μs | 0.7449 μs |  0.97 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 159.780 μs | 1.1465 μs | 1.7160 μs |  1.00 | Baseline        |    0.02 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 157.017 μs | 1.1410 μs | 1.7079 μs |  0.98 | Same            |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 179.037 μs | 0.7626 μs | 1.1414 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 173.076 μs | 0.6948 μs | 1.0399 μs |  0.97 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 189.943 μs | 0.6812 μs | 0.9985 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 184.774 μs | 0.6777 μs | 1.0143 μs |  0.97 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 180.397 μs | 0.5558 μs | 0.8319 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 174.866 μs | 0.4415 μs | 0.6609 μs |  0.97 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 181.278 μs | 0.6079 μs | 0.9099 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 175.073 μs | 0.3983 μs | 0.5961 μs |  0.97 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 163.953 μs | 0.7571 μs | 1.1332 μs |  1.00 | Baseline        |    0.01 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 162.596 μs | 0.8670 μs | 1.2977 μs |  0.99 | Same            |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 346.651 μs | 1.3524 μs | 2.0242 μs |  1.00 | Baseline        |    0.01 |      - |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 343.021 μs | 1.0153 μs | 1.5196 μs |  0.99 | Same            |    0.01 |      - |     200 B |        0.31 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 179.263 μs | 0.6533 μs | 0.9778 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 173.178 μs | 0.5823 μs | 0.8350 μs |  0.97 | Same            |    0.01 |      - |         - |        0.00 |

## net8.0

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
INTEL XEON PLATINUM 8573C 2.30GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.400
  [Host]     : .NET 8.0.30 (8.0.30, 8.0.3026.36720), X64 RyuJIT x86-64-v4
  Job-IDGKZI : .NET 8.0.30 (8.0.30, 8.0.3026.36720), X64 RyuJIT x86-64-v4

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                         | Categories           | Mean       | Error     | StdDev    | Ratio | MannWhitney(5%) | RatioSD | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|----------:|----------:|------:|---------------- |--------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               |  91.879 μs | 1.0611 μs | 1.5881 μs |  1.00 | Baseline        |    0.02 |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               |  91.252 μs | 1.4727 μs | 2.1587 μs |  0.99 | Same            |    0.03 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Get              | GET                  |  95.367 μs | 1.0748 μs | 1.6087 μs |  1.00 | Baseline        |    0.02 |     504 B |        1.00 |
| Respire_Get                    | GET                  |  91.414 μs | 1.4063 μs | 2.0614 μs |  0.96 | Same            |    0.03 |      48 B |        0.10 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  |  80.808 μs | 1.1390 μs | 1.5968 μs |  1.00 | Baseline        |    0.03 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  |  77.710 μs | 1.5731 μs | 2.3545 μs |  0.96 | Same            |    0.03 |      50 B |        0.15 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Get_Pipelined    | GET x200 pipelined   |   1.609 μs | 0.0922 μs | 0.1351 μs |  1.01 | Baseline        |    0.12 |     289 B |        1.00 |
| Respire_Get_Pipelined          | GET x200 pipelined   |   1.332 μs | 0.0162 μs | 0.0238 μs |  0.83 | Faster          |    0.07 |      49 B |        0.17 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   2.977 μs | 0.0813 μs | 0.1166 μs |  1.00 | Baseline        |    0.05 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   2.687 μs | 0.0343 μs | 0.0458 μs |  0.90 | Faster          |    0.04 |      52 B |        0.18 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_HGet             | HGET                 |  96.588 μs | 1.5108 μs | 2.2612 μs |  1.00 | Baseline        |    0.03 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 |  91.658 μs | 1.0913 μs | 1.6334 μs |  0.95 | Same            |    0.03 |      48 B |        0.09 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_HSet             | HSET                 |  95.299 μs | 1.2078 μs | 1.7322 μs |  1.00 | Baseline        |    0.03 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 |  93.552 μs | 1.1879 μs | 1.7412 μs |  0.98 | Same            |    0.02 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Incr             | INCR                 |  92.231 μs | 0.8295 μs | 1.2159 μs |  1.00 | Baseline        |    0.02 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 |  89.276 μs | 1.8990 μs | 2.6622 μs |  0.97 | Same            |    0.03 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 178.748 μs | 1.3748 μs | 1.9717 μs |  1.00 | Baseline        |    0.02 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 173.426 μs | 2.3199 μs | 3.4723 μs |  0.97 | Same            |    0.02 |     256 B |        0.34 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Ping             | PING                 |  92.230 μs | 1.0442 μs | 1.5629 μs |  1.00 | Baseline        |    0.02 |     304 B |        1.00 |
| Respire_Ping                   | PING                 |  90.300 μs | 1.3311 μs | 1.9923 μs |  0.98 | Same            |    0.03 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential |  78.122 μs | 1.3534 μs | 2.0257 μs |  1.00 | Baseline        |    0.04 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential |  77.378 μs | 1.2408 μs | 1.7795 μs |  0.99 | Same            |    0.03 |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_SAdd             | SADD                 |  94.583 μs | 1.1396 μs | 1.5976 μs |  1.00 | Baseline        |    0.02 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 |  90.730 μs | 0.9859 μs | 1.4757 μs |  0.96 | Same            |    0.02 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 101.297 μs | 1.7287 μs | 2.4792 μs |  1.00 | Baseline        |    0.03 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             |  96.753 μs | 1.0439 μs | 1.5624 μs |  0.96 | Same            |    0.03 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Set_Small        | SET 13B              |  94.329 μs | 1.2411 μs | 1.8191 μs |  1.00 | Baseline        |    0.03 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              |  91.891 μs | 1.1815 μs | 1.7683 μs |  0.97 | Same            |    0.03 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              |  96.317 μs | 1.2266 μs | 1.8359 μs |  1.00 | Baseline        |    0.03 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              |  92.014 μs | 0.6735 μs | 0.9872 μs |  0.96 | Same            |    0.02 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  |  80.674 μs | 1.1368 μs | 1.7016 μs |  1.00 | Baseline        |    0.03 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  |  78.537 μs | 1.0761 μs | 1.6106 μs |  0.97 | Same            |    0.03 |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 175.138 μs | 2.0664 μs | 3.0289 μs |  1.00 | Baseline        |    0.02 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 167.751 μs | 1.7747 μs | 2.6013 μs |  0.96 | Same            |    0.02 |     200 B |        0.31 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            |  93.236 μs | 0.8771 μs | 1.3128 μs |  1.00 | Baseline        |    0.02 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            |  91.951 μs | 1.0752 μs | 1.5761 μs |  0.99 | Same            |    0.02 |         - |        0.00 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
