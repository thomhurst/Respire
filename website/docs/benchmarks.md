---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-09 23:25 UTC from commit `f5949be1a4f3`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31341619242) for logs and downloadable artifacts.
:::

StackExchange.Redis is the baseline. A ratio below `1.00` means Respire completed the operation faster.

## net10.0

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD EPYC 7763 3.06GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                         | Categories           | Mean       | Error      | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|-----------:|----------:|------:|--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 189.616 μs | 64.0565 μs | 3.5112 μs |  1.00 |    0.02 |      - |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 191.419 μs | 23.5619 μs | 1.2915 μs |  1.01 |    0.02 |      - |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Get              | GET                  | 194.287 μs | 27.4566 μs | 1.5050 μs |  1.00 |    0.01 |      - |     504 B |        1.00 |
| Respire_Get                    | GET                  | 191.608 μs | 28.3842 μs | 1.5558 μs |  0.99 |    0.01 |      - |      80 B |        0.16 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 176.161 μs | 40.2832 μs | 2.2081 μs |  1.00 |    0.02 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 175.635 μs |  8.5234 μs | 0.4672 μs |  1.00 |    0.01 |      - |      50 B |        0.15 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.470 μs |  0.5753 μs | 0.0315 μs |  1.00 |    0.01 | 0.0098 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   5.368 μs |  0.1042 μs | 0.0057 μs |  0.98 |    0.00 |      - |      52 B |        0.18 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_HGet             | HGET                 | 193.941 μs | 37.0689 μs | 2.0319 μs |  1.00 |    0.01 |      - |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 192.756 μs |  8.9424 μs | 0.4902 μs |  0.99 |    0.01 |      - |      80 B |        0.15 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_HSet             | HSET                 | 193.353 μs | 22.4976 μs | 1.2332 μs |  1.00 |    0.01 |      - |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 192.656 μs | 14.6624 μs | 0.8037 μs |  1.00 |    0.01 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Incr             | INCR                 | 190.647 μs | 40.6195 μs | 2.2265 μs |  1.00 |    0.01 |      - |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 192.099 μs | 10.9953 μs | 0.6027 μs |  1.01 |    0.01 |      - |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 374.621 μs | 43.0453 μs | 2.3595 μs |  1.00 |    0.01 |      - |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 371.375 μs | 28.8333 μs | 1.5804 μs |  0.99 |    0.01 |      - |     256 B |        0.34 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Ping             | PING                 | 189.764 μs | 15.1901 μs | 0.8326 μs |  1.00 |    0.01 |      - |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 192.023 μs | 17.6946 μs | 0.9699 μs |  1.01 |    0.01 |      - |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 174.562 μs | 15.3673 μs | 0.8423 μs |  1.00 |    0.01 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 171.877 μs | 18.1487 μs | 0.9948 μs |  0.98 |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 190.890 μs | 46.8629 μs | 2.5687 μs |  1.00 |    0.02 |      - |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 190.260 μs | 73.7547 μs | 4.0427 μs |  1.00 |    0.02 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 202.312 μs | 26.2955 μs | 1.4413 μs |  1.00 |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 208.123 μs |  6.0929 μs | 0.3340 μs |  1.03 |    0.01 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 193.201 μs | 36.2194 μs | 1.9853 μs |  1.00 |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 192.651 μs | 19.9774 μs | 1.0950 μs |  1.00 |    0.01 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 195.407 μs | 16.1802 μs | 0.8869 μs |  1.00 |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 191.798 μs | 20.6269 μs | 1.1306 μs |  0.98 |    0.01 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 175.390 μs | 10.2737 μs | 0.5631 μs |  1.00 |    0.00 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 174.682 μs | 39.0505 μs | 2.1405 μs |  1.00 |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 370.609 μs | 25.7835 μs | 1.4133 μs |  1.00 |    0.00 |      - |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 368.678 μs | 14.4362 μs | 0.7913 μs |  0.99 |    0.00 |      - |     200 B |        0.31 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 192.474 μs | 25.1144 μs | 1.3766 μs |  1.00 |    0.01 |      - |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 191.845 μs | 21.8939 μs | 1.2001 μs |  1.00 |    0.01 |      - |      32 B |        0.10 |

