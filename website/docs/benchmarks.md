---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-08 23:00 UTC from commit `93383b71ecd1`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31282816204) for logs and downloadable artifacts.
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
| StackExchange_Exists           | EXISTS               | 176.703 μs |  6.5980 μs | 0.3617 μs |  1.00 |    0.00 |      - |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 176.672 μs |  7.3893 μs | 0.4050 μs |  1.00 |    0.00 |      - |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Get              | GET                  | 178.269 μs | 35.9294 μs | 1.9694 μs |  1.00 |    0.01 |      - |     504 B |        1.00 |
| Respire_Get                    | GET                  | 178.557 μs |  6.8108 μs | 0.3733 μs |  1.00 |    0.01 |      - |      80 B |        0.16 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 160.057 μs | 13.6575 μs | 0.7486 μs |  1.00 |    0.01 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 158.751 μs |  5.9229 μs | 0.3247 μs |  0.99 |    0.00 |      - |      50 B |        0.15 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   4.805 μs |  0.4385 μs | 0.0240 μs |  1.00 |    0.01 | 0.0098 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   4.413 μs |  0.2911 μs | 0.0160 μs |  0.92 |    0.00 |      - |      52 B |        0.18 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_HGet             | HGET                 | 178.390 μs |  8.4890 μs | 0.4653 μs |  1.00 |    0.00 |      - |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 178.416 μs |  3.9854 μs | 0.2185 μs |  1.00 |    0.00 |      - |      80 B |        0.15 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_HSet             | HSET                 | 178.753 μs |  4.3108 μs | 0.2363 μs |  1.00 |    0.00 |      - |     327 B |        1.00 |
| Respire_HSet                   | HSET                 | 178.979 μs |  5.2150 μs | 0.2859 μs |  1.00 |    0.00 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Incr             | INCR                 | 177.485 μs | 16.3025 μs | 0.8936 μs |  1.00 |    0.01 |      - |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 178.292 μs | 13.8163 μs | 0.7573 μs |  1.00 |    0.01 |      - |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 346.795 μs | 26.6454 μs | 1.4605 μs |  1.00 |    0.01 |      - |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 342.713 μs | 40.3767 μs | 2.2132 μs |  0.99 |    0.01 |      - |     576 B |        0.76 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Ping             | PING                 | 174.831 μs | 25.9756 μs | 1.4238 μs |  1.00 |    0.01 |      - |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 174.253 μs | 65.8129 μs | 3.6074 μs |  1.00 |    0.02 |      - |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 155.919 μs | 27.9998 μs | 1.5348 μs |  1.00 |    0.01 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 152.736 μs | 44.9647 μs | 2.4647 μs |  0.98 |    0.02 |      - |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 178.560 μs | 30.7563 μs | 1.6859 μs |  1.00 |    0.01 |      - |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 178.222 μs | 11.4899 μs | 0.6298 μs |  1.00 |    0.01 |      - |      96 B |        0.31 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 187.322 μs | 14.3992 μs | 0.7893 μs |  1.00 |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 188.561 μs |  7.7828 μs | 0.4266 μs |  1.01 |    0.00 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 177.572 μs | 38.5646 μs | 2.1139 μs |  1.00 |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 179.419 μs |  1.5784 μs | 0.0865 μs |  1.01 |    0.01 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 179.679 μs |  6.8339 μs | 0.3746 μs |  1.00 |    0.00 |      - |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 180.849 μs |  9.8061 μs | 0.5375 μs |  1.01 |    0.00 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 161.375 μs | 14.6382 μs | 0.8024 μs |  1.00 |    0.01 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 159.659 μs | 19.2372 μs | 1.0545 μs |  0.99 |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 339.764 μs | 50.1325 μs | 2.7479 μs |  1.00 |    0.01 |      - |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 337.009 μs | 31.3296 μs | 1.7173 μs |  0.99 |    0.01 |      - |     264 B |        0.41 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 177.101 μs | 13.2592 μs | 0.7268 μs |  1.00 |    0.01 |      - |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 176.888 μs | 16.9766 μs | 0.9305 μs |  1.00 |    0.01 |      - |      32 B |        0.10 |

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
| Method                         | Categories           | Mean       | Error     | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|----------:|----------:|------:|--------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 193.023 μs | 18.416 μs | 1.0094 μs |  1.00 |    0.01 |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 192.902 μs | 39.046 μs | 2.1403 μs |  1.00 |    0.01 |      32 B |        0.11 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Get              | GET                  | 197.037 μs | 46.402 μs | 2.5434 μs |  1.00 |    0.02 |     504 B |        1.00 |
| Respire_Get                    | GET                  | 193.038 μs | 26.946 μs | 1.4770 μs |  0.98 |    0.01 |      80 B |        0.16 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 177.877 μs | 34.442 μs | 1.8879 μs |  1.00 |    0.01 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 179.525 μs | 42.195 μs | 2.3128 μs |  1.01 |    0.01 |      50 B |        0.15 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.378 μs |  1.266 μs | 0.0694 μs |  1.00 |    0.02 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   4.963 μs |  1.437 μs | 0.0788 μs |  0.92 |    0.02 |      52 B |        0.18 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_HGet             | HGET                 | 198.034 μs | 16.406 μs | 0.8993 μs |  1.00 |    0.01 |     519 B |        1.00 |
| Respire_HGet                   | HGET                 | 194.016 μs | 65.659 μs | 3.5990 μs |  0.98 |    0.02 |      80 B |        0.15 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_HSet             | HSET                 | 193.755 μs | 23.052 μs | 1.2635 μs |  1.00 |    0.01 |     327 B |        1.00 |
| Respire_HSet                   | HSET                 | 193.830 μs | 23.074 μs | 1.2647 μs |  1.00 |    0.01 |      32 B |        0.10 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Incr             | INCR                 | 192.832 μs | 22.501 μs | 1.2334 μs |  1.00 |    0.01 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 194.736 μs |  3.790 μs | 0.2077 μs |  1.01 |    0.01 |      32 B |        0.11 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 387.340 μs | 47.481 μs | 2.6026 μs |  1.00 |    0.01 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 381.690 μs | 26.326 μs | 1.4430 μs |  0.99 |    0.01 |     576 B |        0.76 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Ping             | PING                 | 191.161 μs | 19.263 μs | 1.0559 μs |  1.00 |    0.01 |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 192.277 μs | 17.413 μs | 0.9544 μs |  1.01 |    0.01 |      32 B |        0.11 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 176.195 μs | 27.023 μs | 1.4812 μs |  1.00 |    0.01 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 175.592 μs | 43.278 μs | 2.3722 μs |  1.00 |    0.01 |       2 B |       0.008 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_SAdd             | SADD                 | 193.965 μs | 21.410 μs | 1.1736 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 191.134 μs | 47.373 μs | 2.5967 μs |  0.99 |    0.01 |      96 B |        0.31 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 201.869 μs | 73.799 μs | 4.0452 μs |  1.00 |    0.02 |     311 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 206.790 μs | 14.073 μs | 0.7714 μs |  1.02 |    0.02 |      32 B |        0.10 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 187.565 μs | 56.757 μs | 3.1111 μs |  1.00 |    0.02 |     311 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 186.451 μs | 21.326 μs | 1.1689 μs |  0.99 |    0.02 |      32 B |        0.10 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 196.142 μs | 27.301 μs | 1.4965 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 195.895 μs | 11.322 μs | 0.6206 μs |  1.00 |    0.01 |      32 B |        0.10 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 179.511 μs | 79.140 μs | 4.3379 μs |  1.00 |    0.03 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 180.262 μs | 22.931 μs | 1.2569 μs |  1.00 |    0.02 |       2 B |       0.008 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 382.663 μs | 33.348 μs | 1.8279 μs |  1.00 |    0.01 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 375.632 μs | 34.580 μs | 1.8954 μs |  0.98 |    0.01 |     264 B |        0.41 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 193.702 μs |  7.116 μs | 0.3901 μs |  1.00 |    0.00 |     310 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 193.547 μs | 29.204 μs | 1.6007 μs |  1.00 |    0.01 |      32 B |        0.10 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
