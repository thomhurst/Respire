---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-09 04:40 UTC from commit `c8d54c52c441`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31294890730) for logs and downloadable artifacts.
:::

StackExchange.Redis is the baseline. A ratio below `1.00` means Respire completed the operation faster.

## net10.0

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD EPYC 7763 2.45GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                         | Categories           | Mean       | Error       | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|------------:|----------:|------:|--------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 187.575 μs |  20.1142 μs | 1.1025 μs |  1.00 |    0.01 |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 188.258 μs |  48.3698 μs | 2.6513 μs |  1.00 |    0.01 |      32 B |        0.11 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Get              | GET                  | 190.130 μs |  14.7481 μs | 0.8084 μs |  1.00 |    0.01 |     504 B |        1.00 |
| Respire_Get                    | GET                  | 190.826 μs |  15.3520 μs | 0.8415 μs |  1.00 |    0.01 |      80 B |        0.16 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 174.045 μs |  45.3084 μs | 2.4835 μs |  1.00 |    0.02 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 174.169 μs |  17.2592 μs | 0.9460 μs |  1.00 |    0.01 |      50 B |        0.15 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.183 μs |   0.1870 μs | 0.0103 μs |  1.00 |    0.00 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   4.721 μs |   1.4764 μs | 0.0809 μs |  0.91 |    0.01 |      52 B |        0.18 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_HGet             | HGET                 | 192.119 μs |  16.1308 μs | 0.8842 μs |  1.00 |    0.01 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 190.781 μs |  11.8104 μs | 0.6474 μs |  0.99 |    0.00 |      80 B |        0.15 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_HSet             | HSET                 | 190.826 μs |  21.6152 μs | 1.1848 μs |  1.00 |    0.01 |     327 B |        1.00 |
| Respire_HSet                   | HSET                 | 192.089 μs |  20.1347 μs | 1.1037 μs |  1.01 |    0.01 |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Incr             | INCR                 | 190.156 μs |  19.3115 μs | 1.0585 μs |  1.00 |    0.01 |     295 B |        1.00 |
| Respire_Incr                   | INCR                 | 191.060 μs |  13.0848 μs | 0.7172 μs |  1.00 |    0.01 |      32 B |        0.11 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 377.764 μs | 119.2468 μs | 6.5363 μs |  1.00 |    0.02 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 367.861 μs |  43.3553 μs | 2.3765 μs |  0.97 |    0.02 |     256 B |        0.34 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Ping             | PING                 | 189.225 μs |  13.0788 μs | 0.7169 μs |  1.00 |    0.00 |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 188.618 μs |  36.6475 μs | 2.0088 μs |  1.00 |    0.01 |      32 B |        0.11 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 175.339 μs |  11.0827 μs | 0.6075 μs |  1.00 |    0.00 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 175.378 μs |  15.1730 μs | 0.8317 μs |  1.00 |    0.01 |       2 B |       0.008 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_SAdd             | SADD                 | 191.755 μs |  10.3422 μs | 0.5669 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 189.153 μs |  67.1387 μs | 3.6801 μs |  0.99 |    0.02 |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 202.277 μs |   6.0805 μs | 0.3333 μs |  1.00 |    0.00 |     310 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 201.966 μs |  12.3499 μs | 0.6769 μs |  1.00 |    0.00 |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 191.940 μs |  22.5943 μs | 1.2385 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 193.220 μs |  11.1547 μs | 0.6114 μs |  1.01 |    0.01 |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 191.935 μs |  18.5980 μs | 1.0194 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 193.226 μs |  21.6609 μs | 1.1873 μs |  1.01 |    0.01 |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 175.825 μs |  15.1969 μs | 0.8330 μs |  1.00 |    0.01 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 174.293 μs |  27.7811 μs | 1.5228 μs |  0.99 |    0.01 |       2 B |       0.008 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 367.253 μs |  22.7151 μs | 1.2451 μs |  1.00 |    0.00 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 366.645 μs |  62.1371 μs | 3.4059 μs |  1.00 |    0.01 |     200 B |        0.31 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 189.202 μs |  17.1370 μs | 0.9393 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 189.902 μs |   7.1404 μs | 0.3914 μs |  1.00 |    0.00 |      32 B |        0.10 |

