---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-10 00:09 UTC from commit `b19d17d7f46b`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31343488103) for logs and downloadable artifacts.
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
| Method                         | Categories           | Mean       | Error      | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|-----------:|----------:|------:|--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 135.130 μs | 13.6901 μs | 0.7504 μs |  1.00 |    0.01 |      - |     295 B |        1.00 |
| Respire_Exists                 | EXISTS               | 142.527 μs | 16.6762 μs | 0.9141 μs |  1.05 |    0.01 |      - |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Get              | GET                  | 145.375 μs | 59.6943 μs | 3.2720 μs |  1.00 |    0.03 |      - |     504 B |        1.00 |
| Respire_Get                    | GET                  | 141.362 μs | 12.0280 μs | 0.6593 μs |  0.97 |    0.02 |      - |      80 B |        0.16 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 116.578 μs |  9.5675 μs | 0.5244 μs |  1.00 |    0.01 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 115.653 μs | 14.1400 μs | 0.7751 μs |  0.99 |    0.01 |      - |      50 B |        0.15 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   4.673 μs |  0.2060 μs | 0.0113 μs |  1.00 |    0.00 | 0.0098 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   4.622 μs |  1.0852 μs | 0.0595 μs |  0.99 |    0.01 |      - |      52 B |        0.18 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_HGet             | HGET                 | 146.518 μs | 42.2230 μs | 2.3144 μs |  1.00 |    0.02 |      - |     519 B |        1.00 |
| Respire_HGet                   | HGET                 | 143.177 μs | 10.7116 μs | 0.5871 μs |  0.98 |    0.01 |      - |      80 B |        0.15 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_HSet             | HSET                 | 141.988 μs | 26.1995 μs | 1.4361 μs |  1.00 |    0.01 |      - |     324 B |        1.00 |
| Respire_HSet                   | HSET                 | 143.055 μs | 10.0061 μs | 0.5485 μs |  1.01 |    0.01 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Incr             | INCR                 | 139.328 μs | 11.0122 μs | 0.6036 μs |  1.00 |    0.01 |      - |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 141.169 μs |  2.4408 μs | 0.1338 μs |  1.01 |    0.00 |      - |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 286.301 μs | 46.0357 μs | 2.5234 μs |  1.00 |    0.01 |      - |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 277.770 μs | 49.4042 μs | 2.7080 μs |  0.97 |    0.01 |      - |     256 B |        0.34 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Ping             | PING                 | 135.873 μs |  7.1605 μs | 0.3925 μs |  1.00 |    0.00 |      - |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 138.305 μs | 32.2892 μs | 1.7699 μs |  1.02 |    0.01 |      - |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 113.768 μs | 12.2511 μs | 0.6715 μs |  1.00 |    0.01 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 112.893 μs | 11.6635 μs | 0.6393 μs |  0.99 |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 136.163 μs | 24.8566 μs | 1.3625 μs |  1.00 |    0.01 |      - |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 141.708 μs |  9.7299 μs | 0.5333 μs |  1.04 |    0.01 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 155.352 μs | 14.2448 μs | 0.7808 μs |  1.00 |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 159.239 μs | 13.9738 μs | 0.7660 μs |  1.03 |    0.01 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 140.951 μs | 20.3805 μs | 1.1171 μs |  1.00 |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 139.065 μs | 14.1523 μs | 0.7757 μs |  0.99 |    0.01 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 145.269 μs |  2.2811 μs | 0.1250 μs |  1.00 |    0.00 |      - |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 141.992 μs | 29.0870 μs | 1.5944 μs |  0.98 |    0.01 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 117.193 μs | 30.7388 μs | 1.6849 μs |  1.00 |    0.02 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 116.077 μs |  7.8580 μs | 0.4307 μs |  0.99 |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 277.609 μs |  9.5706 μs | 0.5246 μs |  1.00 |    0.00 |      - |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 273.115 μs | 24.1672 μs | 1.3247 μs |  0.98 |    0.00 |      - |     200 B |        0.31 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 135.920 μs | 69.2036 μs | 3.7933 μs |  1.00 |    0.03 |      - |     310 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 139.286 μs | 33.5000 μs | 1.8362 μs |  1.03 |    0.03 |      - |      32 B |        0.10 |

## net8.0

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD EPYC 9V74 2.60GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 8.0.29 (8.0.29, 8.0.2926.32403), X64 RyuJIT x86-64-v3
  ShortRun : .NET 8.0.29 (8.0.29, 8.0.2926.32403), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                         | Categories           | Mean       | Error       | StdDev     | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|------------:|-----------:|------:|--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 133.534 μs |  23.3209 μs |  1.2783 μs |  1.00 |    0.01 |      - |     292 B |        1.00 |
