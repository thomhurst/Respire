---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-10 01:04 UTC from commit `d7f17e7ce598`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31345896041) for logs and downloadable artifacts.
:::

StackExchange.Redis is the baseline. A ratio below `1.00` means Respire completed the operation faster.

## net10.0

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
INTEL XEON PLATINUM 8573C 2.30GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                         | Categories           | Mean       | Error      | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|-----------:|----------:|------:|--------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 108.970 μs | 44.6241 μs | 2.4460 μs |  1.00 |    0.03 |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 102.608 μs | 17.1572 μs | 0.9404 μs |  0.94 |    0.02 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get              | GET                  | 108.160 μs | 43.9002 μs | 2.4063 μs |  1.00 |    0.03 |     504 B |        1.00 |
| Respire_Get                    | GET                  | 103.473 μs | 12.4140 μs | 0.6805 μs |  0.96 |    0.02 |      80 B |        0.16 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  |  98.577 μs |  8.7302 μs | 0.4785 μs |  1.00 |    0.01 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  |  87.748 μs | 14.0290 μs | 0.7690 μs |  0.89 |    0.01 |      50 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   3.320 μs |  1.5056 μs | 0.0825 μs |  1.00 |    0.03 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   2.932 μs |  0.1547 μs | 0.0085 μs |  0.88 |    0.02 |      52 B |        0.18 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HGet             | HGET                 | 105.186 μs | 26.2755 μs | 1.4402 μs |  1.00 |    0.02 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 |  98.164 μs | 17.5660 μs | 0.9629 μs |  0.93 |    0.01 |      80 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HSet             | HSET                 | 107.742 μs | 38.9045 μs | 2.1325 μs |  1.00 |    0.02 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 103.198 μs |  8.5043 μs | 0.4661 μs |  0.96 |    0.02 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Incr             | INCR                 | 104.051 μs |  9.1445 μs | 0.5012 μs |  1.00 |    0.01 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 |  97.180 μs |  6.6974 μs | 0.3671 μs |  0.93 |    0.00 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 197.651 μs | 28.5361 μs | 1.5642 μs |  1.00 |    0.01 |     759 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 188.410 μs | 19.9976 μs | 1.0961 μs |  0.95 |    0.01 |     256 B |        0.34 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping             | PING                 | 101.401 μs | 25.3157 μs | 1.3876 μs |  1.00 |    0.02 |     304 B |        1.00 |
| Respire_Ping                   | PING                 |  98.838 μs | 21.9784 μs | 1.2047 μs |  0.97 |    0.02 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential |  81.916 μs | 48.1343 μs | 2.6384 μs |  1.00 |    0.04 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential |  85.425 μs | 15.7582 μs | 0.8638 μs |  1.04 |    0.03 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SAdd             | SADD                 | 106.481 μs |  9.9899 μs | 0.5476 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 |  99.443 μs |  5.6876 μs | 0.3118 μs |  0.93 |    0.00 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 109.446 μs | 27.1811 μs | 1.4899 μs |  1.00 |    0.02 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 101.607 μs | 13.4903 μs | 0.7394 μs |  0.93 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 104.512 μs | 31.2959 μs | 1.7154 μs |  1.00 |    0.02 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              |  97.279 μs |  6.8456 μs | 0.3752 μs |  0.93 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 101.269 μs | 31.5256 μs | 1.7280 μs |  1.00 |    0.02 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              |  96.227 μs |  9.6276 μs | 0.5277 μs |  0.95 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  |  85.011 μs | 27.6654 μs | 1.5164 μs |  1.00 |    0.02 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  |  84.016 μs |  7.6052 μs | 0.4169 μs |  0.99 |    0.02 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 192.966 μs | 12.2489 μs | 0.6714 μs |  1.00 |    0.00 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 188.831 μs | 53.2928 μs | 2.9212 μs |  0.98 |    0.01 |     200 B |        0.31 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 102.466 μs | 31.4120 μs | 1.7218 μs |  1.00 |    0.02 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            |  99.308 μs | 29.4367 μs | 1.6135 μs |  0.97 |    0.02 |      32 B |        0.10 |

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
| StackExchange_Exists           | EXISTS               | 188.170 μs | 37.8107 μs | 2.0725 μs |  1.00 |    0.01 |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 192.104 μs | 40.8450 μs | 2.2389 μs |  1.02 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get              | GET                  | 194.473 μs | 44.3511 μs | 2.4310 μs |  1.00 |    0.02 |     504 B |        1.00 |
| Respire_Get                    | GET                  | 190.425 μs | 30.1488 μs | 1.6526 μs |  0.98 |    0.01 |      80 B |        0.16 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 178.129 μs | 14.2995 μs | 0.7838 μs |  1.00 |    0.01 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 176.543 μs | 26.4267 μs | 1.4485 μs |  0.99 |    0.01 |      50 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.541 μs |  0.5668 μs | 0.0311 μs |  1.00 |    0.01 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   5.426 μs |  0.6348 μs | 0.0348 μs |  0.98 |    0.01 |      52 B |        0.18 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HGet             | HGET                 | 195.220 μs | 26.2657 μs | 1.4397 μs |  1.00 |    0.01 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 190.116 μs |  8.4668 μs | 0.4641 μs |  0.97 |    0.01 |      80 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HSet             | HSET                 | 191.862 μs | 30.5757 μs | 1.6760 μs |  1.00 |    0.01 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 192.767 μs | 17.6314 μs | 0.9664 μs |  1.00 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Incr             | INCR                 | 188.131 μs | 85.5693 μs | 4.6903 μs |  1.00 |    0.03 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 192.062 μs | 13.3667 μs | 0.7327 μs |  1.02 |    0.02 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 380.785 μs | 44.3541 μs | 2.4312 μs |  1.00 |    0.01 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 376.511 μs | 22.2632 μs | 1.2203 μs |  0.99 |    0.01 |     256 B |        0.34 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping             | PING                 | 188.055 μs | 25.8250 μs | 1.4156 μs |  1.00 |    0.01 |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 189.850 μs | 20.5892 μs | 1.1286 μs |  1.01 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 174.925 μs | 84.9913 μs | 4.6587 μs |  1.00 |    0.03 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 174.022 μs | 15.9780 μs | 0.8758 μs |  1.00 |    0.02 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SAdd             | SADD                 | 189.069 μs | 66.8115 μs | 3.6622 μs |  1.00 |    0.02 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 190.835 μs | 22.3463 μs | 1.2249 μs |  1.01 |    0.02 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 202.745 μs |  5.5216 μs | 0.3027 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 206.210 μs |  9.0806 μs | 0.4977 μs |  1.02 |    0.00 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 192.502 μs |  5.8507 μs | 0.3207 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 192.811 μs |  6.8027 μs | 0.3729 μs |  1.00 |    0.00 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 192.598 μs |  6.5367 μs | 0.3583 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 194.035 μs | 25.4383 μs | 1.3944 μs |  1.01 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 180.740 μs | 37.8448 μs | 2.0744 μs |  1.00 |    0.01 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 179.104 μs | 29.6628 μs | 1.6259 μs |  0.99 |    0.01 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 377.564 μs | 15.6595 μs | 0.8583 μs |  1.00 |    0.00 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 376.036 μs | 40.3462 μs | 2.2115 μs |  1.00 |    0.01 |     200 B |        0.31 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 191.021 μs | 14.9162 μs | 0.8176 μs |  1.00 |    0.01 |     311 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 191.030 μs | 13.3613 μs | 0.7324 μs |  1.00 |    0.00 |      32 B |        0.10 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
