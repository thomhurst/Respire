---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-09 03:35 UTC from commit `0c33806350ec`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31292576457) for logs and downloadable artifacts.
:::

StackExchange.Redis is the baseline. A ratio below `1.00` means Respire completed the operation faster.

## net10.0

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD EPYC 9V74 2.60GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                         | Categories           | Mean       | Error       | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|------------:|----------:|------:|--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               |  87.406 μs |  61.0349 μs | 3.3455 μs |  1.00 |    0.05 |      - |     295 B |        1.00 |
| Respire_Exists                 | EXISTS               |  89.919 μs |  19.7599 μs | 1.0831 μs |  1.03 |    0.04 |      - |      32 B |        0.11 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Get              | GET                  | 104.035 μs |  35.6813 μs | 1.9558 μs |  1.00 |    0.02 |      - |     504 B |        1.00 |
| Respire_Get                    | GET                  | 104.956 μs |  10.0605 μs | 0.5515 μs |  1.01 |    0.02 |      - |      80 B |        0.16 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  |  83.354 μs |  17.7152 μs | 0.9710 μs |  1.00 |    0.01 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  |  80.393 μs |  25.6838 μs | 1.4078 μs |  0.96 |    0.02 |      - |      50 B |        0.15 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   3.030 μs |   2.0894 μs | 0.1145 μs |  1.00 |    0.05 | 0.0146 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   2.979 μs |   0.2237 μs | 0.0123 μs |  0.98 |    0.03 |      - |      52 B |        0.18 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_HGet             | HGET                 | 106.451 μs |  74.1255 μs | 4.0631 μs |  1.00 |    0.05 |      - |     520 B |        1.00 |
| Respire_HGet                   | HGET                 |  98.177 μs |  88.2004 μs | 4.8346 μs |  0.92 |    0.05 |      - |      80 B |        0.15 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_HSet             | HSET                 |  94.416 μs |  44.8174 μs | 2.4566 μs |  1.00 |    0.03 |      - |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 102.879 μs |  53.5610 μs | 2.9359 μs |  1.09 |    0.04 |      - |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Incr             | INCR                 |  93.695 μs |  73.8503 μs | 4.0480 μs |  1.00 |    0.05 |      - |     296 B |        1.00 |
| Respire_Incr                   | INCR                 |  93.781 μs |  39.0464 μs | 2.1403 μs |  1.00 |    0.04 |      - |      32 B |        0.11 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 201.401 μs |  30.2349 μs | 1.6573 μs |  1.00 |    0.01 |      - |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 201.017 μs |  74.1861 μs | 4.0664 μs |  1.00 |    0.02 |      - |     256 B |        0.34 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Ping             | PING                 |  88.090 μs |  14.0416 μs | 0.7697 μs |  1.00 |    0.01 |      - |     303 B |        1.00 |
| Respire_Ping                   | PING                 |  94.858 μs |  40.1033 μs | 2.1982 μs |  1.08 |    0.02 |      - |      32 B |        0.11 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential |  80.468 μs |  19.7574 μs | 1.0830 μs |  1.00 |    0.02 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential |  77.182 μs |  74.6751 μs | 4.0932 μs |  0.96 |    0.05 |      - |       2 B |       0.008 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_SAdd             | SADD                 |  95.198 μs |  29.8475 μs | 1.6360 μs |  1.00 |    0.02 |      - |     311 B |        1.00 |
| Respire_SAdd                   | SADD                 |  97.358 μs |  15.1379 μs | 0.8298 μs |  1.02 |    0.02 |      - |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 114.408 μs |   9.0016 μs | 0.4934 μs |  1.00 |    0.01 |      - |     311 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 118.265 μs |   6.0095 μs | 0.3294 μs |  1.03 |    0.00 |      - |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              |  96.704 μs |  16.4110 μs | 0.8995 μs |  1.00 |    0.01 |      - |     310 B |        1.00 |
| Respire_Set_Small              | SET 13B              |  93.395 μs |  82.2919 μs | 4.5107 μs |  0.97 |    0.04 |      - |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              |  98.562 μs |  40.5636 μs | 2.2234 μs |  1.00 |    0.03 |      - |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 103.721 μs |  19.6428 μs | 1.0767 μs |  1.05 |    0.02 |      - |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  |  82.445 μs |  54.9066 μs | 3.0096 μs |  1.00 |    0.05 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  |  79.812 μs |   8.7365 μs | 0.4789 μs |  0.97 |    0.03 |      - |       2 B |       0.008 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 197.280 μs |  36.1867 μs | 1.9835 μs |  1.00 |    0.01 |      - |     647 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 188.071 μs | 102.7359 μs | 5.6313 μs |  0.95 |    0.03 |      - |     198 B |        0.31 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            |  97.021 μs |  59.3362 μs | 3.2524 μs |  1.00 |    0.04 |      - |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            |  96.101 μs |  27.5721 μs | 1.5113 μs |  0.99 |    0.03 |      - |      32 B |        0.10 |

## net8.0

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD EPYC 9V74 2.87GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 8.0.29 (8.0.29, 8.0.2926.32403), X64 RyuJIT x86-64-v3
  ShortRun : .NET 8.0.29 (8.0.29, 8.0.2926.32403), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                         | Categories           | Mean       | Error       | StdDev     | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|------------:|-----------:|------:|--------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 120.564 μs |  80.5804 μs |  4.4169 μs |  1.00 |    0.05 |     295 B |        1.00 |