| Respire_Exists                 | EXISTS               | 133.310 μs |  24.2226 μs |  1.3277 μs |  1.00 |    0.01 |      - |      32 B |        0.11 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Get              | GET                  | 137.103 μs |  65.8613 μs |  3.6101 μs |  1.00 |    0.03 |      - |     499 B |        1.00 |
| Respire_Get                    | GET                  | 139.470 μs |  20.6589 μs |  1.1324 μs |  1.02 |    0.02 |      - |      80 B |        0.16 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 120.145 μs |  23.9354 μs |  1.3120 μs |  1.00 |    0.01 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 112.992 μs |  23.7824 μs |  1.3036 μs |  0.94 |    0.01 |      - |      50 B |        0.15 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   4.663 μs |   0.9137 μs |  0.0501 μs |  1.00 |    0.01 | 0.0098 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   4.609 μs |   1.9673 μs |  0.1078 μs |  0.99 |    0.02 |      - |      52 B |        0.18 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_HGet             | HGET                 | 143.181 μs |  29.8814 μs |  1.6379 μs |  1.00 |    0.01 |      - |     517 B |        1.00 |
| Respire_HGet                   | HGET                 | 142.312 μs |  35.5616 μs |  1.9493 μs |  0.99 |    0.02 |      - |      80 B |        0.15 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_HSet             | HSET                 | 134.079 μs |  13.5363 μs |  0.7420 μs |  1.00 |    0.01 |      - |     326 B |        1.00 |
| Respire_HSet                   | HSET                 | 141.643 μs |  38.1337 μs |  2.0902 μs |  1.06 |    0.01 |      - |      32 B |        0.10 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Incr             | INCR                 | 131.131 μs |  45.0394 μs |  2.4688 μs |  1.00 |    0.02 |      - |     294 B |        1.00 |
| Respire_Incr                   | INCR                 | 140.350 μs |  58.2894 μs |  3.1950 μs |  1.07 |    0.03 |      - |      32 B |        0.11 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 295.200 μs | 187.2469 μs | 10.2636 μs |  1.00 |    0.04 |      - |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 280.569 μs |  47.0413 μs |  2.5785 μs |  0.95 |    0.03 |      - |     256 B |        0.34 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Ping             | PING                 | 124.444 μs | 103.0167 μs |  5.6467 μs |  1.00 |    0.06 |      - |     301 B |        1.00 |
| Respire_Ping                   | PING                 | 126.801 μs |  74.1089 μs |  4.0622 μs |  1.02 |    0.05 |      - |      32 B |        0.11 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 113.029 μs |  16.3923 μs |  0.8985 μs |  1.00 |    0.01 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 103.496 μs |  31.1621 μs |  1.7081 μs |  0.92 |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 126.813 μs |  35.8750 μs |  1.9664 μs |  1.00 |    0.02 |      - |     306 B |        1.00 |
| Respire_SAdd                   | SADD                 | 135.388 μs |  36.0332 μs |  1.9751 μs |  1.07 |    0.02 |      - |      32 B |        0.10 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 169.891 μs |  39.8128 μs |  2.1823 μs |  1.00 |    0.02 |      - |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 162.197 μs |  24.2814 μs |  1.3309 μs |  0.95 |    0.01 |      - |      32 B |        0.10 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 126.173 μs | 114.4755 μs |  6.2748 μs |  1.00 |    0.06 |      - |     311 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 137.894 μs | 104.6623 μs |  5.7369 μs |  1.09 |    0.06 |      - |      32 B |        0.10 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 140.326 μs |  58.6017 μs |  3.2122 μs |  1.00 |    0.03 |      - |     311 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 140.182 μs |  94.7392 μs |  5.1930 μs |  1.00 |    0.04 |      - |      32 B |        0.10 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 116.810 μs |  65.0714 μs |  3.5668 μs |  1.00 |    0.04 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 116.218 μs |  47.6448 μs |  2.6116 μs |  1.00 |    0.03 |      - |       2 B |       0.008 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 230.715 μs | 542.0347 μs | 29.7108 μs |  1.01 |    0.17 |      - |     638 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 242.706 μs | 128.6424 μs |  7.0513 μs |  1.06 |    0.13 |      - |     188 B |        0.29 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 130.663 μs |  27.0938 μs |  1.4851 μs |  1.00 |    0.01 |      - |     309 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 138.049 μs |  76.2450 μs |  4.1792 μs |  1.06 |    0.03 |      - |      32 B |        0.10 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
