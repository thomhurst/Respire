---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-10 00:15 UTC from commit `86d0a3c86690`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31343697952) for logs and downloadable artifacts.
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
| StackExchange_Exists           | EXISTS               | 190.925 μs |  15.333 μs | 0.8405 μs |  1.00 |    0.01 |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 193.902 μs |  22.065 μs | 1.2094 μs |  1.02 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get              | GET                  | 193.563 μs |  19.336 μs | 1.0599 μs |  1.00 |    0.01 |     504 B |        1.00 |
| Respire_Get                    | GET                  | 193.692 μs |  20.196 μs | 1.1070 μs |  1.00 |    0.01 |      80 B |        0.16 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 175.461 μs |  46.767 μs | 2.5634 μs |  1.00 |    0.02 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 173.466 μs |  47.329 μs | 2.5943 μs |  0.99 |    0.02 |      50 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.464 μs |   2.544 μs | 0.1394 μs |  1.00 |    0.03 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   5.323 μs |   1.371 μs | 0.0752 μs |  0.97 |    0.02 |      52 B |        0.18 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HGet             | HGET                 | 193.683 μs |   5.342 μs | 0.2928 μs |  1.00 |    0.00 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 191.818 μs |   7.978 μs | 0.4373 μs |  0.99 |    0.00 |      80 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HSet             | HSET                 | 194.415 μs |  23.557 μs | 1.2912 μs |  1.00 |    0.01 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 193.327 μs |   8.155 μs | 0.4470 μs |  0.99 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Incr             | INCR                 | 193.887 μs |  35.964 μs | 1.9713 μs |  1.00 |    0.01 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 192.386 μs |  19.845 μs | 1.0878 μs |  0.99 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 374.686 μs |   7.391 μs | 0.4051 μs |  1.00 |    0.00 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 370.900 μs |  26.341 μs | 1.4438 μs |  0.99 |    0.00 |     256 B |        0.34 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping             | PING                 | 189.685 μs |  16.812 μs | 0.9215 μs |  1.00 |    0.01 |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 191.482 μs |  22.428 μs | 1.2293 μs |  1.01 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 169.849 μs | 108.515 μs | 5.9481 μs |  1.00 |    0.04 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 169.787 μs |  40.290 μs | 2.2084 μs |  1.00 |    0.03 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SAdd             | SADD                 | 193.259 μs |  59.202 μs | 3.2451 μs |  1.00 |    0.02 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 193.402 μs |  17.866 μs | 0.9793 μs |  1.00 |    0.02 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 203.081 μs |   2.742 μs | 0.1503 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 206.866 μs |  31.584 μs | 1.7312 μs |  1.02 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 193.402 μs |   4.303 μs | 0.2359 μs |  1.00 |    0.00 |     311 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 191.458 μs |  21.174 μs | 1.1606 μs |  0.99 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 195.429 μs |  13.030 μs | 0.7142 μs |  1.00 |    0.00 |     310 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 192.831 μs |  26.112 μs | 1.4313 μs |  0.99 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 177.516 μs |  12.008 μs | 0.6582 μs |  1.00 |    0.00 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 174.788 μs |  15.527 μs | 0.8511 μs |  0.98 |    0.01 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 372.684 μs |  26.974 μs | 1.4786 μs |  1.00 |    0.00 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 369.945 μs |   3.771 μs | 0.2067 μs |  0.99 |    0.00 |     200 B |        0.31 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 191.692 μs |  15.296 μs | 0.8385 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 192.947 μs |  19.752 μs | 1.0827 μs |  1.01 |    0.01 |      32 B |        0.10 |

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
| StackExchange_Exists           | EXISTS               | 194.135 μs | 107.538 μs | 5.8945 μs |  1.00 |    0.04 |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 191.551 μs |  21.074 μs | 1.1551 μs |  0.99 |    0.03 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get              | GET                  | 198.037 μs |  59.249 μs | 3.2476 μs |  1.00 |    0.02 |     504 B |        1.00 |
| Respire_Get                    | GET                  | 194.871 μs |  23.056 μs | 1.2638 μs |  0.98 |    0.02 |      80 B |        0.16 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 176.061 μs |  61.980 μs | 3.3973 μs |  1.00 |    0.02 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 179.482 μs |  28.528 μs | 1.5637 μs |  1.02 |    0.02 |      50 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.589 μs |   1.567 μs | 0.0859 μs |  1.00 |    0.02 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   5.517 μs |   1.187 μs | 0.0651 μs |  0.99 |    0.02 |      52 B |        0.18 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HGet             | HGET                 | 199.573 μs |   7.523 μs | 0.4124 μs |  1.00 |    0.00 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 194.918 μs |  22.462 μs | 1.2312 μs |  0.98 |    0.01 |      80 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HSet             | HSET                 | 193.541 μs |  48.171 μs | 2.6404 μs |  1.00 |    0.02 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 196.462 μs |   5.074 μs | 0.2781 μs |  1.02 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Incr             | INCR                 | 194.566 μs |  13.502 μs | 0.7401 μs |  1.00 |    0.00 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 194.214 μs |  11.232 μs | 0.6157 μs |  1.00 |    0.00 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 389.578 μs |   9.131 μs | 0.5005 μs |  1.00 |    0.00 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 383.920 μs |  27.347 μs | 1.4990 μs |  0.99 |    0.00 |     256 B |        0.34 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping             | PING                 | 190.194 μs |   4.075 μs | 0.2234 μs |  1.00 |    0.00 |     301 B |        1.00 |
| Respire_Ping                   | PING                 | 192.439 μs |  32.410 μs | 1.7765 μs |  1.01 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 178.901 μs |  59.903 μs | 3.2835 μs |  1.00 |    0.02 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 177.574 μs |  35.243 μs | 1.9318 μs |  0.99 |    0.02 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SAdd             | SADD                 | 192.131 μs |  69.893 μs | 3.8311 μs |  1.00 |    0.02 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 194.219 μs |  17.062 μs | 0.9352 μs |  1.01 |    0.02 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 204.160 μs |  44.148 μs | 2.4199 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 210.897 μs |  21.716 μs | 1.1903 μs |  1.03 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 184.904 μs |  53.687 μs | 2.9428 μs |  1.00 |    0.02 |     310 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 195.802 μs |  20.622 μs | 1.1303 μs |  1.06 |    0.02 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 194.543 μs |  30.349 μs | 1.6635 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 198.039 μs |  33.612 μs | 1.8424 μs |  1.02 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 180.382 μs |  39.618 μs | 2.1716 μs |  1.00 |    0.01 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 180.750 μs |  18.431 μs | 1.0103 μs |  1.00 |    0.01 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 382.155 μs |  64.801 μs | 3.5520 μs |  1.00 |    0.01 |     647 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 377.975 μs |  55.678 μs | 3.0519 μs |  0.99 |    0.01 |     200 B |        0.31 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 194.243 μs |   8.588 μs | 0.4708 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 195.835 μs |   6.824 μs | 0.3741 μs |  1.01 |    0.00 |      32 B |        0.10 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