| Respire_Exists                 | EXISTS               |  99.198 μs | 222.3745 μs | 12.1891 μs |  0.82 |    0.09 |      32 B |        0.11 |
|                                |                      |            |             |            |       |         |           |             |
| StackExchange_Get              | GET                  | 121.539 μs | 155.5743 μs |  8.5276 μs |  1.00 |    0.09 |     496 B |        1.00 |
| Respire_Get                    | GET                  | 104.938 μs |  51.9498 μs |  2.8475 μs |  0.87 |    0.06 |      80 B |        0.16 |
|                                |                      |            |             |            |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 104.270 μs |  56.1131 μs |  3.0757 μs |  1.00 |    0.04 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 102.836 μs |  70.1673 μs |  3.8461 μs |  0.99 |    0.04 |      50 B |        0.15 |
|                                |                      |            |             |            |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   4.065 μs |   3.1714 μs |  0.1738 μs |  1.00 |    0.05 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   4.043 μs |   0.9885 μs |  0.0542 μs |  1.00 |    0.04 |      52 B |        0.18 |
|                                |                      |            |             |            |       |         |           |             |
| StackExchange_HGet             | HGET                 | 128.473 μs |  84.7304 μs |  4.6444 μs |  1.00 |    0.04 |     518 B |        1.00 |
| Respire_HGet                   | HGET                 | 107.764 μs | 222.2256 μs | 12.1809 μs |  0.84 |    0.09 |      80 B |        0.15 |
|                                |                      |            |             |            |       |         |           |             |
| StackExchange_HSet             | HSET                 | 125.122 μs |  32.5426 μs |  1.7838 μs |  1.00 |    0.02 |     326 B |        1.00 |
| Respire_HSet                   | HSET                 | 123.111 μs |  45.6382 μs |  2.5016 μs |  0.98 |    0.02 |      32 B |        0.10 |
|                                |                      |            |             |            |       |         |           |             |
| StackExchange_Incr             | INCR                 | 120.870 μs |  28.8918 μs |  1.5837 μs |  1.00 |    0.02 |     293 B |        1.00 |
| Respire_Incr                   | INCR                 | 118.526 μs | 141.8303 μs |  7.7742 μs |  0.98 |    0.06 |      32 B |        0.11 |
|                                |                      |            |             |            |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 263.895 μs |  26.2755 μs |  1.4403 μs |  1.00 |    0.01 |     756 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 257.653 μs |  65.6790 μs |  3.6001 μs |  0.98 |    0.01 |     252 B |        0.33 |
|                                |                      |            |             |            |       |         |           |             |
| StackExchange_Ping             | PING                 | 117.288 μs | 141.1864 μs |  7.7389 μs |  1.00 |    0.08 |     301 B |        1.00 |
| Respire_Ping                   | PING                 | 111.750 μs |  16.5040 μs |  0.9046 μs |  0.96 |    0.05 |      32 B |        0.11 |
|                                |                      |            |             |            |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 107.982 μs |  11.5195 μs |  0.6314 μs |  1.00 |    0.01 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential |  97.206 μs |  47.8902 μs |  2.6250 μs |  0.90 |    0.02 |       2 B |       0.008 |
|                                |                      |            |             |            |       |         |           |             |
| StackExchange_SAdd             | SADD                 | 123.632 μs |  23.8172 μs |  1.3055 μs |  1.00 |    0.01 |     308 B |        1.00 |
| Respire_SAdd                   | SADD                 | 108.137 μs | 114.2171 μs |  6.2606 μs |  0.87 |    0.04 |      32 B |        0.10 |
|                                |                      |            |             |            |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 149.602 μs |  19.6996 μs |  1.0798 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 151.298 μs |   3.7467 μs |  0.2054 μs |  1.01 |    0.01 |      32 B |        0.10 |
|                                |                      |            |             |            |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 122.562 μs |  41.4160 μs |  2.2701 μs |  1.00 |    0.02 |     305 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 109.157 μs | 109.0840 μs |  5.9793 μs |  0.89 |    0.04 |      32 B |        0.10 |
|                                |                      |            |             |            |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 132.392 μs |  47.0857 μs |  2.5809 μs |  1.00 |    0.02 |     308 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 111.592 μs | 116.5971 μs |  6.3911 μs |  0.84 |    0.04 |      32 B |        0.10 |
|                                |                      |            |             |            |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 111.549 μs |  31.2445 μs |  1.7126 μs |  1.00 |    0.02 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 110.010 μs |  64.2471 μs |  3.5216 μs |  0.99 |    0.03 |       2 B |       0.008 |
|                                |                      |            |             |            |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 252.772 μs | 223.5477 μs | 12.2534 μs |  1.00 |    0.06 |     643 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 231.369 μs |  25.1110 μs |  1.3764 μs |  0.92 |    0.04 |     199 B |        0.31 |
|                                |                      |            |             |            |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 119.799 μs | 147.6054 μs |  8.0907 μs |  1.00 |    0.08 |     310 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 119.164 μs |  41.7751 μs |  2.2898 μs |  1.00 |    0.06 |      32 B |        0.10 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
