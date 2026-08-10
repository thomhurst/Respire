---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-10 00:36 UTC from commit `1eedd6bd1fec`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31344683026) for logs and downloadable artifacts.
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
| StackExchange_Exists           | EXISTS               | 180.810 μs | 63.7152 μs | 3.4924 μs |  1.00 |    0.02 |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 180.824 μs | 17.6357 μs | 0.9667 μs |  1.00 |    0.02 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get              | GET                  | 184.090 μs | 50.4827 μs | 2.7671 μs |  1.00 |    0.02 |     504 B |        1.00 |
| Respire_Get                    | GET                  | 182.283 μs |  7.1435 μs | 0.3916 μs |  0.99 |    0.01 |      80 B |        0.16 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 169.769 μs | 32.4888 μs | 1.7808 μs |  1.00 |    0.01 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 165.852 μs | 22.5674 μs | 1.2370 μs |  0.98 |    0.01 |      50 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.089 μs |  1.0690 μs | 0.0586 μs |  1.00 |    0.01 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   5.038 μs |  0.9789 μs | 0.0537 μs |  0.99 |    0.01 |      52 B |        0.18 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HGet             | HGET                 | 186.115 μs | 22.3348 μs | 1.2242 μs |  1.00 |    0.01 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 183.068 μs | 42.3609 μs | 2.3219 μs |  0.98 |    0.01 |      80 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HSet             | HSET                 | 183.881 μs | 20.5980 μs | 1.1290 μs |  1.00 |    0.01 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 183.163 μs | 18.0459 μs | 0.9892 μs |  1.00 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Incr             | INCR                 | 182.070 μs | 12.0455 μs | 0.6603 μs |  1.00 |    0.00 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 182.910 μs | 28.4094 μs | 1.5572 μs |  1.00 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 358.251 μs | 61.7547 μs | 3.3850 μs |  1.00 |    0.01 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 358.551 μs | 37.0700 μs | 2.0319 μs |  1.00 |    0.01 |     253 B |        0.33 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping             | PING                 | 182.422 μs | 12.5056 μs | 0.6855 μs |  1.00 |    0.00 |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 181.351 μs | 12.1096 μs | 0.6638 μs |  0.99 |    0.00 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 164.749 μs | 62.3518 μs | 3.4177 μs |  1.00 |    0.03 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 160.560 μs | 22.4209 μs | 1.2290 μs |  0.97 |    0.02 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SAdd             | SADD                 | 183.612 μs | 43.6883 μs | 2.3947 μs |  1.00 |    0.02 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 183.096 μs |  8.6847 μs | 0.4760 μs |  1.00 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 196.559 μs | 15.0038 μs | 0.8224 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 198.334 μs |  8.6111 μs | 0.4720 μs |  1.01 |    0.00 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 185.242 μs |  1.7084 μs | 0.0936 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 184.556 μs | 13.4444 μs | 0.7369 μs |  1.00 |    0.00 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 186.468 μs |  8.4485 μs | 0.4631 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 184.307 μs | 20.9440 μs | 1.1480 μs |  0.99 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 169.825 μs | 42.8372 μs | 2.3481 μs |  1.00 |    0.02 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 167.959 μs | 45.8594 μs | 2.5137 μs |  0.99 |    0.02 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 357.572 μs | 51.2659 μs | 2.8101 μs |  1.00 |    0.01 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 357.924 μs | 98.6358 μs | 5.4066 μs |  1.00 |    0.01 |     200 B |        0.31 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 183.258 μs | 17.1050 μs | 0.9376 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 183.132 μs | 30.2074 μs | 1.6558 μs |  1.00 |    0.01 |      32 B |        0.10 |

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
| StackExchange_Exists           | EXISTS               | 179.517 μs |  8.0483 μs | 0.4412 μs |  1.00 |    0.00 |     294 B |        1.00 |
| Respire_Exists                 | EXISTS               | 176.858 μs |  4.8718 μs | 0.2670 μs |  0.99 |    0.00 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get              | GET                  | 184.132 μs |  0.5516 μs | 0.0302 μs |  1.00 |    0.00 |     504 B |        1.00 |
| Respire_Get                    | GET                  | 177.204 μs | 20.3529 μs | 1.1156 μs |  0.96 |    0.01 |      80 B |        0.16 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 166.172 μs | 31.8293 μs | 1.7447 μs |  1.00 |    0.01 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 163.122 μs | 22.8578 μs | 1.2529 μs |  0.98 |    0.01 |      50 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.256 μs |  1.5789 μs | 0.0865 μs |  1.00 |    0.02 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   5.081 μs |  0.5023 μs | 0.0275 μs |  0.97 |    0.01 |      52 B |        0.18 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HGet             | HGET                 | 185.761 μs | 36.4369 μs | 1.9972 μs |  1.00 |    0.01 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 175.978 μs | 13.9980 μs | 0.7673 μs |  0.95 |    0.01 |      80 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HSet             | HSET                 | 182.370 μs | 13.7760 μs | 0.7551 μs |  1.00 |    0.01 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 178.134 μs | 18.3471 μs | 1.0057 μs |  0.98 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Incr             | INCR                 | 182.327 μs | 33.1660 μs | 1.8179 μs |  1.00 |    0.01 |     295 B |        1.00 |
| Respire_Incr                   | INCR                 | 176.835 μs |  9.1636 μs | 0.5023 μs |  0.97 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 359.123 μs | 26.2110 μs | 1.4367 μs |  1.00 |    0.00 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 353.138 μs | 24.5527 μs | 1.3458 μs |  0.98 |    0.00 |     256 B |        0.34 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping             | PING                 | 179.149 μs | 55.6829 μs | 3.0522 μs |  1.00 |    0.02 |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 175.573 μs | 28.3599 μs | 1.5545 μs |  0.98 |    0.02 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 165.345 μs | 56.3006 μs | 3.0860 μs |  1.00 |    0.02 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 161.425 μs | 33.5596 μs | 1.8395 μs |  0.98 |    0.02 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SAdd             | SADD                 | 180.569 μs | 34.2955 μs | 1.8799 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 175.996 μs |  9.9824 μs | 0.5472 μs |  0.97 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 193.078 μs |  5.7160 μs | 0.3133 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 186.474 μs | 12.7797 μs | 0.7005 μs |  0.97 |    0.00 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 183.481 μs |  8.2202 μs | 0.4506 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 178.108 μs | 10.8195 μs | 0.5931 μs |  0.97 |    0.00 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 183.408 μs |  4.6663 μs | 0.2558 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 177.933 μs |  3.8520 μs | 0.2111 μs |  0.97 |    0.00 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 168.711 μs | 33.7906 μs | 1.8522 μs |  1.00 |    0.01 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 165.255 μs | 16.8413 μs | 0.9231 μs |  0.98 |    0.01 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 353.878 μs | 51.6717 μs | 2.8323 μs |  1.00 |    0.01 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 351.723 μs | 39.4705 μs | 2.1635 μs |  0.99 |    0.01 |     200 B |        0.31 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 181.592 μs | 24.0200 μs | 1.3166 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 177.927 μs | 13.0586 μs | 0.7158 μs |  0.98 |    0.01 |      32 B |        0.10 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
