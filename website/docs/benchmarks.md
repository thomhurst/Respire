---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-09 00:00 UTC from commit `ec4c4c3c7ef9`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31285060641) for logs and downloadable artifacts.
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
| StackExchange_Exists           | EXISTS               | 182.095 μs | 20.4420 μs | 1.1205 μs |  1.00 |    0.01 |      - |     293 B |        1.00 |
| Respire_Exists                 | EXISTS               | 185.510 μs | 69.5313 μs | 3.8112 μs |  1.02 |    0.02 |      - |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Get              | GET                  | 185.123 μs | 12.7958 μs | 0.7014 μs |  1.00 |    0.00 |      - |     504 B |        1.00 |
| Respire_Get                    | GET                  | 185.627 μs | 47.9905 μs | 2.6305 μs |  1.00 |    0.01 |      - |      80 B |        0.16 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 167.720 μs | 29.2567 μs | 1.6037 μs |  1.00 |    0.01 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 168.306 μs | 17.1549 μs | 0.9403 μs |  1.00 |    0.01 |      - |      50 B |        0.15 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   4.920 μs |  1.4190 μs | 0.0778 μs |  1.00 |    0.02 | 0.0098 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   4.556 μs |  0.3975 μs | 0.0218 μs |  0.93 |    0.01 |      - |      52 B |        0.18 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_HGet             | HGET                 | 185.688 μs | 17.6508 μs | 0.9675 μs |  1.00 |    0.01 |      - |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 185.612 μs | 31.7412 μs | 1.7398 μs |  1.00 |    0.01 |      - |      80 B |        0.15 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_HSet             | HSET                 | 184.998 μs |  3.2682 μs | 0.1791 μs |  1.00 |    0.00 |      - |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 187.318 μs | 11.9493 μs | 0.6550 μs |  1.01 |    0.00 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Incr             | INCR                 | 183.457 μs | 15.1109 μs | 0.8283 μs |  1.00 |    0.01 |      - |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 184.493 μs | 28.8419 μs | 1.5809 μs |  1.01 |    0.01 |      - |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 357.753 μs | 23.9418 μs | 1.3123 μs |  1.00 |    0.00 |      - |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 356.395 μs | 87.7846 μs | 4.8118 μs |  1.00 |    0.01 |      - |     320 B |        0.42 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Ping             | PING                 | 181.633 μs | 43.7086 μs | 2.3958 μs |  1.00 |    0.02 |      - |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 183.381 μs | 31.9080 μs | 1.7490 μs |  1.01 |    0.01 |      - |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 167.902 μs | 25.6701 μs | 1.4071 μs |  1.00 |    0.01 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 167.583 μs |  7.1191 μs | 0.3902 μs |  1.00 |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 182.786 μs | 20.2188 μs | 1.1083 μs |  1.00 |    0.01 |      - |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 185.978 μs | 14.8136 μs | 0.8120 μs |  1.02 |    0.01 |      - |      96 B |        0.31 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 195.244 μs | 17.1231 μs | 0.9386 μs |  1.00 |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 195.860 μs | 14.7466 μs | 0.8083 μs |  1.00 |    0.01 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 185.423 μs |  3.3291 μs | 0.1825 μs |  1.00 |    0.00 |      - |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 188.980 μs | 14.2374 μs | 0.7804 μs |  1.02 |    0.00 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 185.529 μs |  8.1191 μs | 0.4450 μs |  1.00 |    0.00 |      - |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 188.468 μs | 24.0230 μs | 1.3168 μs |  1.02 |    0.01 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 169.894 μs | 31.3598 μs | 1.7189 μs |  1.00 |    0.01 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 169.873 μs | 25.9079 μs | 1.4201 μs |  1.00 |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 356.760 μs | 67.5269 μs | 3.7014 μs |  1.00 |    0.01 |      - |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 353.816 μs | 15.5325 μs | 0.8514 μs |  0.99 |    0.01 |      - |     264 B |        0.41 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 184.393 μs |  2.7332 μs | 0.1498 μs |  1.00 |    0.00 |      - |     311 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 185.959 μs |  6.0104 μs | 0.3295 μs |  1.01 |    0.00 |      - |      32 B |        0.10 |

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
| Method                         | Categories           | Mean       | Error      | StdDev     | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|-----------:|-----------:|------:|--------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 174.149 μs |  90.885 μs |  4.9817 μs |  1.00 |    0.04 |     294 B |        1.00 |
| Respire_Exists                 | EXISTS               | 179.413 μs | 209.898 μs | 11.5052 μs |  1.03 |    0.06 |      32 B |        0.11 |
|                                |                      |            |            |            |       |         |           |             |
| StackExchange_Get              | GET                  | 193.663 μs |   7.419 μs |  0.4066 μs |  1.00 |    0.00 |     504 B |        1.00 |
| Respire_Get                    | GET                  | 191.668 μs |   9.582 μs |  0.5252 μs |  0.99 |    0.00 |      80 B |        0.16 |
|                                |                      |            |            |            |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 174.050 μs |  14.111 μs |  0.7735 μs |  1.00 |    0.01 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 174.695 μs |  25.826 μs |  1.4156 μs |  1.00 |    0.01 |      50 B |        0.15 |
|                                |                      |            |            |            |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.211 μs |   1.783 μs |  0.0977 μs |  1.00 |    0.02 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   4.827 μs |   1.076 μs |  0.0590 μs |  0.93 |    0.02 |      52 B |        0.18 |
|                                |                      |            |            |            |       |         |           |             |
| StackExchange_HGet             | HGET                 | 193.337 μs |  17.949 μs |  0.9839 μs |  1.00 |    0.01 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 190.903 μs |   7.562 μs |  0.4145 μs |  0.99 |    0.00 |      80 B |        0.15 |
|                                |                      |            |            |            |       |         |           |             |
| StackExchange_HSet             | HSET                 | 189.750 μs |   9.137 μs |  0.5008 μs |  1.00 |    0.00 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 192.017 μs |  17.874 μs |  0.9797 μs |  1.01 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |            |       |         |           |             |
| StackExchange_Incr             | INCR                 | 188.137 μs |  45.957 μs |  2.5191 μs |  1.00 |    0.02 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 189.613 μs |  26.638 μs |  1.4601 μs |  1.01 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |            |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 371.423 μs | 105.933 μs |  5.8066 μs |  1.00 |    0.02 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 370.405 μs |  43.178 μs |  2.3667 μs |  1.00 |    0.01 |     320 B |        0.42 |
|                                |                      |            |            |            |       |         |           |             |
| StackExchange_Ping             | PING                 | 186.011 μs |  29.149 μs |  1.5978 μs |  1.00 |    0.01 |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 188.090 μs |   9.752 μs |  0.5345 μs |  1.01 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |            |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 172.798 μs | 106.818 μs |  5.8550 μs |  1.00 |    0.04 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 172.325 μs |  40.624 μs |  2.2267 μs |  1.00 |    0.03 |       2 B |       0.008 |
|                                |                      |            |            |            |       |         |           |             |
| StackExchange_SAdd             | SADD                 | 190.150 μs |  39.218 μs |  2.1497 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 190.480 μs |  23.670 μs |  1.2974 μs |  1.00 |    0.01 |      96 B |        0.31 |
|                                |                      |            |            |            |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 200.439 μs |  17.306 μs |  0.9486 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 201.329 μs |  38.623 μs |  2.1171 μs |  1.00 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |            |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 190.496 μs |  17.802 μs |  0.9758 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 193.233 μs |  66.284 μs |  3.6333 μs |  1.01 |    0.02 |      32 B |        0.10 |
|                                |                      |            |            |            |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 191.934 μs |  23.371 μs |  1.2810 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 191.460 μs |  23.803 μs |  1.3047 μs |  1.00 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |            |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 178.082 μs |  48.135 μs |  2.6384 μs |  1.00 |    0.02 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 176.373 μs |  45.087 μs |  2.4713 μs |  0.99 |    0.02 |       2 B |       0.008 |
|                                |                      |            |            |            |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 368.440 μs |  55.885 μs |  3.0633 μs |  1.00 |    0.01 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 366.431 μs | 128.245 μs |  7.0296 μs |  0.99 |    0.02 |     264 B |        0.41 |
|                                |                      |            |            |            |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 189.226 μs |  23.485 μs |  1.2873 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 189.468 μs |   7.798 μs |  0.4274 μs |  1.00 |    0.01 |      32 B |        0.10 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
