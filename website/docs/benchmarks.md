---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-09 00:57 UTC from commit `97907edf742e`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31287086184) for logs and downloadable artifacts.
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
| Method                         | Categories           | Mean       | Error      | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|-----------:|----------:|------:|--------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 184.425 μs |  9.5057 μs | 0.5210 μs |  1.00 |    0.00 |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 184.033 μs | 60.6052 μs | 3.3220 μs |  1.00 |    0.02 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get              | GET                  | 185.473 μs |  9.4538 μs | 0.5182 μs |  1.00 |    0.00 |     504 B |        1.00 |
| Respire_Get                    | GET                  | 186.651 μs | 14.7510 μs | 0.8086 μs |  1.01 |    0.00 |      80 B |        0.16 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 169.586 μs |  4.1971 μs | 0.2301 μs |  1.00 |    0.00 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 171.496 μs | 21.6044 μs | 1.1842 μs |  1.01 |    0.01 |      50 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.081 μs |  0.7394 μs | 0.0405 μs |  1.00 |    0.01 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   4.611 μs |  1.5816 μs | 0.0867 μs |  0.91 |    0.02 |      52 B |        0.18 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HGet             | HGET                 | 187.440 μs |  5.0627 μs | 0.2775 μs |  1.00 |    0.00 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 186.654 μs | 45.7975 μs | 2.5103 μs |  1.00 |    0.01 |      80 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HSet             | HSET                 | 186.525 μs |  8.2093 μs | 0.4500 μs |  1.00 |    0.00 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 187.046 μs |  6.1172 μs | 0.3353 μs |  1.00 |    0.00 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Incr             | INCR                 | 184.440 μs |  7.0703 μs | 0.3875 μs |  1.00 |    0.00 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 186.910 μs | 12.1126 μs | 0.6639 μs |  1.01 |    0.00 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 362.961 μs | 85.8142 μs | 4.7038 μs |  1.00 |    0.02 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 358.783 μs | 11.5196 μs | 0.6314 μs |  0.99 |    0.01 |     256 B |        0.34 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping             | PING                 | 184.313 μs | 46.0101 μs | 2.5220 μs |  1.00 |    0.02 |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 185.455 μs | 11.7536 μs | 0.6443 μs |  1.01 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 170.233 μs | 23.9668 μs | 1.3137 μs |  1.00 |    0.01 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 169.889 μs | 16.4426 μs | 0.9013 μs |  1.00 |    0.01 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SAdd             | SADD                 | 185.830 μs | 22.4595 μs | 1.2311 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 189.642 μs | 28.5069 μs | 1.5626 μs |  1.02 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 198.686 μs | 39.5327 μs | 2.1669 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 200.494 μs | 27.0849 μs | 1.4846 μs |  1.01 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 186.247 μs | 27.6051 μs | 1.5131 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 189.736 μs | 39.9272 μs | 2.1885 μs |  1.02 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 189.937 μs | 23.6688 μs | 1.2974 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 192.430 μs | 13.9897 μs | 0.7668 μs |  1.01 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 171.482 μs | 19.3713 μs | 1.0618 μs |  1.00 |    0.01 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 171.965 μs | 16.0563 μs | 0.8801 μs |  1.00 |    0.01 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 358.687 μs | 23.4287 μs | 1.2842 μs |  1.00 |    0.00 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 355.891 μs | 23.9378 μs | 1.3121 μs |  0.99 |    0.00 |     200 B |        0.31 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 185.050 μs |  6.2702 μs | 0.3437 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 187.262 μs | 15.7006 μs | 0.8606 μs |  1.01 |    0.00 |      32 B |        0.10 |

