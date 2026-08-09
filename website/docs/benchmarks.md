---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-09 08:16 UTC from commit `e1ff154194ae`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31302929591) for logs and downloadable artifacts.
:::

StackExchange.Redis is the baseline. A ratio below `1.00` means Respire completed the operation faster.

## net10.0

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD EPYC 9V74 2.60GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                         | Categories           | Mean       | Error      | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|-----------:|----------:|------:|--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 138.673 μs | 19.8782 μs | 1.0896 μs |  1.00 |    0.01 |      - |     292 B |        1.00 |
| Respire_Exists                 | EXISTS               | 138.199 μs |  4.4653 μs | 0.2448 μs |  1.00 |    0.01 |      - |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Get              | GET                  | 142.784 μs | 58.3217 μs | 3.1968 μs |  1.00 |    0.03 |      - |     499 B |        1.00 |
| Respire_Get                    | GET                  | 139.558 μs | 54.0506 μs | 2.9627 μs |  0.98 |    0.03 |      - |      80 B |        0.16 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 120.106 μs | 48.9160 μs | 2.6813 μs |  1.00 |    0.03 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 116.807 μs | 11.8316 μs | 0.6485 μs |  0.97 |    0.02 |      - |      50 B |        0.15 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   4.666 μs |  0.9975 μs | 0.0547 μs |  1.00 |    0.01 | 0.0098 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   4.373 μs |  1.1437 μs | 0.0627 μs |  0.94 |    0.02 |      - |      52 B |        0.18 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_HGet             | HGET                 | 150.679 μs | 14.1904 μs | 0.7778 μs |  1.00 |    0.01 |      - |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 144.035 μs | 15.8896 μs | 0.8710 μs |  0.96 |    0.01 |      - |      80 B |        0.15 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_HSet             | HSET                 | 142.407 μs | 28.9646 μs | 1.5876 μs |  1.00 |    0.01 |      - |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 142.881 μs | 20.5734 μs | 1.1277 μs |  1.00 |    0.01 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Incr             | INCR                 | 141.221 μs | 18.4059 μs | 1.0089 μs |  1.00 |    0.01 |      - |     295 B |        1.00 |
| Respire_Incr                   | INCR                 | 139.768 μs | 46.8535 μs | 2.5682 μs |  0.99 |    0.02 |      - |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 291.916 μs | 43.4766 μs | 2.3831 μs |  1.00 |    0.01 |      - |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 283.614 μs | 22.0081 μs | 1.2063 μs |  0.97 |    0.01 |      - |     256 B |        0.34 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Ping             | PING                 | 137.510 μs | 13.1475 μs | 0.7207 μs |  1.00 |    0.01 |      - |     303 B |        1.00 |
| Respire_Ping                   | PING                 | 131.547 μs | 77.7353 μs | 4.2609 μs |  0.96 |    0.03 |      - |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 114.982 μs | 23.3843 μs | 1.2818 μs |  1.00 |    0.01 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 114.218 μs |  8.6628 μs | 0.4748 μs |  0.99 |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 132.812 μs | 88.7265 μs | 4.8634 μs |  1.00 |    0.05 |      - |     311 B |        1.00 |
| Respire_SAdd                   | SADD                 | 133.740 μs | 91.4483 μs | 5.0126 μs |  1.01 |    0.05 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 157.265 μs |  1.4369 μs | 0.0788 μs |  1.00 |    0.00 |      - |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 159.404 μs | 18.9177 μs | 1.0369 μs |  1.01 |    0.01 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 143.393 μs | 27.5690 μs | 1.5111 μs |  1.00 |    0.01 |      - |     311 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 141.597 μs | 25.3727 μs | 1.3908 μs |  0.99 |    0.01 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 146.516 μs | 20.5196 μs | 1.1247 μs |  1.00 |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 145.120 μs | 16.8675 μs | 0.9246 μs |  0.99 |    0.01 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 119.108 μs | 17.8165 μs | 0.9766 μs |  1.00 |    0.01 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 119.096 μs | 36.3027 μs | 1.9899 μs |  1.00 |    0.02 |      - |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 284.235 μs | 51.5030 μs | 2.8231 μs |  1.00 |    0.01 |      - |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 274.179 μs | 27.5462 μs | 1.5099 μs |  0.96 |    0.01 |      - |     200 B |        0.31 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 139.080 μs | 16.9567 μs | 0.9295 μs |  1.00 |    0.01 |      - |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 139.867 μs | 57.5933 μs | 3.1569 μs |  1.01 |    0.02 |      - |      32 B |        0.10 |

## net8.0

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD EPYC 9V45 2.60GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 8.0.29 (8.0.29, 8.0.2926.32403), X64 RyuJIT x86-64-v4
  ShortRun : .NET 8.0.29 (8.0.29, 8.0.2926.32403), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                         | Categories           | Mean       | Error       | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|------------:|----------:|------:|--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               |  59.878 μs |  65.8511 μs | 3.6095 μs |  1.00 |    0.07 |      - |     267 B |        1.00 |
