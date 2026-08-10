---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-10 00:40 UTC from commit `ef67b92ea1a4`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31344842043) for logs and downloadable artifacts.
:::

StackExchange.Redis is the baseline. A ratio below `1.00` means Respire completed the operation faster.

## net10.0

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD EPYC 7763 2.60GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                         | Categories           | Mean       | Error      | StdDev    | Ratio | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|-----------:|----------:|------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 179.977 μs | 31.8072 μs | 1.7435 μs |  1.00 |     295 B |        1.00 |
| Respire_Exists                 | EXISTS               | 175.715 μs | 23.0281 μs | 1.2622 μs |  0.98 |      32 B |        0.11 |
|                                |                      |            |            |           |       |           |             |
| StackExchange_Get              | GET                  | 181.958 μs | 19.8432 μs | 1.0877 μs |  1.00 |     503 B |        1.00 |
| Respire_Get                    | GET                  | 175.315 μs | 11.8688 μs | 0.6506 μs |  0.96 |      80 B |        0.16 |
|                                |                      |            |            |           |       |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 164.125 μs | 28.1303 μs | 1.5419 μs |  1.00 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 159.270 μs | 12.9379 μs | 0.7092 μs |  0.97 |      50 B |        0.15 |
|                                |                      |            |            |           |       |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.117 μs |  0.8012 μs | 0.0439 μs |  1.00 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   4.993 μs |  0.5969 μs | 0.0327 μs |  0.98 |      52 B |        0.18 |
|                                |                      |            |            |           |       |           |             |
| StackExchange_HGet             | HGET                 | 183.663 μs | 21.4348 μs | 1.1749 μs |  1.00 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 175.270 μs | 25.5713 μs | 1.4016 μs |  0.95 |      80 B |        0.15 |
|                                |                      |            |            |           |       |           |             |
| StackExchange_HSet             | HSET                 | 181.539 μs | 17.5509 μs | 0.9620 μs |  1.00 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 174.787 μs | 25.4681 μs | 1.3960 μs |  0.96 |      32 B |        0.10 |
|                                |                      |            |            |           |       |           |             |
| StackExchange_Incr             | INCR                 | 179.879 μs | 15.4129 μs | 0.8448 μs |  1.00 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 174.343 μs | 12.1910 μs | 0.6682 μs |  0.97 |      32 B |        0.11 |
|                                |                      |            |            |           |       |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 352.999 μs | 28.8737 μs | 1.5827 μs |  1.00 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 349.040 μs | 42.1259 μs | 2.3091 μs |  0.99 |     256 B |        0.34 |
|                                |                      |            |            |           |       |           |             |
| StackExchange_Ping             | PING                 | 177.634 μs |  8.4392 μs | 0.4626 μs |  1.00 |     303 B |        1.00 |
| Respire_Ping                   | PING                 | 172.341 μs | 12.4431 μs | 0.6820 μs |  0.97 |      32 B |        0.11 |
|                                |                      |            |            |           |       |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 162.865 μs |  7.5844 μs | 0.4157 μs |  1.00 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 158.215 μs | 17.6902 μs | 0.9697 μs |  0.97 |       2 B |       0.008 |
|                                |                      |            |            |           |       |           |             |
| StackExchange_SAdd             | SADD                 | 180.900 μs | 25.4270 μs | 1.3937 μs |  1.00 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 173.589 μs | 40.2359 μs | 2.2055 μs |  0.96 |      32 B |        0.10 |
|                                |                      |            |            |           |       |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 191.296 μs |  2.4815 μs | 0.1360 μs |  1.00 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 188.527 μs | 17.9029 μs | 0.9813 μs |  0.99 |      32 B |        0.10 |
|                                |                      |            |            |           |       |           |             |
| StackExchange_Set_Small        | SET 13B              | 182.175 μs |  9.3979 μs | 0.5151 μs |  1.00 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 176.176 μs |  7.2063 μs | 0.3950 μs |  0.97 |      32 B |        0.10 |
|                                |                      |            |            |           |       |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 182.600 μs | 11.9817 μs | 0.6568 μs |  1.00 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 177.400 μs |  9.6381 μs | 0.5283 μs |  0.97 |      32 B |        0.10 |
|                                |                      |            |            |           |       |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 165.826 μs | 12.2873 μs | 0.6735 μs |  1.00 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 164.948 μs | 11.3483 μs | 0.6220 μs |  0.99 |       2 B |       0.008 |
|                                |                      |            |            |           |       |           |             |
| StackExchange_SetDel           | SET+DEL              | 351.490 μs | 34.8068 μs | 1.9079 μs |  1.00 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 345.134 μs | 24.9900 μs | 1.3698 μs |  0.98 |     200 B |        0.31 |
|                                |                      |            |            |           |       |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 179.227 μs | 13.3751 μs | 0.7331 μs |  1.00 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 174.557 μs |  0.7420 μs | 0.0407 μs |  0.97 |      32 B |        0.10 |

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
| Method                         | Categories           | Mean       | Error       | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|------------:|----------:|------:|--------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 194.205 μs |  36.1952 μs | 1.9840 μs |  1.00 |    0.01 |     295 B |        1.00 |
| Respire_Exists                 | EXISTS               | 189.386 μs |  42.7946 μs | 2.3457 μs |  0.98 |    0.01 |      32 B |        0.11 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Get              | GET                  | 200.111 μs |  23.3349 μs | 1.2791 μs |  1.00 |    0.01 |     504 B |        1.00 |
| Respire_Get                    | GET                  | 192.814 μs |  18.9315 μs | 1.0377 μs |  0.96 |    0.01 |      80 B |        0.16 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 175.874 μs |  95.4873 μs | 5.2340 μs |  1.00 |    0.04 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 178.731 μs |  30.0582 μs | 1.6476 μs |  1.02 |    0.03 |      50 B |        0.15 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.473 μs |   1.7428 μs | 0.0955 μs |  1.00 |    0.02 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   5.434 μs |   0.6562 μs | 0.0360 μs |  0.99 |    0.02 |      52 B |        0.18 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_HGet             | HGET                 | 196.928 μs |  15.9181 μs | 0.8725 μs |  1.00 |    0.01 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 193.428 μs |  19.1450 μs | 1.0494 μs |  0.98 |    0.01 |      80 B |        0.15 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_HSet             | HSET                 | 194.553 μs |  15.8487 μs | 0.8687 μs |  1.00 |    0.01 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 194.202 μs |  35.9750 μs | 1.9719 μs |  1.00 |    0.01 |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Incr             | INCR                 | 190.204 μs |  33.1013 μs | 1.8144 μs |  1.00 |    0.01 |     294 B |        1.00 |
| Respire_Incr                   | INCR                 | 192.540 μs |  11.0179 μs | 0.6039 μs |  1.01 |    0.01 |      32 B |        0.11 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 382.971 μs |  29.0404 μs | 1.5918 μs |  1.00 |    0.01 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 384.260 μs |  39.1050 μs | 2.1435 μs |  1.00 |    0.01 |     256 B |        0.34 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Ping             | PING                 | 190.103 μs |  27.8658 μs | 1.5274 μs |  1.00 |    0.01 |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 192.517 μs |  33.7793 μs | 1.8516 μs |  1.01 |    0.01 |      32 B |        0.11 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 176.952 μs |  68.0279 μs | 3.7288 μs |  1.00 |    0.03 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 176.059 μs |  41.9502 μs | 2.2994 μs |  1.00 |    0.02 |       2 B |       0.008 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_SAdd             | SADD                 | 192.393 μs |   6.8859 μs | 0.3774 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 192.024 μs |   6.6454 μs | 0.3643 μs |  1.00 |    0.00 |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 203.549 μs |   1.9328 μs | 0.1059 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 209.959 μs |  24.5493 μs | 1.3456 μs |  1.03 |    0.01 |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 194.845 μs |  15.7289 μs | 0.8622 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 195.538 μs |   3.8413 μs | 0.2106 μs |  1.00 |    0.00 |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 193.859 μs |  49.7570 μs | 2.7273 μs |  1.00 |    0.02 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 196.485 μs |  44.2781 μs | 2.4270 μs |  1.01 |    0.02 |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 180.757 μs | 105.4087 μs | 5.7778 μs |  1.00 |    0.04 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 181.060 μs |  39.9493 μs | 2.1898 μs |  1.00 |    0.03 |       2 B |       0.008 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 371.006 μs |  98.2606 μs | 5.3860 μs |  1.00 |    0.02 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 360.865 μs |  18.9037 μs | 1.0362 μs |  0.97 |    0.01 |     200 B |        0.31 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 190.928 μs |  66.5978 μs | 3.6505 μs |  1.00 |    0.02 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 191.972 μs |  20.7578 μs | 1.1378 μs |  1.01 |    0.02 |      32 B |        0.10 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
