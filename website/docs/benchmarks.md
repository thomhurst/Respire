---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-09 11:07 UTC from commit `e6edf94532c1`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31309794387) for logs and downloadable artifacts.
:::

StackExchange.Redis is the baseline. A ratio below `1.00` means Respire completed the operation faster.

## net10.0

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
INTEL XEON PLATINUM 8573C 3.05GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                         | Categories           | Mean       | Error      | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|-----------:|----------:|------:|--------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               |  97.709 μs | 99.6092 μs | 5.4599 μs |  1.00 |    0.07 |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               |  93.860 μs | 55.6862 μs | 3.0523 μs |  0.96 |    0.05 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get              | GET                  |  94.452 μs | 13.9630 μs | 0.7654 μs |  1.00 |    0.01 |     504 B |        1.00 |
| Respire_Get                    | GET                  |  97.585 μs | 25.0884 μs | 1.3752 μs |  1.03 |    0.01 |      80 B |        0.16 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  |  81.307 μs | 27.7766 μs | 1.5225 μs |  1.00 |    0.02 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  |  80.215 μs | 55.5492 μs | 3.0448 μs |  0.99 |    0.04 |      50 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   3.006 μs |  2.2648 μs | 0.1241 μs |  1.00 |    0.05 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   2.646 μs |  0.5280 μs | 0.0289 μs |  0.88 |    0.03 |      52 B |        0.18 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HGet             | HGET                 |  99.119 μs | 31.3798 μs | 1.7200 μs |  1.00 |    0.02 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 |  96.263 μs | 17.4273 μs | 0.9552 μs |  0.97 |    0.02 |      80 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HSet             | HSET                 |  97.649 μs | 25.9076 μs | 1.4201 μs |  1.00 |    0.02 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 |  93.857 μs | 28.4334 μs | 1.5585 μs |  0.96 |    0.02 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Incr             | INCR                 |  94.728 μs | 22.2512 μs | 1.2197 μs |  1.00 |    0.02 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 |  92.443 μs | 11.1922 μs | 0.6135 μs |  0.98 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 183.250 μs | 40.9482 μs | 2.2445 μs |  1.00 |    0.01 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 183.048 μs | 14.5675 μs | 0.7985 μs |  1.00 |    0.01 |     256 B |        0.34 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping             | PING                 |  95.266 μs | 28.1065 μs | 1.5406 μs |  1.00 |    0.02 |     304 B |        1.00 |
| Respire_Ping                   | PING                 |  95.633 μs | 23.9234 μs | 1.3113 μs |  1.00 |    0.02 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential |  79.217 μs | 27.4417 μs | 1.5042 μs |  1.00 |    0.02 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential |  73.264 μs | 50.6668 μs | 2.7772 μs |  0.93 |    0.03 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SAdd             | SADD                 |  97.613 μs | 28.6309 μs | 1.5694 μs |  1.00 |    0.02 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 |  93.930 μs | 17.8497 μs | 0.9784 μs |  0.96 |    0.02 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 101.421 μs | 12.8604 μs | 0.7049 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 101.206 μs | 14.0912 μs | 0.7724 μs |  1.00 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              |  95.827 μs | 12.9138 μs | 0.7078 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              |  97.268 μs | 72.5010 μs | 3.9740 μs |  1.02 |    0.04 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              |  99.134 μs |  9.7051 μs | 0.5320 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              |  97.687 μs | 51.5576 μs | 2.8260 μs |  0.99 |    0.03 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  |  82.801 μs | 49.6679 μs | 2.7225 μs |  1.00 |    0.04 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  |  80.254 μs | 23.7645 μs | 1.3026 μs |  0.97 |    0.03 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 177.750 μs | 18.7446 μs | 1.0275 μs |  1.00 |    0.01 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 179.389 μs | 62.0086 μs | 3.3989 μs |  1.01 |    0.02 |     200 B |        0.31 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            |  97.562 μs | 44.6568 μs | 2.4478 μs |  1.00 |    0.03 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            |  94.659 μs | 14.2290 μs | 0.7799 μs |  0.97 |    0.02 |      32 B |        0.10 |

