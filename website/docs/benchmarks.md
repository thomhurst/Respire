---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-09 11:05 UTC from commit `d504970e1a89`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31309756495) for logs and downloadable artifacts.
:::

StackExchange.Redis is the baseline. A ratio below `1.00` means Respire completed the operation faster.

## net10.0

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD EPYC 7763 3.12GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                         | Categories           | Mean       | Error       | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|------------:|----------:|------:|--------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 186.380 μs |  18.0686 μs | 0.9904 μs |  1.00 |    0.01 |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 186.754 μs |  17.0593 μs | 0.9351 μs |  1.00 |    0.01 |      32 B |        0.11 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Get              | GET                  | 187.861 μs |  27.6474 μs | 1.5154 μs |  1.00 |    0.01 |     504 B |        1.00 |
| Respire_Get                    | GET                  | 186.246 μs |   7.6399 μs | 0.4188 μs |  0.99 |    0.01 |      80 B |        0.16 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 170.478 μs |  23.0512 μs | 1.2635 μs |  1.00 |    0.01 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 169.918 μs |  16.2325 μs | 0.8898 μs |  1.00 |    0.01 |      50 B |        0.15 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.283 μs |   1.1513 μs | 0.0631 μs |  1.00 |    0.01 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   4.712 μs |   0.7736 μs | 0.0424 μs |  0.89 |    0.01 |      52 B |        0.18 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_HGet             | HGET                 | 186.516 μs |   5.4321 μs | 0.2977 μs |  1.00 |    0.00 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 187.723 μs |  14.4308 μs | 0.7910 μs |  1.01 |    0.00 |      80 B |        0.15 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_HSet             | HSET                 | 187.071 μs |  11.4736 μs | 0.6289 μs |  1.00 |    0.00 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 189.757 μs |  14.9607 μs | 0.8200 μs |  1.01 |    0.00 |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Incr             | INCR                 | 185.103 μs |   7.3189 μs | 0.4012 μs |  1.00 |    0.00 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 186.815 μs |  11.7793 μs | 0.6457 μs |  1.01 |    0.00 |      32 B |        0.11 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 363.362 μs |  26.4400 μs | 1.4493 μs |  1.00 |    0.00 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 358.520 μs |  29.5639 μs | 1.6205 μs |  0.99 |    0.01 |     256 B |        0.34 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Ping             | PING                 | 183.733 μs |  17.9872 μs | 0.9859 μs |  1.00 |    0.01 |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 185.747 μs |  15.7270 μs | 0.8621 μs |  1.01 |    0.01 |      32 B |        0.11 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 167.555 μs |  15.1249 μs | 0.8290 μs |  1.00 |    0.01 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 168.034 μs |  19.7957 μs | 1.0851 μs |  1.00 |    0.01 |       2 B |       0.008 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_SAdd             | SADD                 | 185.650 μs |  14.6262 μs | 0.8017 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 186.450 μs |  30.8405 μs | 1.6905 μs |  1.00 |    0.01 |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 198.408 μs |  22.6522 μs | 1.2416 μs |  1.00 |    0.01 |     311 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 198.664 μs |  49.1147 μs | 2.6921 μs |  1.00 |    0.01 |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 186.781 μs |  23.3660 μs | 1.2808 μs |  1.00 |    0.01 |     311 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 188.548 μs |  31.8900 μs | 1.7480 μs |  1.01 |    0.01 |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 188.876 μs |  48.5853 μs | 2.6631 μs |  1.00 |    0.02 |     311 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 190.061 μs |  13.6043 μs | 0.7457 μs |  1.01 |    0.01 |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 172.394 μs |   7.4109 μs | 0.4062 μs |  1.00 |    0.00 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 169.959 μs | 120.2208 μs | 6.5897 μs |  0.99 |    0.03 |       2 B |       0.008 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 356.842 μs |  57.2384 μs | 3.1374 μs |  1.00 |    0.01 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 352.558 μs |  21.0394 μs | 1.1532 μs |  0.99 |    0.01 |     200 B |        0.31 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 184.676 μs |  10.6517 μs | 0.5839 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 186.097 μs |  19.9577 μs | 1.0939 μs |  1.01 |    0.01 |      32 B |        0.10 |

