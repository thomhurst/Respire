---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-09 22:16 UTC from commit `8862493bcfe2`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31338785185) for logs and downloadable artifacts.
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
| Method                         | Categories           | Mean       | Error       | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|------------:|----------:|------:|--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 128.935 μs |  28.4721 μs | 1.5607 μs |  1.00 |    0.01 |      - |     294 B |        1.00 |
| Respire_Exists                 | EXISTS               | 127.625 μs |  92.9957 μs | 5.0974 μs |  0.99 |    0.04 |      - |      32 B |        0.11 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Get              | GET                  | 132.607 μs |  43.8091 μs | 2.4013 μs |  1.00 |    0.02 |      - |     500 B |        1.00 |
| Respire_Get                    | GET                  | 124.304 μs | 140.2344 μs | 7.6867 μs |  0.94 |    0.05 |      - |      80 B |        0.16 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 109.846 μs |  38.7529 μs | 2.1242 μs |  1.00 |    0.02 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 111.588 μs |   6.8277 μs | 0.3742 μs |  1.02 |    0.02 |      - |      50 B |        0.15 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   4.412 μs |   1.5858 μs | 0.0869 μs |  1.00 |    0.02 | 0.0098 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   4.243 μs |   0.7842 μs | 0.0430 μs |  0.96 |    0.02 |      - |      52 B |        0.18 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_HGet             | HGET                 | 131.658 μs | 109.5377 μs | 6.0041 μs |  1.00 |    0.06 |      - |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 132.110 μs |  32.6174 μs | 1.7879 μs |  1.00 |    0.04 |      - |      80 B |        0.15 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_HSet             | HSET                 | 129.819 μs |  54.5115 μs | 2.9880 μs |  1.00 |    0.03 |      - |     327 B |        1.00 |
| Respire_HSet                   | HSET                 | 131.848 μs |  45.7635 μs | 2.5085 μs |  1.02 |    0.03 |      - |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Incr             | INCR                 | 130.183 μs |  10.9913 μs | 0.6025 μs |  1.00 |    0.01 |      - |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 124.884 μs |  62.7547 μs | 3.4398 μs |  0.96 |    0.02 |      - |      32 B |        0.11 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 269.049 μs |  61.3907 μs | 3.3650 μs |  1.00 |    0.02 |      - |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 262.106 μs |  52.6537 μs | 2.8861 μs |  0.97 |    0.01 |      - |     254 B |        0.33 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Ping             | PING                 | 130.643 μs |  30.2248 μs | 1.6567 μs |  1.00 |    0.02 |      - |     303 B |        1.00 |
| Respire_Ping                   | PING                 | 128.103 μs | 101.7113 μs | 5.5751 μs |  0.98 |    0.04 |      - |      32 B |        0.11 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 107.786 μs |  29.9965 μs | 1.6442 μs |  1.00 |    0.02 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 106.636 μs |  19.3680 μs | 1.0616 μs |  0.99 |    0.02 |      - |       2 B |       0.008 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 129.692 μs |  26.7806 μs | 1.4679 μs |  1.00 |    0.01 |      - |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 130.955 μs |  23.3617 μs | 1.2805 μs |  1.01 |    0.01 |      - |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 148.510 μs |   2.6871 μs | 0.1473 μs |  1.00 |    0.00 |      - |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 151.449 μs |  19.8154 μs | 1.0861 μs |  1.02 |    0.01 |      - |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 126.551 μs |  78.4347 μs | 4.2993 μs |  1.00 |    0.04 |      - |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 127.274 μs |  82.3710 μs | 4.5150 μs |  1.01 |    0.04 |      - |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 133.106 μs |  18.9244 μs | 1.0373 μs |  1.00 |    0.01 |      - |     311 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 132.396 μs |  57.4903 μs | 3.1512 μs |  0.99 |    0.02 |      - |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 112.608 μs |  31.2177 μs | 1.7111 μs |  1.00 |    0.02 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 111.882 μs |  15.0797 μs | 0.8266 μs |  0.99 |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 266.303 μs |  23.7937 μs | 1.3042 μs |  1.00 |    0.01 |      - |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 256.526 μs |  95.4206 μs | 5.2303 μs |  0.96 |    0.02 |      - |     200 B |        0.31 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 127.174 μs |  44.3303 μs | 2.4299 μs |  1.00 |    0.02 |      - |     311 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 128.029 μs |  21.4958 μs | 1.1783 μs |  1.01 |    0.02 |      - |      32 B |        0.10 |