## net8.0

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
INTEL XEON PLATINUM 8573C 2.30GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 8.0.29 (8.0.29, 8.0.2926.32403), X64 RyuJIT x86-64-v4
  ShortRun : .NET 8.0.29 (8.0.29, 8.0.2926.32403), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                         | Categories           | Mean       | Error       | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|------------:|----------:|------:|--------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 101.265 μs |  35.4202 μs | 1.9415 μs |  1.00 |    0.02 |     294 B |        1.00 |
| Respire_Exists                 | EXISTS               | 100.069 μs |   7.5639 μs | 0.4146 μs |  0.99 |    0.02 |      32 B |        0.11 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Get              | GET                  | 103.818 μs |  12.7686 μs | 0.6999 μs |  1.00 |    0.01 |     504 B |        1.00 |
| Respire_Get                    | GET                  | 100.174 μs |  12.7689 μs | 0.6999 μs |  0.96 |    0.01 |      80 B |        0.16 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  |  89.107 μs |   9.8028 μs | 0.5373 μs |  1.00 |    0.01 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  |  87.909 μs |   4.3271 μs | 0.2372 μs |  0.99 |    0.01 |      50 B |        0.15 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   3.291 μs |   1.7311 μs | 0.0949 μs |  1.00 |    0.04 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   3.160 μs |   0.5413 μs | 0.0297 μs |  0.96 |    0.03 |      52 B |        0.18 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_HGet             | HGET                 | 104.310 μs |  17.1816 μs | 0.9418 μs |  1.00 |    0.01 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 |  99.651 μs |  12.9835 μs | 0.7117 μs |  0.96 |    0.01 |      80 B |        0.15 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_HSet             | HSET                 | 105.257 μs |  37.1722 μs | 2.0375 μs |  1.00 |    0.02 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 100.652 μs |  14.3186 μs | 0.7849 μs |  0.96 |    0.02 |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Incr             | INCR                 | 104.002 μs |  17.1256 μs | 0.9387 μs |  1.00 |    0.01 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 |  99.912 μs |   7.6003 μs | 0.4166 μs |  0.96 |    0.01 |      32 B |        0.11 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 202.830 μs |  35.9122 μs | 1.9685 μs |  1.00 |    0.01 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 192.480 μs |  59.5213 μs | 3.2626 μs |  0.95 |    0.02 |     256 B |        0.34 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Ping             | PING                 |  99.274 μs |  27.5528 μs | 1.5103 μs |  1.00 |    0.02 |     304 B |        1.00 |
| Respire_Ping                   | PING                 |  96.069 μs |  43.8343 μs | 2.4027 μs |  0.97 |    0.02 |      32 B |        0.11 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential |  86.310 μs |  50.8669 μs | 2.7882 μs |  1.00 |    0.04 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential |  87.151 μs |  11.6942 μs | 0.6410 μs |  1.01 |    0.03 |       2 B |       0.008 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_SAdd             | SADD                 | 105.095 μs |  17.1714 μs | 0.9412 μs |  1.00 |    0.01 |     311 B |        1.00 |
| Respire_SAdd                   | SADD                 | 100.153 μs |  20.7937 μs | 1.1398 μs |  0.95 |    0.01 |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 109.541 μs |  16.2919 μs | 0.8930 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 106.609 μs |   4.5312 μs | 0.2484 μs |  0.97 |    0.01 |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 105.916 μs |  20.9994 μs | 1.1510 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 100.633 μs |  11.4439 μs | 0.6273 μs |  0.95 |    0.01 |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 105.377 μs |  43.4231 μs | 2.3802 μs |  1.00 |    0.03 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 103.410 μs |  15.0555 μs | 0.8252 μs |  0.98 |    0.02 |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  |  90.060 μs |  14.4793 μs | 0.7937 μs |  1.00 |    0.01 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  |  90.279 μs |  12.2258 μs | 0.6701 μs |  1.00 |    0.01 |       2 B |       0.008 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 199.964 μs |   9.4837 μs | 0.5198 μs |  1.00 |    0.00 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 188.302 μs | 142.0541 μs | 7.7865 μs |  0.94 |    0.03 |     200 B |        0.31 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 103.317 μs |  31.8170 μs | 1.7440 μs |  1.00 |    0.02 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 100.958 μs |  26.2638 μs | 1.4396 μs |  0.98 |    0.02 |      32 B |        0.10 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
