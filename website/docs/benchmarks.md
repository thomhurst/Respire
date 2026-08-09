---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-09 00:55 UTC from commit `1e602f563ccd`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31287053268) for logs and downloadable artifacts.
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
| StackExchange_Exists           | EXISTS               | 133.391 μs |  57.811 μs | 3.1688 μs |  1.00 |    0.03 |      - |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 122.013 μs |  47.799 μs | 2.6200 μs |  0.92 |    0.03 |      - |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Get              | GET                  | 126.513 μs | 122.426 μs | 6.7106 μs |  1.00 |    0.06 |      - |     502 B |        1.00 |
| Respire_Get                    | GET                  | 130.607 μs |  73.102 μs | 4.0070 μs |  1.03 |    0.05 |      - |      80 B |        0.16 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 112.266 μs |  19.298 μs | 1.0578 μs |  1.00 |    0.01 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 109.535 μs |  15.022 μs | 0.8234 μs |  0.98 |    0.01 |      - |      50 B |        0.15 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   4.031 μs |   1.496 μs | 0.0820 μs |  1.00 |    0.02 | 0.0098 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   4.034 μs |   1.335 μs | 0.0732 μs |  1.00 |    0.02 |      - |      52 B |        0.18 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_HGet             | HGET                 | 132.851 μs |  37.819 μs | 2.0730 μs |  1.00 |    0.02 |      - |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 125.055 μs | 124.639 μs | 6.8319 μs |  0.94 |    0.05 |      - |      80 B |        0.15 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_HSet             | HSET                 | 132.294 μs |  46.058 μs | 2.5246 μs |  1.00 |    0.02 |      - |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 128.447 μs |  67.214 μs | 3.6843 μs |  0.97 |    0.03 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Incr             | INCR                 | 128.554 μs |  25.689 μs | 1.4081 μs |  1.00 |    0.01 |      - |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 130.607 μs |  33.293 μs | 1.8249 μs |  1.02 |    0.02 |      - |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 262.543 μs | 108.245 μs | 5.9333 μs |  1.00 |    0.03 |      - |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 259.147 μs |  38.002 μs | 2.0830 μs |  0.99 |    0.02 |      - |     256 B |        0.34 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Ping             | PING                 | 126.824 μs |  44.238 μs | 2.4248 μs |  1.00 |    0.02 |      - |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 129.135 μs |  46.562 μs | 2.5522 μs |  1.02 |    0.02 |      - |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 107.586 μs |  21.934 μs | 1.2023 μs |  1.00 |    0.01 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 106.619 μs |  16.411 μs | 0.8996 μs |  0.99 |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 128.424 μs |  81.842 μs | 4.4861 μs |  1.00 |    0.04 |      - |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 127.942 μs |  36.287 μs | 1.9890 μs |  1.00 |    0.03 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 151.017 μs | 105.438 μs | 5.7794 μs |  1.00 |    0.05 |      - |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 150.504 μs |  31.571 μs | 1.7305 μs |  1.00 |    0.03 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 129.924 μs |  21.649 μs | 1.1866 μs |  1.00 |    0.01 |      - |     311 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 129.378 μs |  73.555 μs | 4.0318 μs |  1.00 |    0.03 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 134.093 μs |  21.531 μs | 1.1802 μs |  1.00 |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 125.246 μs |  74.973 μs | 4.1095 μs |  0.93 |    0.03 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 111.456 μs |  31.133 μs | 1.7065 μs |  1.00 |    0.02 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 110.091 μs |  19.801 μs | 1.0854 μs |  0.99 |    0.02 |      - |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 250.254 μs |  18.937 μs | 1.0380 μs |  1.00 |    0.01 |      - |     647 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 231.995 μs | 113.777 μs | 6.2365 μs |  0.93 |    0.02 |      - |     198 B |        0.31 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 123.763 μs |  16.796 μs | 0.9206 μs |  1.00 |    0.01 |      - |     311 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 122.392 μs |  39.652 μs | 2.1735 μs |  0.99 |    0.02 |      - |      32 B |        0.10 |

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
| Method                         | Categories           | Mean       | Error       | StdDev     | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|------------:|-----------:|------:|--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 121.568 μs |   8.2095 μs |  0.4500 μs |  1.00 |    0.00 |      - |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 123.665 μs |  38.7310 μs |  2.1230 μs |  1.02 |    0.02 |      - |      32 B |        0.11 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Get              | GET                  | 121.929 μs |  30.0051 μs |  1.6447 μs |  1.00 |    0.02 |      - |     504 B |        1.00 |
| Respire_Get                    | GET                  | 120.272 μs |  25.8172 μs |  1.4151 μs |  0.99 |    0.02 |      - |      80 B |        0.16 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 106.384 μs |  19.8615 μs |  1.0887 μs |  1.00 |    0.01 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 105.819 μs |  22.6249 μs |  1.2401 μs |  0.99 |    0.01 |      - |      50 B |        0.15 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   3.779 μs |   3.2438 μs |  0.1778 μs |  1.00 |    0.06 | 0.0098 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   3.587 μs |   0.7390 μs |  0.0405 μs |  0.95 |    0.04 |      - |      52 B |        0.18 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_HGet             | HGET                 | 124.859 μs |  14.6292 μs |  0.8019 μs |  1.00 |    0.01 |      - |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 121.235 μs |  37.9598 μs |  2.0807 μs |  0.97 |    0.02 |      - |      80 B |        0.15 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_HSet             | HSET                 | 122.978 μs |  21.3592 μs |  1.1708 μs |  1.00 |    0.01 |      - |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 121.106 μs |  15.5484 μs |  0.8523 μs |  0.98 |    0.01 |      - |      32 B |        0.10 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Incr             | INCR                 | 120.379 μs |  11.1478 μs |  0.6110 μs |  1.00 |    0.01 |      - |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 121.476 μs |  11.9728 μs |  0.6563 μs |  1.01 |    0.01 |      - |      32 B |        0.11 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 239.517 μs |  37.2941 μs |  2.0442 μs |  1.00 |    0.01 |      - |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 232.403 μs |  32.3667 μs |  1.7741 μs |  0.97 |    0.01 |      - |     256 B |        0.34 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Ping             | PING                 | 119.820 μs |  44.5324 μs |  2.4410 μs |  1.00 |    0.03 |      - |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 120.201 μs |  42.8153 μs |  2.3469 μs |  1.00 |    0.02 |      - |      32 B |        0.11 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 104.477 μs |  26.2154 μs |  1.4370 μs |  1.00 |    0.02 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 105.135 μs |  21.9753 μs |  1.2045 μs |  1.01 |    0.02 |      - |       2 B |       0.008 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 122.485 μs |  16.9185 μs |  0.9274 μs |  1.00 |    0.01 |      - |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 121.889 μs |  21.6475 μs |  1.1866 μs |  1.00 |    0.01 |      - |      32 B |        0.10 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 129.361 μs |   2.4682 μs |  0.1353 μs |  1.00 |    0.00 |      - |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 129.746 μs |  15.3846 μs |  0.8433 μs |  1.00 |    0.01 |      - |      32 B |        0.10 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 121.903 μs |  72.7029 μs |  3.9851 μs |  1.00 |    0.04 |      - |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 120.411 μs |  45.0778 μs |  2.4709 μs |  0.99 |    0.03 |      - |      32 B |        0.10 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 121.034 μs | 185.9843 μs | 10.1944 μs |  1.00 |    0.11 |      - |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 125.290 μs |  10.5566 μs |  0.5786 μs |  1.04 |    0.08 |      - |      32 B |        0.10 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 105.825 μs |  46.4108 μs |  2.5439 μs |  1.00 |    0.03 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  |  98.584 μs |  72.7516 μs |  3.9878 μs |  0.93 |    0.04 |      - |       2 B |       0.008 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 231.074 μs |  54.4978 μs |  2.9872 μs |  1.00 |    0.02 |      - |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 229.104 μs |  47.1228 μs |  2.5830 μs |  0.99 |    0.01 |      - |     200 B |        0.31 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 120.976 μs |  18.3441 μs |  1.0055 μs |  1.00 |    0.01 |      - |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 118.793 μs |  21.1296 μs |  1.1582 μs |  0.98 |    0.01 |      - |      32 B |        0.10 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
