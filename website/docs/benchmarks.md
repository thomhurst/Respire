---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-09 00:09 UTC from commit `4b72854f7bc6`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31285386324) for logs and downloadable artifacts.
:::

StackExchange.Redis is the baseline. A ratio below `1.00` means Respire completed the operation faster.

## net10.0

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD EPYC 7763 3.10GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                         | Categories           | Mean       | Error      | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|-----------:|----------:|------:|--------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 176.926 μs |  4.0004 μs | 0.2193 μs |  1.00 |    0.00 |     295 B |        1.00 |
| Respire_Exists                 | EXISTS               | 175.966 μs | 18.7800 μs | 1.0294 μs |  0.99 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get              | GET                  | 177.452 μs | 40.3529 μs | 2.2119 μs |  1.00 |    0.02 |     504 B |        1.00 |
| Respire_Get                    | GET                  | 178.603 μs | 20.0561 μs | 1.0993 μs |  1.01 |    0.01 |      80 B |        0.16 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 158.000 μs | 16.6826 μs | 0.9144 μs |  1.00 |    0.01 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 157.337 μs | 18.5686 μs | 1.0178 μs |  1.00 |    0.01 |      50 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   4.897 μs |  0.1092 μs | 0.0060 μs |  1.00 |    0.00 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   4.402 μs |  0.5963 μs | 0.0327 μs |  0.90 |    0.01 |      52 B |        0.18 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HGet             | HGET                 | 178.249 μs | 37.4226 μs | 2.0513 μs |  1.00 |    0.01 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 179.212 μs | 17.7437 μs | 0.9726 μs |  1.01 |    0.01 |      80 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HSet             | HSET                 | 178.790 μs | 38.1302 μs | 2.0900 μs |  1.00 |    0.01 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 179.072 μs | 12.8305 μs | 0.7033 μs |  1.00 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Incr             | INCR                 | 178.231 μs | 35.0574 μs | 1.9216 μs |  1.00 |    0.01 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 178.685 μs | 20.1102 μs | 1.1023 μs |  1.00 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 346.821 μs | 39.6705 μs | 2.1745 μs |  1.00 |    0.01 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 342.913 μs | 30.8800 μs | 1.6926 μs |  0.99 |    0.01 |     256 B |        0.34 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping             | PING                 | 174.982 μs | 19.1662 μs | 1.0506 μs |  1.00 |    0.01 |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 176.902 μs |  9.6905 μs | 0.5312 μs |  1.01 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 157.871 μs | 25.6423 μs | 1.4055 μs |  1.00 |    0.01 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 156.055 μs | 19.0270 μs | 1.0429 μs |  0.99 |    0.01 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SAdd             | SADD                 | 175.804 μs | 42.1763 μs | 2.3118 μs |  1.00 |    0.02 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 176.775 μs | 34.1839 μs | 1.8737 μs |  1.01 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 189.173 μs | 10.0542 μs | 0.5511 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 190.005 μs | 13.8914 μs | 0.7614 μs |  1.00 |    0.00 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 180.759 μs | 33.5347 μs | 1.8381 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 180.550 μs | 11.3392 μs | 0.6215 μs |  1.00 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 181.303 μs | 17.7561 μs | 0.9733 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 182.125 μs | 19.4522 μs | 1.0662 μs |  1.00 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 161.500 μs | 23.7399 μs | 1.3013 μs |  1.00 |    0.01 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 160.961 μs |  1.7324 μs | 0.0950 μs |  1.00 |    0.01 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 342.948 μs | 31.5126 μs | 1.7273 μs |  1.00 |    0.01 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 337.570 μs | 16.7423 μs | 0.9177 μs |  0.98 |    0.00 |     200 B |        0.31 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 176.851 μs | 21.6490 μs | 1.1867 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 176.636 μs | 25.9524 μs | 1.4225 μs |  1.00 |    0.01 |      32 B |        0.10 |