## net8.0

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD EPYC 7763 2.45GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 8.0.29 (8.0.29, 8.0.2926.32403), X64 RyuJIT x86-64-v3
  ShortRun : .NET 8.0.29 (8.0.29, 8.0.2926.32403), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                         | Categories           | Mean       | Error      | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|-----------:|----------:|------:|--------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 186.439 μs | 16.9467 μs | 0.9289 μs |  1.00 |    0.01 |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 187.945 μs | 29.3574 μs | 1.6092 μs |  1.01 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get              | GET                  | 188.539 μs | 37.5693 μs | 2.0593 μs |  1.00 |    0.01 |     503 B |        1.00 |
| Respire_Get                    | GET                  | 186.522 μs | 22.4729 μs | 1.2318 μs |  0.99 |    0.01 |      80 B |        0.16 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 172.999 μs | 49.8900 μs | 2.7346 μs |  1.00 |    0.02 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 173.342 μs | 12.3683 μs | 0.6779 μs |  1.00 |    0.01 |      50 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.356 μs |  1.6116 μs | 0.0883 μs |  1.00 |    0.02 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   4.949 μs |  0.5309 μs | 0.0291 μs |  0.92 |    0.01 |      52 B |        0.18 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HGet             | HGET                 | 190.630 μs | 54.0180 μs | 2.9609 μs |  1.00 |    0.02 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 189.376 μs |  2.6963 μs | 0.1478 μs |  0.99 |    0.01 |      80 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HSet             | HSET                 | 185.089 μs | 17.4025 μs | 0.9539 μs |  1.00 |    0.01 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 187.556 μs | 37.4783 μs | 2.0543 μs |  1.01 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Incr             | INCR                 | 185.608 μs | 22.6372 μs | 1.2408 μs |  1.00 |    0.01 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 185.953 μs | 14.5865 μs | 0.7995 μs |  1.00 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 371.776 μs | 47.7574 μs | 2.6177 μs |  1.00 |    0.01 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 364.488 μs | 78.7500 μs | 4.3166 μs |  0.98 |    0.01 |     256 B |        0.34 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping             | PING                 | 182.692 μs | 31.4653 μs | 1.7247 μs |  1.00 |    0.01 |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 183.614 μs | 45.9384 μs | 2.5180 μs |  1.01 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 169.437 μs | 27.8750 μs | 1.5279 μs |  1.00 |    0.01 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 167.840 μs | 53.1003 μs | 2.9106 μs |  0.99 |    0.02 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SAdd             | SADD                 | 185.478 μs | 17.6273 μs | 0.9662 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 187.357 μs | 41.7956 μs | 2.2910 μs |  1.01 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 196.223 μs | 15.4544 μs | 0.8471 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 197.790 μs | 34.0692 μs | 1.8674 μs |  1.01 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 187.923 μs |  8.4736 μs | 0.4645 μs |  1.00 |    0.00 |     310 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 189.439 μs | 57.8669 μs | 3.1719 μs |  1.01 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 189.438 μs | 31.1040 μs | 1.7049 μs |  1.00 |    0.01 |     309 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 188.441 μs |  5.8077 μs | 0.3183 μs |  0.99 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 175.724 μs | 47.7249 μs | 2.6160 μs |  1.00 |    0.02 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 171.960 μs | 29.3147 μs | 1.6068 μs |  0.98 |    0.01 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 365.083 μs | 38.2829 μs | 2.0984 μs |  1.00 |    0.01 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 358.288 μs | 86.7855 μs | 4.7570 μs |  0.98 |    0.01 |     200 B |        0.31 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 186.886 μs | 18.7372 μs | 1.0270 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 182.469 μs | 38.8027 μs | 2.1269 μs |  0.98 |    0.01 |      32 B |        0.10 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