## net8.0

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD EPYC 9V74 2.60GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 8.0.29 (8.0.29, 8.0.2926.32403), X64 RyuJIT x86-64-v3
  ShortRun : .NET 8.0.29 (8.0.29, 8.0.2926.32403), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                         | Categories           | Mean       | Error       | StdDev     | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|------------:|-----------:|------:|--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 122.472 μs |  51.4023 μs |  2.8175 μs |  1.00 |    0.03 |      - |     293 B |        1.00 |
| Respire_Exists                 | EXISTS               | 120.846 μs | 144.3237 μs |  7.9109 μs |  0.99 |    0.06 |      - |      32 B |        0.11 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Get              | GET                  | 121.565 μs | 201.2379 μs | 11.0305 μs |  1.01 |    0.11 |      - |     496 B |        1.00 |
| Respire_Get                    | GET                  | 108.606 μs | 156.0505 μs |  8.5537 μs |  0.90 |    0.10 |      - |      80 B |        0.16 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 105.407 μs |  42.7952 μs |  2.3457 μs |  1.00 |    0.03 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 108.048 μs |  40.5411 μs |  2.2222 μs |  1.03 |    0.03 |      - |      50 B |        0.15 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   4.334 μs |   1.5429 μs |  0.0846 μs |  1.00 |    0.02 | 0.0098 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   4.324 μs |   0.2178 μs |  0.0119 μs |  1.00 |    0.02 |      - |      52 B |        0.18 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_HGet             | HGET                 | 127.147 μs | 123.9857 μs |  6.7961 μs |  1.00 |    0.07 |      - |     512 B |        1.00 |
| Respire_HGet                   | HGET                 | 119.947 μs |  81.7889 μs |  4.4831 μs |  0.95 |    0.05 |      - |      80 B |        0.16 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_HSet             | HSET                 | 129.067 μs |  34.4702 μs |  1.8894 μs |  1.00 |    0.02 |      - |     326 B |        1.00 |
| Respire_HSet                   | HSET                 | 114.076 μs |  85.2312 μs |  4.6718 μs |  0.88 |    0.03 |      - |      32 B |        0.10 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Incr             | INCR                 | 120.923 μs |  61.4470 μs |  3.3681 μs |  1.00 |    0.03 |      - |     291 B |        1.00 |
| Respire_Incr                   | INCR                 | 113.883 μs |  22.6345 μs |  1.2407 μs |  0.94 |    0.02 |      - |      32 B |        0.11 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 268.039 μs | 220.9731 μs | 12.1123 μs |  1.00 |    0.05 |      - |     759 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 260.775 μs |  81.3554 μs |  4.4594 μs |  0.97 |    0.04 |      - |     254 B |        0.33 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Ping             | PING                 | 118.267 μs |  73.7234 μs |  4.0410 μs |  1.00 |    0.04 |      - |     295 B |        1.00 |
| Respire_Ping                   | PING                 | 115.035 μs |  71.6660 μs |  3.9283 μs |  0.97 |    0.04 |      - |      32 B |        0.11 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 108.925 μs |   7.9339 μs |  0.4349 μs |  1.00 |    0.00 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 105.565 μs |  13.0161 μs |  0.7135 μs |  0.97 |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 124.970 μs |  22.2475 μs |  1.2195 μs |  1.00 |    0.01 |      - |     305 B |        1.00 |
| Respire_SAdd                   | SADD                 | 114.742 μs | 165.5604 μs |  9.0749 μs |  0.92 |    0.06 |      - |      32 B |        0.10 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 153.067 μs |  68.1922 μs |  3.7378 μs |  1.00 |    0.03 |      - |     311 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 150.744 μs |  99.7342 μs |  5.4668 μs |  0.99 |    0.04 |      - |      32 B |        0.10 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 122.155 μs |  49.1995 μs |  2.6968 μs |  1.00 |    0.03 |      - |     308 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 118.395 μs |  35.1974 μs |  1.9293 μs |  0.97 |    0.02 |      - |      32 B |        0.10 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 130.268 μs |  61.5598 μs |  3.3743 μs |  1.00 |    0.03 |      - |     310 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 115.922 μs | 102.4785 μs |  5.6172 μs |  0.89 |    0.04 |      - |      32 B |        0.10 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 113.341 μs |  56.6064 μs |  3.1028 μs |  1.00 |    0.03 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 109.038 μs |  86.3705 μs |  4.7343 μs |  0.96 |    0.04 |      - |       2 B |       0.008 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 254.971 μs | 109.5755 μs |  6.0062 μs |  1.00 |    0.03 |      - |     647 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 234.368 μs |  94.6487 μs |  5.1880 μs |  0.92 |    0.03 |      - |     200 B |        0.31 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 119.204 μs |  21.0925 μs |  1.1562 μs |  1.00 |    0.01 |      - |     308 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 113.031 μs | 136.3745 μs |  7.4751 μs |  0.95 |    0.05 |      - |      32 B |        0.10 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
