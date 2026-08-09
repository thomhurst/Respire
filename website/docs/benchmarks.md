---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-09 11:02 UTC from commit `e916cc003024`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31309599842) for logs and downloadable artifacts.
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
| StackExchange_Exists           | EXISTS               | 186.076 μs | 44.9819 μs | 2.4656 μs |  1.00 |    0.02 |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 190.537 μs | 14.9115 μs | 0.8174 μs |  1.02 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get              | GET                  | 189.683 μs | 13.9037 μs | 0.7621 μs |  1.00 |    0.00 |     504 B |        1.00 |
| Respire_Get                    | GET                  | 189.168 μs | 20.1368 μs | 1.1038 μs |  1.00 |    0.01 |      80 B |        0.16 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 173.833 μs | 43.2021 μs | 2.3681 μs |  1.00 |    0.02 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 172.157 μs | 32.8221 μs | 1.7991 μs |  0.99 |    0.01 |      50 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.316 μs |  0.6429 μs | 0.0352 μs |  1.00 |    0.01 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   4.824 μs |  0.3821 μs | 0.0209 μs |  0.91 |    0.01 |      52 B |        0.18 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HGet             | HGET                 | 191.136 μs |  7.1835 μs | 0.3938 μs |  1.00 |    0.00 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 190.177 μs | 12.5924 μs | 0.6902 μs |  0.99 |    0.00 |      80 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HSet             | HSET                 | 189.843 μs | 15.0198 μs | 0.8233 μs |  1.00 |    0.01 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 190.199 μs | 12.8571 μs | 0.7047 μs |  1.00 |    0.00 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Incr             | INCR                 | 187.970 μs |  4.7336 μs | 0.2595 μs |  1.00 |    0.00 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 188.446 μs | 30.3772 μs | 1.6651 μs |  1.00 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 367.446 μs | 42.2558 μs | 2.3162 μs |  1.00 |    0.01 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 364.418 μs | 13.0565 μs | 0.7157 μs |  0.99 |    0.01 |     256 B |        0.34 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping             | PING                 | 185.270 μs | 13.2603 μs | 0.7268 μs |  1.00 |    0.00 |     301 B |        1.00 |
| Respire_Ping                   | PING                 | 186.772 μs |  8.1442 μs | 0.4464 μs |  1.01 |    0.00 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 171.939 μs | 36.7272 μs | 2.0131 μs |  1.00 |    0.01 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 171.235 μs | 11.1364 μs | 0.6104 μs |  1.00 |    0.01 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SAdd             | SADD                 | 189.286 μs | 12.2878 μs | 0.6735 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 187.072 μs | 73.9782 μs | 4.0550 μs |  0.99 |    0.02 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 201.805 μs | 24.2280 μs | 1.3280 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 199.900 μs | 19.2818 μs | 1.0569 μs |  0.99 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 188.441 μs | 25.5711 μs | 1.4016 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 192.491 μs |  6.0250 μs | 0.3302 μs |  1.02 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 193.636 μs | 18.3207 μs | 1.0042 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 192.257 μs |  3.7230 μs | 0.2041 μs |  0.99 |    0.00 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 175.877 μs |  3.5608 μs | 0.1952 μs |  1.00 |    0.00 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 174.832 μs |  8.9782 μs | 0.4921 μs |  0.99 |    0.00 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 364.517 μs | 52.0349 μs | 2.8522 μs |  1.00 |    0.01 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 361.430 μs | 12.5099 μs | 0.6857 μs |  0.99 |    0.01 |     200 B |        0.31 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 187.074 μs |  7.0658 μs | 0.3873 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 188.774 μs | 13.0391 μs | 0.7147 μs |  1.01 |    0.00 |      32 B |        0.10 |

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
| StackExchange_Exists           | EXISTS               |  91.020 μs |  31.2810 μs | 1.7146 μs |  1.00 |    0.02 |      - |     293 B |        1.00 |
| Respire_Exists                 | EXISTS               |  78.197 μs |  29.5646 μs | 1.6205 μs |  0.86 |    0.02 |      - |      32 B |        0.11 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Get              | GET                  |  92.530 μs |  40.4318 μs | 2.2162 μs |  1.00 |    0.03 |      - |     483 B |        1.00 |
| Respire_Get                    | GET                  |  83.913 μs |  14.6482 μs | 0.8029 μs |  0.91 |    0.02 |      - |      80 B |        0.17 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  |  79.120 μs |  25.9992 μs | 1.4251 μs |  1.00 |    0.02 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  |  84.182 μs |   4.9227 μs | 0.2698 μs |  1.06 |    0.02 |      - |      50 B |        0.15 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   3.262 μs |   0.2659 μs | 0.0146 μs |  1.00 |    0.01 | 0.0098 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   3.095 μs |   0.1928 μs | 0.0106 μs |  0.95 |    0.00 |      - |      52 B |        0.18 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_HGet             | HGET                 |  88.791 μs |  50.1793 μs | 2.7505 μs |  1.00 |    0.04 |      - |     489 B |        1.00 |
| Respire_HGet                   | HGET                 |  86.614 μs |  86.7288 μs | 4.7539 μs |  0.98 |    0.05 |      - |      80 B |        0.16 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_HSet             | HSET                 |  91.523 μs |  43.4272 μs | 2.3804 μs |  1.00 |    0.03 |      - |     311 B |        1.00 |
| Respire_HSet                   | HSET                 |  86.017 μs |  69.4496 μs | 3.8068 μs |  0.94 |    0.04 |      - |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Incr             | INCR                 |  95.243 μs |   9.2101 μs | 0.5048 μs |  1.00 |    0.01 |      - |     291 B |        1.00 |
| Respire_Incr                   | INCR                 |  84.905 μs |  33.8698 μs | 1.8565 μs |  0.89 |    0.02 |      - |      32 B |        0.11 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 193.382 μs | 181.0701 μs | 9.9251 μs |  1.00 |    0.06 |      - |     757 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 186.006 μs | 143.9595 μs | 7.8909 μs |  0.96 |    0.06 |      - |     251 B |        0.33 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Ping             | PING                 |  86.105 μs |  30.7397 μs | 1.6849 μs |  1.00 |    0.02 |      - |     297 B |        1.00 |
| Respire_Ping                   | PING                 |  78.546 μs |  51.2891 μs | 2.8113 μs |  0.91 |    0.03 |      - |      32 B |        0.11 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential |  81.258 μs |   7.5961 μs | 0.4164 μs |  1.00 |    0.01 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential |  77.430 μs |  55.4532 μs | 3.0396 μs |  0.95 |    0.03 |      - |       2 B |       0.008 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_SAdd             | SADD                 |  92.379 μs |   8.5592 μs | 0.4692 μs |  1.00 |    0.01 |      - |     308 B |        1.00 |
| Respire_SAdd                   | SADD                 |  87.066 μs |  47.1545 μs | 2.5847 μs |  0.94 |    0.02 |      - |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 110.711 μs |  61.1660 μs | 3.3527 μs |  1.00 |    0.04 |      - |     311 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 106.406 μs |  47.4414 μs | 2.6004 μs |  0.96 |    0.03 |      - |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              |  99.652 μs |  27.4400 μs | 1.5041 μs |  1.00 |    0.02 |      - |     309 B |        1.00 |
| Respire_Set_Small              | SET 13B              |  80.381 μs |  38.9495 μs | 2.1350 μs |  0.81 |    0.02 |      - |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              |  98.717 μs |  43.1515 μs | 2.3653 μs |  1.00 |    0.03 |      - |     308 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              |  93.635 μs |  26.0841 μs | 1.4298 μs |  0.95 |    0.02 |      - |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  |  85.218 μs |   7.8064 μs | 0.4279 μs |  1.00 |    0.01 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  |  80.586 μs |  24.6406 μs | 1.3506 μs |  0.95 |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 190.286 μs |  85.8052 μs | 4.7033 μs |  1.00 |    0.03 |      - |     644 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 168.006 μs |  72.8588 μs | 3.9936 μs |  0.88 |    0.03 |      - |     195 B |        0.30 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            |  92.407 μs |  30.1116 μs | 1.6505 μs |  1.00 |    0.02 |      - |     306 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            |  85.135 μs |  14.8185 μs | 0.8123 μs |  0.92 |    0.02 |      - |      32 B |        0.10 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
