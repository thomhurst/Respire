---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-09 11:04 UTC from commit `03d6cb15049c`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31309682564) for logs and downloadable artifacts.
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
| StackExchange_Exists           | EXISTS               | 105.191 μs | 20.6737 μs | 1.1332 μs |  1.00 |    0.01 |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               |  99.377 μs |  4.6816 μs | 0.2566 μs |  0.94 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get              | GET                  | 100.399 μs | 17.6825 μs | 0.9692 μs |  1.00 |    0.01 |     504 B |        1.00 |
| Respire_Get                    | GET                  |  99.779 μs | 26.1823 μs | 1.4351 μs |  0.99 |    0.01 |      80 B |        0.16 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  |  86.893 μs | 56.3749 μs | 3.0901 μs |  1.00 |    0.04 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  |  82.074 μs | 20.5911 μs | 1.1287 μs |  0.95 |    0.03 |      50 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   3.098 μs |  1.1156 μs | 0.0611 μs |  1.00 |    0.02 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   2.737 μs |  0.9920 μs | 0.0544 μs |  0.88 |    0.02 |      52 B |        0.18 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HGet             | HGET                 | 102.019 μs |  5.0557 μs | 0.2771 μs |  1.00 |    0.00 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 101.266 μs |  7.9514 μs | 0.4358 μs |  0.99 |    0.00 |      80 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HSet             | HSET                 | 106.004 μs | 21.6473 μs | 1.1866 μs |  1.00 |    0.01 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 100.592 μs | 16.4825 μs | 0.9035 μs |  0.95 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Incr             | INCR                 | 100.894 μs | 22.8571 μs | 1.2529 μs |  1.00 |    0.02 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 |  98.770 μs | 25.0813 μs | 1.3748 μs |  0.98 |    0.02 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 192.204 μs | 41.0717 μs | 2.2513 μs |  1.00 |    0.01 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 192.626 μs | 26.1955 μs | 1.4359 μs |  1.00 |    0.01 |     256 B |        0.34 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping             | PING                 | 100.830 μs | 14.1618 μs | 0.7763 μs |  1.00 |    0.01 |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 101.291 μs | 22.5485 μs | 1.2360 μs |  1.00 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential |  83.367 μs |  7.7760 μs | 0.4262 μs |  1.00 |    0.01 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential |  87.004 μs |  6.0989 μs | 0.3343 μs |  1.04 |    0.01 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SAdd             | SADD                 | 101.685 μs |  4.5450 μs | 0.2491 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 100.438 μs | 38.9680 μs | 2.1360 μs |  0.99 |    0.02 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 105.309 μs |  7.8266 μs | 0.4290 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 104.683 μs |  4.5979 μs | 0.2520 μs |  0.99 |    0.00 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              |  99.931 μs | 16.8483 μs | 0.9235 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 100.677 μs |  4.4084 μs | 0.2416 μs |  1.01 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              |  98.819 μs | 28.5302 μs | 1.5638 μs |  1.00 |    0.02 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 103.716 μs | 10.7875 μs | 0.5913 μs |  1.05 |    0.02 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  |  90.726 μs |  9.7563 μs | 0.5348 μs |  1.00 |    0.01 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  |  82.653 μs | 17.8547 μs | 0.9787 μs |  0.91 |    0.01 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 190.828 μs | 66.2094 μs | 3.6292 μs |  1.00 |    0.02 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 192.641 μs | 24.5027 μs | 1.3431 μs |  1.01 |    0.02 |     200 B |        0.31 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 101.261 μs | 20.9335 μs | 1.1474 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            |  99.629 μs | 11.2586 μs | 0.6171 μs |  0.98 |    0.01 |      32 B |        0.10 |

## net8.0

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD EPYC 9V74 2.60GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 8.0.29 (8.0.29, 8.0.2926.32403), X64 RyuJIT x86-64-v4
  ShortRun : .NET 8.0.29 (8.0.29, 8.0.2926.32403), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                         | Categories           | Mean       | Error       | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|------------:|----------:|------:|--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               |  99.898 μs |  55.6969 μs | 3.0529 μs |  1.00 |    0.04 |      - |     289 B |        1.00 |
