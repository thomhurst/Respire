---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-09 08:16 UTC from commit `4daca66a9af7`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31302927703) for logs and downloadable artifacts.
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
| Method                         | Categories           | Mean       | Error      | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|-----------:|----------:|------:|--------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 137.239 μs | 55.6873 μs | 3.0524 μs |  1.00 |    0.03 |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 139.624 μs | 61.2569 μs | 3.3577 μs |  1.02 |    0.03 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get              | GET                  | 143.480 μs | 26.3484 μs | 1.4442 μs |  1.00 |    0.01 |     497 B |        1.00 |
| Respire_Get                    | GET                  | 135.747 μs | 49.5185 μs | 2.7143 μs |  0.95 |    0.02 |      80 B |        0.16 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 119.434 μs | 17.8976 μs | 0.9810 μs |  1.00 |    0.01 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 116.257 μs |  9.7828 μs | 0.5362 μs |  0.97 |    0.01 |      50 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   4.389 μs |  3.7687 μs | 0.2066 μs |  1.00 |    0.06 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   4.392 μs |  0.3294 μs | 0.0181 μs |  1.00 |    0.04 |      52 B |        0.18 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HGet             | HGET                 | 149.811 μs | 20.4437 μs | 1.1206 μs |  1.00 |    0.01 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 144.060 μs | 52.5399 μs | 2.8799 μs |  0.96 |    0.02 |      80 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HSet             | HSET                 | 144.539 μs |  6.7500 μs | 0.3700 μs |  1.00 |    0.00 |     327 B |        1.00 |
| Respire_HSet                   | HSET                 | 145.433 μs |  3.5141 μs | 0.1926 μs |  1.01 |    0.00 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Incr             | INCR                 | 143.374 μs | 27.4554 μs | 1.5049 μs |  1.00 |    0.01 |     295 B |        1.00 |
| Respire_Incr                   | INCR                 | 140.981 μs | 48.9772 μs | 2.6846 μs |  0.98 |    0.02 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 293.593 μs | 28.6043 μs | 1.5679 μs |  1.00 |    0.01 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 283.150 μs | 81.1994 μs | 4.4508 μs |  0.96 |    0.01 |     256 B |        0.34 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping             | PING                 | 136.180 μs | 11.4444 μs | 0.6273 μs |  1.00 |    0.01 |     303 B |        1.00 |
| Respire_Ping                   | PING                 | 135.759 μs | 63.7108 μs | 3.4922 μs |  1.00 |    0.02 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 117.132 μs | 16.9691 μs | 0.9301 μs |  1.00 |    0.01 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 112.982 μs |  6.5323 μs | 0.3581 μs |  0.96 |    0.01 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SAdd             | SADD                 | 139.750 μs | 13.1843 μs | 0.7227 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 142.042 μs | 13.0058 μs | 0.7129 μs |  1.02 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 158.530 μs |  8.3203 μs | 0.4561 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 160.611 μs | 28.0092 μs | 1.5353 μs |  1.01 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 144.584 μs | 48.4078 μs | 2.6534 μs |  1.00 |    0.02 |     311 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 144.168 μs | 61.6281 μs | 3.3780 μs |  1.00 |    0.03 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 146.205 μs | 34.8595 μs | 1.9108 μs |  1.00 |    0.02 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 148.713 μs | 13.6360 μs | 0.7474 μs |  1.02 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 119.811 μs |  6.0729 μs | 0.3329 μs |  1.00 |    0.00 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 118.275 μs | 13.5015 μs | 0.7401 μs |  0.99 |    0.01 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 287.527 μs | 51.7287 μs | 2.8354 μs |  1.00 |    0.01 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 277.947 μs |  3.8552 μs | 0.2113 μs |  0.97 |    0.01 |     200 B |        0.31 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 139.427 μs |  7.7247 μs | 0.4234 μs |  1.00 |    0.00 |     309 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 139.261 μs | 39.3109 μs | 2.1548 μs |  1.00 |    0.01 |      32 B |        0.10 |

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
| StackExchange_Exists           | EXISTS               | 185.222 μs | 20.2529 μs | 1.1101 μs |  1.00 |    0.01 |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 187.467 μs |  7.8838 μs | 0.4321 μs |  1.01 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get              | GET                  | 189.352 μs | 45.1941 μs | 2.4772 μs |  1.00 |    0.02 |     504 B |        1.00 |
| Respire_Get                    | GET                  | 188.670 μs | 20.8910 μs | 1.1451 μs |  1.00 |    0.01 |      80 B |        0.16 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 174.086 μs | 41.1773 μs | 2.2571 μs |  1.00 |    0.02 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 173.381 μs | 26.0263 μs | 1.4266 μs |  1.00 |    0.01 |      50 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.486 μs |  0.1498 μs | 0.0082 μs |  1.00 |    0.00 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   5.001 μs |  0.9898 μs | 0.0543 μs |  0.91 |    0.01 |      52 B |        0.18 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HGet             | HGET                 | 193.144 μs | 36.4658 μs | 1.9988 μs |  1.00 |    0.01 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 188.681 μs | 22.8353 μs | 1.2517 μs |  0.98 |    0.01 |      80 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HSet             | HSET                 | 188.253 μs | 21.9703 μs | 1.2043 μs |  1.00 |    0.01 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 189.216 μs | 23.6538 μs | 1.2965 μs |  1.01 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Incr             | INCR                 | 188.029 μs | 10.0226 μs | 0.5494 μs |  1.00 |    0.00 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 189.210 μs | 10.1119 μs | 0.5543 μs |  1.01 |    0.00 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 381.164 μs | 55.4694 μs | 3.0405 μs |  1.00 |    0.01 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 367.746 μs | 41.9565 μs | 2.2998 μs |  0.96 |    0.01 |     256 B |        0.34 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping             | PING                 | 186.223 μs | 17.3763 μs | 0.9525 μs |  1.00 |    0.01 |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 185.805 μs | 23.1561 μs | 1.2693 μs |  1.00 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 173.164 μs | 20.3288 μs | 1.1143 μs |  1.00 |    0.01 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 169.934 μs | 34.4638 μs | 1.8891 μs |  0.98 |    0.01 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SAdd             | SADD                 | 186.320 μs | 57.3877 μs | 3.1456 μs |  1.00 |    0.02 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 190.496 μs | 20.6708 μs | 1.1330 μs |  1.02 |    0.02 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 200.654 μs | 32.9824 μs | 1.8079 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 200.904 μs |  8.0364 μs | 0.4405 μs |  1.00 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 189.698 μs | 27.9791 μs | 1.5336 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 191.924 μs |  8.8816 μs | 0.4868 μs |  1.01 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 191.061 μs | 25.2086 μs | 1.3818 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 188.610 μs | 65.0675 μs | 3.5666 μs |  0.99 |    0.02 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 177.334 μs | 44.6199 μs | 2.4458 μs |  1.00 |    0.02 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 174.575 μs | 87.9422 μs | 4.8204 μs |  0.98 |    0.03 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 373.423 μs | 54.5672 μs | 2.9910 μs |  1.00 |    0.01 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 365.824 μs | 30.7303 μs | 1.6844 μs |  0.98 |    0.01 |     200 B |        0.31 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 187.697 μs | 19.0974 μs | 1.0468 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 189.262 μs | 14.0973 μs | 0.7727 μs |  1.01 |    0.01 |      32 B |        0.10 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
