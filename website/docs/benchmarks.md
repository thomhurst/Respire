---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-08 22:36 UTC from commit `a81082486120`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31281896496) for logs and downloadable artifacts.
:::

StackExchange.Redis is the baseline. A ratio below `1.00` means Respire completed the operation faster.

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
| StackExchange_Exists           | EXISTS               | 116.989 μs |  45.3380 μs |  2.4851 μs |  1.00 |    0.03 |      - |     292 B |        1.00 |
| Respire_Exists                 | EXISTS               | 106.193 μs | 148.2496 μs |  8.1261 μs |  0.91 |    0.06 |      - |      32 B |        0.11 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Get              | GET                  | 119.629 μs |  85.7263 μs |  4.6989 μs |  1.00 |    0.05 |      - |     497 B |        1.00 |
| Respire_Get                    | GET                  | 104.905 μs |  12.3166 μs |  0.6751 μs |  0.88 |    0.03 |      - |      80 B |        0.16 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 108.409 μs |  18.6288 μs |  1.0211 μs |  1.00 |    0.01 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 107.846 μs |  58.5185 μs |  3.2076 μs |  0.99 |    0.03 |      - |      50 B |        0.15 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   4.098 μs |   1.4284 μs |  0.0783 μs |  1.00 |    0.02 | 0.0098 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   4.135 μs |   0.7967 μs |  0.0437 μs |  1.01 |    0.02 |      - |      52 B |        0.18 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_HGet             | HGET                 | 120.300 μs |  82.7157 μs |  4.5339 μs |  1.00 |    0.05 |      - |     514 B |        1.00 |
| Respire_HGet                   | HGET                 | 117.032 μs |  38.8539 μs |  2.1297 μs |  0.97 |    0.03 |      - |      80 B |        0.16 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_HSet             | HSET                 | 125.507 μs |  30.2685 μs |  1.6591 μs |  1.00 |    0.02 |      - |     323 B |        1.00 |
| Respire_HSet                   | HSET                 | 120.346 μs |  83.6802 μs |  4.5868 μs |  0.96 |    0.03 |      - |      32 B |        0.10 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Incr             | INCR                 | 113.084 μs | 134.0089 μs |  7.3455 μs |  1.00 |    0.08 |      - |     294 B |        1.00 |
| Respire_Incr                   | INCR                 | 110.969 μs |  92.8862 μs |  5.0914 μs |  0.98 |    0.07 |      - |      32 B |        0.11 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 242.164 μs | 466.9691 μs | 25.5962 μs |  1.01 |    0.13 |      - |     759 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 248.353 μs | 109.4331 μs |  5.9984 μs |  1.03 |    0.10 |      - |     575 B |        0.76 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Ping             | PING                 | 115.763 μs |  33.6740 μs |  1.8458 μs |  1.00 |    0.02 |      - |     295 B |        1.00 |
| Respire_Ping                   | PING                 | 111.145 μs |  81.4952 μs |  4.4670 μs |  0.96 |    0.04 |      - |      32 B |        0.11 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 106.265 μs |  19.6194 μs |  1.0754 μs |  1.00 |    0.01 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential |  99.251 μs |  19.0520 μs |  1.0443 μs |  0.93 |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 119.838 μs |  76.8913 μs |  4.2147 μs |  1.00 |    0.04 |      - |     301 B |        1.00 |
| Respire_SAdd                   | SADD                 | 117.919 μs |  33.4358 μs |  1.8327 μs |  0.98 |    0.03 |      - |      96 B |        0.32 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 146.967 μs |  17.2475 μs |  0.9454 μs |  1.00 |    0.01 |      - |     311 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 145.556 μs |  41.5047 μs |  2.2750 μs |  0.99 |    0.01 |      - |      32 B |        0.10 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 115.795 μs |  41.9394 μs |  2.2988 μs |  1.00 |    0.02 |      - |     305 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 121.860 μs |  32.2278 μs |  1.7665 μs |  1.05 |    0.02 |      - |      32 B |        0.10 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 123.803 μs |  61.3943 μs |  3.3652 μs |  1.00 |    0.03 |      - |     309 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 122.316 μs |  94.2078 μs |  5.1638 μs |  0.99 |    0.04 |      - |      32 B |        0.10 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 108.478 μs |  40.5001 μs |  2.2199 μs |  1.00 |    0.03 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 104.425 μs |  51.9639 μs |  2.8483 μs |  0.96 |    0.03 |      - |       2 B |       0.008 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 227.596 μs | 196.3255 μs | 10.7613 μs |  1.00 |    0.06 |      - |     645 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 235.482 μs |  79.9917 μs |  4.3846 μs |  1.04 |    0.05 |      - |     263 B |        0.41 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 120.586 μs |  14.0847 μs |  0.7720 μs |  1.00 |    0.01 |      - |     305 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 117.142 μs |  14.7858 μs |  0.8105 μs |  0.97 |    0.01 |      - |      32 B |        0.10 |

