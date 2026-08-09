---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-09 07:33 UTC from commit `a0648fcc2b8d`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31301221248) for logs and downloadable artifacts.
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
| Method                         | Categories           | Mean       | Error       | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|------------:|----------:|------:|--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 137.817 μs |  24.8734 μs | 1.3634 μs |  1.00 |    0.01 |      - |     295 B |        1.00 |
| Respire_Exists                 | EXISTS               | 125.938 μs |  23.2818 μs | 1.2762 μs |  0.91 |    0.01 |      - |      32 B |        0.11 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Get              | GET                  | 144.370 μs |  34.5192 μs | 1.8921 μs |  1.00 |    0.02 |      - |     502 B |        1.00 |
| Respire_Get                    | GET                  | 136.902 μs |  61.9427 μs | 3.3953 μs |  0.95 |    0.02 |      - |      80 B |        0.16 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 120.094 μs |  10.0853 μs | 0.5528 μs |  1.00 |    0.01 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 115.766 μs |   5.8992 μs | 0.3234 μs |  0.96 |    0.00 |      - |      50 B |        0.15 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   4.662 μs |   1.4106 μs | 0.0773 μs |  1.00 |    0.02 | 0.0098 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   4.425 μs |   0.8892 μs | 0.0487 μs |  0.95 |    0.02 |      - |      52 B |        0.18 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_HGet             | HGET                 | 138.226 μs | 170.3190 μs | 9.3358 μs |  1.00 |    0.08 |      - |     519 B |        1.00 |
| Respire_HGet                   | HGET                 | 142.570 μs |  33.4704 μs | 1.8346 μs |  1.03 |    0.06 |      - |      80 B |        0.15 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_HSet             | HSET                 | 142.181 μs |  34.9876 μs | 1.9178 μs |  1.00 |    0.02 |      - |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 144.476 μs |  11.4840 μs | 0.6295 μs |  1.02 |    0.01 |      - |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Incr             | INCR                 | 140.235 μs |  19.4275 μs | 1.0649 μs |  1.00 |    0.01 |      - |     295 B |        1.00 |
| Respire_Incr                   | INCR                 | 139.460 μs |  25.0181 μs | 1.3713 μs |  0.99 |    0.01 |      - |      32 B |        0.11 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 290.721 μs |  22.6825 μs | 1.2433 μs |  1.00 |    0.01 |      - |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 276.906 μs |  41.9916 μs | 2.3017 μs |  0.95 |    0.01 |      - |     255 B |        0.34 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Ping             | PING                 | 135.391 μs |  15.5070 μs | 0.8500 μs |  1.00 |    0.01 |      - |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 138.545 μs |   6.1805 μs | 0.3388 μs |  1.02 |    0.01 |      - |      32 B |        0.11 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 115.583 μs |  25.4709 μs | 1.3961 μs |  1.00 |    0.01 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 112.692 μs |   7.7845 μs | 0.4267 μs |  0.98 |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 140.290 μs |  22.8897 μs | 1.2547 μs |  1.00 |    0.01 |      - |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 139.860 μs |  17.9895 μs | 0.9861 μs |  1.00 |    0.01 |      - |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 154.945 μs |  60.8149 μs | 3.3335 μs |  1.00 |    0.03 |      - |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 159.004 μs |   2.7392 μs | 0.1501 μs |  1.03 |    0.02 |      - |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 145.413 μs |  22.0193 μs | 1.2069 μs |  1.00 |    0.01 |      - |     311 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 143.035 μs |  45.3889 μs | 2.4879 μs |  0.98 |    0.02 |      - |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 146.165 μs |  18.0994 μs | 0.9921 μs |  1.00 |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 146.424 μs |  69.3025 μs | 3.7987 μs |  1.00 |    0.02 |      - |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 118.623 μs |   6.5691 μs | 0.3601 μs |  1.00 |    0.00 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 119.431 μs |  37.5714 μs | 2.0594 μs |  1.01 |    0.02 |      - |       2 B |       0.008 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 282.748 μs |  34.1339 μs | 1.8710 μs |  1.00 |    0.01 |      - |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 277.433 μs |  35.2844 μs | 1.9341 μs |  0.98 |    0.01 |      - |     200 B |        0.31 |
|                                |                      |            |             |           |       |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 137.717 μs |  34.2580 μs | 1.8778 μs |  1.00 |    0.02 |      - |     311 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 140.840 μs |   9.2243 μs | 0.5056 μs |  1.02 |    0.01 |      - |      32 B |        0.10 |

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
| StackExchange_Exists           | EXISTS               | 192.897 μs | 27.2938 μs | 1.4961 μs |  1.00 |    0.01 |     294 B |        1.00 |
| Respire_Exists                 | EXISTS               | 193.253 μs | 10.2901 μs | 0.5640 μs |  1.00 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get              | GET                  | 197.010 μs | 25.3535 μs | 1.3897 μs |  1.00 |    0.01 |     504 B |        1.00 |
| Respire_Get                    | GET                  | 193.268 μs | 16.1650 μs | 0.8861 μs |  0.98 |    0.01 |      80 B |        0.16 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 179.019 μs | 40.4736 μs | 2.2185 μs |  1.00 |    0.02 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 180.580 μs | 41.7666 μs | 2.2894 μs |  1.01 |    0.02 |      50 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.498 μs |  1.5278 μs | 0.0837 μs |  1.00 |    0.02 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   5.014 μs |  0.4784 μs | 0.0262 μs |  0.91 |    0.01 |      52 B |        0.18 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HGet             | HGET                 | 197.718 μs | 23.5762 μs | 1.2923 μs |  1.00 |    0.01 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 194.117 μs | 15.2287 μs | 0.8347 μs |  0.98 |    0.01 |      80 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HSet             | HSET                 | 192.818 μs | 10.1412 μs | 0.5559 μs |  1.00 |    0.00 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 193.775 μs | 44.4996 μs | 2.4392 μs |  1.00 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Incr             | INCR                 | 193.625 μs | 26.7677 μs | 1.4672 μs |  1.00 |    0.01 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 193.842 μs | 36.1409 μs | 1.9810 μs |  1.00 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 389.557 μs | 33.9361 μs | 1.8602 μs |  1.00 |    0.01 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 382.008 μs | 58.0485 μs | 3.1818 μs |  0.98 |    0.01 |     256 B |        0.34 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping             | PING                 | 190.082 μs | 13.8665 μs | 0.7601 μs |  1.00 |    0.00 |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 191.456 μs | 18.3064 μs | 1.0034 μs |  1.01 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 178.853 μs | 26.9135 μs | 1.4752 μs |  1.00 |    0.01 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 176.797 μs | 33.4560 μs | 1.8338 μs |  0.99 |    0.01 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SAdd             | SADD                 | 192.081 μs | 52.1932 μs | 2.8609 μs |  1.00 |    0.02 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 193.982 μs | 24.7569 μs | 1.3570 μs |  1.01 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 205.416 μs | 19.5307 μs | 1.0705 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 209.438 μs | 49.5032 μs | 2.7134 μs |  1.02 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 196.514 μs |  5.2771 μs | 0.2893 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 194.420 μs | 27.7675 μs | 1.5220 μs |  0.99 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 195.908 μs |  4.1987 μs | 0.2301 μs |  1.00 |    0.00 |     310 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 196.042 μs | 26.1301 μs | 1.4323 μs |  1.00 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 181.433 μs | 41.9888 μs | 2.3015 μs |  1.00 |    0.02 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 182.768 μs | 21.6416 μs | 1.1862 μs |  1.01 |    0.01 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 374.227 μs | 89.9909 μs | 4.9327 μs |  1.00 |    0.02 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 373.482 μs | 75.1761 μs | 4.1207 μs |  1.00 |    0.01 |     200 B |        0.31 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 186.329 μs | 75.8268 μs | 4.1563 μs |  1.00 |    0.03 |     311 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 184.295 μs | 62.6310 μs | 3.4330 μs |  0.99 |    0.02 |      32 B |        0.10 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