## net8.0

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
INTEL XEON PLATINUM 8573C 2.95GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 8.0.29 (8.0.29, 8.0.2926.32403), X64 RyuJIT x86-64-v4
  ShortRun : .NET 8.0.29 (8.0.29, 8.0.2926.32403), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                         | Categories           | Mean       | Error      | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|-----------:|----------:|------:|--------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 101.543 μs | 17.4693 μs | 0.9576 μs |  1.00 |    0.01 |     295 B |        1.00 |
| Respire_Exists                 | EXISTS               |  97.855 μs | 56.3323 μs | 3.0878 μs |  0.96 |    0.03 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get              | GET                  | 100.367 μs | 16.7401 μs | 0.9176 μs |  1.00 |    0.01 |     504 B |        1.00 |
| Respire_Get                    | GET                  |  99.671 μs | 27.4827 μs | 1.5064 μs |  0.99 |    0.02 |      80 B |        0.16 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  |  87.028 μs |  6.0366 μs | 0.3309 μs |  1.00 |    0.00 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  |  85.407 μs | 16.3133 μs | 0.8942 μs |  0.98 |    0.01 |      50 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   3.064 μs |  2.2701 μs | 0.1244 μs |  1.00 |    0.05 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   2.853 μs |  0.5532 μs | 0.0303 μs |  0.93 |    0.03 |      52 B |        0.18 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HGet             | HGET                 | 101.220 μs | 39.2714 μs | 2.1526 μs |  1.00 |    0.03 |     517 B |        1.00 |
| Respire_HGet                   | HGET                 |  95.572 μs | 99.0163 μs | 5.4274 μs |  0.94 |    0.05 |      80 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HSet             | HSET                 | 105.159 μs |  5.9035 μs | 0.3236 μs |  1.00 |    0.00 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 101.511 μs | 14.9796 μs | 0.8211 μs |  0.97 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Incr             | INCR                 | 105.140 μs | 18.4616 μs | 1.0119 μs |  1.00 |    0.01 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 100.974 μs | 38.0369 μs | 2.0849 μs |  0.96 |    0.02 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 205.370 μs | 59.8902 μs | 3.2828 μs |  1.00 |    0.02 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 201.812 μs | 56.1550 μs | 3.0780 μs |  0.98 |    0.02 |     256 B |        0.34 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping             | PING                 | 102.648 μs | 29.0966 μs | 1.5949 μs |  1.00 |    0.02 |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 102.908 μs | 10.2049 μs | 0.5594 μs |  1.00 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential |  88.381 μs |  5.1499 μs | 0.2823 μs |  1.00 |    0.00 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential |  87.988 μs | 21.8515 μs | 1.1978 μs |  1.00 |    0.01 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SAdd             | SADD                 | 105.793 μs | 16.8972 μs | 0.9262 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 104.148 μs |  5.8304 μs | 0.3196 μs |  0.98 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 115.950 μs |  2.0428 μs | 0.1120 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 113.795 μs | 28.5305 μs | 1.5639 μs |  0.98 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 106.725 μs | 19.7932 μs | 1.0849 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 105.086 μs |  9.9714 μs | 0.5466 μs |  0.98 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 107.185 μs | 14.1171 μs | 0.7738 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 105.851 μs |  7.2789 μs | 0.3990 μs |  0.99 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  |  91.054 μs | 27.8625 μs | 1.5272 μs |  1.00 |    0.02 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  |  89.412 μs | 49.5820 μs | 2.7178 μs |  0.98 |    0.03 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 203.523 μs | 85.8476 μs | 4.7056 μs |  1.00 |    0.03 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 200.777 μs | 32.5207 μs | 1.7826 μs |  0.99 |    0.02 |     201 B |        0.31 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 105.846 μs |  9.7642 μs | 0.5352 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 103.574 μs | 47.9692 μs | 2.6294 μs |  0.98 |    0.02 |      32 B |        0.10 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
