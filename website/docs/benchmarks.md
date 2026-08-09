---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-09 03:05 UTC from commit `9fc25f4e7d64`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31291230029) for logs and downloadable artifacts.
:::

StackExchange.Redis is the baseline. A ratio below `1.00` means Respire completed the operation faster.

## net10.0

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Xeon 6973P-C 2.60GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                         | Categories           | Mean       | Error      | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|-----------:|----------:|------:|--------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               |  96.899 μs | 26.9789 μs | 1.4788 μs |  1.00 |    0.02 |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 101.353 μs | 46.7114 μs | 2.5604 μs |  1.05 |    0.03 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get              | GET                  | 100.532 μs | 52.4620 μs | 2.8756 μs |  1.00 |    0.03 |     504 B |        1.00 |
| Respire_Get                    | GET                  |  96.429 μs |  3.6869 μs | 0.2021 μs |  0.96 |    0.02 |      80 B |        0.16 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  |  80.325 μs | 35.4973 μs | 1.9457 μs |  1.00 |    0.03 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  |  77.966 μs | 27.2042 μs | 1.4912 μs |  0.97 |    0.03 |      50 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   2.518 μs |  0.3399 μs | 0.0186 μs |  1.00 |    0.01 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   2.411 μs |  0.2983 μs | 0.0163 μs |  0.96 |    0.01 |      52 B |        0.18 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HGet             | HGET                 |  96.092 μs | 42.5046 μs | 2.3298 μs |  1.00 |    0.03 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 |  96.763 μs |  0.2702 μs | 0.0148 μs |  1.01 |    0.02 |      80 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HSet             | HSET                 |  97.289 μs | 14.4954 μs | 0.7945 μs |  1.00 |    0.01 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 |  97.348 μs | 24.5735 μs | 1.3470 μs |  1.00 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Incr             | INCR                 |  95.967 μs | 50.1906 μs | 2.7511 μs |  1.00 |    0.04 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 |  96.976 μs | 12.0813 μs | 0.6622 μs |  1.01 |    0.03 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 182.178 μs | 60.9481 μs | 3.3408 μs |  1.00 |    0.02 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 181.412 μs | 44.9676 μs | 2.4648 μs |  1.00 |    0.02 |     256 B |        0.34 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping             | PING                 |  96.075 μs |  9.5160 μs | 0.5216 μs |  1.00 |    0.01 |     304 B |        1.00 |
| Respire_Ping                   | PING                 |  96.150 μs | 17.0613 μs | 0.9352 μs |  1.00 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential |  81.572 μs | 52.7409 μs | 2.8909 μs |  1.00 |    0.04 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential |  76.212 μs | 10.8631 μs | 0.5954 μs |  0.94 |    0.03 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SAdd             | SADD                 |  99.574 μs | 22.7855 μs | 1.2490 μs |  1.00 |    0.02 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 |  98.132 μs | 35.7586 μs | 1.9600 μs |  0.99 |    0.02 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 100.615 μs |  5.5460 μs | 0.3040 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             |  99.538 μs | 14.6824 μs | 0.8048 μs |  0.99 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              |  97.079 μs | 16.6485 μs | 0.9126 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              |  96.279 μs |  0.1565 μs | 0.0086 μs |  0.99 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              |  97.872 μs |  9.3861 μs | 0.5145 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              |  96.430 μs | 11.3749 μs | 0.6235 μs |  0.99 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  |  77.220 μs | 44.9963 μs | 2.4664 μs |  1.00 |    0.04 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  |  76.564 μs | 20.9591 μs | 1.1488 μs |  0.99 |    0.03 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 180.001 μs | 60.7867 μs | 3.3319 μs |  1.00 |    0.02 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 180.500 μs | 26.9363 μs | 1.4765 μs |  1.00 |    0.02 |     200 B |        0.31 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            |  97.651 μs | 50.6550 μs | 2.7766 μs |  1.00 |    0.03 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            |  99.972 μs | 26.0095 μs | 1.4257 μs |  1.02 |    0.03 |      32 B |        0.10 |

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
| StackExchange_Exists           | EXISTS               | 177.529 μs |  20.853 μs | 1.1430 μs |  1.00 |    0.01 |     294 B |        1.00 |
| Respire_Exists                 | EXISTS               | 178.579 μs |  45.317 μs | 2.4840 μs |  1.01 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get              | GET                  | 181.587 μs |  27.341 μs | 1.4987 μs |  1.00 |    0.01 |     503 B |        1.00 |
| Respire_Get                    | GET                  | 180.044 μs |  33.587 μs | 1.8410 μs |  0.99 |    0.01 |      80 B |        0.16 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 162.949 μs |  21.138 μs | 1.1587 μs |  1.00 |    0.01 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 164.182 μs |  24.437 μs | 1.3395 μs |  1.01 |    0.01 |      50 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.097 μs |   3.784 μs | 0.2074 μs |  1.00 |    0.05 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   4.520 μs |   1.958 μs | 0.1073 μs |  0.89 |    0.04 |      52 B |        0.18 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HGet             | HGET                 | 182.620 μs |  50.842 μs | 2.7868 μs |  1.00 |    0.02 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 180.949 μs |  12.076 μs | 0.6619 μs |  0.99 |    0.01 |      80 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HSet             | HSET                 | 180.150 μs |  46.154 μs | 2.5299 μs |  1.00 |    0.02 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 179.319 μs |  60.056 μs | 3.2919 μs |  1.00 |    0.02 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Incr             | INCR                 | 179.619 μs |  15.097 μs | 0.8275 μs |  1.00 |    0.01 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 179.347 μs |   5.531 μs | 0.3032 μs |  1.00 |    0.00 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 353.320 μs |  45.196 μs | 2.4773 μs |  1.00 |    0.01 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 346.815 μs |  84.139 μs | 4.6119 μs |  0.98 |    0.01 |     256 B |        0.34 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping             | PING                 | 176.520 μs |   3.139 μs | 0.1721 μs |  1.00 |    0.00 |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 176.717 μs |  12.585 μs | 0.6898 μs |  1.00 |    0.00 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 162.787 μs |  35.317 μs | 1.9358 μs |  1.00 |    0.01 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 160.899 μs |  52.465 μs | 2.8758 μs |  0.99 |    0.02 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SAdd             | SADD                 | 177.749 μs |  61.091 μs | 3.3486 μs |  1.00 |    0.02 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 179.559 μs |   7.201 μs | 0.3947 μs |  1.01 |    0.02 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 190.394 μs |  11.684 μs | 0.6404 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 188.071 μs |  49.952 μs | 2.7380 μs |  0.99 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 180.356 μs |  43.363 μs | 2.3769 μs |  1.00 |    0.02 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 180.884 μs |  11.596 μs | 0.6356 μs |  1.00 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 181.570 μs |  20.999 μs | 1.1510 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 180.416 μs |  47.816 μs | 2.6210 μs |  0.99 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 165.157 μs |  40.189 μs | 2.2029 μs |  1.00 |    0.02 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 164.313 μs |  25.650 μs | 1.4060 μs |  1.00 |    0.01 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 349.103 μs | 100.518 μs | 5.5097 μs |  1.00 |    0.02 |     647 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 346.709 μs |  29.121 μs | 1.5962 μs |  0.99 |    0.01 |     200 B |        0.31 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 178.600 μs |  19.461 μs | 1.0667 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 178.407 μs |  30.977 μs | 1.6980 μs |  1.00 |    0.01 |      32 B |        0.10 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