| Respire_Exists                 | EXISTS               |  54.262 μs |  51.5254 μs | 2.8243 μs |  0.91 |    0.06 |      - |      32 B |        0.12 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Get              | GET                  |  55.610 μs |  35.9865 μs | 1.9725 μs |  1.00 |    0.04 |      - |     478 B |        1.00 |
| Respire_Get                    | GET                  |  52.745 μs |  23.4717 μs | 1.2866 μs |  0.95 |    0.04 |      - |      80 B |        0.17 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  |  53.727 μs |  21.4095 μs | 1.1735 μs |  1.00 |    0.03 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  |  53.363 μs |  30.8767 μs | 1.6925 μs |  0.99 |    0.03 |      - |      50 B |        0.15 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   2.320 μs |   1.0239 μs | 0.0561 μs |  1.00 |    0.03 | 0.0146 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   2.170 μs |   0.4363 μs | 0.0239 μs |  0.94 |    0.02 |      - |      52 B |        0.18 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_HGet             | HGET                 |  58.261 μs | 100.9871 μs | 5.5354 μs |  1.01 |    0.12 |      - |     488 B |        1.00 |
| Respire_HGet                   | HGET                 |  57.342 μs |  11.2857 μs | 0.6186 μs |  0.99 |    0.09 |      - |      80 B |        0.16 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_HSet             | HSET                 |  61.164 μs |  16.7006 μs | 0.9154 μs |  1.00 |    0.02 |      - |     310 B |        1.00 |
| Respire_HSet                   | HSET                 |  57.243 μs |  24.4291 μs | 1.3390 μs |  0.94 |    0.02 |      - |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Incr             | INCR                 |  61.238 μs |   3.3104 μs | 0.1815 μs |  1.00 |    0.00 |      - |     284 B |        1.00 |
| Respire_Incr                   | INCR                 |  56.395 μs |  29.2030 μs | 1.6007 μs |  0.92 |    0.02 |      - |      32 B |        0.11 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 114.660 μs |  59.6598 μs | 3.2702 μs |  1.00 |    0.03 |      - |     756 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 112.025 μs |  20.3243 μs | 1.1140 μs |  0.98 |    0.03 |      - |     253 B |        0.33 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Ping             | PING                 |  60.341 μs |  20.7143 μs | 1.1354 μs |  1.00 |    0.02 |      - |     293 B |        1.00 |
| Respire_Ping                   | PING                 |  56.306 μs |  20.7920 μs | 1.1397 μs |  0.93 |    0.02 |      - |      32 B |        0.11 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential |  52.558 μs |  17.4294 μs | 0.9554 μs |  1.00 |    0.02 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential |  52.767 μs |  26.6668 μs | 1.4617 μs |  1.00 |    0.03 |      - |       2 B |       0.008 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_SAdd             | SADD                 |  60.455 μs |   9.5598 μs | 0.5240 μs |  1.00 |    0.01 |      - |     287 B |        1.00 |
| Respire_SAdd                   | SADD                 |  55.952 μs |  14.6067 μs | 0.8006 μs |  0.93 |    0.01 |      - |      32 B |        0.11 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             |  67.084 μs |  39.5244 μs | 2.1665 μs |  1.00 |    0.04 |      - |     305 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             |  58.477 μs |  31.4071 μs | 1.7215 μs |  0.87 |    0.03 |      - |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              |  61.854 μs |  28.2667 μs | 1.5494 μs |  1.00 |    0.03 |      - |     301 B |        1.00 |
| Respire_Set_Small              | SET 13B              |  57.443 μs |  31.4172 μs | 1.7221 μs |  0.93 |    0.03 |      - |      32 B |        0.11 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              |  63.309 μs |  32.8629 μs | 1.8013 μs |  1.00 |    0.04 |      - |     308 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              |  58.045 μs |  34.4486 μs | 1.8882 μs |  0.92 |    0.03 |      - |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  |  53.929 μs |  18.7719 μs | 1.0290 μs |  1.00 |    0.02 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  |  52.422 μs |   7.8536 μs | 0.4305 μs |  0.97 |    0.02 |      - |       2 B |       0.008 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 113.533 μs |  57.9841 μs | 3.1783 μs |  1.00 |    0.03 |      - |     632 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 114.702 μs |  53.8449 μs | 2.9514 μs |  1.01 |    0.03 |      - |     192 B |        0.30 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            |  58.498 μs |  47.0038 μs | 2.5764 μs |  1.00 |    0.05 |      - |     301 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            |  55.304 μs |  26.1631 μs | 1.4341 μs |  0.95 |    0.04 |      - |      32 B |        0.11 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
