---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-09 00:59 UTC from commit `70025b07aea4`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31287153088) for logs and downloadable artifacts.
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
| StackExchange_Exists           | EXISTS               | 184.436 μs | 26.6756 μs | 1.4622 μs |  1.00 |    0.01 |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 186.942 μs |  7.2157 μs | 0.3955 μs |  1.01 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get              | GET                  | 186.012 μs | 15.3235 μs | 0.8399 μs |  1.00 |    0.01 |     504 B |        1.00 |
| Respire_Get                    | GET                  | 188.512 μs | 29.7256 μs | 1.6294 μs |  1.01 |    0.01 |      80 B |        0.16 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 172.650 μs | 18.3386 μs | 1.0052 μs |  1.00 |    0.01 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 170.465 μs | 33.8266 μs | 1.8542 μs |  0.99 |    0.01 |      50 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   4.998 μs |  1.2064 μs | 0.0661 μs |  1.00 |    0.02 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   4.650 μs |  0.2282 μs | 0.0125 μs |  0.93 |    0.01 |      52 B |        0.18 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HGet             | HGET                 | 188.476 μs | 22.6345 μs | 1.2407 μs |  1.00 |    0.01 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 186.961 μs | 21.2833 μs | 1.1666 μs |  0.99 |    0.01 |      80 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HSet             | HSET                 | 187.001 μs |  9.1014 μs | 0.4989 μs |  1.00 |    0.00 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 186.865 μs | 39.5803 μs | 2.1695 μs |  1.00 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Incr             | INCR                 | 185.032 μs | 12.6166 μs | 0.6916 μs |  1.00 |    0.00 |     295 B |        1.00 |
| Respire_Incr                   | INCR                 | 187.644 μs | 14.0401 μs | 0.7696 μs |  1.01 |    0.00 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 363.261 μs | 20.5706 μs | 1.1275 μs |  1.00 |    0.00 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 360.547 μs | 14.5504 μs | 0.7976 μs |  0.99 |    0.00 |     256 B |        0.34 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping             | PING                 | 183.875 μs | 27.4491 μs | 1.5046 μs |  1.00 |    0.01 |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 184.386 μs |  6.2848 μs | 0.3445 μs |  1.00 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 168.303 μs | 55.5035 μs | 3.0423 μs |  1.00 |    0.02 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 165.553 μs | 52.3885 μs | 2.8716 μs |  0.98 |    0.02 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SAdd             | SADD                 | 185.168 μs |  8.3096 μs | 0.4555 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 185.415 μs | 13.4657 μs | 0.7381 μs |  1.00 |    0.00 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 197.622 μs |  4.1910 μs | 0.2297 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 197.804 μs | 18.9896 μs | 1.0409 μs |  1.00 |    0.00 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 186.457 μs | 10.0895 μs | 0.5530 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 190.112 μs | 15.1723 μs | 0.8316 μs |  1.02 |    0.00 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 187.779 μs |  8.0375 μs | 0.4406 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 189.824 μs | 12.9650 μs | 0.7107 μs |  1.01 |    0.00 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 169.802 μs | 41.5312 μs | 2.2765 μs |  1.00 |    0.02 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 169.697 μs | 44.2550 μs | 2.4258 μs |  1.00 |    0.02 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 360.549 μs | 28.0848 μs | 1.5394 μs |  1.00 |    0.01 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 355.938 μs | 38.6241 μs | 2.1171 μs |  0.99 |    0.01 |     200 B |        0.31 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 185.251 μs | 22.6378 μs | 1.2409 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 187.002 μs | 10.8168 μs | 0.5929 μs |  1.01 |    0.01 |      32 B |        0.10 |

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
| StackExchange_Exists           | EXISTS               |  86.027 μs | 28.7346 μs | 1.5750 μs |  1.00 |    0.02 |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               |  88.406 μs | 20.3644 μs | 1.1162 μs |  1.03 |    0.02 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get              | GET                  |  88.087 μs | 64.0658 μs | 3.5117 μs |  1.00 |    0.05 |     500 B |        1.00 |
| Respire_Get                    | GET                  |  86.616 μs | 22.7799 μs | 1.2486 μs |  0.98 |    0.04 |      80 B |        0.16 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  |  72.787 μs | 26.1876 μs | 1.4354 μs |  1.00 |    0.02 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  |  72.349 μs | 15.1608 μs | 0.8310 μs |  0.99 |    0.02 |      50 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   2.302 μs |  1.7475 μs | 0.0958 μs |  1.00 |    0.05 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   2.276 μs |  0.4639 μs | 0.0254 μs |  0.99 |    0.04 |      52 B |        0.18 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HGet             | HGET                 |  89.046 μs | 16.9308 μs | 0.9280 μs |  1.00 |    0.01 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 |  89.027 μs | 67.7852 μs | 3.7155 μs |  1.00 |    0.04 |      80 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HSet             | HSET                 |  91.041 μs | 30.5689 μs | 1.6756 μs |  1.00 |    0.02 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 |  88.981 μs | 10.4602 μs | 0.5734 μs |  0.98 |    0.02 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Incr             | INCR                 |  85.364 μs |  9.7826 μs | 0.5362 μs |  1.00 |    0.01 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 |  85.324 μs | 11.2545 μs | 0.6169 μs |  1.00 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 175.247 μs | 40.7897 μs | 2.2358 μs |  1.00 |    0.02 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 175.034 μs |  8.8887 μs | 0.4872 μs |  1.00 |    0.01 |     256 B |        0.34 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping             | PING                 |  87.499 μs | 12.6320 μs | 0.6924 μs |  1.00 |    0.01 |     304 B |        1.00 |
| Respire_Ping                   | PING                 |  84.155 μs | 24.1588 μs | 1.3242 μs |  0.96 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential |  70.727 μs | 13.9748 μs | 0.7660 μs |  1.00 |    0.01 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential |  74.495 μs | 86.4795 μs | 4.7402 μs |  1.05 |    0.06 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SAdd             | SADD                 |  91.500 μs |  8.1342 μs | 0.4459 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 |  86.244 μs |  4.8286 μs | 0.2647 μs |  0.94 |    0.00 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             |  97.494 μs | 36.5560 μs | 2.0038 μs |  1.00 |    0.03 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 102.595 μs | 73.8267 μs | 4.0467 μs |  1.05 |    0.04 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              |  94.029 μs |  7.5934 μs | 0.4162 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              |  87.317 μs |  0.6092 μs | 0.0334 μs |  0.93 |    0.00 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              |  89.988 μs | 25.5490 μs | 1.4004 μs |  1.00 |    0.02 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              |  92.573 μs |  5.7501 μs | 0.3152 μs |  1.03 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  |  73.848 μs | 45.9378 μs | 2.5180 μs |  1.00 |    0.04 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  |  75.570 μs | 14.3504 μs | 0.7866 μs |  1.02 |    0.03 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 167.915 μs | 22.4562 μs | 1.2309 μs |  1.00 |    0.01 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 164.370 μs | 69.5325 μs | 3.8113 μs |  0.98 |    0.02 |     198 B |        0.31 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            |  88.433 μs | 34.4012 μs | 1.8856 μs |  1.00 |    0.03 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            |  93.295 μs | 47.9878 μs | 2.6304 μs |  1.06 |    0.03 |      32 B |        0.10 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