## net8.0

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Xeon 6973P-C 2.60GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 8.0.29 (8.0.29, 8.0.2926.32403), X64 RyuJIT x86-64-v4
  ShortRun : .NET 8.0.29 (8.0.29, 8.0.2926.32403), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                         | Categories           | Mean       | Error      | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|-----------:|----------:|------:|--------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               |  84.482 μs | 11.9841 μs | 0.6569 μs |  1.00 |    0.01 |     294 B |        1.00 |
| Respire_Exists                 | EXISTS               |  88.993 μs | 54.7794 μs | 3.0026 μs |  1.05 |    0.03 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get              | GET                  |  90.929 μs | 24.4089 μs | 1.3379 μs |  1.00 |    0.02 |     504 B |        1.00 |
| Respire_Get                    | GET                  |  90.463 μs |  6.1229 μs | 0.3356 μs |  1.00 |    0.01 |      80 B |        0.16 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  |  78.454 μs |  1.9880 μs | 0.1090 μs |  1.00 |    0.00 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  |  74.177 μs |  4.2124 μs | 0.2309 μs |  0.95 |    0.00 |      50 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   2.619 μs |  0.1392 μs | 0.0076 μs |  1.00 |    0.00 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   2.439 μs |  0.1710 μs | 0.0094 μs |  0.93 |    0.00 |      52 B |        0.18 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HGet             | HGET                 |  90.245 μs | 18.8894 μs | 1.0354 μs |  1.00 |    0.01 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 |  90.679 μs | 25.9518 μs | 1.4225 μs |  1.00 |    0.02 |      80 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HSet             | HSET                 |  89.794 μs | 21.5036 μs | 1.1787 μs |  1.00 |    0.02 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 |  95.711 μs | 21.6160 μs | 1.1848 μs |  1.07 |    0.02 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Incr             | INCR                 |  89.886 μs | 17.0071 μs | 0.9322 μs |  1.00 |    0.01 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 |  90.448 μs | 27.5497 μs | 1.5101 μs |  1.01 |    0.02 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 167.798 μs | 30.2307 μs | 1.6570 μs |  1.00 |    0.01 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 166.502 μs | 31.2291 μs | 1.7118 μs |  0.99 |    0.01 |     256 B |        0.34 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping             | PING                 |  92.185 μs | 73.5871 μs | 4.0336 μs |  1.00 |    0.05 |     304 B |        1.00 |
| Respire_Ping                   | PING                 |  93.965 μs |  8.5341 μs | 0.4678 μs |  1.02 |    0.04 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential |  73.448 μs |  3.9400 μs | 0.2160 μs |  1.00 |    0.00 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential |  73.575 μs | 35.6404 μs | 1.9536 μs |  1.00 |    0.02 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SAdd             | SADD                 |  89.615 μs | 24.5155 μs | 1.3438 μs |  1.00 |    0.02 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 |  95.440 μs | 26.5903 μs | 1.4575 μs |  1.07 |    0.02 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             |  94.743 μs | 27.0248 μs | 1.4813 μs |  1.00 |    0.02 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             |  97.357 μs | 17.9051 μs | 0.9814 μs |  1.03 |    0.02 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              |  91.643 μs | 62.6344 μs | 3.4332 μs |  1.00 |    0.05 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              |  92.999 μs | 22.6308 μs | 1.2405 μs |  1.02 |    0.03 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              |  92.742 μs | 73.3600 μs | 4.0211 μs |  1.00 |    0.05 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              |  96.486 μs | 46.6031 μs | 2.5545 μs |  1.04 |    0.05 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  |  77.269 μs | 20.4744 μs | 1.1223 μs |  1.00 |    0.02 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  |  78.989 μs | 68.7712 μs | 3.7696 μs |  1.02 |    0.04 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 162.226 μs | 40.3729 μs | 2.2130 μs |  1.00 |    0.02 |     647 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 163.082 μs | 83.4344 μs | 4.5733 μs |  1.01 |    0.03 |     200 B |        0.31 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            |  89.522 μs | 37.4749 μs | 2.0541 μs |  1.00 |    0.03 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            |  92.986 μs | 44.1081 μs | 2.4177 μs |  1.04 |    0.03 |      32 B |        0.10 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
