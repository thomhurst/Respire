---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-10 07:52 UTC from commit `764959e40334`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31367258585) for logs and downloadable artifacts.
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
| StackExchange_Exists           | EXISTS               | 185.801 μs |  4.5289 μs | 0.2482 μs |  1.00 |    0.00 |      - |     295 B |        1.00 |
| Respire_Exists                 | EXISTS               | 185.467 μs | 21.7720 μs | 1.1934 μs |  1.00 |    0.01 |      - |         - |        0.00 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Get              | GET                  | 188.514 μs | 13.4942 μs | 0.7397 μs |  1.00 |    0.00 |      - |     503 B |        1.00 |
| Respire_Get                    | GET                  | 185.577 μs | 20.1260 μs | 1.1032 μs |  0.98 |    0.01 |      - |      48 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 173.606 μs | 24.1474 μs | 1.3236 μs |  1.00 |    0.01 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 171.568 μs | 31.9887 μs | 1.7534 μs |  0.99 |    0.01 |      - |      50 B |        0.15 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.214 μs |  1.1610 μs | 0.0636 μs |  1.00 |    0.02 | 0.0098 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   5.279 μs |  0.0719 μs | 0.0039 μs |  1.01 |    0.01 |      - |      52 B |        0.18 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_HGet             | HGET                 | 190.006 μs | 38.2896 μs | 2.0988 μs |  1.00 |    0.01 |      - |     518 B |        1.00 |
| Respire_HGet                   | HGET                 | 186.567 μs |  4.7896 μs | 0.2625 μs |  0.98 |    0.01 |      - |      48 B |        0.09 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_HSet             | HSET                 | 189.482 μs | 21.8763 μs | 1.1991 μs |  1.00 |    0.01 |      - |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 187.622 μs | 34.3419 μs | 1.8824 μs |  0.99 |    0.01 |      - |         - |        0.00 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Incr             | INCR                 | 188.107 μs |  3.0764 μs | 0.1686 μs |  1.00 |    0.00 |      - |     293 B |        1.00 |
| Respire_Incr                   | INCR                 | 187.922 μs | 11.1645 μs | 0.6120 μs |  1.00 |    0.00 |      - |         - |        0.00 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 367.833 μs | 27.5582 μs | 1.5106 μs |  1.00 |    0.01 |      - |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 363.126 μs |  2.4989 μs | 0.1370 μs |  0.99 |    0.00 |      - |     256 B |        0.34 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Ping             | PING                 | 186.396 μs | 17.2503 μs | 0.9455 μs |  1.00 |    0.01 |      - |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 184.252 μs |  6.8456 μs | 0.3752 μs |  0.99 |    0.00 |      - |         - |        0.00 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 170.336 μs | 47.7623 μs | 2.6180 μs |  1.00 |    0.02 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 170.094 μs | 14.5560 μs | 0.7979 μs |  1.00 |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 188.757 μs | 16.5753 μs | 0.9085 μs |  1.00 |    0.01 |      - |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 186.363 μs |  8.4903 μs | 0.4654 μs |  0.99 |    0.00 |      - |         - |        0.00 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 203.378 μs |  9.2113 μs | 0.5049 μs |  1.00 |    0.00 |      - |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 199.735 μs |  9.9784 μs | 0.5470 μs |  0.98 |    0.00 |      - |         - |        0.00 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 188.469 μs |  4.7375 μs | 0.2597 μs |  1.00 |    0.00 |      - |     311 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 185.440 μs |  2.4981 μs | 0.1369 μs |  0.98 |    0.00 |      - |         - |        0.00 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 190.566 μs |  9.3585 μs | 0.5130 μs |  1.00 |    0.00 |      - |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 188.554 μs | 23.7638 μs | 1.3026 μs |  0.99 |    0.01 |      - |         - |        0.00 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 174.330 μs | 23.4077 μs | 1.2831 μs |  1.00 |    0.01 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 174.711 μs | 31.4801 μs | 1.7255 μs |  1.00 |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 363.659 μs | 97.7077 μs | 5.3557 μs |  1.00 |    0.02 |      - |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 358.766 μs | 41.5537 μs | 2.2777 μs |  0.99 |    0.01 |      - |     200 B |        0.31 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 188.799 μs |  8.4927 μs | 0.4655 μs |  1.00 |    0.00 |      - |     311 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 185.848 μs | 43.7717 μs | 2.3993 μs |  0.98 |    0.01 |      - |         - |        0.00 |

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
| StackExchange_Exists           | EXISTS               | 190.174 μs |  37.5491 μs | 2.0582 μs |  1.00 |    0.01 |     295 B |        1.00 |
| Respire_Exists                 | EXISTS               | 192.064 μs |  40.9465 μs | 2.2444 μs |  1.01 |    0.01 |         - |        0.00 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Get              | GET                  | 194.454 μs |  27.5527 μs | 1.5103 μs |  1.00 |    0.01 |     504 B |        1.00 |
| Respire_Get                    | GET                  | 190.353 μs |  31.3283 μs | 1.7172 μs |  0.98 |    0.01 |      48 B |        0.10 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 178.094 μs |  34.9545 μs | 1.9160 μs |  1.00 |    0.01 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 176.494 μs |  29.4066 μs | 1.6119 μs |  0.99 |    0.01 |      50 B |        0.15 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.424 μs |   0.4253 μs | 0.0233 μs |  1.00 |    0.01 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   5.371 μs |   1.4873 μs | 0.0815 μs |  0.99 |    0.01 |      52 B |        0.18 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_HGet             | HGET                 | 195.539 μs |  27.7993 μs | 1.5238 μs |  1.00 |    0.01 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 192.899 μs |   7.8430 μs | 0.4299 μs |  0.99 |    0.01 |      48 B |        0.09 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_HSet             | HSET                 | 192.666 μs |   4.0484 μs | 0.2219 μs |  1.00 |    0.00 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 193.790 μs |  11.7022 μs | 0.6414 μs |  1.01 |    0.00 |         - |        0.00 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Incr             | INCR                 | 190.853 μs |  36.9355 μs | 2.0246 μs |  1.00 |    0.01 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 190.767 μs |  37.4272 μs | 2.0515 μs |  1.00 |    0.01 |         - |        0.00 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 376.694 μs | 107.4966 μs | 5.8923 μs |  1.00 |    0.02 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 375.225 μs |  55.6412 μs | 3.0499 μs |  1.00 |    0.02 |     256 B |        0.34 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Ping             | PING                 | 189.329 μs |  51.7015 μs | 2.8339 μs |  1.00 |    0.02 |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 188.342 μs |   6.4819 μs | 0.3553 μs |  0.99 |    0.01 |         - |        0.00 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 176.693 μs |  24.8082 μs | 1.3598 μs |  1.00 |    0.01 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 173.842 μs |  42.7926 μs | 2.3456 μs |  0.98 |    0.01 |       2 B |       0.008 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_SAdd             | SADD                 | 189.203 μs |  24.4541 μs | 1.3404 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 191.770 μs |   8.9578 μs | 0.4910 μs |  1.01 |    0.01 |         - |        0.00 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 202.029 μs |  15.9214 μs | 0.8727 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 205.837 μs |   8.2833 μs | 0.4540 μs |  1.02 |    0.00 |         - |        0.00 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 191.506 μs |  24.6141 μs | 1.3492 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 191.987 μs |  29.0679 μs | 1.5933 μs |  1.00 |    0.01 |         - |        0.00 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 192.487 μs |  30.3963 μs | 1.6661 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 191.537 μs |  22.4384 μs | 1.2299 μs |  1.00 |    0.01 |         - |        0.00 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 177.956 μs |  56.1850 μs | 3.0797 μs |  1.00 |    0.02 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 178.482 μs |  10.1837 μs | 0.5582 μs |  1.00 |    0.02 |       2 B |       0.008 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 370.981 μs |  39.2501 μs | 2.1514 μs |  1.00 |    0.01 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 369.697 μs |  12.1690 μs | 0.6670 μs |  1.00 |    0.01 |     200 B |        0.31 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 188.560 μs |  36.8020 μs | 2.0172 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 187.374 μs |   5.4696 μs | 0.2998 μs |  0.99 |    0.01 |         - |        0.00 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
