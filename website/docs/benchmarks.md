---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-10 08:46 UTC from commit `e0589fef38d6`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31371170867) for logs and downloadable artifacts.
:::

StackExchange.Redis is the baseline. A ratio below `1.00` means Respire completed the operation faster.

## net10.0

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Xeon 6973P-C 2.60GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                         | Categories           | Mean       | Error      | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|-----------:|----------:|------:|--------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               |  87.654 μs | 10.2807 μs | 0.5635 μs |  1.00 |    0.01 |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               |  84.769 μs |  6.9892 μs | 0.3831 μs |  0.97 |    0.01 |         - |        0.00 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get              | GET                  | 100.348 μs | 38.5641 μs | 2.1138 μs |  1.00 |    0.03 |     504 B |        1.00 |
| Respire_Get                    | GET                  |  86.982 μs | 15.8248 μs | 0.8674 μs |  0.87 |    0.02 |      48 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  |  76.546 μs | 17.7947 μs | 0.9754 μs |  1.00 |    0.02 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  |  84.493 μs | 16.7084 μs | 0.9158 μs |  1.10 |    0.02 |      50 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   2.486 μs |  0.5787 μs | 0.0317 μs |  1.00 |    0.02 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   2.362 μs |  0.4732 μs | 0.0259 μs |  0.95 |    0.01 |      52 B |        0.18 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HGet             | HGET                 |  95.176 μs | 23.7911 μs | 1.3041 μs |  1.00 |    0.02 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 |  87.209 μs | 20.7799 μs | 1.1390 μs |  0.92 |    0.01 |      48 B |        0.09 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HSet             | HSET                 |  98.120 μs | 90.0009 μs | 4.9333 μs |  1.00 |    0.06 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 |  91.339 μs | 64.7759 μs | 3.5506 μs |  0.93 |    0.05 |         - |        0.00 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Incr             | INCR                 |  94.635 μs | 24.5190 μs | 1.3440 μs |  1.00 |    0.02 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 |  87.053 μs | 53.5554 μs | 2.9356 μs |  0.92 |    0.03 |         - |        0.00 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 191.393 μs |  8.2862 μs | 0.4542 μs |  1.00 |    0.00 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 165.981 μs | 71.6329 μs | 3.9264 μs |  0.87 |    0.02 |     256 B |        0.34 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping             | PING                 |  88.139 μs | 32.6006 μs | 1.7870 μs |  1.00 |    0.02 |     304 B |        1.00 |
| Respire_Ping                   | PING                 |  89.293 μs | 63.2763 μs | 3.4684 μs |  1.01 |    0.04 |         - |        0.00 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential |  78.071 μs | 44.8400 μs | 2.4578 μs |  1.00 |    0.04 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential |  73.282 μs | 17.7884 μs | 0.9750 μs |  0.94 |    0.03 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SAdd             | SADD                 |  93.481 μs | 18.4259 μs | 1.0100 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 |  92.013 μs | 21.4297 μs | 1.1746 μs |  0.98 |    0.01 |         - |        0.00 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 100.722 μs | 48.7254 μs | 2.6708 μs |  1.00 |    0.03 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             |  91.175 μs |  7.3975 μs | 0.4055 μs |  0.91 |    0.02 |         - |        0.00 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 100.613 μs | 22.6888 μs | 1.2437 μs |  1.00 |    0.02 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              |  88.589 μs | 27.0152 μs | 1.4808 μs |  0.88 |    0.02 |         - |        0.00 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              |  93.127 μs | 34.8614 μs | 1.9109 μs |  1.00 |    0.02 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              |  86.711 μs | 10.9127 μs | 0.5982 μs |  0.93 |    0.02 |         - |        0.00 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  |  76.534 μs | 17.6546 μs | 0.9677 μs |  1.00 |    0.02 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  |  76.489 μs | 20.7312 μs | 1.1363 μs |  1.00 |    0.02 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 172.955 μs | 39.2444 μs | 2.1511 μs |  1.00 |    0.02 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 182.540 μs | 43.7064 μs | 2.3957 μs |  1.06 |    0.02 |     200 B |        0.31 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            |  91.957 μs | 63.2753 μs | 3.4683 μs |  1.00 |    0.05 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            |  86.680 μs |  9.0987 μs | 0.4987 μs |  0.94 |    0.03 |         - |        0.00 |

## net8.0

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD EPYC 9V74 2.87GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 8.0.29 (8.0.29, 8.0.2926.32403), X64 RyuJIT x86-64-v3
  ShortRun : .NET 8.0.29 (8.0.29, 8.0.2926.32403), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                         | Categories           | Mean       | Error      | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|-----------:|----------:|------:|--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 123.118 μs |  57.625 μs | 3.1586 μs |  1.00 |    0.03 |      - |     295 B |        1.00 |