## net8.0

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Xeon Platinum 8370C CPU 2.80GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 8.0.29 (8.0.29, 8.0.2926.32403), X64 RyuJIT x86-64-v4
  ShortRun : .NET 8.0.29 (8.0.29, 8.0.2926.32403), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                         | Categories           | Mean       | Error      | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|-----------:|----------:|------:|--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 111.333 μs |  4.3724 μs | 0.2397 μs |  1.00 |    0.00 |      - |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 112.073 μs | 30.9912 μs | 1.6987 μs |  1.01 |    0.01 |      - |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Get              | GET                  | 113.824 μs |  1.2890 μs | 0.0707 μs |  1.00 |    0.00 |      - |     504 B |        1.00 |
| Respire_Get                    | GET                  | 110.233 μs | 15.7120 μs | 0.8612 μs |  0.97 |    0.01 |      - |      80 B |        0.16 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  |  98.330 μs | 18.5625 μs | 1.0175 μs |  1.00 |    0.01 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  |  97.524 μs |  8.8890 μs | 0.4872 μs |  0.99 |    0.01 |      - |      50 B |        0.15 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   3.666 μs |  2.5097 μs | 0.1376 μs |  1.00 |    0.05 | 0.0098 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   3.305 μs |  0.5766 μs | 0.0316 μs |  0.90 |    0.03 |      - |      52 B |        0.18 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_HGet             | HGET                 | 114.521 μs | 35.3774 μs | 1.9392 μs |  1.00 |    0.02 |      - |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 111.275 μs | 10.4778 μs | 0.5743 μs |  0.97 |    0.01 |      - |      80 B |        0.15 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_HSet             | HSET                 | 115.332 μs | 14.4715 μs | 0.7932 μs |  1.00 |    0.01 |      - |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 112.082 μs | 38.6993 μs | 2.1212 μs |  0.97 |    0.02 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Incr             | INCR                 | 113.371 μs | 10.1471 μs | 0.5562 μs |  1.00 |    0.01 |      - |     295 B |        1.00 |
| Respire_Incr                   | INCR                 | 112.118 μs | 34.6761 μs | 1.9007 μs |  0.99 |    0.02 |      - |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 223.008 μs |  8.6642 μs | 0.4749 μs |  1.00 |    0.00 |      - |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 220.260 μs | 37.7579 μs | 2.0696 μs |  0.99 |    0.01 |      - |     256 B |        0.34 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Ping             | PING                 | 110.836 μs | 28.7335 μs | 1.5750 μs |  1.00 |    0.02 |      - |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 111.185 μs | 11.9120 μs | 0.6529 μs |  1.00 |    0.01 |      - |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential |  97.011 μs |  7.1941 μs | 0.3943 μs |  1.00 |    0.00 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential |  96.306 μs | 17.9535 μs | 0.9841 μs |  0.99 |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 114.188 μs | 16.9996 μs | 0.9318 μs |  1.00 |    0.01 |      - |     311 B |        1.00 |
| Respire_SAdd                   | SADD                 | 111.515 μs | 29.5872 μs | 1.6218 μs |  0.98 |    0.01 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 122.148 μs | 11.5493 μs | 0.6331 μs |  1.00 |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 121.249 μs |  2.5720 μs | 0.1410 μs |  0.99 |    0.00 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 112.305 μs | 49.2551 μs | 2.6998 μs |  1.00 |    0.03 |      - |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 113.980 μs |  7.4472 μs | 0.4082 μs |  1.02 |    0.02 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 114.897 μs | 23.5371 μs | 1.2901 μs |  1.00 |    0.01 |      - |     311 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 114.725 μs |  9.6782 μs | 0.5305 μs |  1.00 |    0.01 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  |  99.669 μs | 15.3502 μs | 0.8414 μs |  1.00 |    0.01 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  |  99.665 μs |  3.9923 μs | 0.2188 μs |  1.00 |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 220.240 μs | 13.9227 μs | 0.7631 μs |  1.00 |    0.00 |      - |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 217.264 μs |  5.0872 μs | 0.2788 μs |  0.99 |    0.00 |      - |     199 B |        0.31 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 114.527 μs | 11.7373 μs | 0.6434 μs |  1.00 |    0.01 |      - |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 112.324 μs | 11.8817 μs | 0.6513 μs |  0.98 |    0.01 |      - |      32 B |        0.10 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