## net8.0

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Xeon Platinum 8370C CPU 2.80GHz (Max: 2.79GHz), 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 8.0.29 (8.0.29, 8.0.2926.32403), X64 RyuJIT x86-64-v4
  ShortRun : .NET 8.0.29 (8.0.29, 8.0.2926.32403), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                         | Categories           | Mean       | Error       | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|------------:|----------:|------:|--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 113.254 μs |  13.1233 μs | 0.7193 μs |  1.00 |    0.01 |      - |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 109.672 μs |  63.2620 μs | 3.4676 μs |  0.97 |    0.03 |      - |      32 B |        0.11 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Get              | GET                  | 117.741 μs |  73.3630 μs | 4.0213 μs |  1.00 |    0.04 |      - |     504 B |        1.00 |
| Respire_Get                    | GET                  | 117.720 μs |  31.4941 μs | 1.7263 μs |  1.00 |    0.03 |      - |      80 B |        0.16 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 100.514 μs |  40.7606 μs | 2.2342 μs |  1.00 |    0.03 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 102.232 μs |  17.2573 μs | 0.9459 μs |  1.02 |    0.02 |      - |      50 B |        0.15 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   3.705 μs |   1.3101 μs | 0.0718 μs |  1.00 |    0.02 | 0.0098 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   3.382 μs |   0.5878 μs | 0.0322 μs |  0.91 |    0.02 |      - |      52 B |        0.18 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_HGet             | HGET                 | 120.065 μs |  27.9111 μs | 1.5299 μs |  1.00 |    0.02 |      - |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 113.429 μs |  33.5636 μs | 1.8397 μs |  0.94 |    0.02 |      - |      80 B |        0.15 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_HSet             | HSET                 | 118.446 μs |  41.1608 μs | 2.2562 μs |  1.00 |    0.02 |      - |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 115.717 μs |  29.2109 μs | 1.6011 μs |  0.98 |    0.02 |      - |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Incr             | INCR                 | 115.235 μs |  24.3024 μs | 1.3321 μs |  1.00 |    0.01 |      - |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 115.596 μs |  44.4855 μs | 2.4384 μs |  1.00 |    0.02 |      - |      32 B |        0.11 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 228.365 μs |  46.9952 μs | 2.5760 μs |  1.00 |    0.01 |      - |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 223.790 μs |   2.7967 μs | 0.1533 μs |  0.98 |    0.01 |      - |     256 B |        0.34 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Ping             | PING                 | 113.574 μs |  12.8185 μs | 0.7026 μs |  1.00 |    0.01 |      - |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 113.069 μs |  22.1402 μs | 1.2136 μs |  1.00 |    0.01 |      - |      32 B |        0.11 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential |  99.322 μs |  33.1843 μs | 1.8189 μs |  1.00 |    0.02 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential |  99.411 μs |  12.8260 μs | 0.7030 μs |  1.00 |    0.02 |      - |       2 B |       0.008 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 116.343 μs |  14.1090 μs | 0.7734 μs |  1.00 |    0.01 |      - |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 115.138 μs |  32.1456 μs | 1.7620 μs |  0.99 |    0.01 |      - |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 124.403 μs |  13.8337 μs | 0.7583 μs |  1.00 |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 124.303 μs |  77.1698 μs | 4.2299 μs |  1.00 |    0.03 |      - |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 117.096 μs |  11.4241 μs | 0.6262 μs |  1.00 |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 114.760 μs |   3.6930 μs | 0.2024 μs |  0.98 |    0.00 |      - |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 119.366 μs |   9.4075 μs | 0.5157 μs |  1.00 |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 113.576 μs |  98.8978 μs | 5.4209 μs |  0.95 |    0.04 |      - |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 102.350 μs |  21.1355 μs | 1.1585 μs |  1.00 |    0.01 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  |  99.614 μs |   7.4874 μs | 0.4104 μs |  0.97 |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 226.094 μs |  84.7234 μs | 4.6440 μs |  1.00 |    0.03 |      - |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 221.773 μs | 114.5599 μs | 6.2794 μs |  0.98 |    0.03 |      - |     200 B |        0.31 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 115.481 μs |   9.9743 μs | 0.5467 μs |  1.00 |    0.01 |      - |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 115.364 μs |  15.7093 μs | 0.8611 μs |  1.00 |    0.01 |      - |      32 B |        0.10 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