| Respire_Exists                 | EXISTS               | 125.577 μs |  41.718 μs | 2.2867 μs |  1.02 |    0.03 |      - |         - |        0.00 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Get              | GET                  | 121.835 μs | 126.242 μs | 6.9198 μs |  1.00 |    0.07 |      - |     490 B |        1.00 |
| Respire_Get                    | GET                  | 117.052 μs | 154.678 μs | 8.4784 μs |  0.96 |    0.08 |      - |      48 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 108.800 μs |  47.515 μs | 2.6045 μs |  1.00 |    0.03 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 105.403 μs |  59.661 μs | 3.2702 μs |  0.97 |    0.03 |      - |      50 B |        0.15 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   4.210 μs |   1.031 μs | 0.0565 μs |  1.00 |    0.02 | 0.0098 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   4.503 μs |   1.119 μs | 0.0613 μs |  1.07 |    0.02 |      - |      52 B |        0.18 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_HGet             | HGET                 | 122.756 μs |  93.541 μs | 5.1273 μs |  1.00 |    0.05 |      - |     506 B |        1.00 |
| Respire_HGet                   | HGET                 | 124.456 μs |  82.559 μs | 4.5254 μs |  1.02 |    0.05 |      - |      48 B |        0.09 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_HSet             | HSET                 | 121.795 μs |  25.243 μs | 1.3837 μs |  1.00 |    0.01 |      - |     321 B |        1.00 |
| Respire_HSet                   | HSET                 | 123.241 μs |  93.017 μs | 5.0986 μs |  1.01 |    0.04 |      - |         - |        0.00 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Incr             | INCR                 | 118.979 μs |  70.634 μs | 3.8717 μs |  1.00 |    0.04 |      - |     292 B |        1.00 |
| Respire_Incr                   | INCR                 | 122.802 μs |  74.471 μs | 4.0820 μs |  1.03 |    0.04 |      - |         - |        0.00 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 259.069 μs | 115.663 μs | 6.3399 μs |  1.00 |    0.03 |      - |     759 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 251.306 μs |  40.099 μs | 2.1980 μs |  0.97 |    0.02 |      - |     255 B |        0.34 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Ping             | PING                 | 113.461 μs | 120.051 μs | 6.5804 μs |  1.00 |    0.07 |      - |     301 B |        1.00 |
| Respire_Ping                   | PING                 | 122.546 μs |  48.826 μs | 2.6763 μs |  1.08 |    0.06 |      - |         - |        0.00 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 107.333 μs |  80.457 μs | 4.4101 μs |  1.00 |    0.05 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 101.820 μs |  63.752 μs | 3.4945 μs |  0.95 |    0.04 |      - |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 122.979 μs |  18.616 μs | 1.0204 μs |  1.00 |    0.01 |      - |     308 B |        1.00 |
| Respire_SAdd                   | SADD                 | 122.890 μs | 106.622 μs | 5.8443 μs |  1.00 |    0.04 |      - |         - |        0.00 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 144.164 μs |  48.412 μs | 2.6536 μs |  1.00 |    0.02 |      - |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 153.159 μs |  16.341 μs | 0.8957 μs |  1.06 |    0.02 |      - |         - |        0.00 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 125.736 μs |  19.986 μs | 1.0955 μs |  1.00 |    0.01 |      - |     310 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 125.896 μs | 115.820 μs | 6.3485 μs |  1.00 |    0.04 |      - |         - |        0.00 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 127.295 μs |  27.259 μs | 1.4941 μs |  1.00 |    0.01 |      - |     307 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 132.579 μs |  21.810 μs | 1.1955 μs |  1.04 |    0.01 |      - |         - |        0.00 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 107.924 μs |  42.198 μs | 2.3130 μs |  1.00 |    0.03 |      - |     250 B |        1.00 |
| Respire_Set_SteadyState        | SET x100 sequential  | 107.375 μs |  42.916 μs | 2.3524 μs |  1.00 |    0.03 |      - |       3 B |        0.01 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 242.201 μs |  75.954 μs | 4.1633 μs |  1.00 |    0.02 |      - |     647 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 250.759 μs |  41.951 μs | 2.2995 μs |  1.04 |    0.02 |      - |     200 B |        0.31 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 122.777 μs |  17.921 μs | 0.9823 μs |  1.00 |    0.01 |      - |     310 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 122.121 μs |  33.763 μs | 1.8507 μs |  0.99 |    0.01 |      - |         - |        0.00 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