## net9.0

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD EPYC 7763 2.45GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 9.0.18 (9.0.18, 9.0.1826.31522), X64 RyuJIT x86-64-v3
  ShortRun : .NET 9.0.18 (9.0.18, 9.0.1826.31522), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                         | Categories           | Mean       | Error      | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|-----------:|----------:|------:|--------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 187.724 μs | 19.3112 μs | 1.0585 μs |  1.00 |    0.01 |     295 B |        1.00 |
| Respire_Exists                 | EXISTS               | 187.909 μs | 21.9528 μs | 1.2033 μs |  1.00 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get              | GET                  | 190.727 μs | 24.2710 μs | 1.3304 μs |  1.00 |    0.01 |     504 B |        1.00 |
| Respire_Get                    | GET                  | 189.160 μs | 18.4724 μs | 1.0125 μs |  0.99 |    0.01 |      80 B |        0.16 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 173.591 μs | 25.4789 μs | 1.3966 μs |  1.00 |    0.01 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 172.721 μs | 16.3719 μs | 0.8974 μs |  1.00 |    0.01 |      50 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.312 μs |  0.6064 μs | 0.0332 μs |  1.00 |    0.01 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   4.779 μs |  0.5220 μs | 0.0286 μs |  0.90 |    0.01 |      52 B |        0.18 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HGet             | HGET                 | 190.530 μs | 10.5489 μs | 0.5782 μs |  1.00 |    0.00 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 190.270 μs | 19.7829 μs | 1.0844 μs |  1.00 |    0.01 |      80 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HSet             | HSET                 | 188.541 μs | 18.0756 μs | 0.9908 μs |  1.00 |    0.01 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 189.871 μs | 23.2504 μs | 1.2744 μs |  1.01 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Incr             | INCR                 | 187.897 μs |  1.6915 μs | 0.0927 μs |  1.00 |    0.00 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 191.069 μs | 17.7592 μs | 0.9734 μs |  1.02 |    0.00 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 371.323 μs | 19.8747 μs | 1.0894 μs |  1.00 |    0.00 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 364.056 μs | 35.7282 μs | 1.9584 μs |  0.98 |    0.01 |     576 B |        0.76 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping             | PING                 | 185.893 μs | 10.0856 μs | 0.5528 μs |  1.00 |    0.00 |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 186.802 μs | 32.6950 μs | 1.7921 μs |  1.00 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 169.945 μs | 51.0389 μs | 2.7976 μs |  1.00 |    0.02 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 169.893 μs | 31.2773 μs | 1.7144 μs |  1.00 |    0.02 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SAdd             | SADD                 | 188.898 μs | 41.4996 μs | 2.2747 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 189.054 μs | 12.4972 μs | 0.6850 μs |  1.00 |    0.01 |      96 B |        0.31 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 198.318 μs | 12.6087 μs | 0.6911 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 198.693 μs | 17.9118 μs | 0.9818 μs |  1.00 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 189.339 μs |  8.6315 μs | 0.4731 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 190.197 μs | 13.5612 μs | 0.7433 μs |  1.00 |    0.00 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 190.666 μs | 12.3517 μs | 0.6770 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 190.630 μs | 17.9819 μs | 0.9856 μs |  1.00 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 175.593 μs | 66.3575 μs | 3.6373 μs |  1.00 |    0.03 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 173.603 μs | 12.6975 μs | 0.6960 μs |  0.99 |    0.02 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 366.368 μs | 26.7114 μs | 1.4641 μs |  1.00 |    0.00 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 358.346 μs | 49.6806 μs | 2.7232 μs |  0.98 |    0.01 |     264 B |        0.41 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 188.094 μs | 28.6976 μs | 1.5730 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 189.781 μs | 20.8588 μs | 1.1433 μs |  1.01 |    0.01 |      32 B |        0.10 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