| Respire_Exists                 | EXISTS               |  81.604 μs |  53.2559 μs | 2.9191 μs |  0.82 |    0.03 |      - |      32 B |        0.11 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Get              | GET                  |  88.206 μs |  18.0067 μs | 0.9870 μs |  1.00 |    0.01 |      - |     482 B |        1.00 |
| Respire_Get                    | GET                  |  93.114 μs |  35.1306 μs | 1.9256 μs |  1.06 |    0.02 |      - |      80 B |        0.17 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  |  93.249 μs |  67.6222 μs | 3.7066 μs |  1.00 |    0.05 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  |  86.309 μs |  13.8750 μs | 0.7605 μs |  0.93 |    0.03 |      - |      50 B |        0.15 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   3.412 μs |   4.9459 μs | 0.2711 μs |  1.00 |    0.10 | 0.0098 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   3.178 μs |   0.5393 μs | 0.0296 μs |  0.94 |    0.07 |      - |      52 B |        0.18 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_HGet             | HGET                 | 100.508 μs |  34.7245 μs | 1.9034 μs |  1.00 |    0.02 |      - |     517 B |        1.00 |
| Respire_HGet                   | HGET                 |  98.387 μs |  73.5110 μs | 4.0294 μs |  0.98 |    0.04 |      - |      80 B |        0.15 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_HSet             | HSET                 |  97.938 μs |  82.4004 μs | 4.5166 μs |  1.00 |    0.06 |      - |     320 B |        1.00 |
| Respire_HSet                   | HSET                 |  91.535 μs |  81.8785 μs | 4.4880 μs |  0.94 |    0.06 |      - |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Incr             | INCR                 | 101.412 μs | 105.3288 μs | 5.7734 μs |  1.00 |    0.07 |      - |     291 B |        1.00 |
| Respire_Incr                   | INCR                 |  93.865 μs | 157.2121 μs | 8.6173 μs |  0.93 |    0.09 |      - |      32 B |        0.11 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 201.934 μs | 165.1410 μs | 9.0519 μs |  1.00 |    0.06 |      - |     755 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 200.481 μs |  52.3237 μs | 2.8680 μs |  0.99 |    0.04 |      - |     251 B |        0.33 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Ping             | PING                 |  98.488 μs |  34.3289 μs | 1.8817 μs |  1.00 |    0.02 |      - |     300 B |        1.00 |
| Respire_Ping                   | PING                 |  81.985 μs |  81.3747 μs | 4.4604 μs |  0.83 |    0.04 |      - |      32 B |        0.11 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential |  88.804 μs |  78.1244 μs | 4.2823 μs |  1.00 |    0.06 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential |  82.980 μs |  61.9255 μs | 3.3943 μs |  0.94 |    0.05 |      - |       2 B |       0.008 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 101.096 μs |  20.5655 μs | 1.1273 μs |  1.00 |    0.01 |      - |     308 B |        1.00 |
| Respire_SAdd                   | SADD                 |  85.488 μs |  50.8873 μs | 2.7893 μs |  0.85 |    0.03 |      - |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 119.801 μs |  22.0774 μs | 1.2101 μs |  1.00 |    0.01 |      - |     306 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 117.661 μs |  23.4051 μs | 1.2829 μs |  0.98 |    0.01 |      - |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              |  98.405 μs |  30.1750 μs | 1.6540 μs |  1.00 |    0.02 |      - |     307 B |        1.00 |
| Respire_Set_Small              | SET 13B              |  93.742 μs |  80.1743 μs | 4.3946 μs |  0.95 |    0.04 |      - |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 103.362 μs |  74.1996 μs | 4.0671 μs |  1.00 |    0.05 |      - |     308 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              |  92.912 μs | 153.4322 μs | 8.4101 μs |  0.90 |    0.08 |      - |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  |  98.667 μs |  25.7159 μs | 1.4096 μs |  1.00 |    0.02 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  |  89.140 μs |  94.1208 μs | 5.1591 μs |  0.90 |    0.05 |      - |       2 B |       0.008 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 194.546 μs |  52.6594 μs | 2.8864 μs |  1.00 |    0.02 |      - |     644 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 186.899 μs |  55.7844 μs | 3.0577 μs |  0.96 |    0.02 |      - |     197 B |        0.31 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 101.551 μs |  13.1065 μs | 0.7184 μs |  1.00 |    0.01 |      - |     310 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            |  88.759 μs |  82.1508 μs | 4.5030 μs |  0.87 |    0.04 |      - |      32 B |        0.10 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
