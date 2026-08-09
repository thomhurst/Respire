---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-09 01:01 UTC from commit `ece7bd9c6bc4`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31287241060) for logs and downloadable artifacts.
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
| Method                         | Categories           | Mean       | Error      | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|-----------:|----------:|------:|--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 176.580 μs |   3.474 μs | 0.1904 μs |  1.00 |    0.00 |      - |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 172.568 μs | 140.352 μs | 7.6931 μs |  0.98 |    0.04 |      - |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Get              | GET                  | 177.978 μs |  32.458 μs | 1.7791 μs |  1.00 |    0.01 |      - |     504 B |        1.00 |
| Respire_Get                    | GET                  | 178.287 μs |   5.824 μs | 0.3192 μs |  1.00 |    0.01 |      - |      80 B |        0.16 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 158.683 μs |  43.267 μs | 2.3716 μs |  1.00 |    0.02 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 159.091 μs |   7.965 μs | 0.4366 μs |  1.00 |    0.01 |      - |      50 B |        0.15 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   4.793 μs |   1.031 μs | 0.0565 μs |  1.00 |    0.01 | 0.0098 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   4.479 μs |   1.845 μs | 0.1011 μs |  0.93 |    0.02 |      - |      52 B |        0.18 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_HGet             | HGET                 | 178.090 μs |  29.691 μs | 1.6275 μs |  1.00 |    0.01 |      - |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 177.390 μs |   7.395 μs | 0.4054 μs |  1.00 |    0.01 |      - |      80 B |        0.15 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_HSet             | HSET                 | 178.989 μs |   5.467 μs | 0.2997 μs |  1.00 |    0.00 |      - |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 178.572 μs |   5.938 μs | 0.3255 μs |  1.00 |    0.00 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Incr             | INCR                 | 176.668 μs |  19.340 μs | 1.0601 μs |  1.00 |    0.01 |      - |     295 B |        1.00 |
| Respire_Incr                   | INCR                 | 175.630 μs |  17.482 μs | 0.9582 μs |  0.99 |    0.01 |      - |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 344.175 μs |  19.110 μs | 1.0475 μs |  1.00 |    0.00 |      - |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 343.212 μs |  46.694 μs | 2.5594 μs |  1.00 |    0.01 |      - |     256 B |        0.34 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Ping             | PING                 | 175.019 μs |  21.306 μs | 1.1679 μs |  1.00 |    0.01 |      - |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 176.529 μs |  19.171 μs | 1.0508 μs |  1.01 |    0.01 |      - |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 157.158 μs |  25.179 μs | 1.3802 μs |  1.00 |    0.01 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 151.712 μs |  51.511 μs | 2.8235 μs |  0.97 |    0.02 |      - |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 172.380 μs |  27.143 μs | 1.4878 μs |  1.00 |    0.01 |      - |     309 B |        1.00 |
| Respire_SAdd                   | SADD                 | 176.500 μs |  36.590 μs | 2.0056 μs |  1.02 |    0.01 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 186.795 μs |  14.033 μs | 0.7692 μs |  1.00 |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 188.802 μs |   7.125 μs | 0.3905 μs |  1.01 |    0.00 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 177.607 μs |  11.008 μs | 0.6034 μs |  1.00 |    0.00 |      - |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 179.058 μs |  33.679 μs | 1.8461 μs |  1.01 |    0.01 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 179.325 μs |   7.177 μs | 0.3934 μs |  1.00 |    0.00 |      - |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 179.107 μs |  15.191 μs | 0.8326 μs |  1.00 |    0.00 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 160.145 μs |  30.263 μs | 1.6588 μs |  1.00 |    0.01 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 158.798 μs |   5.105 μs | 0.2798 μs |  0.99 |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 343.245 μs |  71.088 μs | 3.8966 μs |  1.00 |    0.01 |      - |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 339.280 μs |  58.927 μs | 3.2300 μs |  0.99 |    0.01 |      - |     200 B |        0.31 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 177.569 μs |  16.048 μs | 0.8797 μs |  1.00 |    0.01 |      - |     311 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 177.516 μs |  15.702 μs | 0.8607 μs |  1.00 |    0.01 |      - |      32 B |        0.10 |

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
| Method                         | Categories           | Mean       | Error       | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|------------:|----------:|------:|--------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 178.767 μs |  21.4008 μs | 1.1731 μs |  1.00 |    0.01 |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 179.122 μs |  17.6042 μs | 0.9649 μs |  1.00 |    0.01 |      32 B |        0.11 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Get              | GET                  | 183.391 μs |   5.7481 μs | 0.3151 μs |  1.00 |    0.00 |     504 B |        1.00 |
| Respire_Get                    | GET                  | 182.257 μs |  15.6864 μs | 0.8598 μs |  0.99 |    0.00 |      80 B |        0.16 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 164.658 μs |  17.1812 μs | 0.9418 μs |  1.00 |    0.01 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 163.333 μs |  20.3837 μs | 1.1173 μs |  0.99 |    0.01 |      50 B |        0.15 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.038 μs |   0.7283 μs | 0.0399 μs |  1.00 |    0.01 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   4.524 μs |   0.8716 μs | 0.0478 μs |  0.90 |    0.01 |      52 B |        0.18 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_HGet             | HGET                 | 184.191 μs |  15.7084 μs | 0.8610 μs |  1.00 |    0.01 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 181.537 μs |   6.8886 μs | 0.3776 μs |  0.99 |    0.00 |      80 B |        0.15 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_HSet             | HSET                 | 183.412 μs |  48.5245 μs | 2.6598 μs |  1.00 |    0.02 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 180.494 μs |  22.9425 μs | 1.2576 μs |  0.98 |    0.01 |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Incr             | INCR                 | 181.207 μs |  31.0963 μs | 1.7045 μs |  1.00 |    0.01 |     294 B |        1.00 |
| Respire_Incr                   | INCR                 | 179.783 μs |  36.6246 μs | 2.0075 μs |  0.99 |    0.01 |      32 B |        0.11 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 358.163 μs |  31.4814 μs | 1.7256 μs |  1.00 |    0.01 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 351.716 μs |  87.7763 μs | 4.8113 μs |  0.98 |    0.01 |     256 B |        0.34 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Ping             | PING                 | 177.112 μs |  27.5819 μs | 1.5119 μs |  1.00 |    0.01 |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 177.805 μs |  13.4013 μs | 0.7346 μs |  1.00 |    0.01 |      32 B |        0.11 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 163.595 μs |  29.0430 μs | 1.5919 μs |  1.00 |    0.01 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 162.991 μs |  18.5288 μs | 1.0156 μs |  1.00 |    0.01 |       2 B |       0.008 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_SAdd             | SADD                 | 179.853 μs |   5.8849 μs | 0.3226 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 179.531 μs |  24.2379 μs | 1.3286 μs |  1.00 |    0.01 |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 192.383 μs |  16.7042 μs | 0.9156 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 191.736 μs |  14.2950 μs | 0.7836 μs |  1.00 |    0.01 |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 182.485 μs |  13.5887 μs | 0.7448 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 182.570 μs |  18.5438 μs | 1.0164 μs |  1.00 |    0.01 |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 183.276 μs |   1.9687 μs | 0.1079 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 182.344 μs |  11.1622 μs | 0.6118 μs |  0.99 |    0.00 |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 166.078 μs |  29.6382 μs | 1.6246 μs |  1.00 |    0.01 |     250 B |        1.00 |
| Respire_Set_SteadyState        | SET x100 sequential  | 165.807 μs |   5.1639 μs | 0.2831 μs |  1.00 |    0.01 |       3 B |        0.01 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 351.803 μs | 119.1550 μs | 6.5313 μs |  1.00 |    0.02 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 348.178 μs |  36.3710 μs | 1.9936 μs |  0.99 |    0.02 |     200 B |        0.31 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 180.004 μs |   8.1320 μs | 0.4457 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 180.246 μs |  45.3369 μs | 2.4851 μs |  1.00 |    0.01 |      32 B |        0.10 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
